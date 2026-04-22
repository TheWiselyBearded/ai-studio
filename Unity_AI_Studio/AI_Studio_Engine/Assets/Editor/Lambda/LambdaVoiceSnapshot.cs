using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using AIStudio.Core;

namespace AIStudio.Lambda
{
    /// Downloads every cloned voice .pt file from the active endpoint into the
    /// local cache dir. Called right before terminating a Lambda instance so
    /// voice clones survive the instance dying.
    public static class LambdaVoiceSnapshot
    {
        public static string CacheDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "AIStudio", "voices");

        [Serializable]
        private struct VoiceListResponse
        {
            public string[] voices;
        }

        public static async Task<int> SnapshotAllAsync()
        {
            var voices = await FetchVoiceListAsync();
            if (voices == null || voices.Length == 0) return 0;

            Directory.CreateDirectory(CacheDir);
            int saved = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                var name = voices[i];
                EditorUtility.DisplayProgressBar("Snapshotting voices",
                    $"Downloading {name} ({i + 1}/{voices.Length})",
                    (float)(i + 1) / voices.Length);
                try
                {
                    await DownloadOneAsync(name);
                    saved++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Lambda] Voice snapshot failed for '{name}': {ex.Message}");
                }
            }
            EditorUtility.ClearProgressBar();
            Debug.Log($"[Lambda] Snapshotted {saved}/{voices.Length} voices to {CacheDir}");
            return saved;
        }

        private static async Task<string[]> FetchVoiceListAsync()
        {
            using var req = AIStudioClient.CreateRequest(HttpMethod.Get, "/voices");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(15));
            var resp = await AIStudioClient.Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            string body = await resp.Content.ReadAsStringAsync();
            var parsed = JsonUtility.FromJson<VoiceListResponse>(body);
            return parsed.voices ?? Array.Empty<string>();
        }

        private static async Task DownloadOneAsync(string name)
        {
            string path = $"/voices/{Uri.EscapeDataString(name)}/download";
            using var req = AIStudioClient.CreateRequest(HttpMethod.Get, path);
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(1));
            using var resp = await AIStudioClient.Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"{(int)resp.StatusCode}: {body}");
            }
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            File.WriteAllBytes(Path.Combine(CacheDir, name + ".pt"), bytes);
        }
    }
}
