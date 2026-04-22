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
    private const string ApiPath = "/generate/3d";
    private string _outputFolder = "Assets/Generated3D";

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
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}{ApiPath}");
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

    // --- Generation ---
    private async void RunGeneration()
    {
        _isGenerating = true;
        _statusMessage = "Uploading image and generating 3D model...";
        _statusType = MessageType.Info;
        _progress = 0.1f;
        Repaint();

        try
        {
            string resultPath = await GenerateAsync();

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

        var form = new MultipartFormDataContent();

        byte[] fileBytes = File.ReadAllBytes(_imagePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "image", Path.GetFileName(_imagePath));

        _progress = 0.3f;
        Repaint();

        string url = AIStudioSettings.BuildUrl(ApiPath);
        Debug.Log($"[3D Generator] Sending request to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, ApiPath);
        request.Content = form;
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        _progress = 0.8f;
        Repaint();

        await AIStudioClient.EnsureSuccessAsync(response, "3D Generator");

        // Determine output filename from Content-Disposition header or fallback
        string outputName = "generated_model.glb";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
        {
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
        }

        // Save the GLB file
        byte[] glbBytes = await response.Content.ReadAsByteArrayAsync();
        string savePath = Path.Combine(fullOutputDir, outputName);

        // Avoid overwriting
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
        _progress = 1f;
        Repaint();

        Debug.Log($"[3D Generator] Saved {glbBytes.Length} bytes to {savePath}");

        string assetsRelative = "Assets" + savePath.Substring(Application.dataPath.Length).Replace('\\', '/');
        return assetsRelative;
    }
}
