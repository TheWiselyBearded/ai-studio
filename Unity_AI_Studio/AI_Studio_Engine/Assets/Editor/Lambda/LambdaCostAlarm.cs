using System;
using UnityEditor;
using UnityEngine;
using AIStudio.Core;

namespace AIStudio.Lambda
{
    /// Watches the running cost of the active Lambda instance and prompts the
    /// user to terminate once it exceeds AIStudioSettings.MaxSessionCostUsd.
    /// Fires at most once per instance per session.
    [InitializeOnLoad]
    public static class LambdaCostAlarm
    {
        private const double PollSeconds = 15.0;
        private static double _nextPoll;
        private static string _alarmedInstanceId;

        static LambdaCostAlarm()
        {
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollSeconds;

            if (!LambdaInstanceState.HasActiveInstance) { _alarmedInstanceId = null; return; }

            var limit = AIStudioSettings.MaxSessionCostUsd;
            if (limit <= 0f) return; // disabled

            var current = LambdaInstanceState.EstimatedCostUsd;
            if (current < limit) return;

            // Already prompted for this particular instance; don't nag every 15s.
            if (_alarmedInstanceId == LambdaInstanceState.ActiveInstanceId) return;
            _alarmedInstanceId = LambdaInstanceState.ActiveInstanceId;

            var rate = LambdaInstanceState.PriceCentsPerHour / 100.0;
            int choice = EditorUtility.DisplayDialogComplex(
                "Lambda session cost exceeded threshold",
                $"Instance {LambdaInstanceState.ActiveInstanceId} has accrued about ${current:F2} " +
                $"(threshold: ${limit:F2}).\n" +
                $"Uptime: {LambdaInstanceState.Uptime:hh\\:mm\\:ss}  ·  Rate: ${rate:F2}/hr\n\n" +
                "What would you like to do?",
                "Terminate now",
                "Keep running",
                "Raise threshold by $5");

            switch (choice)
            {
                case 0:
                    _ = TerminateAsync();
                    break;
                case 2:
                    AIStudioSettings.MaxSessionCostUsd = limit + 5.0f;
                    _alarmedInstanceId = null; // re-arm for the new threshold
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
