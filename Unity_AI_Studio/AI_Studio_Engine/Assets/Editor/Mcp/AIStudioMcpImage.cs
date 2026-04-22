using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AIStudio.Core;
using Newtonsoft.Json;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// MCP tool wrapping the Z-Image Turbo ComfyUI image-generation pipeline.
    /// Mirrors ImageGeneratorWindow's request shape against /generate/image.
    public static class AIStudioMcpImage
    {
        public class GenerateImageParams
        {
            [McpDescription("Text prompt describing the image to generate", Required = true)]
            public string Prompt { get; set; }

            [McpDescription("Image width in pixels", Default = 1024)]
            public int Width { get; set; } = 1024;

            [McpDescription("Image height in pixels", Default = 1024)]
            public int Height { get; set; } = 1024;

            [McpDescription("Diffusion steps (1-20 typical)", Default = 8)]
            public int Steps { get; set; } = 8;

            [McpDescription("Random seed; -1 picks one server-side", Default = -1)]
            public int Seed { get; set; } = -1;

            [McpDescription("Project-relative folder under Assets/ to save into")]
            public string SavePath { get; set; } = "Assets/GeneratedImages";
        }

        [McpTool(
            "AIStudio_GenerateImage",
            "Generate an image via the AI Studio Z-Image Turbo ComfyUI pipeline. " +
            "Returns the project-relative path of the imported asset.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateImage(GenerateImageParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.Prompt))
                throw new ArgumentException("Prompt is required");

            string fullDir = AIStudioMcpAssetWriter.ResolveOutputFolder(p.SavePath, "Assets/GeneratedImages");

            var payload = new { prompt = p.Prompt, width = p.Width, height = p.Height, steps = p.Steps, seed = p.Seed };
            string json = JsonConvert.SerializeObject(payload);

            const string apiPath = "/generate/image";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, apiPath);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(5));

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateImage -> {AIStudioSettings.BuildUrl(apiPath)}");

            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"API returned {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
            }

            string outputName = AIStudioMcpAssetWriter.FilenameFromResponse(response, "generated_image.png");
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            string savePath = AIStudioMcpAssetWriter.DedupedSavePath(fullDir, outputName);
            var (assetsPath, byteCount) = AIStudioMcpAssetWriter.WriteAndImport(savePath, bytes);

            Debug.Log($"[AIStudio MCP] AIStudio_GenerateImage -> {assetsPath} ({byteCount} bytes)");

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
