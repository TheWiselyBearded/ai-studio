using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIStudio.Core;
using AIStudio.Lambda;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// MCP tools for managing Lambda Cloud GPU instance lifecycle.
    ///
    /// HIGH-BLAST-RADIUS: launching costs real money. The Launch tool requires explicit
    /// confirm_cost=true and returns a dry-run price preview otherwise. All operations
    /// log to the Unity console for an audit trail.
    public static class AIStudioMcpLambda
    {
        // --------------------------------------------------------------------
        // Status (read-only)
        // --------------------------------------------------------------------
        [McpTool(
            "AIStudio_GetLambdaInstanceStatus",
            "List currently-running Lambda Cloud GPU instances and the AI Studio editor's " +
            "tracked active instance (if any), including uptime and rolling cost estimate. Read-only.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GetLambdaInstanceStatus()
        {
            var instances = await LambdaClient.ListInstancesAsync();
            return new
            {
                success = true,
                count = instances.Count,
                instances = instances.Select(i => new
                {
                    id = i.Id,
                    name = i.Name,
                    status = i.Status,
                    ip = i.Ip,
                    region = i.Region?.Name,
                    instance_type = i.InstanceType?.Name,
                    price_cents_per_hour = i.InstanceType?.PriceCentsPerHour ?? 0,
                    file_systems = i.FileSystemNames
                }).ToArray(),
                tracked = LambdaInstanceState.HasActiveInstance ? (object)new
                {
                    instance_id = LambdaInstanceState.ActiveInstanceId,
                    instance_type = LambdaInstanceState.InstanceTypeName,
                    region = LambdaInstanceState.RegionName,
                    public_ip = LambdaInstanceState.PublicIp,
                    tunnel_url = LambdaInstanceState.TunnelUrl,
                    uptime_seconds = (long)LambdaInstanceState.Uptime.TotalSeconds,
                    estimated_cost_usd = LambdaInstanceState.EstimatedCostUsd,
                    price_cents_per_hour = LambdaInstanceState.PriceCentsPerHour
                } : null
            };
        }

        // --------------------------------------------------------------------
        // Launch (HIGH BLAST RADIUS — requires confirm_cost)
        // --------------------------------------------------------------------
        public class LaunchLambdaInstanceParams
        {
            [McpDescription(
                "Lambda instance type name (e.g. 'gpu_1x_a10', 'gpu_1x_a100_sxm4', 'gpu_1x_h100_pcie'). " +
                "Use AI Studio's Lambda settings panel to discover currently-available types.",
                Required = true)]
            public string InstanceType { get; set; }

            [McpDescription(
                "Lambda region name (e.g. 'us-west-1', 'us-east-1', 'us-south-1')",
                Required = true)]
            public string Region { get; set; }

            [McpDescription(
                "SSH key name registered in Lambda Cloud. Defaults to AIStudioSettings.SshKeyName " +
                "if omitted.")]
            public string SshKeyName { get; set; }

            [McpDescription(
                "Optional persistent file system name to attach (e.g. 'ai-studio-models-us-west-1'). " +
                "Required for fast bootstrap once first-time model init has run.")]
            public string FileSystemName { get; set; }

            [McpDescription("Optional friendly name for the instance")]
            public string Name { get; set; }

            [McpDescription(
                "REQUIRED safety flag. Set to true to acknowledge that launching this instance will " +
                "incur Lambda Cloud GPU charges (typically $0.50-$2.50+ per hour, billed per minute). " +
                "If false, the call returns the hourly price as a dry-run preview without launching.",
                Required = true, Default = false)]
            public bool ConfirmCost { get; set; }
        }

        [McpTool(
            "AIStudio_LaunchLambdaInstance",
            "Launch a new Lambda Cloud GPU instance. *** COSTS REAL MONEY *** -- requires " +
            "confirm_cost=true. Without confirmation returns the hourly price as a dry-run preview. " +
            "On success, updates the editor's tracked active instance.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> LaunchLambdaInstance(LaunchLambdaInstanceParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.InstanceType)) throw new ArgumentException("InstanceType is required");
            if (string.IsNullOrWhiteSpace(p.Region)) throw new ArgumentException("Region is required");

            string sshKey = string.IsNullOrWhiteSpace(p.SshKeyName)
                ? AIStudioSettings.SshKeyName
                : p.SshKeyName;
            if (string.IsNullOrWhiteSpace(sshKey))
                throw new InvalidOperationException(
                    "SshKeyName is empty. Provide one or configure AIStudioSettings.SshKeyName via AI Studio/Settings.");

            // Look up the price up-front for the dry-run preview AND for cost tracking on success.
            int priceCents = 0;
            try
            {
                var types = await LambdaClient.ListInstanceTypesAsync();
                priceCents = types.FirstOrDefault(t => t.Name == p.InstanceType)?.PriceCentsPerHour ?? 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIStudio MCP] Could not fetch instance type price: {ex.Message}");
            }

            if (!p.ConfirmCost)
            {
                return new
                {
                    success = false,
                    dry_run = true,
                    message = $"Refusing to launch '{p.InstanceType}' in '{p.Region}': confirm_cost is false. " +
                              $"Price: ${priceCents / 100.0:F2}/hour " +
                              $"(~${priceCents / 100.0 * 24:F2}/day if left running). " +
                              "Re-call with confirm_cost=true to proceed.",
                    instance_type = p.InstanceType,
                    region = p.Region,
                    price_cents_per_hour = priceCents,
                    price_usd_per_hour = priceCents / 100.0
                };
            }

            var launchReq = new LambdaClient.LaunchRequest
            {
                InstanceTypeName = p.InstanceType,
                RegionName = p.Region,
                SshKeyNames = new List<string> { sshKey },
                FileSystemNames = string.IsNullOrWhiteSpace(p.FileSystemName)
                    ? null
                    : new List<string> { p.FileSystemName },
                Name = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name,
            };

            Debug.Log(
                $"[AIStudio MCP] AIStudio_LaunchLambdaInstance launching {p.InstanceType} in {p.Region} " +
                $"(${priceCents / 100.0:F2}/hr, fs={p.FileSystemName ?? "(none)"})");

            var instanceIds = await LambdaClient.LaunchAsync(launchReq);
            string instanceId = instanceIds.FirstOrDefault();
            if (string.IsNullOrEmpty(instanceId))
                throw new Exception("Launch succeeded but no instance ID returned");

            // Mirror the editor's existing state-tracking behaviour so the Lambda window
            // and any uptime/cost UI pick up the instance launched via MCP.
            LambdaInstanceState.ActiveInstanceId = instanceId;
            LambdaInstanceState.LaunchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            LambdaInstanceState.PriceCentsPerHour = priceCents;
            LambdaInstanceState.InstanceTypeName = p.InstanceType;
            LambdaInstanceState.RegionName = p.Region;
            if (!string.IsNullOrWhiteSpace(p.FileSystemName))
                LambdaInstanceState.FileSystemId = p.FileSystemName;

            Debug.Log(
                $"[AIStudio MCP] Launched instance {instanceId} (${priceCents / 100.0:F2}/hr) -- " +
                "remember to terminate when done");

            return new
            {
                success = true,
                instance_id = instanceId,
                instance_type = p.InstanceType,
                region = p.Region,
                price_cents_per_hour = priceCents,
                price_usd_per_hour = priceCents / 100.0,
                next_steps = "Use AIStudio_GetLambdaInstanceStatus to monitor, " +
                             "AIStudio_TerminateLambdaInstance to shut down."
            };
        }

        // --------------------------------------------------------------------
        // Terminate
        // --------------------------------------------------------------------
        public class TerminateLambdaInstanceParams
        {
            [McpDescription(
                "Lambda instance ID to terminate. If omitted, falls back to the AI Studio editor's " +
                "tracked active instance.")]
            public string InstanceId { get; set; }
        }

        [McpTool(
            "AIStudio_TerminateLambdaInstance",
            "Terminate a running Lambda Cloud GPU instance. Logs the final estimated session cost " +
            "to the Unity console. If no instance ID is provided, terminates the editor's tracked " +
            "active instance.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> TerminateLambdaInstance(TerminateLambdaInstanceParams p)
        {
            string instanceId = p?.InstanceId;
            if (string.IsNullOrWhiteSpace(instanceId))
                instanceId = LambdaInstanceState.ActiveInstanceId;
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new ArgumentException(
                    "InstanceId is required (no AI Studio active instance is tracked either)");

            // Snapshot tracked cost BEFORE we clear state so we can log it on success.
            double finalCost = 0;
            int trackedPriceCents = 0;
            string trackedType = null;
            bool wasTracked = LambdaInstanceState.HasActiveInstance &&
                              LambdaInstanceState.ActiveInstanceId == instanceId;
            if (wasTracked)
            {
                finalCost = LambdaInstanceState.EstimatedCostUsd;
                trackedPriceCents = LambdaInstanceState.PriceCentsPerHour;
                trackedType = LambdaInstanceState.InstanceTypeName;
            }

            Debug.Log(
                $"[AIStudio MCP] AIStudio_TerminateLambdaInstance terminating {instanceId} " +
                (wasTracked
                    ? $"(tracked: {trackedType}, ${finalCost:F2} accrued)"
                    : "(not tracked)"));

            var terminated = await LambdaClient.TerminateAsync(new[] { instanceId });

            if (wasTracked)
            {
                Debug.Log(
                    $"[AIStudio MCP] Final session cost for {instanceId}: ~${finalCost:F2} " +
                    $"({trackedType} @ ${trackedPriceCents / 100.0:F2}/hr)");
                LambdaInstanceState.Clear();
            }

            return new
            {
                success = true,
                instance_id = instanceId,
                terminated_count = terminated.Count,
                final_estimated_cost_usd = wasTracked ? (double?)finalCost : null,
                instance_type = trackedType
            };
        }
    }
}
