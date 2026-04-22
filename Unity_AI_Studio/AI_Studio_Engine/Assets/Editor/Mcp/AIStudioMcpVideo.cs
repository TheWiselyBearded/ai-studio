using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AIStudio.Core;
using Newtonsoft.Json.Linq;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIStudio.Mcp
{
    /// MCP tool wrapping the AI Studio video-generation job pipeline (Wan2 / Veo / Runway).
    /// Submits a job, polls until complete, downloads the result, imports to Assets/.
    /// Blocks the MCP call for the duration of the job (capped by MaxWaitMinutes).
    public static class AIStudioMcpVideo
    {
        public class GenerateVideoParams
        {
            [McpDescription(
                "Path to reference image (absolute or 'Assets/...').", Required = true)]
            public string InputImagePath { get; set; }

            [McpDescription("Text prompt describing the desired motion", Required = true)]
            public string Prompt { get; set; }

            [McpDescription(
                "Provider: 'wan2' (local ComfyUI), 'veo' (Google), or 'runway' (Gen-4 Turbo)",
                Default = "wan2")]
            public string Provider { get; set; } = "wan2";

            // ---- Wan2-only ----
            [McpDescription("Wan2: video width in pixels", Default = 640)]
            public int Width { get; set; } = 640;

            [McpDescription("Wan2: video height in pixels", Default = 640)]
            public int Height { get; set; } = 640;

            [McpDescription("Wan2: number of frames (49-601)", Default = 301)]
            public int Length { get; set; } = 301;

            [McpDescription("Wan2: enable 4-step LoRA (faster, lower quality)", Default = true)]
            public bool Enable4StepLora { get; set; } = true;

            [McpDescription("Wan2: diffusion steps (used when 4-step LoRA disabled)", Default = 20)]
            public int Steps { get; set; } = 20;

            [McpDescription("Wan2: CFG scale (used when 4-step LoRA disabled)", Default = 3.5)]
            public float Cfg { get; set; } = 3.5f;

            [McpDescription("Wan2: random seed; -1 picks one server-side", Default = -1)]
            public int Seed { get; set; } = -1;

            [McpDescription("Wan2: optional negative prompt; empty for server default")]
            public string NegativePrompt { get; set; } = "";

            // ---- Runway-only ----
            [McpDescription("Runway: aspect ratio ('16:9', '9:16', '1:1')", Default = "16:9")]
            public string Ratio { get; set; } = "16:9";

            [McpDescription("Runway: duration in seconds (5 or 10)", Default = 10)]
            public int Duration { get; set; } = 10;

            // ---- Output / waiting ----
            [McpDescription("Project-relative folder under Assets/ to save into")]
            public string SavePath { get; set; } = "Assets/GeneratedVideos";

            [McpDescription(
                "Maximum minutes to wait for job completion before timing out. " +
                "Long Wan2 videos can take 10+ min on cloud GPU.", Default = 30)]
            public int MaxWaitMinutes { get; set; } = 30;
        }

        [McpTool(
            "AIStudio_GenerateVideo",
            "Submit a video-generation job (Wan2 / Veo / Runway), poll until complete, " +
            "and import the result into Assets/. Blocks for the duration of the job " +
            "(capped by MaxWaitMinutes). On timeout the job remains running on the server.",
            EnabledByDefault = true,
            Groups = new[] { "AI Studio" })]
        public static async Task<object> GenerateVideo(GenerateVideoParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.Prompt)) throw new ArgumentException("Prompt is required");

            string imagePath = AIStudioMcpAssetWriter.ResolveInputPath(p.InputImagePath, nameof(p.InputImagePath));
            string fullDir = AIStudioMcpAssetWriter.ResolveOutputFolder(p.SavePath, "Assets/GeneratedVideos");

            string provider = string.IsNullOrWhiteSpace(p.Provider)
                ? "wan2"
                : p.Provider.Trim().ToLowerInvariant();
            if (provider != "wan2" && provider != "veo" && provider != "runway")
                throw new ArgumentException($"Provider must be one of: wan2, veo, runway (got '{p.Provider}')");

            // 1) Submit
            string jobId = await SubmitJobAsync(imagePath, provider, p);
            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVideo submitted job {jobId} (provider={provider})");

            // 2) Poll
            var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, p.MaxWaitMinutes));
            string lastStatus = "pending";
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(5000);
                lastStatus = await CheckJobStatusAsync(jobId);
                if (lastStatus == "complete") break;
                if (lastStatus == "error" || lastStatus == "cancelled")
                    throw new Exception($"Video job {jobId} ended with status '{lastStatus}'");
            }

            if (lastStatus != "complete")
                throw new TimeoutException(
                    $"Video job {jobId} did not complete within {p.MaxWaitMinutes} min (last status: {lastStatus}). " +
                    $"Job is still running server-side; results may become available later.");

            // 3) Download
            var (assetsPath, byteCount, outputName) = await DownloadJobResultAsync(jobId, fullDir);
            Debug.Log($"[AIStudio MCP] AIStudio_GenerateVideo -> {assetsPath} ({byteCount} bytes)");

            return new
            {
                success = true,
                job_id = jobId,
                provider,
                asset_path = assetsPath,
                file_name = outputName,
                bytes = byteCount
            };
        }

        // ----- helpers -----

        private static async Task<string> SubmitJobAsync(string imagePath, string provider, GenerateVideoParams p)
        {
            var form = new MultipartFormDataContent();

            byte[] fileBytes = File.ReadAllBytes(imagePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "image", Path.GetFileName(imagePath));
            form.Add(new StringContent(provider), "provider");
            form.Add(new StringContent(p.Prompt), "prompt");

            switch (provider)
            {
                case "wan2":
                    form.Add(new StringContent(p.Width.ToString()), "width");
                    form.Add(new StringContent(p.Height.ToString()), "height");
                    form.Add(new StringContent(p.Length.ToString()), "length");
                    form.Add(new StringContent(p.Enable4StepLora ? "true" : "false"), "enable_4step_lora");
                    form.Add(new StringContent(p.Steps.ToString()), "steps");
                    form.Add(new StringContent(p.Cfg.ToString("F1", CultureInfo.InvariantCulture)), "cfg");
                    if (p.Seed != -1)
                        form.Add(new StringContent(p.Seed.ToString()), "seed");
                    if (!string.IsNullOrWhiteSpace(p.NegativePrompt))
                        form.Add(new StringContent(p.NegativePrompt), "negative_prompt");
                    break;

                case "runway":
                    form.Add(new StringContent(p.Ratio ?? "16:9"), "ratio");
                    form.Add(new StringContent(p.Duration.ToString()), "duration");
                    break;

                // veo: no extra fields
            }

            const string submitPath = "/jobs/submit/video";
            using var request = AIStudioClient.CreateRequest(HttpMethod.Post, submitPath);
            request.Content = form;
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(2));
            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Submit failed ({(int)response.StatusCode}): {body}");

            var parsed = JObject.Parse(body);
            string jobId = parsed["job_id"]?.ToString();
            if (string.IsNullOrEmpty(jobId)) throw new Exception($"No job_id in response: {body}");
            return jobId;
        }

        private static async Task<string> CheckJobStatusAsync(string jobId)
        {
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, $"/jobs/{jobId}/status");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromSeconds(30));
            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Status check failed ({(int)response.StatusCode}): {body}");
            return JObject.Parse(body)["status"]?.ToString() ?? "unknown";
        }

        private static async Task<(string assetsPath, int byteCount, string fileName)> DownloadJobResultAsync(
            string jobId, string fullDir)
        {
            using var request = AIStudioClient.CreateRequest(HttpMethod.Get, $"/jobs/{jobId}/result");
            using var cts = AIStudioClient.TimeoutCts(TimeSpan.FromMinutes(5));
            using var response = await AIStudioClient.Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Download failed ({(int)response.StatusCode}): {err}");
            }
            string outputName = AIStudioMcpAssetWriter.FilenameFromResponse(response, "generated_video.mp4");
            byte[] videoBytes = await response.Content.ReadAsByteArrayAsync();
            string savePath = AIStudioMcpAssetWriter.DedupedSavePath(fullDir, outputName);
            var (assetsPath, byteCount) = AIStudioMcpAssetWriter.WriteAndImport(savePath, videoBytes);
            return (assetsPath, byteCount, outputName);
        }
    }
}
