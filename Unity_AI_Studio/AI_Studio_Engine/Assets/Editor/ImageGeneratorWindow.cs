using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Editor window for generating images via the Z-Image Turbo ComfyUI pipeline.
/// Sends a text prompt + settings to the run_comfy_server.py Flask API and imports the resulting image.
/// </summary>
public class ImageGeneratorWindow : EditorWindow
{
    // --- Configuration ---
    private const string ApiPath = "/generate/image";
    private string _outputFolder = "Assets/GeneratedImages";

    // --- Image Settings ---
    private static readonly int[] SizePresets = { 512, 768, 1024, 1280, 1536 };
    private static readonly string[] SizeLabels = { "512", "768", "1024", "1280", "1536" };
    private int _widthIndex = 2;   // default 1024
    private int _heightIndex = 2;  // default 1024
    private int _steps = 8;
    private bool _useCustomSeed;
    private int _seed;

    // --- Prompt ---
    private string _prompt = "";

    // --- State ---
    private bool _isGenerating;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;

    // --- Preview ---
    private Texture2D _resultPreview;
    private string _lastResultPath;

    [MenuItem("AI Studio/Image Generator")]
    public static void ShowWindow()
    {
        var window = GetWindow<ImageGeneratorWindow>("Image Generator");
        window.minSize = new Vector2(420, 440);
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // --- Header ---
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Z-Image Turbo Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Generate images from text using Z-Image Turbo via ComfyUI.\n" +
            "Enter a descriptive prompt, adjust settings, then click Generate.",
            MessageType.Info);
        EditorGUILayout.Space(4);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}{ApiPath}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        // --- Image Configuration ---
        EditorGUILayout.LabelField("Image Configuration", EditorStyles.boldLabel);
        _widthIndex = EditorGUILayout.Popup("Width", _widthIndex, SizeLabels);
        _heightIndex = EditorGUILayout.Popup("Height", _heightIndex, SizeLabels);
        _steps = EditorGUILayout.IntSlider("Steps", _steps, 1, 20);

        _useCustomSeed = EditorGUILayout.Toggle("Custom Seed", _useCustomSeed);
        if (_useCustomSeed)
        {
            _seed = EditorGUILayout.IntField("Seed", _seed);
        }
        EditorGUILayout.Space(8);

        // --- Prompt Input ---
        EditorGUILayout.LabelField("Prompt", EditorStyles.boldLabel);
        _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.MinHeight(80));
        int charCount = _prompt?.Length ?? 0;
        EditorGUILayout.LabelField($"{charCount} characters", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.Space(8);

        // --- Generate Button ---
        bool canGenerate = !_isGenerating && !string.IsNullOrWhiteSpace(_prompt);
        EditorGUI.BeginDisabledGroup(!canGenerate);
        var generateStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };
        if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate Image", generateStyle))
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

        // --- Result Preview ---
        if (_resultPreview != null)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            var previewRect = GUILayoutUtility.GetRect(200, 200, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(previewRect, _resultPreview, ScaleMode.ScaleToFit);
        }

        EditorGUILayout.EndScrollView();
    }

    // --- Generation ---
    private async void RunGeneration()
    {
        _isGenerating = true;
        _statusMessage = "Sending prompt to image pipeline...";
        _statusType = MessageType.Info;
        _progress = 0.1f;
        ClearPreview();
        Repaint();

        try
        {
            string resultPath = await GenerateAsync();

            _statusMessage = $"Image imported: {resultPath}";
            _statusType = MessageType.Info;

            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(resultPath);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }

            // Show preview
            string fullPath = Path.Combine(Application.dataPath, "..", resultPath);
            if (File.Exists(fullPath))
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                _resultPreview = tex;
                _lastResultPath = resultPath;
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Image Generator] {ex}");
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
        var payload = new ImageRequest
        {
            prompt = _prompt,
            width = SizePresets[_widthIndex],
            height = SizePresets[_heightIndex],
            steps = _steps,
            seed = _useCustomSeed ? _seed : -1
        };
        string json = JsonUtility.ToJson(payload);

        _progress = 0.3f;
        Repaint();

        string url = AIStudioSettings.BuildUrl(ApiPath);
        Debug.Log($"[Image Generator] Sending request to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, ApiPath);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(5));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        _progress = 0.8f;
        Repaint();

        await AIStudioClient.EnsureSuccessAsync(response, "Image Generator");

        // Determine output filename from Content-Disposition header or fallback
        string outputName = "generated_image.png";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
        {
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
        }

        // Save the image file
        byte[] imageBytes = await response.Content.ReadAsByteArrayAsync();
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

        File.WriteAllBytes(savePath, imageBytes);
        _progress = 1f;
        Repaint();

        Debug.Log($"[Image Generator] Saved {imageBytes.Length} bytes to {savePath}");

        // Convert to Assets-relative path for Unity
        string assetsRelative = "Assets" + savePath.Substring(Application.dataPath.Length).Replace('\\', '/');
        return assetsRelative;
    }

    private void ClearPreview()
    {
        if (_resultPreview != null)
        {
            DestroyImmediate(_resultPreview);
            _resultPreview = null;
        }
        _lastResultPath = null;
    }

    [Serializable]
    private struct ImageRequest
    {
        public string prompt;
        public int width;
        public int height;
        public int steps;
        public int seed;
    }
}
