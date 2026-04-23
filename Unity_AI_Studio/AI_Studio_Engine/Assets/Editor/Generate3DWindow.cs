using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Unified 3D generator window. Pick a provider (Hunyuan 2.1 PBR or Trellis2)
/// from the dropdown, drop or browse to a reference image, and submit.
/// Uses the async-job pattern (submit -> poll -> download) so first-run model
/// loads don't hit Cloudflare's 100s tunnel timeout.
/// </summary>
public class Generate3DWindow : EditorWindow
{
    private enum Provider { Hunyuan, Trellis }

    private Provider _provider = Provider.Hunyuan;

    private string _outputFolder = "Assets/Generated3D";
    private string _currentJobId;
    private bool _cancelRequested;

    private string _imagePath;
    private Texture2D _preview;

    private bool _isGenerating;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;

    [MenuItem("AI Studio/3D Generator")]
    public static void ShowWindow()
    {
        var window = GetWindow<Generate3DWindow>("3D Generator");
        window.minSize = new Vector2(420, 440);
    }

    private string SubmitPath => _provider == Provider.Trellis ? "/jobs/submit/trellis" : "/jobs/submit/3d";
    private string LogService => _provider == Provider.Trellis ? "trellis" : "hunyuan";

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("3D Asset Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Select a provider and drop an image to generate a 3D model.\n" +
            "• Hunyuan 2.1 PBR — textured mesh with PBR materials.\n" +
            "• Trellis2 — fast mesh-from-image (image must have a clear subject; alpha is respected).",
            MessageType.Info);
        EditorGUILayout.Space(4);

        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(_isGenerating);
        _provider = (Provider)EditorGUILayout.EnumPopup("Provider", _provider);
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}{SubmitPath}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Reference Image", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(200));
        if (_preview != null)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(20), GUILayout.Height(18)))
                ClearImage();
            EditorGUILayout.EndHorizontal();

            var previewRect = GUILayoutUtility.GetRect(200, 140, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(previewRect, _preview, ScaleMode.ScaleToFit);
            EditorGUILayout.LabelField(Path.GetFileName(_imagePath), EditorStyles.centeredGreyMiniLabel);
        }
        else
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Browse...", GUILayout.Width(120), GUILayout.Height(32)))
                BrowseImage();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("or drag & drop an image here", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();

            var dropArea = GUILayoutUtility.GetLastRect();
            HandleDragDrop(dropArea);
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(8);

        bool canGenerate = !_isGenerating && !string.IsNullOrEmpty(_imagePath);
        EditorGUI.BeginDisabledGroup(!canGenerate);
        var generateStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };
        string buttonLabel = _isGenerating
            ? "Generating..."
            : $"Generate with {_provider}";
        if (GUILayout.Button(buttonLabel, generateStyle))
            RunGeneration();
        EditorGUI.EndDisabledGroup();

        if (_isGenerating)
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(rect, _progress, "Processing...");

            EditorGUILayout.Space(2);
            if (GUILayout.Button("Cancel", GUILayout.Height(22)))
                _cancelRequested = true;
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(_statusMessage, _statusType);
        }

        EditorGUILayout.EndScrollView();
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
        _preview = tex;
        Repaint();
    }

    private void ClearImage()
    {
        _imagePath = null;
        if (_preview != null)
        {
            DestroyImmediate(_preview);
            _preview = null;
        }
        Repaint();
    }

    private async void RunGeneration()
    {
        _isGenerating = true;
        _cancelRequested = false;
        _currentJobId = null;
        _statusMessage = $"Submitting {_provider} 3D job...";
        _statusType = MessageType.Info;
        _progress = 0.05f;
        Repaint();

        var submitPath = SubmitPath;
        var logService = LogService;
        var providerLabel = _provider.ToString();

        try
        {
            string jobId = await SubmitJobAsync(submitPath);
            _currentJobId = jobId;
            _progress = 0.1f;
            _statusMessage = $"Job submitted ({jobId.Substring(0, Math.Min(8, jobId.Length))}...). Generating mesh...";
            Repaint();

            while (!_cancelRequested)
            {
                await Task.Delay(5000);
                if (_cancelRequested) break;

                var st = await CheckJobStatusAsync(jobId);
                if (st.status == "complete")
                {
                    _progress = 0.9f;
                    _statusMessage = "Downloading GLB...";
                    Repaint();
                    break;
                }
                if (st.status == "error" || st.status == "cancelled")
                {
                    await TryFetchAndLogTailAsync(logService, providerLabel);
                    var detail = string.IsNullOrEmpty(st.error) ? "(no detail)" : st.error;
                    throw new Exception($"Job {st.status}: {detail}");
                }

                _progress = Mathf.Min(0.85f, _progress + 0.02f);
                _statusMessage = $"Generating mesh ({jobId.Substring(0, Math.Min(8, jobId.Length))}...)...";
                Repaint();
            }

            if (_cancelRequested)
            {
                _statusMessage = "Generation cancelled.";
                _statusType = MessageType.Warning;
                return;
            }

            string resultPath = await DownloadJobResultAsync(jobId, providerLabel);
            _progress = 1f;
            _statusMessage = $"3D model imported: {resultPath}";
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
            Debug.LogError($"[3D Generator/{providerLabel}] {ex}");
        }
        finally
        {
            _isGenerating = false;
            _progress = 0f;
            _currentJobId = null;
            Repaint();
        }
    }

    private async Task<string> SubmitJobAsync(string submitPath)
    {
        var form = new MultipartFormDataContent();
        byte[] fileBytes = File.ReadAllBytes(_imagePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "image", Path.GetFileName(_imagePath));

        string url = AIStudioSettings.BuildUrl(submitPath);
        Debug.Log($"[3D Generator] Submitting job to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, submitPath);
        request.Content = form;
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(2));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync();
        await AIStudioClient.EnsureSuccessAsync(response, "3D Generator (submit)");

        var result = JsonUtility.FromJson<JobSubmitResponse>(body);
        if (string.IsNullOrEmpty(result.job_id))
            throw new Exception($"No job_id in response: {body}");
        return result.job_id;
    }

    private async Task<JobStatusResponse> CheckJobStatusAsync(string jobId)
    {
        string path = $"/jobs/{jobId}/status";
        using var request = AIStudioClient.CreateRequest(HttpMethod.Get, path);
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(30));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);
        string body = await response.Content.ReadAsStringAsync();
        return JsonUtility.FromJson<JobStatusResponse>(body);
    }

    // Log tail on failure: async-job errors only carry a short summary; the
    // real Python traceback lives in comfy-{hunyuan,trellis}.log on the
    // instance. Pull 60 lines into the Unity console so the user doesn't need
    // SSH to debug a failure.
    private async Task TryFetchAndLogTailAsync(string service, string providerLabel)
    {
        try
        {
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, $"/logs/{service}?lines=60");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(15));
            var resp = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!resp.IsSuccessStatusCode) return;
            var text = await resp.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(text))
                Debug.LogError($"[3D Generator/{providerLabel}] comfy-{service} log tail:\n{text}");
        }
        catch { /* best-effort */ }
    }

    private async Task<string> DownloadJobResultAsync(string jobId, string providerLabel)
    {
        string fullOutputDir = Path.Combine(Application.dataPath,
            _outputFolder.StartsWith("Assets/") ? _outputFolder.Substring(7) : _outputFolder);
        Directory.CreateDirectory(fullOutputDir);

        string path = $"/jobs/{jobId}/result";
        using var request = AIStudioClient.CreateRequest(HttpMethod.Get, path);
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(5));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);
        await AIStudioClient.EnsureSuccessAsync(response, "3D Generator (download)");

        string outputName = $"{providerLabel.ToLowerInvariant()}_model.glb";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');

        byte[] glbBytes = await response.Content.ReadAsByteArrayAsync();
        string savePath = Path.Combine(fullOutputDir, outputName);

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

        File.WriteAllBytes(savePath, glbBytes);
        Debug.Log($"[3D Generator/{providerLabel}] Saved {glbBytes.Length} bytes to {savePath}");
        return "Assets" + savePath.Substring(Application.dataPath.Length).Replace('\\', '/');
    }

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
