using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIStudio.Core
{
    /// Tracks the current cloud endpoint URL in a project-local JSON file
    /// (Assets/StreamingAssets/AIStudio/endpoint.json) so every editor window
    /// sees the same value without each one re-reading EditorPrefs. The file
    /// is the source of truth for the URL; secrets stay in EditorPrefs.
    ///
    /// Cloudflare quick-tunnel URLs rotate every time cloudflared restarts
    /// (instance reboots, service restarts, etc). The Lambda Instance Manager
    /// SSHes in periodically to re-fetch the URL and calls Write() whenever
    /// it changes, which invalidates the in-memory cache everywhere.
    public static class AIStudioEndpointManifest
    {
        private const string RelativePath = "Assets/StreamingAssets/AIStudio/endpoint.json";

        public static string FullPath =>
            Path.Combine(Directory.GetCurrentDirectory(), RelativePath);

        [Serializable]
        public class Data
        {
            public string remoteBaseUrl = "";
            public string instanceId = "";
            public long updatedUnix = 0;
        }

        public static event Action<Data> Changed;

        private static Data _cache;
        private static DateTime _cacheMtime = DateTime.MinValue;

        public static Data Read()
        {
            var path = FullPath;
            if (!File.Exists(path))
                return _cache ?? new Data();
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (_cache != null && mtime == _cacheMtime) return _cache;
                var json = File.ReadAllText(path);
                _cache = JsonUtility.FromJson<Data>(json) ?? new Data();
                _cacheMtime = mtime;
                return _cache;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIStudio] endpoint.json read failed: {ex.Message}");
                return _cache ?? new Data();
            }
        }

        public static void Write(string remoteBaseUrl, string instanceId = null)
        {
            var existing = Read();
            var sanitized = (remoteBaseUrl ?? string.Empty).TrimEnd('/');
            if (existing.remoteBaseUrl == sanitized &&
                (instanceId == null || existing.instanceId == instanceId))
                return;

            var data = new Data
            {
                remoteBaseUrl = sanitized,
                instanceId = instanceId ?? existing.instanceId,
                updatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FullPath));
                File.WriteAllText(FullPath, JsonUtility.ToJson(data, prettyPrint: true));
                _cache = data;
                _cacheMtime = File.GetLastWriteTimeUtc(FullPath);
                Changed?.Invoke(data);

                AssetDatabase.ImportAsset(RelativePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIStudio] endpoint.json write failed: {ex.Message}");
            }
        }

        public static void Clear()
        {
            if (File.Exists(FullPath))
            {
                File.Delete(FullPath);
                AssetDatabase.DeleteAsset(RelativePath);
            }
            _cache = new Data();
            _cacheMtime = DateTime.MinValue;
            Changed?.Invoke(_cache);
        }
    }
}
