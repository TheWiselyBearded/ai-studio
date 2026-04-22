using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;

/// <summary>
/// Editor window for cloning voices and generating speech with cloned voices
/// via the QwenTTS ComfyUI pipeline.
/// </summary>
public class VoiceCloneWindow : EditorWindow
{
    // --- Configuration ---
    private const string VoicesPath = "/voices";
    private const string ClonePath = "/generate/voice-clone";
    private const string SpeechPath = "/generate/voice-clone-speech";
    private string _outputFolder = "Assets/GeneratedAudio";

    // --- Clone Voice ---
    private AudioClip _referenceClip;
    private string _voiceName = "my_voice";
    private static readonly string[] Languages = { "zh", "en", "ja", "ko", "fr", "de", "es", "auto" };
    private int _languageIndex = 1; // default "en"

    // --- Voice Clone Speech ---
    private string[] _availableVoices = { };
    private int _selectedVoiceIndex;
    private AudioClip _speechReferenceClip;
    private string _textInput = "";
    private static readonly string[] Characters = { "Female", "Male" };
    private static readonly string[] Styles = { "Warm", "Bright", "Calm", "Energetic", "Soft", "Deep" };
    private int _characterIndex;
    private int _styleIndex;

    // --- State ---
    private bool _isCloning;
    private bool _isGenerating;
    private bool _isFetchingVoices;
    private string _statusMessage = "";
    private MessageType _statusType = MessageType.None;
    private float _progress;
    private Vector2 _scrollPos;

    [MenuItem("AI Studio/Voice Clone")]
    public static void ShowWindow()
    {
        var window = GetWindow<VoiceCloneWindow>("Voice Clone");
        window.minSize = new Vector2(440, 560);
    }

    private void OnEnable()
    {
        FetchVoices();
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // --- Header ---
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Voice Clone", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Clone a voice from a reference audio clip, then generate speech using that cloned voice.\n" +
            "1) Clone a voice from audio to create a .pt file.\n" +
            "2) Select a cloned voice and enter text to generate speech.",
            MessageType.Info);
        EditorGUILayout.Space(4);

        // --- Settings ---
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Endpoint", $"{AIStudioSettings.ActiveMode} · {AIStudioSettings.ActiveBaseUrl}");
        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        EditorGUILayout.Space(8);

        DrawSeparator();

        // =====================================================================
        // Section 1: Clone Voice
        // =====================================================================
        EditorGUILayout.LabelField("1. Clone Voice", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Upload a reference audio clip to clone the voice. This creates a .pt voice file on the server.",
            MessageType.None);
        EditorGUILayout.Space(4);

        _referenceClip = (AudioClip)EditorGUILayout.ObjectField(
            "Reference Audio", _referenceClip, typeof(AudioClip), false);
        _voiceName = EditorGUILayout.TextField("Voice Name", _voiceName);
        _languageIndex = EditorGUILayout.Popup("Language", _languageIndex, Languages);

        EditorGUILayout.Space(4);

