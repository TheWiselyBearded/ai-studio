using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Editor window for generating 3D assets via the Hunyuan3D 2.1 PBR ComfyUI pipeline.
/// Sends a single image to the run_comfy_server.py Flask API and imports the resulting GLB.
/// </summary>
public class Hunyuan3DGeneratorWindow : EditorWindow
{
    // --- Configuration ---
    // Hunyuan3D's first-run cold-load (7 GB dit + VAE decode + postprocess +
    // paintPBR) routinely exceeds Cloudflare's 100 s edge timeout, so we use
    // the async job system instead of the synchronous /generate/3d endpoint.
    private const string SubmitPath = "/jobs/submit/3d";
    private string _outputFolder = "Assets/Generated3D";
    private string _currentJobId;
    private bool _cancelRequested;

    // --- Image slot ---
    private string _imagePath;
    private Texture2D _preview;

    // --- State ---
    private bool _isGenerating;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;

    [MenuItem("AI Studio/Hunyuan 3D Generator")]
    public static void ShowWindow()
    {
        var window = GetWindow<Hunyuan3DGeneratorWindow>("3D Generator");
        window.minSize = new Vector2(420, 400);
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // --- Header ---
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Hunyuan 3D Asset Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Upload a single image to generate a textured 3D model with PBR materials.",
            MessageType.Info);
        EditorGUILayout.Space(4);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}{SubmitPath}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        // --- Image Slot ---
        EditorGUILayout.LabelField("Reference Image", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(200));

        if (_preview != null)
        {
            // Show preview + filename + remove button
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(20), GUILayout.Height(18)))
            {
                ClearImage();
            }
            EditorGUILayout.EndHorizontal();

            var previewRect = GUILayoutUtility.GetRect(200, 140, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(previewRect, _preview, ScaleMode.ScaleToFit);

            string filename = Path.GetFileName(_imagePath);
            EditorGUILayout.LabelField(filename, EditorStyles.centeredGreyMiniLabel);
        }
        else
        {
            // Browse button + drag-and-drop
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Browse...", GUILayout.Width(120), GUILayout.Height(32)))
            {
                BrowseImage();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("or drag & drop an image here", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();

            var dropArea = GUILayoutUtility.GetLastRect();
            HandleDragDrop(dropArea);
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(8);

        // --- Generate Button ---
        bool canGenerate = !_isGenerating && !string.IsNullOrEmpty(_imagePath);
        EditorGUI.BeginDisabledGroup(!canGenerate);
        var generateStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };
        if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate 3D Model", generateStyle))
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

    private void BrowseImage()
    {
        string path = EditorUtility.OpenFilePanel(
            "Select Reference Image", "", "png,jpg,jpeg,bmp,tga,tiff");

        if (!string.IsNullOrEmpty(path))
        {
            SetImage(path);
        }
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
            {
                SetImage(DragAndDrop.paths[0]);
            }
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

    // --- Generation (async job pattern, mirrors VideoGeneratorWindow) ---
    private async void RunGeneration()
    {
        _isGenerating = true;
        _cancelRequested = false;
        _currentJobId = null;
        _statusMessage = "Submitting 3D generation job...";
        _statusType = MessageType.Info;
        _progress = 0.05f;
        Repaint();

        try
        {
            string jobId = await SubmitJobAsync();
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
                    // Fetch the last 60 lines of the hunyuan log so the full
                    // Python traceback ends up in the Unity console even when
                    // the /jobs/status response only carries the short summary.
                    await TryFetchAndLogHunyuanTailAsync();
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

            string resultPath = await DownloadJobResultAsync(jobId);
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
            Debug.LogError($"[3D Generator] {ex}");
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
        byte[] fileBytes = File.ReadAllBytes(_imagePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "image", Path.GetFileName(_imagePath));

        string url = AIStudioSettings.BuildUrl(SubmitPath);
        Debug.Log($"[3D Generator] Submitting job to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, SubmitPath);
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

    /// Pull the last 60 lines of the Hunyuan ComfyUI log and dump as a
    /// Debug.LogError. Async-job failures only give us status=error; this
    /// gives the actual Python traceback without needing SSH.
    private async Task TryFetchAndLogHunyuanTailAsync()
    {
        try
        {
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, "/logs/hunyuan?lines=60");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(15));
            var resp = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!resp.IsSuccessStatusCode) return;
            var text = await resp.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(text))
                Debug.LogError($"[3D Generator] comfy-hunyuan log tail:\n{text}");
        }
        catch { /* best-effort */ }
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
        await AIStudioClient.EnsureSuccessAsync(response, "3D Generator (download)");

        string outputName = "generated_model.glb";
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
        Debug.Log($"[3D Generator] Saved {glbBytes.Length} bytes to {savePath}");
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
