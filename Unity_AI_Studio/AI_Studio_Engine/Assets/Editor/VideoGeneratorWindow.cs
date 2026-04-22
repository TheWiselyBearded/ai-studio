using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Editor window for generating videos via multiple providers:
/// Wan 2.2 (local ComfyUI), Google Veo 3.1, and Runway Gen-4 Turbo.
/// Uses the async job system for long-running video generation tasks.
/// </summary>
public class VideoGeneratorWindow : EditorWindow
{
    // --- Provider ---
    private enum VideoProvider { Wan2, Veo, Runway }
    private static readonly string[] ProviderLabels = { "Wan 2.2 (Local)", "Veo 3.1 (Cloud)", "Runway Gen-4 Turbo (Cloud)" };
    private int _providerIndex = 0;

    // --- Configuration ---
    private const string SubmitPath = "/jobs/submit/video";
    private string _outputFolder = "Assets/GeneratedVideos";

    // --- Shared Fields ---
    private string _imagePath;
    private Texture2D _imagePreview;
    private string _prompt = "";

    // --- Wan2 Settings ---
    private static readonly int[] Wan2Sizes = { 480, 640, 832, 1024 };
    private static readonly string[] Wan2SizeLabels = { "480", "640", "832", "1024" };
    private int _wan2WidthIndex = 1;   // 640
    private int _wan2HeightIndex = 1;  // 640
    private int _wan2Length = 301;
    private bool _wan2Enable4StepLora = true;
    private bool _wan2UseCustomSeed;
    private int _wan2Seed;
    private int _wan2Steps = 20;
    private float _wan2Cfg = 3.5f;
    private string _wan2NegativePrompt = "";
    private bool _wan2ShowNegativePrompt;

    // --- Runway Settings ---
    private static readonly string[] RunwayRatios = { "16:9", "9:16", "1:1" };
    private int _runwayRatioIndex = 0;
    private static readonly string[] RunwayDurations = { "5 seconds", "10 seconds" };
    private static readonly int[] RunwayDurationValues = { 5, 10 };
    private int _runwayDurationIndex = 1;

    // --- State ---
    private bool _isGenerating;
    private string _currentJobId;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;
    private string _lastResultPath;
    private bool _cancelRequested;

    [MenuItem("AI Studio/Video Generator")]
    public static void ShowWindow()
    {
        var window = GetWindow<VideoGeneratorWindow>("Video Generator");
        window.minSize = new Vector2(440, 560);
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // --- Header ---
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Video Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Generate videos from a reference image and text prompt.\n" +
            "Choose a provider, upload an image, describe the motion, then click Generate.",
            MessageType.Info);
        EditorGUILayout.Space(4);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        // --- Provider ---
        EditorGUILayout.LabelField("Provider", EditorStyles.boldLabel);
        _providerIndex = EditorGUILayout.Popup("Provider", _providerIndex, ProviderLabels);
        EditorGUILayout.Space(8);

        // --- Image Slot ---
        EditorGUILayout.LabelField("Reference Image", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);
        DrawImageSlot();
        EditorGUILayout.Space(8);

