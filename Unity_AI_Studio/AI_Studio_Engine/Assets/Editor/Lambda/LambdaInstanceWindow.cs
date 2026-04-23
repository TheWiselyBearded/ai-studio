using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using AIStudio.Core;

namespace AIStudio.Lambda
{
    public class LambdaInstanceWindow : EditorWindow
    {
        private const string DefaultBootstrapUrl =
            "https://raw.githubusercontent.com/TheWiselyBearded/ai-studio/main/cloud/bootstrap.sh";
        private const string DefaultBundleUrl =
            "https://github.com/TheWiselyBearded/ai-studio/releases/latest/download/ai-studio-light.zip";
        // Lambda mounts persistent filesystems at /lambda/nfs/<fs-name> (virtiofs).
        // The previous value "/lambda-fs/ai-studio-models" was wrong on both axes —
        // wrong path convention AND hardcoded a name that excluded the region suffix
        // we actually use (ai-studio-models-<region>).
        private const string FileSystemMountRoot = "/lambda/nfs/";

        // --- Remote state (refreshed from Lambda) ---
        private List<LambdaClient.InstanceType> _instanceTypes = new List<LambdaClient.InstanceType>();
        private List<LambdaClient.Instance> _instances = new List<LambdaClient.Instance>();
        private List<LambdaClient.SshKey> _sshKeys = new List<LambdaClient.SshKey>();
        private List<LambdaClient.FileSystem> _fileSystems = new List<LambdaClient.FileSystem>();
        private DateTime _lastRefresh = DateTime.MinValue;
        private string _refreshError;
        private bool _isRefreshing;
        private bool _isLaunching;
        private bool _isTerminating;

        // --- Launch form ---
        private int _typeIndex;
        private int _regionIndex;
        private int _sshKeyIndex;
        private int _fileSystemIndex;
        private string _bundleUrl = DefaultBundleUrl;
        private string _bootstrapUrl = DefaultBootstrapUrl;
        private string _instanceName = "ai-studio";
        private bool _skipTrellis;

        private Vector2 _scroll;

        [MenuItem("AI Studio/Cloud/Instance Manager")]
        public static void ShowWindow()
        {
            var w = GetWindow<LambdaInstanceWindow>("Lambda Instance Manager");
            w.minSize = new Vector2(520, 560);
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            LambdaInstanceState.Changed += Repaint;
            _ = RefreshAllAsync();
            // When the window opens and there's already an active instance,
            // re-pull the tunnel URL — the service may have restarted while
            // the window was closed, giving cloudflared a fresh URL.
            if (LambdaInstanceState.HasActiveInstance
                && !string.IsNullOrEmpty(LambdaInstanceState.PublicIp))
                _ = RefreshTunnelUrlAsync();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            LambdaInstanceState.Changed -= Repaint;
        }

        private void OnEditorUpdate()
        {
            // Repaint every second to tick uptime/cost; cheap and window-scoped.
            if (LambdaInstanceState.HasActiveInstance)
                Repaint();
            // Auto-refresh remote status every 30s while the window is open, and
            // pause entirely during a launch — that flow runs its own pollers.
            if (!_isLaunching && !_isRefreshing
                && (DateTime.UtcNow - _lastRefresh).TotalSeconds > 30)
                _ = RefreshInstancesAsync();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Lambda Cloud Instance Manager", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawApiKeyCheck();
            DrawActiveInstance();
            DrawLaunchForm();
            DrawAllInstancesTable();

            if (!string.IsNullOrEmpty(_refreshError))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_refreshError, MessageType.Error);
            }

