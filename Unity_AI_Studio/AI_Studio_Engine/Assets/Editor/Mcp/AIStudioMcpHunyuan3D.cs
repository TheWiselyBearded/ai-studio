using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AIStudio.Core;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// MCP tool wrapping the Hunyuan 3D 2.1 PBR ComfyUI pipeline.
    /// Mirrors Hunyuan3DGeneratorWindow's multipart request against /generate/3d.
    public static class AIStudioMcpHunyuan3D
    {
        public class GenerateHunyuan3DParams
        {
            [McpDescription(
                "Path to reference image. Absolute filesystem path or 'Assets/...'-relative.",
                Required = true)]
            public string InputImagePath { get; set; }

            [McpDescription("Project-relative folder under Assets/ to save into")]
            public string SavePath { get; set; } = "Assets/Generated3D";
        }

        [McpTool(
            "AIStudio_GenerateHunyuan3D",
            "Generate a textured 3D model (.glb) from a reference image via the " +
            "AI Studio Hunyuan 3D 2.1 ComfyUI pipeline. Long-running (often 1-5 min).",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateHunyuan3D(GenerateHunyuan3DParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            string imagePath = AIStudioMcpAssetWriter.ResolveInputPath(p.InputImagePath, nameof(p.InputImagePath));
            string fullDir = AIStudioMcpAssetWriter.ResolveOutputFolder(p.SavePath, "Assets/Generated3D");

            var form = new MultipartFormDataContent();
            byte[] fileBytes = File.ReadAllBytes(imagePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "image", Path.GetFileName(imagePath));

            const string apiPath = "/generate/3d";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, apiPath);
            request.Content = form;
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(10));

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateHunyuan3D -> {AIStudioSettings.BuildUrl(apiPath)} ({fileBytes.Length} bytes)");

            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
            }

            string outputName = AIStudioMcpAssetWriter.FilenameFromResponse(response, "generated_model.glb");
            byte[] glbBytes = await response.Content.ReadAsByteArrayAsync();
            string savePath = AIStudioMcpAssetWriter.DedupedSavePath(fullDir, outputName);
            var (assetsPath, byteCount) = AIStudioMcpAssetWriter.WriteAndImport(savePath, glbBytes);

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateHunyuan3D -> {assetsPath} ({byteCount} bytes)");

            return new
            {
                success = true,
                asset_path = assetsPath,
                bytes = byteCount,
                endpoint = AIStudioSettings.BuildUrl(apiPath)
            };
        }
    }
}