        // --- Prompt ---
        EditorGUILayout.LabelField("Prompt", EditorStyles.boldLabel);
        _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.MinHeight(60));
        int charCount = _prompt?.Length ?? 0;
        EditorGUILayout.LabelField($"{charCount} characters", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.Space(8);

        // --- Provider-Specific Settings ---
        DrawProviderSettings();
        EditorGUILayout.Space(8);

        // --- Generate / Cancel Buttons ---
        DrawActionButtons();

        // --- Progress / Status ---
        if (_isGenerating)
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(rect, _progress, _statusMessage ?? "Processing...");
        }

        if (!string.IsNullOrEmpty(_statusMessage) && !_isGenerating)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
        }

        // --- Result ---
        if (!string.IsNullOrEmpty(_lastResultPath))
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_lastResultPath, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Play Video", GUILayout.Height(28)))
            {
                string fullPath = Path.Combine(Application.dataPath, "..",
                    _lastResultPath).Replace('/', '\\');
                Application.OpenURL(fullPath);
            }
            if (GUILayout.Button("Show in Explorer", GUILayout.Height(28)))
            {
                string fullPath = Path.Combine(Application.dataPath, "..",
                    _lastResultPath).Replace('/', '\\');
                System.Diagnostics.Process.Start("explorer", $"/select,\"{fullPath}\"");
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // =========================================================================
    // Image Slot (Browse + Drag-and-Drop)
    // =========================================================================

    private void DrawImageSlot()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(180));

        if (_imagePreview != null)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(20), GUILayout.Height(18)))
            {
                ClearImage();
            }
            EditorGUILayout.EndHorizontal();

            var previewRect = GUILayoutUtility.GetRect(200, 120, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(previewRect, _imagePreview, ScaleMode.ScaleToFit);

            string filename = Path.GetFileName(_imagePath);
            EditorGUILayout.LabelField(filename, EditorStyles.centeredGreyMiniLabel);
        }
        else
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Browse...", GUILayout.Width(120), GUILayout.Height(32)))
            {
                BrowseImage();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("or drag & drop an image here",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();

            var dropArea = GUILayoutUtility.GetLastRect();
            HandleDragDrop(dropArea);
        }

        EditorGUILayout.EndVertical();
    }

    private void BrowseImage()
    {
        string path = EditorUtility.OpenFilePanel(
            "Select Reference Image", "", "png,jpg,jpeg,bmp,tga,tiff");
        if (!string.IsNullOrEmpty(path))
            SetImage(path);
    }

    private void HandleDragDrop(Rect dropArea)
    {
        Event evt = Event.current;
        if (!dropArea.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.Use();
        }
        else if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            if (DragAndDrop.paths.Length > 0)
                SetImage(DragAndDrop.paths[0]);
            evt.Use();
        }
    }

    private void SetImage(string path)
    {
        _imagePath = path;
        byte[] bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2);
        tex.LoadImage(bytes);
        _imagePreview = tex;
        Repaint();
    }

    private void ClearImage()
    {
        _imagePath = null;
        if (_imagePreview != null)
        {
            DestroyImmediate(_imagePreview);
            _imagePreview = null;
        }
        Repaint();
    }

    // =========================================================================
    // Provider-Specific Settings
    // =========================================================================

    private void DrawProviderSettings()
    {
        var provider = (VideoProvider)_providerIndex;

        switch (provider)
        {
            case VideoProvider.Wan2:
                EditorGUILayout.LabelField("Wan 2.2 Settings", EditorStyles.boldLabel);
                _wan2WidthIndex = EditorGUILayout.Popup("Width", _wan2WidthIndex, Wan2SizeLabels);
                _wan2HeightIndex = EditorGUILayout.Popup("Height", _wan2HeightIndex, Wan2SizeLabels);
                _wan2Length = EditorGUILayout.IntSlider("Frame Count", _wan2Length, 49, 601);
                _wan2Enable4StepLora = EditorGUILayout.Toggle("Enable 4-Step LoRA", _wan2Enable4StepLora);

                if (!_wan2Enable4StepLora)
                {
                    _wan2Steps = EditorGUILayout.IntSlider("Steps", _wan2Steps, 1, 40);
                    _wan2Cfg = EditorGUILayout.Slider("CFG", _wan2Cfg, 1f, 10f);
                }

                _wan2UseCustomSeed = EditorGUILayout.Toggle("Custom Seed", _wan2UseCustomSeed);
                if (_wan2UseCustomSeed)
                    _wan2Seed = EditorGUILayout.IntField("Seed", _wan2Seed);

                _wan2ShowNegativePrompt = EditorGUILayout.Foldout(_wan2ShowNegativePrompt,
                    "Negative Prompt (optional)");
                if (_wan2ShowNegativePrompt)
                {
                    _wan2NegativePrompt = EditorGUILayout.TextArea(_wan2NegativePrompt,
                        GUILayout.MinHeight(40));
                    EditorGUILayout.HelpBox(
                        "Leave empty to use the default negative prompt.",
                        MessageType.None);
                }
                break;

            case VideoProvider.Veo:
                EditorGUILayout.HelpBox(
                    "Google Veo 3.1 uses your image and prompt directly.\n" +
                    "No additional settings are required.",
                    MessageType.Info);
                break;

            case VideoProvider.Runway:
                EditorGUILayout.LabelField("Runway Settings", EditorStyles.boldLabel);
                _runwayRatioIndex = EditorGUILayout.Popup("Aspect Ratio", _runwayRatioIndex, RunwayRatios);
                _runwayDurationIndex = EditorGUILayout.Popup("Duration", _runwayDurationIndex, RunwayDurations);
                break;
        }
    }

    // =========================================================================
    // Generate / Cancel
    // =========================================================================

    private void DrawActionButtons()
    {
        bool canGenerate = !_isGenerating
            && !string.IsNullOrEmpty(_imagePath)
            && !string.IsNullOrWhiteSpace(_prompt);

        var buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };

        if (_isGenerating)
        {
            // Cancel button
            if (GUILayout.Button("Cancel", buttonStyle))
            {
                _cancelRequested = true;
                CancelJobAsync();
            }
        }
        else
        {
            EditorGUI.BeginDisabledGroup(!canGenerate);
            if (GUILayout.Button("Generate Video", buttonStyle))
            {
                RunGeneration();
            }
            EditorGUI.EndDisabledGroup();
        }
    }

    // =========================================================================
    // Async Job System
    // =========================================================================

    private async void RunGeneration()
    {
        _isGenerating = true;
        _cancelRequested = false;
        _currentJobId = null;
        _lastResultPath = null;
        _statusMessage = "Submitting video generation job...";
        _statusType = MessageType.Info;
        _progress = 0.05f;
        Repaint();

        try
        {
            // Step 1: Submit job
            string jobId = await SubmitJobAsync();
            _currentJobId = jobId;
            _progress = 0.1f;
            _statusMessage = $"Job submitted ({jobId.Substring(0, 8)}...). Rendering video...";
            Repaint();

            // Step 2: Poll until complete
            while (!_cancelRequested)
            {
                await Task.Delay(5000);
                if (_cancelRequested) break;

                string status = await CheckJobStatusAsync(jobId);

                if (status == "complete")
                {
                    _progress = 0.9f;
                    _statusMessage = "Downloading video...";
                    Repaint();
                    break;
                }
                else if (status == "error" || status == "cancelled")
                {
                    throw new Exception($"Job {status}.");
                }

                // Animate progress (0.1 -> 0.85)
                _progress = Mathf.Min(0.85f, _progress + 0.015f);
                _statusMessage = $"Rendering video ({jobId.Substring(0, 8)}...)...";
                Repaint();
            }

            if (_cancelRequested)
            {
                _statusMessage = "Generation cancelled.";
                _statusType = MessageType.Warning;
                return;
            }

            // Step 3: Download result
            string resultPath = await DownloadJobResultAsync(jobId);
            _lastResultPath = resultPath;
            _progress = 1f;
            _statusMessage = $"Video saved: {resultPath}";
            _statusType = MessageType.Info;

            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Video Generator] {ex}");
        }
        finally
        {
            _isGenerating = false;
            _progress = 0f;
            _currentJobId = null;
            Repaint();
        }
    }

    private async Task<string> SubmitJobAsync()
    {
        var form = new MultipartFormDataContent();

        // Image file
        byte[] fileBytes = File.ReadAllBytes(_imagePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "image", Path.GetFileName(_imagePath));

        // Provider
        var provider = (VideoProvider)_providerIndex;
        string providerName = provider switch
        {
            VideoProvider.Wan2 => "wan2",
            VideoProvider.Veo => "veo",
            VideoProvider.Runway => "runway",
            _ => "wan2"
        };
        form.Add(new StringContent(providerName), "provider");
        form.Add(new StringContent(_prompt), "prompt");

        // Provider-specific fields
        switch (provider)
        {
            case VideoProvider.Wan2:
                form.Add(new StringContent(Wan2Sizes[_wan2WidthIndex].ToString()), "width");
                form.Add(new StringContent(Wan2Sizes[_wan2HeightIndex].ToString()), "height");
                form.Add(new StringContent(_wan2Length.ToString()), "length");
                form.Add(new StringContent(_wan2Enable4StepLora.ToString().ToLower()), "enable_4step_lora");
                form.Add(new StringContent(_wan2Steps.ToString()), "steps");
                form.Add(new StringContent(_wan2Cfg.ToString("F1")), "cfg");

                if (_wan2UseCustomSeed)
                    form.Add(new StringContent(_wan2Seed.ToString()), "seed");

                if (!string.IsNullOrWhiteSpace(_wan2NegativePrompt))
                    form.Add(new StringContent(_wan2NegativePrompt), "negative_prompt");
                break;

            case VideoProvider.Runway:
                form.Add(new StringContent(RunwayRatios[_runwayRatioIndex]), "ratio");
                form.Add(new StringContent(RunwayDurationValues[_runwayDurationIndex].ToString()), "duration");
                break;
        }

        string url = AIStudioSettings.BuildUrl(SubmitPath);
        Debug.Log($"[Video Generator] Submitting job to {url} (provider={providerName})...");

        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, SubmitPath);
        request.Content = form;
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(2));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync();

        await AIStudioClient.EnsureSuccessAsync(response, "Video Generator (submit)");

        var result = JsonUtility.FromJson<JobSubmitResponse>(body);
        if (string.IsNullOrEmpty(result.job_id))
            throw new Exception($"No job_id in response: {body}");

        return result.job_id;
    }

    private async Task<string> CheckJobStatusAsync(string jobId)
    {
        string path = $"/jobs/{jobId}/status";
        using var request = AIStudioClient.CreateRequest(HttpMethod.Get, path);
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(30));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync();

        var result = JsonUtility.FromJson<JobStatusResponse>(body);
        return result.status;
    }

    private async Task<string> DownloadJobResultAsync(string jobId)
    {
        string fullOutputDir = Path.Combine(Application.dataPath,
            _outputFolder.StartsWith("Assets/") ? _outputFolder.Substring(7) : _outputFolder);
        Directory.CreateDirectory(fullOutputDir);

        string path = $"/jobs/{jobId}/result";
        using var request = AIStudioClient.CreateRequest(HttpMethod.Get, path);
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(5));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        await AIStudioClient.EnsureSuccessAsync(response, "Video Generator (download)");

        // Determine filename
        string outputName = "generated_video.mp4";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');

        byte[] videoBytes = await response.Content.ReadAsByteArrayAsync();
        string savePath = Path.Combine(fullOutputDir, outputName);

        // Dedup
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

        File.WriteAllBytes(savePath, videoBytes);
        Debug.Log($"[Video Generator] Saved {videoBytes.Length} bytes to {savePath}");

        string assetsRelative = "Assets" + savePath.Substring(Application.dataPath.Length)
            .Replace('\\', '/');
        return assetsRelative;
    }

    private async void CancelJobAsync()
    {
        if (string.IsNullOrEmpty(_currentJobId)) return;

        try
        {
            string path = $"/jobs/{_currentJobId}/cancel";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, path);
            request.Content = new StringContent("");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(10));
            await AIStudioClient.Http.SendAsync(request, cts.Token);
            Debug.Log($"[Video Generator] Cancel requested for job {_currentJobId}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Video Generator] Cancel request failed: {ex.Message}");
        }
    }

    // =========================================================================
    // JSON Response Types
    // =========================================================================

    [Serializable]
    private struct JobSubmitResponse
    {
        public string job_id;
        public string status;
    }

    [Serializable]
    private struct JobStatusResponse
    {
        public string job_id;
        public string status;
        public string error;
    }
}