            EditorGUILayout.EndScrollView();
        }

        // -----------------------------------------------------------------
        private void DrawApiKeyCheck()
        {
            if (string.IsNullOrWhiteSpace(AIStudioSettings.LambdaApiKey))
            {
                EditorGUILayout.HelpBox(
                    "Lambda API key is not set. Open AI Studio/Settings and paste your key.",
                    MessageType.Warning);
                if (GUILayout.Button("Open Settings"))
                    EditorApplication.ExecuteMenuItem("AI Studio/Settings");
                EditorGUILayout.Space(6);
            }
        }

        // -----------------------------------------------------------------
        private void DrawActiveInstance()
        {
            if (!LambdaInstanceState.HasActiveInstance) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var uptime = LambdaInstanceState.Uptime;
            var cost = LambdaInstanceState.EstimatedCostUsd;
            var dollarPerHour = LambdaInstanceState.PriceCentsPerHour / 100.0;

            EditorGUILayout.LabelField("Active Instance", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ID", LambdaInstanceState.ActiveInstanceId);
            EditorGUILayout.LabelField("Type", $"{LambdaInstanceState.InstanceTypeName} · ${dollarPerHour:F2}/hr");
            EditorGUILayout.LabelField("Region", LambdaInstanceState.RegionName);
            EditorGUILayout.LabelField("Public IP", string.IsNullOrEmpty(LambdaInstanceState.PublicIp) ? "(waiting)" : LambdaInstanceState.PublicIp);
            EditorGUILayout.LabelField("Uptime", uptime.ToString(@"hh\:mm\:ss"));
            EditorGUILayout.LabelField("Estimated cost", $"${cost:F2}");
            EditorGUILayout.LabelField("Tunnel URL", string.IsNullOrEmpty(LambdaInstanceState.TunnelUrl) ? "(waiting for bootstrap)" : LambdaInstanceState.TunnelUrl);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(LambdaInstanceState.TunnelUrl)))
            {
                if (GUILayout.Button("Use as Editor Endpoint"))
                {
                    AIStudioSettings.RemoteBaseUrl = LambdaInstanceState.TunnelUrl;
                    AIStudioSettings.ActiveMode = EndpointMode.Remote;
                    ShowNotification(new GUIContent("Endpoint set to cloud instance"));
                }
                if (GUILayout.Button("Copy Tunnel URL"))
                {
                    EditorGUIUtility.systemCopyBuffer = LambdaInstanceState.TunnelUrl ?? string.Empty;
                }
            }
            if (GUILayout.Button("Refresh Tunnel URL"))
            {
                _ = RefreshTunnelUrlAsync();
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(_isTerminating))
            {
                if (GUILayout.Button(_isTerminating ? "Terminating..." : "Terminate Instance", GUILayout.Height(28)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Terminate Lambda instance?",
                            $"Terminate {LambdaInstanceState.ActiveInstanceId} ({LambdaInstanceState.InstanceTypeName})?\n\n" +
                            $"Uptime {uptime:hh\\:mm\\:ss}, estimated cost ${cost:F2}.",
                            "Terminate", "Cancel"))
                    {
                        _ = TerminateActiveInstanceAsync();
                    }
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        // -----------------------------------------------------------------
        private void DrawLaunchForm()
        {
            if (LambdaInstanceState.HasActiveInstance) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Launch a New Instance", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(_isRefreshing ? "Refreshing..." : "Refresh from Lambda"))
                _ = RefreshAllAsync();
            EditorGUILayout.LabelField($"Last refresh: {(_lastRefresh == DateTime.MinValue ? "never" : _lastRefresh.ToLocalTime().ToString("HH:mm:ss"))}",
                EditorStyles.miniLabel, GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();

            if (_instanceTypes.Count == 0)
            {
                EditorGUILayout.HelpBox("No instance types loaded yet. Click Refresh.", MessageType.Info);
                return;
            }

            // Instance type dropdown
            var typeLabels = _instanceTypes
                .Where(t => t.AvailableRegions != null && t.AvailableRegions.Count > 0)
                .Select(t => $"{t.Name} · ${(t.PriceCentsPerHour / 100.0):F2}/hr · {t.Specs?.Gpus ?? 0} GPU · {t.GpuDescription ?? "-"}")
                .ToArray();
            var availableTypes = _instanceTypes
                .Where(t => t.AvailableRegions != null && t.AvailableRegions.Count > 0)
                .ToList();

            if (availableTypes.Count == 0)
            {
                EditorGUILayout.HelpBox("No instance types currently have regional capacity. Try again in a few minutes.", MessageType.Warning);
                return;
            }

            _typeIndex = Mathf.Clamp(_typeIndex, 0, availableTypes.Count - 1);
            _typeIndex = EditorGUILayout.Popup("Instance Type", _typeIndex, typeLabels);
            var selectedType = availableTypes[_typeIndex];

            // Region dropdown (scoped to this type's available regions)
            var regionLabels = selectedType.AvailableRegions.Select(r => $"{r.Name} ({r.Description})").ToArray();
            _regionIndex = Mathf.Clamp(_regionIndex, 0, regionLabels.Length - 1);
            _regionIndex = EditorGUILayout.Popup("Region", _regionIndex, regionLabels);
            var selectedRegion = selectedType.AvailableRegions[_regionIndex];

            // SSH key dropdown
            if (_sshKeys.Count == 0)
            {
                EditorGUILayout.HelpBox("No SSH keys registered on this Lambda account. Add one via the Lambda console first.", MessageType.Warning);
                return;
            }
            var sshLabels = _sshKeys.Select(k => k.Name).ToArray();
            _sshKeyIndex = Mathf.Clamp(_sshKeyIndex, 0, sshLabels.Length - 1);
            _sshKeyIndex = EditorGUILayout.Popup("SSH Key", _sshKeyIndex, sshLabels);

            // File system dropdown (optional, region-filtered)
            var regionFs = _fileSystems.Where(fs => fs.Region?.Name == selectedRegion.Name).ToList();
            var fsLabels = new[] { "(none — models will download on-instance)" }
                .Concat(regionFs.Select(fs => fs.Name)).ToArray();
            _fileSystemIndex = Mathf.Clamp(_fileSystemIndex, 0, fsLabels.Length - 1);
            _fileSystemIndex = EditorGUILayout.Popup("Persistent FS", _fileSystemIndex, fsLabels);

            _instanceName = EditorGUILayout.TextField("Name Tag", _instanceName);
            _bundleUrl = EditorGUILayout.TextField("Bundle URL", _bundleUrl);
            _bootstrapUrl = EditorGUILayout.TextField("Bootstrap URL", _bootstrapUrl);
            _skipTrellis = EditorGUILayout.Toggle("Skip comfy_trellis", _skipTrellis);

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_isLaunching))
            {
                if (GUILayout.Button(_isLaunching ? "Launching..." : "Launch", GUILayout.Height(32)))
                {
                    _ = LaunchAsync(selectedType, selectedRegion, _sshKeys[_sshKeyIndex], _fileSystemIndex > 0 ? regionFs[_fileSystemIndex - 1] : null);
                }
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Launch fills out the bootstrap script with a freshly generated auth token, posts to Lambda, waits for the instance to go active, SSHes in to read the Cloudflare tunnel URL, and then points AI Studio at the cloud endpoint.\n\n" +
                "First-time bootstrap is ~25–35 minutes. Don't close the editor during launch.",
                MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Models Filesystem (one-time setup)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Lambda persistent filesystems keep model weights across instance spin-ups. " +
                "Run the initializer once per region (~30 min, ~$5 in GPU time); afterward every launch in this region can mount it and skip model downloads.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(_isLaunching || _isTerminating))
            {
                if (GUILayout.Button("Initialize Models Filesystem in " + selectedRegion.Name))
                {
                    var defaultName = $"ai-studio-models-{selectedRegion.Name}";
                    if (EditorUtility.DisplayDialog(
                            "Initialize persistent filesystem",
                            $"This will:\n  • Create filesystem '{defaultName}' in {selectedRegion.Name}\n  • Launch a gpu_1x_a10 to populate it from Hugging Face\n  • Self-terminate when finished (~30 min)\n\nYou will be billed for GPU time during the download.",
                            "Start", "Cancel"))
                    {
                        _ = InitializeFileSystemAsync(defaultName, selectedRegion, _sshKeys[_sshKeyIndex]);
                    }
                }
            }
        }

        // -----------------------------------------------------------------
        private void DrawAllInstancesTable()
        {
            if (_instances.Count == 0) return;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("All Lambda Instances (from API)", EditorStyles.boldLabel);
            foreach (var inst in _instances)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    $"{inst.Id?.Substring(0, Math.Min(8, inst.Id?.Length ?? 0))}… " +
                    $"· {inst.InstanceType?.Name ?? "?"} " +
                    $"· {inst.Status} " +
                    $"· {inst.Ip ?? "-"} " +
                    $"· {inst.Region?.Name ?? "-"}");
                if (GUILayout.Button("Track", GUILayout.Width(60)))
                {
                    AdoptInstance(inst);
                }
                if (GUILayout.Button("Terminate", GUILayout.Width(80)))
                {
                    if (EditorUtility.DisplayDialog("Terminate?", $"Terminate {inst.Id}?", "Terminate", "Cancel"))
                        _ = TerminateByIdAsync(inst.Id);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        // -----------------------------------------------------------------
        private void AdoptInstance(LambdaClient.Instance inst)
        {
            LambdaInstanceState.ActiveInstanceId = inst.Id;
            LambdaInstanceState.InstanceTypeName = inst.InstanceType?.Name;
            LambdaInstanceState.PriceCentsPerHour = inst.InstanceType?.PriceCentsPerHour ?? 0;
            LambdaInstanceState.RegionName = inst.Region?.Name;
            LambdaInstanceState.PublicIp = inst.Ip;
            // We don't know the launched-at time for already-running instances; zero it.
            if (LambdaInstanceState.LaunchedAtUnix == 0)
                LambdaInstanceState.LaunchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ShowNotification(new GUIContent($"Tracking {inst.Id}"));
        }

        // -----------------------------------------------------------------
        private async Task RefreshAllAsync()
        {
            _isRefreshing = true;
            _refreshError = null;
            Repaint();
            try
            {
                var typesTask = LambdaClient.ListInstanceTypesAsync();
                var instancesTask = LambdaClient.ListInstancesAsync();
                var sshTask = LambdaClient.ListSshKeysAsync();
                var fsTask = LambdaClient.ListFileSystemsAsync();
                await Task.WhenAll(typesTask, instancesTask, sshTask, fsTask);
                _instanceTypes = typesTask.Result;
                _instances = instancesTask.Result;
                _sshKeys = sshTask.Result;
                _fileSystems = fsTask.Result;
                _lastRefresh = DateTime.UtcNow;

                // Preselect the SSH key that matches what's in Settings.
                var preferred = AIStudioSettings.SshKeyName;
                if (!string.IsNullOrEmpty(preferred))
                {
                    var idx = _sshKeys.FindIndex(k => k.Name == preferred);
                    if (idx >= 0) _sshKeyIndex = idx;
                }
            }
            catch (Exception ex)
            {
                _refreshError = $"Refresh failed: {ex.Message}";
            }
            finally
            {
                _isRefreshing = false;
                Repaint();
            }
        }

        /// SSH-poll the instance for its current /var/ai-studio/tunnel.url.
        /// Updates both LambdaInstanceState.TunnelUrl and, if the endpoint
        /// is active in Remote mode, AIStudioSettings.RemoteBaseUrl — which
        /// in turn writes to Assets/StreamingAssets/AIStudio/endpoint.json
        /// so every generator window picks it up on next OnGUI tick.
        private async Task RefreshTunnelUrlAsync()
        {
            if (string.IsNullOrEmpty(LambdaInstanceState.PublicIp))
            {
                ShowNotification(new GUIContent("No public IP yet"));
                return;
            }
            var res = await LambdaSshReadback.FetchCurrentTunnelAsync(
                LambdaInstanceState.PublicIp,
                AIStudioSettings.SshPrivateKeyPath);

            if (!res.Success)
            {
                _refreshError = $"Tunnel refresh failed: {res.Error}";
                Repaint();
                return;
            }

            if (res.TunnelUrl == LambdaInstanceState.TunnelUrl)
            {
                ShowNotification(new GUIContent("Tunnel URL unchanged"));
                return;
            }

            LambdaInstanceState.TunnelUrl = res.TunnelUrl;
            if (AIStudioSettings.ActiveMode == EndpointMode.Remote
                || string.IsNullOrEmpty(AIStudioSettings.RemoteBaseUrl))
            {
                AIStudioSettings.RemoteBaseUrl = res.TunnelUrl;
                AIStudioSettings.ActiveMode = EndpointMode.Remote;
            }
            ShowNotification(new GUIContent("Tunnel URL updated"));
            Repaint();
        }

        private async Task RefreshInstancesAsync()
        {
            try
            {
                _instances = await LambdaClient.ListInstancesAsync();
                _lastRefresh = DateTime.UtcNow;
                _refreshError = null;

                // Reflect any IP/status changes for the active instance.
                if (LambdaInstanceState.HasActiveInstance)
                {
                    var mine = _instances.FirstOrDefault(i => i.Id == LambdaInstanceState.ActiveInstanceId);
                    if (mine != null && !string.IsNullOrEmpty(mine.Ip))
                        LambdaInstanceState.PublicIp = mine.Ip;
                }
                Repaint();
            }
            catch (LambdaClient.LambdaApiException ex) when (LambdaClient.IsTransient(ex))
            {
                // Back off silently — another tick will retry. Don't surface a red
                // banner for rate limits (Cloudflare error 1015 / 429 / 503).
                _lastRefresh = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _refreshError = $"Status poll failed: {ex.Message}";
            }
        }

        // -----------------------------------------------------------------
        private async Task LaunchAsync(
            LambdaClient.InstanceType type,
            LambdaClient.RegionAvailability region,
            LambdaClient.SshKey sshKey,
            LambdaClient.FileSystem fileSystem)
        {
            _isLaunching = true;
            _refreshError = null;
            Repaint();

            try
            {
                // 1. Generate a fresh auth token.
                var token = GenerateToken();

                // 2. Render user_data.
                var userData = RenderUserData(token, fileSystem?.Name);

                // 3. Persist settings so Unity survives restarts.
                AIStudioSettings.SshKeyName = sshKey.Name;
                AIStudioSettings.AuthToken = token;

                // 4. Post to Lambda.
                var launchReq = new LambdaClient.LaunchRequest
                {
                    RegionName = region.Name,
                    InstanceTypeName = type.Name,
                    SshKeyNames = new List<string> { sshKey.Name },
                    FileSystemNames = fileSystem != null ? new List<string> { fileSystem.Name } : null,
                    Name = _instanceName,
                    UserData = userData,
                    Quantity = 1,
                };
                var ids = await LambdaClient.LaunchAsync(launchReq);
                if (ids.Count == 0) throw new Exception("Lambda returned no instance IDs.");

                LambdaInstanceState.ActiveInstanceId = ids[0];
                LambdaInstanceState.InstanceTypeName = type.Name;
                LambdaInstanceState.PriceCentsPerHour = type.PriceCentsPerHour;
                LambdaInstanceState.RegionName = region.Name;
                LambdaInstanceState.FileSystemId = fileSystem?.Id;
                LambdaInstanceState.LaunchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                LambdaInstanceState.TunnelUrl = string.Empty;
                LambdaInstanceState.PublicIp = string.Empty;
                Repaint();

                // 5. Poll for active + IP.
                var active = await WaitForActiveAsync(ids[0], TimeSpan.FromMinutes(10));
                LambdaInstanceState.PublicIp = active.Ip;
                Repaint();

                // 6. SSH readback once bootstrap is ready.
                var readback = await LambdaSshReadback.WaitForTunnelAsync(
                    active.Ip,
                    AIStudioSettings.SshPrivateKeyPath,
                    TimeSpan.FromMinutes(45));
                if (!readback.Success)
                    throw new Exception("Tunnel readback failed: " + readback.Error);

                LambdaInstanceState.TunnelUrl = readback.TunnelUrl;
                AIStudioSettings.RemoteBaseUrl = readback.TunnelUrl;
                AIStudioSettings.ActiveMode = EndpointMode.Remote;
                ShowNotification(new GUIContent("Cloud endpoint ready"));
            }
            catch (Exception ex)
            {
                _refreshError = $"Launch failed: {ex.Message}";
                Debug.LogError($"[Lambda] Launch failed: {ex}");
            }
            finally
            {
                _isLaunching = false;
                Repaint();
            }
        }

        private async Task<LambdaClient.Instance> WaitForActiveAsync(string id, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var pollDelay = TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollDelay);
                try
                {
                    var inst = await LambdaClient.GetInstanceAsync(id);
                    if (inst == null) continue;
                    if (inst.Status == "active" && !string.IsNullOrEmpty(inst.Ip))
                        return inst;
                    if (inst.Status == "terminated" || inst.Status == "terminating")
                        throw new Exception($"Instance went to {inst.Status} before becoming active.");
                    // Reset delay after a good response.
                    pollDelay = TimeSpan.FromSeconds(20);
                }
                catch (LambdaClient.LambdaApiException ex) when (LambdaClient.IsTransient(ex))
                {
                    // Exponential backoff capped at 2 minutes when Cloudflare/edge rate-limits.
                    pollDelay = TimeSpan.FromSeconds(Math.Min(120, pollDelay.TotalSeconds * 2));
                    Debug.Log($"[Lambda] Transient poll error ({ex.Code}); backing off to {pollDelay.TotalSeconds:F0}s");
                }
            }
            throw new TimeoutException("Instance did not become active within " + timeout);
        }

        // -----------------------------------------------------------------
        private async Task TerminateActiveInstanceAsync()
        {
            if (!LambdaInstanceState.HasActiveInstance) return;
            _isTerminating = true;
            Repaint();
            try
            {
                // Snapshot voices first so cloned .pt files survive termination.
                try { await LambdaVoiceSnapshot.SnapshotAllAsync(); }
                catch (Exception ex) { Debug.LogWarning($"[Lambda] Voice snapshot skipped: {ex.Message}"); }

                await LambdaClient.TerminateAsync(new[] { LambdaInstanceState.ActiveInstanceId });
                LambdaInstanceState.Clear();
                // Flip editor back to local mode so stale tunnel URLs don't linger.
                if (AIStudioSettings.ActiveMode == EndpointMode.Remote)
                {
                    AIStudioSettings.RemoteBaseUrl = string.Empty;
                    AIStudioSettings.ActiveMode = EndpointMode.Local;
                }
            }
            catch (Exception ex)
            {
                _refreshError = $"Terminate failed: {ex.Message}";
            }
            finally
            {
                _isTerminating = false;
                await RefreshInstancesAsync();
                Repaint();
            }
        }

        private async Task TerminateByIdAsync(string id)
        {
            try
            {
                await LambdaClient.TerminateAsync(new[] { id });
                if (LambdaInstanceState.ActiveInstanceId == id)
                    LambdaInstanceState.Clear();
            }
            catch (Exception ex)
            {
                _refreshError = $"Terminate failed: {ex.Message}";
            }
            await RefreshInstancesAsync();
        }

        // -----------------------------------------------------------------
        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private string RenderUserData(string token, string fileSystemName)
        {
            // The shell template substitution is done in C# because we can't rely
            // on the user having a POSIX `envsubst` at hand.
            var template = UserDataTemplate;
            var gemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
            var runway = Environment.GetEnvironmentVariable("RUNWAYML_API_SECRET") ?? string.Empty;
            var hfToken = AIStudioSettings.HuggingFaceToken;
            var fsMount = string.IsNullOrEmpty(fileSystemName)
                ? "/opt/ai-studio-models"
                : FileSystemMountRoot + fileSystemName;
            return template
                .Replace("{{AI_STUDIO_TOKEN}}", token)
                .Replace("{{FS_MOUNT}}", fsMount)
                .Replace("{{AI_STUDIO_BUNDLE_URL}}", _bundleUrl ?? string.Empty)
                .Replace("{{BOOTSTRAP_URL}}", _bootstrapUrl ?? string.Empty)
                .Replace("{{GEMINI_API_KEY}}", gemini)
                .Replace("{{RUNWAYML_API_SECRET}}", runway)
                .Replace("{{HF_TOKEN}}", hfToken)
                .Replace("{{SKIP_TRELLIS}}", _skipTrellis ? "1" : "0");
        }

        // Embedded verbatim copy of cloud/user_data_template.sh. Kept here so the
        // editor doesn't have to locate the file on disk at runtime.
        private const string UserDataTemplate = @"#!/usr/bin/env bash
