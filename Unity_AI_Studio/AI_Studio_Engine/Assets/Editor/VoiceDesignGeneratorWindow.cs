using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Editor window for generating voice audio via the QwenTTS ComfyUI pipeline.
/// Sends text + voice settings to the run_comfy_api.py Flask API and imports the resulting audio clip.
/// </summary>
public class VoiceDesignGeneratorWindow : EditorWindow
{
    // --- Configuration ---
    private const string ApiPath = "/generate/audio";
    private string _outputFolder = "Assets/GeneratedAudio";

    // --- Voice Settings ---
    private static readonly string[] Characters = { "Female", "Male" };
    private static readonly string[] Styles = { "Warm", "Bright", "Calm", "Energetic", "Soft", "Deep" };
    private int _characterIndex;
    private int _styleIndex;

    // --- Text Input ---
    private string _textInput = "";

    // --- State ---
    private bool _isGenerating;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;

    [MenuItem("AI Studio/Voice Design Generator")]
    public static void ShowWindow()
    {
        var window = GetWindow<VoiceDesignGeneratorWindow>("Voice Generator");
        window.minSize = new Vector2(420, 380);
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // --- Header ---
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Voice Design Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Generate speech audio using QwenTTS via ComfyUI.\n" +
            "Enter the text you want spoken, choose a voice character and style, then click Generate.",
            MessageType.Info);
        EditorGUILayout.Space(4);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}{ApiPath}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        // --- Voice Configuration ---
        EditorGUILayout.LabelField("Voice Configuration", EditorStyles.boldLabel);
        _characterIndex = EditorGUILayout.Popup("Character", _characterIndex, Characters);
        _styleIndex = EditorGUILayout.Popup("Style", _styleIndex, Styles);
        EditorGUILayout.Space(8);

        // --- Text Input ---
        EditorGUILayout.LabelField("Text to Speak", EditorStyles.boldLabel);
        _textInput = EditorGUILayout.TextArea(_textInput, GUILayout.MinHeight(80));
        int charCount = _textInput?.Length ?? 0;
        EditorGUILayout.LabelField($"{charCount} characters", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.Space(8);

        // --- Generate Button ---
        bool canGenerate = !_isGenerating && !string.IsNullOrWhiteSpace(_textInput);
        EditorGUI.BeginDisabledGroup(!canGenerate);
        var generateStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };
        if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate Voice Audio", generateStyle))
        {
            RunGeneration();
        }
        EditorGUI.EndDisabledGroup();

        // --- Progress / Status ---
        if (_isGenerating)
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(rect, _progress, "Processing...");
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
        }

        EditorGUILayout.EndScrollView();
    }

    // --- Generation ---
    private async void RunGeneration()
    {
        _isGenerating = true;
        _statusMessage = "Sending text to voice pipeline...";
        _statusType = MessageType.Info;
        _progress = 0.1f;
        Repaint();

        try
        {
            string resultPath = await GenerateAsync();

            _statusMessage = $"Audio imported: {resultPath}";
            _statusType = MessageType.Info;

            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(resultPath);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Voice Generator] {ex}");
        }
        finally
        {
            _isGenerating = false;
            _progress = 0f;
            Repaint();
        }
    }

    private async Task<string> GenerateAsync()
    {
        // Ensure output folder exists
        string fullOutputDir = Path.Combine(Application.dataPath,
            _outputFolder.StartsWith("Assets/") ? _outputFolder.Substring(7) : _outputFolder);
        Directory.CreateDirectory(fullOutputDir);

        _progress = 0.2f;
        Repaint();

        // Build JSON payload
        string json = JsonUtility.ToJson(new VoiceRequest
        {
            text = _textInput,
            character = Characters[_characterIndex],
            style = Styles[_styleIndex]
        });

        _progress = 0.3f;
        Repaint();

        string url = AIStudioSettings.BuildUrl(ApiPath);
        Debug.Log($"[Voice Generator] Sending request to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, ApiPath);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        _progress = 0.8f;
        Repaint();

        await AIStudioClient.EnsureSuccessAsync(response, "Voice Design");

        // Determine output filename from Content-Disposition header or fallback
        string outputName = "generated_voice.flac";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
        {
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
        }

        // Save the audio file
        byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
        string savePath = Path.Combine(fullOutputDir, outputName);

        // Avoid overwriting — append number if file exists
        if (File.Exists(savePath))
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(outputName);
            string ext = Path.GetExtension(outputName);
            int counter = 1;
            while (File.Exists(savePath))
            {
                savePath = Path.Combine(fullOutputDir, $"{nameNoExt}_{counter}{ext}");
                counter++;
            }
        }

        File.WriteAllBytes(savePath, audioBytes);
        _progress = 1f;
        Repaint();

        Debug.Log($"[Voice Generator] Saved {audioBytes.Length} bytes to {savePath}");

        // Convert to Assets-relative path for Unity
        string assetsRelative = "Assets" + savePath.Substring(Application.dataPath.Length).Replace('\\', '/');
        return assetsRelative;
    }

    [Serializable]
    private struct VoiceRequest
    {
        public string text;
        public string character;
        public string style;
    }
}
