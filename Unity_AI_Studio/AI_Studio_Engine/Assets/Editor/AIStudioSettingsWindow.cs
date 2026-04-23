using UnityEditor;
using UnityEngine;
using AIStudio.Core;

public class AIStudioSettingsWindow : EditorWindow
{
    private Vector2 _scroll;
    private bool _showApiKey;
    private bool _showAuthToken;
    private bool _showHfToken;

    [MenuItem("AI Studio/Settings")]
    public static void ShowWindow()
    {
        var w = GetWindow<AIStudioSettingsWindow>("AI Studio Settings");
        w.minSize = new Vector2(460, 420);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("AI Studio Settings", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Central config for all AI Studio generator windows. Secrets live in per-user EditorPrefs, never in the repo.",
            MessageType.Info);
        EditorGUILayout.Space(6);

        // --- Endpoint mode ---
        EditorGUILayout.LabelField("Endpoint", EditorStyles.boldLabel);
        var mode = (EndpointMode)EditorGUILayout.EnumPopup("Active Mode", AIStudioSettings.ActiveMode);
        if (mode != AIStudioSettings.ActiveMode) AIStudioSettings.ActiveMode = mode;

        var local = EditorGUILayout.TextField("Local Base URL", AIStudioSettings.LocalBaseUrl);
        if (local != AIStudioSettings.LocalBaseUrl) AIStudioSettings.LocalBaseUrl = local;

        using (new EditorGUI.DisabledScope(mode != EndpointMode.Remote))
        {
            var remote = EditorGUILayout.TextField("Remote Base URL", AIStudioSettings.RemoteBaseUrl);
            if (remote != AIStudioSettings.RemoteBaseUrl) AIStudioSettings.RemoteBaseUrl = remote;
        }
        EditorGUILayout.LabelField("Active URL", AIStudioSettings.ActiveBaseUrl);
        EditorGUILayout.Space(8);

        // --- Auth token ---
        EditorGUILayout.LabelField("Authentication", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var tokenLabel = _showAuthToken ? "Auth Token" : "Auth Token (hidden)";
        var token = _showAuthToken
            ? EditorGUILayout.TextField(tokenLabel, AIStudioSettings.AuthToken)
            : EditorGUILayout.PasswordField(tokenLabel, AIStudioSettings.AuthToken);
        if (token != AIStudioSettings.AuthToken) AIStudioSettings.AuthToken = token;
        if (GUILayout.Button(_showAuthToken ? "Hide" : "Show", GUILayout.Width(60)))
            _showAuthToken = !_showAuthToken;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(
            "Sent as X-AI-Studio-Token header. Must match the --auth-token the Flask server was started with.",
            EditorStyles.miniLabel);
        EditorGUILayout.Space(8);

        // --- Lambda Cloud ---
        EditorGUILayout.LabelField("Lambda Cloud", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var apiKeyLabel = _showApiKey ? "API Key" : "API Key (hidden)";
        var key = _showApiKey
            ? EditorGUILayout.TextField(apiKeyLabel, AIStudioSettings.LambdaApiKey)
            : EditorGUILayout.PasswordField(apiKeyLabel, AIStudioSettings.LambdaApiKey);
        if (key != AIStudioSettings.LambdaApiKey) AIStudioSettings.LambdaApiKey = key;
        if (GUILayout.Button(_showApiKey ? "Hide" : "Show", GUILayout.Width(60)))
            _showApiKey = !_showApiKey;
        EditorGUILayout.EndHorizontal();

        var sshKey = EditorGUILayout.TextField("SSH Key Name", AIStudioSettings.SshKeyName);
        if (sshKey != AIStudioSettings.SshKeyName) AIStudioSettings.SshKeyName = sshKey;

        EditorGUILayout.BeginHorizontal();
        var sshPath = EditorGUILayout.TextField("SSH Private Key Path", AIStudioSettings.SshPrivateKeyPath);
        if (sshPath != AIStudioSettings.SshPrivateKeyPath) AIStudioSettings.SshPrivateKeyPath = sshPath;
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            var picked = EditorUtility.OpenFilePanel("Select Lambda SSH private key", "", "");
            if (!string.IsNullOrEmpty(picked)) AIStudioSettings.SshPrivateKeyPath = picked;
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(
            "The SSH Key Name must match an existing key registered in the Lambda console. The private key file is used locally to read back the tunnel URL from a launched instance.",
            EditorStyles.miniLabel);
        EditorGUILayout.Space(10);

        // --- Hugging Face ---
        EditorGUILayout.LabelField("Hugging Face", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var hfLabel = _showHfToken ? "HF Token" : "HF Token (hidden)";
        var hf = _showHfToken
            ? EditorGUILayout.TextField(hfLabel, AIStudioSettings.HuggingFaceToken)
            : EditorGUILayout.PasswordField(hfLabel, AIStudioSettings.HuggingFaceToken);
        if (hf != AIStudioSettings.HuggingFaceToken) AIStudioSettings.HuggingFaceToken = hf;
        if (GUILayout.Button(_showHfToken ? "Hide" : "Show", GUILayout.Width(60)))
            _showHfToken = !_showHfToken;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(
            "Threaded into user_data so gated models (DINOv3 for Trellis2, BiRefNet) auto-fetch on the instance. Get one at huggingface.co/settings/tokens with 'Read' scope.",
            EditorStyles.miniLabel);
        EditorGUILayout.Space(10);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Session Guardrails", EditorStyles.boldLabel);
        var maxHours = EditorGUILayout.FloatField("Max Session Hours", AIStudioSettings.MaxSessionHours);
        if (Mathf.Abs(maxHours - AIStudioSettings.MaxSessionHours) > 0.001f)
            AIStudioSettings.MaxSessionHours = Mathf.Max(0f, maxHours);
        EditorGUILayout.LabelField(
            "When uptime exceeds this, Unity pops a terminate dialog. 0 disables the check.",
            EditorStyles.miniLabel);

        var maxCost = EditorGUILayout.FloatField("Max Session Cost (USD, optional)", AIStudioSettings.MaxSessionCostUsd);
        if (Mathf.Abs(maxCost - AIStudioSettings.MaxSessionCostUsd) > 0.001f)
            AIStudioSettings.MaxSessionCostUsd = Mathf.Max(0f, maxCost);
        EditorGUILayout.LabelField(
            "Secondary dollar cap — trips whichever limit is reached first. 0 disables it.",
            EditorStyles.miniLabel);
        EditorGUILayout.Space(10);

        if (GUILayout.Button("Reset All AI Studio Settings", GUILayout.Height(24)))
        {
            if (EditorUtility.DisplayDialog(
                    "Reset AI Studio Settings",
                    "Clear endpoint URLs, auth token, Lambda API key, and SSH settings from EditorPrefs?",
                    "Reset", "Cancel"))
            {
                AIStudioSettings.LocalBaseUrl = AIStudioSettings.DefaultLocalBaseUrl;
                AIStudioSettings.RemoteBaseUrl = string.Empty;
                AIStudioSettings.ActiveMode = EndpointMode.Local;
                AIStudioSettings.AuthToken = string.Empty;
                AIStudioSettings.LambdaApiKey = string.Empty;
                AIStudioSettings.SshKeyName = string.Empty;
                AIStudioSettings.SshPrivateKeyPath = string.Empty;
            }
        }

        EditorGUILayout.EndScrollView();
    }
}