set -u
exec > /var/log/ai-studio-user-data.log 2>&1

export AI_STUDIO_TOKEN=""{{AI_STUDIO_TOKEN}}""
export FS_MOUNT=""{{FS_MOUNT}}""
export AI_STUDIO_BUNDLE_URL=""{{AI_STUDIO_BUNDLE_URL}}""
export GEMINI_API_KEY=""{{GEMINI_API_KEY}}""
export RUNWAYML_API_SECRET=""{{RUNWAYML_API_SECRET}}""
export HF_TOKEN=""{{HF_TOKEN}}""
export SKIP_TRELLIS=""{{SKIP_TRELLIS}}""

curl -fsSL ""{{BOOTSTRAP_URL}}"" -o /tmp/bootstrap.sh
chmod +x /tmp/bootstrap.sh
bash /tmp/bootstrap.sh
";

        // -----------------------------------------------------------------
        // Models filesystem initializer
        // -----------------------------------------------------------------

        private const string FsInitScriptUrl =
            "https://raw.githubusercontent.com/TheWiselyBearded/ai-studio/main/cloud/fs_init.sh";

        private const string FsInitUserDataTemplate = @"#!/usr/bin/env bash
set -u
exec > /var/log/ai-studio-fs-init-user-data.log 2>&1

export FS_MOUNT=""{{FS_MOUNT}}""
export LAMBDA_API_KEY=""{{LAMBDA_API_KEY}}""
export LAMBDA_INSTANCE_ID=""$(curl -fsS http://169.254.169.254/latest/meta-data/instance-id 2>/dev/null || echo unknown)""
export HF_TOKEN=""{{HF_TOKEN}}""

curl -fsSL ""{{FS_INIT_URL}}"" -o /tmp/fs_init.sh
chmod +x /tmp/fs_init.sh
bash /tmp/fs_init.sh
";

        private async Task InitializeFileSystemAsync(
            string fsName,
            LambdaClient.RegionAvailability region,
            LambdaClient.SshKey sshKey)
        {
            _isLaunching = true;
            _refreshError = null;
            Repaint();
            try
            {
                // 1. Ensure the filesystem exists.
                var existing = _fileSystems.FirstOrDefault(f => f.Name == fsName && f.Region?.Name == region.Name);
                LambdaClient.FileSystem fs = existing;
                if (fs == null)
                {
                    fs = await LambdaClient.CreateFileSystemAsync(fsName, region.Name);
                    _fileSystems.Add(fs);
                }

                // 2. Pick the cheapest gpu-bearing instance type available in the region.
                var initType = _instanceTypes
                    .Where(t => t.AvailableRegions != null && t.AvailableRegions.Any(r => r.Name == region.Name))
                    .FirstOrDefault();
                if (initType == null)
                    throw new Exception($"No instance type has capacity in {region.Name}.");

                var userData = FsInitUserDataTemplate
                    .Replace("{{FS_MOUNT}}", "/lambda-fs/" + fsName)
                    .Replace("{{FS_INIT_URL}}", FsInitScriptUrl)
                    .Replace("{{LAMBDA_API_KEY}}", AIStudioSettings.LambdaApiKey)
                    .Replace("{{HF_TOKEN}}", AIStudioSettings.HuggingFaceToken);

                var req = new LambdaClient.LaunchRequest
                {
                    RegionName = region.Name,
                    InstanceTypeName = initType.Name,
                    SshKeyNames = new List<string> { sshKey.Name },
                    FileSystemNames = new List<string> { fsName },
                    Name = "ai-studio-fs-init",
                    UserData = userData,
                    Quantity = 1,
                };
                var ids = await LambdaClient.LaunchAsync(req);
                if (ids.Count == 0) throw new Exception("Lambda returned no instance IDs for init job.");

                _refreshError = null;
                EditorUtility.DisplayDialog(
                    "Filesystem init running",
                    $"Launched {ids[0]} ({initType.Name}) to populate filesystem '{fsName}'.\n\n" +
                    "The instance will self-terminate when done. Track progress in the Lambda console; " +
                    "once it's gone, launch a real instance here with this filesystem attached.",
                    "OK");

                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _refreshError = $"FS init failed: {ex.Message}";
                Debug.LogError($"[Lambda] FS init failed: {ex}");
            }
            finally
            {
                _isLaunching = false;
                Repaint();
            }
        }
    }
}
