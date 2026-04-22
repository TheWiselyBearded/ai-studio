using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIStudio.Lambda
{
    /// Blocks editor quit when a Lambda instance is still active. The user sees
    /// a dialog offering to terminate, keep running, or cancel the quit.
    /// Prevents the "I closed Unity and got billed all weekend" class of mistake.
    [InitializeOnLoad]
    public static class LambdaQuitGuard
    {
        static LambdaQuitGuard()
        {
            EditorApplication.wantsToQuit += OnWantsToQuit;
        }

        private static bool OnWantsToQuit()
        {
            if (!LambdaInstanceState.HasActiveInstance) return true;

            var uptime = LambdaInstanceState.Uptime;
            var cost = LambdaInstanceState.EstimatedCostUsd;
            var priceHr = LambdaInstanceState.PriceCentsPerHour / 100.0;

            var choice = EditorUtility.DisplayDialogComplex(
                "Lambda instance is still running",
                $"Instance {LambdaInstanceState.ActiveInstanceId}\n" +
                $"{LambdaInstanceState.InstanceTypeName} in {LambdaInstanceState.RegionName}\n" +
                $"Uptime: {uptime:hh\\:mm\\:ss}  ·  Running cost: ${cost:F2}  ·  Rate: ${priceHr:F2}/hr\n\n" +
                "Leaving it running will keep billing your Lambda account until you terminate it manually.",
                "Terminate & Quit",
                "Cancel Quit",
                "Keep Running & Quit");

            switch (choice)
            {
                case 0: // Terminate & Quit
                    // Fire-and-forget: Unity will quit once the task completes. We block
                    // this invocation by returning false, then quit ourselves afterward.
                    _ = TerminateThenQuitAsync();
                    return false;
                case 1: // Cancel Quit
                    return false;
                case 2: // Keep Running & Quit
                default:
                    return true;
            }
        }

        private static async Task TerminateThenQuitAsync()
        {
            try
            {
                // Rescue any cloned voices before the instance disappears.
                try { await LambdaVoiceSnapshot.SnapshotAllAsync(); }
                catch (Exception ex) { Debug.LogWarning($"[Lambda] Voice snapshot skipped: {ex.Message}"); }

                await LambdaClient.TerminateAsync(new[] { LambdaInstanceState.ActiveInstanceId });
                LambdaInstanceState.Clear();
                Debug.Log("[Lambda] Instance terminated on editor quit.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Lambda] Failed to terminate on quit: {ex.Message}. Please terminate from the Lambda console.");
            }
            finally
            {
                EditorApplication.Exit(0);
            }
        }
    }
}
