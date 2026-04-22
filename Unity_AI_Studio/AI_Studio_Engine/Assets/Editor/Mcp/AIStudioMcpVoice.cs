using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;
using Newtonsoft.Json;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// MCP tools for the QwenTTS / voice-cloning ComfyUI pipelines.
    /// Three tools: clone a voice, generate speech with a cloned voice, generate
    /// design TTS without a cloned voice.
    public static class AIStudioMcpVoice
    {
        // --------------------------------------------------------------------
        // 1) Voice clone
        // --------------------------------------------------------------------
        public class CloneVoiceParams
        {
            [McpDescription(
                "Path to reference audio (absolute or 'Assets/...').", Required = true)]
            public string ReferenceAudioPath { get; set; }

            [McpDescription(
                "Name to give the cloned voice on the server (creates a .pt file)",
                Required = true)]
            public string VoiceName { get; set; }

            [McpDescription("Language code: zh, en, ja, ko, fr, de, es, auto", Default = "en")]
            public string Language { get; set; } = "en";
        }

        [McpTool(
            "AIStudio_GenerateVoiceClone",
            "Clone a voice from a reference audio clip via the QwenTTS pipeline. " +
            "Stores a .pt voice file on the server for later use by AIStudio_GenerateVoiceCloneSpeech. " +
            "Does not write any local asset.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateVoiceClone(CloneVoiceParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.VoiceName)) throw new ArgumentException("VoiceName is required");
            string audioPath = AIStudioMcpAssetWriter.ResolveInputPath(p.ReferenceAudioPath, nameof(p.ReferenceAudioPath));

            var form = new MultipartFormDataContent();
            byte[] audioBytes = File.ReadAllBytes(audioPath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(audioContent, "audio", Path.GetFileName(audioPath));
            form.Add(new StringContent(p.VoiceName), "voice_name");
            form.Add(new StringContent(string.IsNullOrWhiteSpace(p.Language) ? "en" : p.Language), "language");

            const string apiPath = "/generate/voice-clone";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, apiPath);
            request.Content = form;
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVoiceClone -> {AIStudioSettings.BuildUrl(apiPath)} (voice={p.VoiceName})");

            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
            }

            return new
            {
                success = true,
                voice_name = p.VoiceName,
                source_bytes = audioBytes.Length,
                endpoint = AIStudioSettings.BuildUrl(apiPath)
            };
        }

        // --------------------------------------------------------------------
        // 2) Voice clone speech
        // --------------------------------------------------------------------
        public class GenerateVoiceCloneSpeechParams
        {
            [McpDescription(
                "Path to reference audio for the voice (absolute or 'Assets/...').",
                Required = true)]
            public string ReferenceAudioPath { get; set; }

            [McpDescription("Name of a previously-cloned voice on the server", Required = true)]
            public string VoiceName { get; set; }

            [McpDescription("Text to speak", Required = true)]
            public string Text { get; set; }

            [McpDescription("Character: 'Female' or 'Male'", Default = "Female")]
            public string Character { get; set; } = "Female";

            [McpDescription(
                "Style: 'Warm', 'Bright', 'Calm', 'Energetic', 'Soft', or 'Deep'",
                Default = "Warm")]
            public string Style { get; set; } = "Warm";

            [McpDescription("Project-relative folder under Assets/ to save into")]
            public string SavePath { get; set; } = "Assets/GeneratedAudio";
        }

        [McpTool(
            "AIStudio_GenerateVoiceCloneSpeech",
            "Generate speech audio in a previously-cloned voice via the QwenTTS pipeline. " +
            "Returns the project-relative path of the imported audio file.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateVoiceCloneSpeech(GenerateVoiceCloneSpeechParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.VoiceName)) throw new ArgumentException("VoiceName is required");
            if (string.IsNullOrWhiteSpace(p.Text)) throw new ArgumentException("Text is required");
            string audioPath = AIStudioMcpAssetWriter.ResolveInputPath(p.ReferenceAudioPath, nameof(p.ReferenceAudioPath));
            string fullDir = AIStudioMcpAssetWriter.ResolveOutputFolder(p.SavePath, "Assets/GeneratedAudio");

            var form = new MultipartFormDataContent();
            byte[] audioBytes = File.ReadAllBytes(audioPath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(audioContent, "audio", Path.GetFileName(audioPath));
            form.Add(new StringContent(p.Text), "text");
            form.Add(new StringContent(p.VoiceName), "voice_name");
            form.Add(new StringContent(p.Character ?? "Female"), "character");
            form.Add(new StringContent(p.Style ?? "Warm"), "style");

            const string apiPath = "/generate/voice-clone-speech";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, apiPath);
            request.Content = form;
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVoiceCloneSpeech -> {AIStudioSettings.BuildUrl(apiPath)} (voice={p.VoiceName})");

            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
            }

            string outputName = AIStudioMcpAssetWriter.FilenameFromResponse(response, "cloned_voice.flac");
            byte[] resultBytes = await response.Content.ReadAsByteArrayAsync();
            string savePath = AIStudioMcpAssetWriter.DedupedSavePath(fullDir, outputName);
            var (assetsPath, byteCount) = AIStudioMcpAssetWriter.WriteAndImport(savePath, resultBytes);

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVoiceCloneSpeech -> {assetsPath} ({byteCount} bytes)");

            return new
            {
                success = true,
                asset_path = assetsPath,
                bytes = byteCount,
                voice_name = p.VoiceName
            };
        }

        // --------------------------------------------------------------------
        // 3) Voice design (text-to-speech, no cloned voice)
        // --------------------------------------------------------------------
        public class GenerateVoiceDesignParams
        {
            [McpDescription("Text to speak", Required = true)]
            public string Text { get; set; }

            [McpDescription("Character: 'Female' or 'Male'", Default = "Female")]
            public string Character { get; set; } = "Female";

            [McpDescription(
                "Style: 'Warm', 'Bright', 'Calm', 'Energetic', 'Soft', or 'Deep'",
                Default = "Warm")]
            public string Style { get; set; } = "Warm";

            [McpDescription("Project-relative folder under Assets/ to save into")]
            public string SavePath { get; set; } = "Assets/GeneratedAudio";
        }

        [McpTool(
            "AIStudio_GenerateVoiceDesign",
            "Generate text-to-speech audio (without a cloned voice) via the QwenTTS pipeline. " +
            "Picks character/style from a small preset list. Server endpoint is /generate/audio.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateVoiceDesign(GenerateVoiceDesignParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.Text)) throw new ArgumentException("Text is required");
            string fullDir = AIStudioMcpAssetWriter.ResolveOutputFolder(p.SavePath, "Assets/GeneratedAudio");

            var payload = new
            {
                text = p.Text,
                character = p.Character ?? "Female",
                style = p.Style ?? "Warm"
            };
            string json = JsonConvert.SerializeObject(payload);

            const string apiPath = "/generate/audio";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, apiPath);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVoiceDesign -> {AIStudioSettings.BuildUrl(apiPath)}");

            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
            }

            string outputName = AIStudioMcpAssetWriter.FilenameFromResponse(response, "generated_voice.flac");
            byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
            string savePath = AIStudioMcpAssetWriter.DedupedSavePath(fullDir, outputName);
            var (assetsPath, byteCount) = AIStudioMcpAssetWriter.WriteAndImport(savePath, audioBytes);

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVoiceDesign -> {assetsPath} ({byteCount} bytes)");

            return new
            {
                success = true,
                asset_path = assetsPath,
                bytes = byteCount
            };
        }
    }
}