        bool canClone = !_isCloning && !_isGenerating && _referenceClip != null
                        && !string.IsNullOrWhiteSpace(_voiceName);
        EditorGUI.BeginDisabledGroup(!canClone);
        if (GUILayout.Button(_isCloning ? "Cloning..." : "Clone Voice", ButtonStyle()))
        {
            RunCloneVoice();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(8);
        DrawSeparator();

        // =====================================================================
        // Section 2: Generate Speech with Cloned Voice
        // =====================================================================
        EditorGUILayout.LabelField("2. Generate Speech with Cloned Voice", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // Voice selection
        EditorGUILayout.BeginHorizontal();
        if (_availableVoices.Length > 0)
        {
            _selectedVoiceIndex = EditorGUILayout.Popup("Voice", _selectedVoiceIndex, _availableVoices);
        }
        else
        {
            EditorGUILayout.LabelField("Voice", "No voices available");
        }

        EditorGUI.BeginDisabledGroup(_isFetchingVoices);
        if (GUILayout.Button("Refresh", GUILayout.Width(60)))
        {
            FetchVoices();
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        // Local .pt cache: survives cloud-instance termination.
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_availableVoices.Length == 0))
        {
            if (GUILayout.Button("Download to local cache"))
            {
                DownloadVoiceCache(_availableVoices[_selectedVoiceIndex]);
            }
        }
        if (GUILayout.Button("Upload from local cache"))
        {
            UploadVoiceCache();
        }
        EditorGUILayout.EndHorizontal();

        _speechReferenceClip = (AudioClip)EditorGUILayout.ObjectField(
            "Reference Audio", _speechReferenceClip, typeof(AudioClip), false);

        _characterIndex = EditorGUILayout.Popup("Character", _characterIndex, Characters);
        _styleIndex = EditorGUILayout.Popup("Style", _styleIndex, Styles);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Text to Speak", EditorStyles.boldLabel);
        _textInput = EditorGUILayout.TextArea(_textInput, GUILayout.MinHeight(80));
        int charCount = _textInput?.Length ?? 0;
        EditorGUILayout.LabelField($"{charCount} characters", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.Space(4);

        bool canGenerate = !_isGenerating && !_isCloning
                           && _availableVoices.Length > 0
                           && _speechReferenceClip != null
                           && !string.IsNullOrWhiteSpace(_textInput);
        EditorGUI.BeginDisabledGroup(!canGenerate);
        if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate Voice Audio", ButtonStyle()))
        {
            RunGenerateSpeech();
        }
        EditorGUI.EndDisabledGroup();

        // --- Progress / Status ---
        if (_isCloning || _isGenerating)
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

    // --- Helpers ---

    private static GUIStyle ButtonStyle()
    {
        return new GUIStyle(GUI.skin.button)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36
        };
    }

    private static void DrawSeparator()
    {
        EditorGUILayout.Space(4);
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        EditorGUILayout.Space(4);
    }

    private string GetAssetFullPath(UnityEngine.Object asset)
    {
        string assetPath = AssetDatabase.GetAssetPath(asset);
        return Path.GetFullPath(assetPath);
    }

    // --- Voice List ---

    private async void FetchVoices()
    {
        _isFetchingVoices = true;
        Repaint();

        try
        {
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, VoicesPath);
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(10));
            var response = await AIStudioClient.Http.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                var result = JsonUtility.FromJson<VoiceListResponse>(body);
                _availableVoices = result.voices ?? new string[0];
                if (_selectedVoiceIndex >= _availableVoices.Length)
                    _selectedVoiceIndex = 0;
            }
            else
            {
                Debug.LogWarning($"[Voice Clone] Failed to fetch voices: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Voice Clone] Could not fetch voices: {ex.Message}");
        }
        finally
        {
            _isFetchingVoices = false;
            Repaint();
        }
    }

    // --- Clone Voice ---

    private async void RunCloneVoice()
    {
        _isCloning = true;
        _statusMessage = "Cloning voice from reference audio...";
        _statusType = MessageType.Info;
        _progress = 0.1f;
        Repaint();

        try
        {
            string audioPath = GetAssetFullPath(_referenceClip);
            await CloneVoiceAsync(audioPath);

            _statusMessage = $"Voice '{_voiceName}' cloned successfully!";
            _statusType = MessageType.Info;

            // Refresh voice list
            FetchVoices();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Voice Clone] {ex}");
        }
        finally
        {
            _isCloning = false;
            _progress = 0f;
            Repaint();
        }
    }

    private async Task CloneVoiceAsync(string audioPath)
    {
        _progress = 0.2f;
        Repaint();

        var form = new MultipartFormDataContent();

        byte[] audioBytes = File.ReadAllBytes(audioPath);
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(audioContent, "audio", Path.GetFileName(audioPath));
        form.Add(new StringContent(_voiceName), "voice_name");
        form.Add(new StringContent(Languages[_languageIndex]), "language");

        _progress = 0.3f;
        Repaint();

        string url = AIStudioSettings.BuildUrl(ClonePath);
        Debug.Log($"[Voice Clone] Sending clone request to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, ClonePath);
        request.Content = form;
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        _progress = 0.9f;
        Repaint();

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"API returned {response.StatusCode}: {errorBody}");
        }

        _progress = 1f;
        Repaint();

        Debug.Log($"[Voice Clone] Voice '{_voiceName}' cloned successfully.");
    }

    // --- Generate Speech ---

    private async void RunGenerateSpeech()
    {
        _isGenerating = true;
        _statusMessage = "Generating speech with cloned voice...";
        _statusType = MessageType.Info;
        _progress = 0.1f;
        Repaint();

        try
        {
            string audioPath = GetAssetFullPath(_speechReferenceClip);
            string resultPath = await GenerateSpeechAsync(audioPath);

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
            Debug.LogError($"[Voice Clone] {ex}");
        }
        finally
        {
            _isGenerating = false;
            _progress = 0f;
            Repaint();
        }
    }

    private async Task<string> GenerateSpeechAsync(string audioPath)
    {
        string fullOutputDir = Path.Combine(Application.dataPath,
            _outputFolder.StartsWith("Assets/") ? _outputFolder.Substring(7) : _outputFolder);
        Directory.CreateDirectory(fullOutputDir);

        _progress = 0.2f;
        Repaint();

        var form = new MultipartFormDataContent();

        byte[] audioBytes = File.ReadAllBytes(audioPath);
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(audioContent, "audio", Path.GetFileName(audioPath));
        form.Add(new StringContent(_textInput), "text");
        form.Add(new StringContent(_availableVoices[_selectedVoiceIndex]), "voice_name");
        form.Add(new StringContent(Characters[_characterIndex]), "character");
        form.Add(new StringContent(Styles[_styleIndex]), "style");

        _progress = 0.3f;
        Repaint();

        string url = AIStudioSettings.BuildUrl(SpeechPath);
        Debug.Log($"[Voice Clone] Sending speech request to {url}...");
        using var request = AIStudioClient.CreateRequest(HttpMethod.Post, SpeechPath);
        request.Content = form;
        using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));
        HttpResponseMessage response = await AIStudioClient.Http.SendAsync(request, cts.Token);

        _progress = 0.8f;
        Repaint();

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"API returned {response.StatusCode}: {errorBody}");
        }

        string outputName = "cloned_voice.flac";
        if (response.Content.Headers.ContentDisposition?.FileName != null)
        {
            outputName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
        }

        byte[] resultBytes = await response.Content.ReadAsByteArrayAsync();
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

        File.WriteAllBytes(savePath, resultBytes);
        _progress = 1f;
        Repaint();

        Debug.Log($"[Voice Clone] Saved {resultBytes.Length} bytes to {savePath}");

        string assetsRelative = "Assets" + savePath.Substring(Application.dataPath.Length).Replace('\\', '/');
        return assetsRelative;
    }

    // --- Voice .pt cache (survives instance termination) ---

    private static string VoiceCacheDir =>
        Path.Combine(Directory.GetCurrentDirectory(), "Library", "AIStudio", "voices");

    private async void DownloadVoiceCache(string voiceName)
    {
        try
        {
            Directory.CreateDirectory(VoiceCacheDir);
            string path = $"/voices/{Uri.EscapeDataString(voiceName)}/download";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, path);
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(2));
            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new Exception($"Download failed ({response.StatusCode}): {body}");
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            string dest = Path.Combine(VoiceCacheDir, voiceName + ".pt");
            File.WriteAllBytes(dest, bytes);
            _statusMessage = $"Cached '{voiceName}' ({bytes.Length} bytes) at {dest}";
            _statusType = MessageType.Info;
            Debug.Log($"[Voice Clone] {_statusMessage}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"Download failed: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Voice Clone] {ex}");
        }
        Repaint();
    }

    private async void UploadVoiceCache()
    {
        string picked = EditorUtility.OpenFilePanel("Select cached voice .pt", VoiceCacheDir, "pt");
        if (string.IsNullOrEmpty(picked)) return;

        string voiceName = Path.GetFileNameWithoutExtension(picked);
        try
        {
            var form = new MultipartFormDataContent();
            byte[] bytes = File.ReadAllBytes(picked);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "voice", Path.GetFileName(picked));
            form.Add(new StringContent(voiceName), "voice_name");

            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, "/voices/upload");
            request.Content = form;
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(2));
            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new Exception($"Upload failed ({response.StatusCode}): {body}");
            }
            _statusMessage = $"Uploaded '{voiceName}' to server";
            _statusType = MessageType.Info;
            FetchVoices();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Upload failed: {ex.Message}";
            _statusType = MessageType.Error;
            Debug.LogError($"[Voice Clone] {ex}");
        }
        Repaint();
    }

    // --- JSON Helpers ---

    [Serializable]
    private struct VoiceListResponse
    {
        public string[] voices;
    }
}
