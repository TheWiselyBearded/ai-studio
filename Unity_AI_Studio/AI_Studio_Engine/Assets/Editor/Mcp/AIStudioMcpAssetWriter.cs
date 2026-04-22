using System;
using System.IO;
using System.Net.Http;
using AIStudio.Core;
using UnityEditor;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// Shared helpers for AI Studio MCP tools: Assets/-relative path resolution
    /// (with traversal guard), dedup-by-suffix filename, write+refresh+import.
    internal static class AIStudioMcpAssetWriter
    {
        public static string ResolveOutputFolder(string assetsRelativeFolder, string defaultFolder)
        {
            string folder = string.IsNullOrWhiteSpace(assetsRelativeFolder)
                ? defaultFolder
                : assetsRelativeFolder.Replace('\\', '/').TrimEnd('/');

            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException($"savePath must be under 'Assets/' (got '{folder}')");

            string sub = folder == "Assets" ? string.Empty : folder.Substring("Assets/".Length);
            string fullDir = string.IsNullOrEmpty(sub)
                ? Application.dataPath
                : Path.Combine(Application.dataPath, sub);

            // Path-traversal guard: the resolved absolute path must still sit under
            // Application.dataPath after collapsing any ../ segments.
            string canonicalFull = Path.GetFullPath(fullDir);
            string canonicalRoot = Path.GetFullPath(Application.dataPath);
            if (!canonicalFull.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"savePath must remain under Assets/ (got '{folder}')");

            Directory.CreateDirectory(canonicalFull);
            return canonicalFull;
        }

        public static string DedupedSavePath(string fullDir, string fileName)
        {
            string savePath = Path.Combine(fullDir, fileName);
            if (!File.Exists(savePath)) return savePath;

            string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (File.Exists(savePath))
            {
                savePath = Path.Combine(fullDir, $"{nameNoExt}_{counter}{ext}");
                counter++;
            }
            return savePath;
        }

        public static (string assetsPath, int byteCount) WriteAndImport(string fullSavePath, byte[] bytes)
        {
            File.WriteAllBytes(fullSavePath, bytes);
            string assetsRelative = "Assets" + fullSavePath
                .Substring(Application.dataPath.Length)
                .Replace('\\', '/');
            AssetDatabase.Refresh();
            return (assetsRelative, bytes.Length);
        }

        /// Resolve an input file path. Accepts absolute paths or 'Assets/...'-relative paths.
        public static string ResolveInputPath(string path, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"{fieldName} must be provided");

            string resolved;
            if (path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.StartsWith("Assets\\", StringComparison.Ordinal))
                resolved = Path.Combine(Application.dataPath, path.Substring("Assets/".Length));
            else
                resolved = path;

            if (!File.Exists(resolved))
                throw new FileNotFoundException($"{fieldName} not found: {path}");

            return resolved;
        }

        public static string FilenameFromResponse(HttpResponseMessage response, string fallback)
        {
            var fn = response.Content.Headers.ContentDisposition?.FileName;
            return string.IsNullOrEmpty(fn) ? fallback : fn.Trim('"');
        }
    }
}
