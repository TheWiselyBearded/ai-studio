using System;
using UnityEditor;
using UnityEngine;
using AIStudio.Core;

namespace AIStudio.Lambda
{
    /// Watches the running cost of the active Lambda instance and prompts the
    /// user to terminate once uptime exceeds AIStudioSettings.MaxSessionHours
    /// (with a dollar-based safety net via MaxSessionCostUsd if the user set one).
    /// Fires at most once per instance per session.
    [InitializeOnLoad]
    public static class LambdaCostAlarm
    {
        private const double PollSeconds = 15.0;
        private static double _nextPoll;
        private static string _alarmedInstanceId;

        static LambdaCostAlarm()
        {
            MigrateLegacyCostOnlyLimit();
            EditorApplication.update += OnUpdate;
        }

        // One-time migration for users whose stored MaxSessionCostUsd dates back
        // to the pre-hours build (default was $5, ~6 h on an A10). Raise it so
        // it never pre-empts the new 15 h cap. Only touches the value if it's
        // still at (or below) the old default — respects anything the user has
        // already bumped.
        private static void MigrateLegacyCostOnlyLimit()
        {
            const string migratedKey = "AIStudio.CostAlarmMigratedToHours";
            if (EditorPrefs.GetBool(migratedKey, false)) return;
            EditorPrefs.SetBool(migratedKey, true);
            if (AIStudioSettings.MaxSessionCostUsd <= AIStudioSettings.DefaultMaxSessionCostUsd)
                AIStudioSettings.MaxSessionCostUsd = 0f; // disable the cost alarm; hours is the new gate
        }

        private static void OnUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollSeconds;

            if (!LambdaInstanceState.HasActiveInstance) { _alarmedInstanceId = null; return; }

            var hoursLimit = AIStudioSettings.MaxSessionHours;
            var costLimit = AIStudioSettings.MaxSessionCostUsd;
            if (hoursLimit <= 0f && costLimit <= 0f) return; // both gates disabled

            var uptime = LambdaInstanceState.Uptime.TotalHours;
            var current = LambdaInstanceState.EstimatedCostUsd;
            bool hoursTripped = hoursLimit > 0f && uptime >= hoursLimit;
            bool costTripped = costLimit > 0f && current >= costLimit;
            if (!hoursTripped && !costTripped) return;

            // Already prompted for this particular instance; don't nag every 15s.
            if (_alarmedInstanceId == LambdaInstanceState.ActiveInstanceId) return;
            _alarmedInstanceId = LambdaInstanceState.ActiveInstanceId;

            var rate = LambdaInstanceState.PriceCentsPerHour / 100.0;
            string reason = hoursTripped
                ? $"Uptime {uptime:F1} h exceeds limit of {hoursLimit:F1} h."
                : $"Accrued ${current:F2} exceeds threshold ${costLimit:F2}.";
            int choice = EditorUtility.DisplayDialogComplex(
                "Lambda session limit reached",
                $"Instance {LambdaInstanceState.ActiveInstanceId}\n" +
                $"{reason}\n" +
                $"Uptime: {LambdaInstanceState.Uptime:hh\\:mm\\:ss}  ·  Rate: ${rate:F2}/hr  ·  Accrued: ${current:F2}\n\n" +
                "What would you like to do?",
                "Terminate now",
                "Keep running",
                "Extend by 5 hours");

            switch (choice)
            {
                case 0:
                    _ = TerminateAsync();
                    break;
                case 2:
                    AIStudioSettings.MaxSessionHours = hoursLimit + 5.0f;
                    _alarmedInstanceId = null; // re-arm for the new window
                    break;
            }
        }

        private static async System.Threading.Tasks.Task TerminateAsync()
        {
            try
            {
                await LambdaClient.TerminateAsync(new[] { LambdaInstanceState.ActiveInstanceId });
                LambdaInstanceState.Clear();
                if (AIStudioSettings.ActiveMode == EndpointMode.Remote)
                {
                    AIStudioSettings.RemoteBaseUrl = string.Empty;
                    AIStudioSettings.ActiveMode = EndpointMode.Local;
                }
                Debug.Log("[Lambda] Cost alarm: instance terminated.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Lambda] Cost alarm terminate failed: {ex.Message}");
            }
        }
    }
}
