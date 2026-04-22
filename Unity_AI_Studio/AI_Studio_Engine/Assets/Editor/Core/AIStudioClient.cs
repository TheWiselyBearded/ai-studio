using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AIStudio.Core
{
    /// Shared HttpClient for all AI Studio editor windows. A single instance
    /// avoids the socket-exhaustion issues that come from per-call `new HttpClient()`,
    /// and centralizes the X-AI-Studio-Token header that the Flask server's
    /// --auth-token flag enforces.
    ///
    /// Callers build an HttpRequestMessage via CreateRequest(...) and POST it via
    /// Http.SendAsync(...). Timeouts are per-call via CancellationTokenSource.
    public static class AIStudioClient
    {
        public const string AuthHeader = "X-AI-Studio-Token";

        private static readonly Lazy<HttpClient> _http = new Lazy<HttpClient>(() =>
        {
            var handler = new HttpClientHandler();
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            return client;
        });

        public static HttpClient Http => _http.Value;

        /// Build an HttpRequestMessage against the active endpoint, stamping the
        /// auth token header when one is configured.
        public static HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, AIStudioSettings.BuildUrl(relativePath));
            StampAuth(request);
            return request;
        }

        /// Build an HttpRequestMessage against a caller-supplied absolute URL
        /// (used when the request shouldn't follow the active endpoint config,
        /// e.g. polling an instance's IP directly before the tunnel URL is known).
        public static HttpRequestMessage CreateRequestAbsolute(HttpMethod method, string absoluteUrl)
        {
            var request = new HttpRequestMessage(method, absoluteUrl);
            StampAuth(request);
            return request;
        }

        public static CancellationTokenSource TimeoutCts(TimeSpan timeout)
        {
            return new CancellationTokenSource(timeout);
        }

        /// Throws if the response is non-2xx. When the Flask server includes
        /// `recent_logs` in its JSON error body (added by _log_enriched_error),
        /// dumps the tail as a separate Debug.LogError so it's one click away
        /// in the Unity console instead of mashed into a single toast. The
        /// thrown Exception carries only the short "error" field so the UI
        /// stays legible.
        public static async Task EnsureSuccessAsync(HttpResponseMessage response, string context = null)
        {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync();
            var msg = ExtractErrorMessage(body) ?? body;
            var logs = ExtractField(body, "recent_logs");
            var logsService = ExtractField(body, "logs_service");
            if (!string.IsNullOrEmpty(logs))
            {
                var hdr = !string.IsNullOrEmpty(context) ? $"[{context}] " : "";
                Debug.LogError($"{hdr}Server logs ({logsService ?? "?"} tail)\n{logs}");
            }
            throw new Exception($"API returned {(int)response.StatusCode} {response.StatusCode}: {msg}");
        }

        private static string ExtractErrorMessage(string body)
        {
            var err = ExtractField(body, "error");
            return string.IsNullOrEmpty(err) ? null : err;
        }

        private static readonly Regex _fieldRe = new Regex(@"""{0}""\s*:\s*""((?:\\.|[^""\\])*)""", RegexOptions.Singleline);

        /// Best-effort JSON field reader. Intentionally doesn't pull in Newtonsoft
        /// here so the core DLL stays dependency-free; the server's response
        /// shape is flat enough that a regex handles it. Handles escaped newlines.
        private static string ExtractField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = new Regex(@"""" + Regex.Escape(field) + @"""\s*:\s*""((?:\\.|[^""\\])*)""", RegexOptions.Singleline).Match(json);
            if (!m.Success) return null;
            return Regex.Unescape(m.Groups[1].Value);
        }

        private static void StampAuth(HttpRequestMessage request)
        {
            var token = AIStudioSettings.AuthToken;
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Remove(AuthHeader);
                request.Headers.Add(AuthHeader, token);
            }
        }
    }
}
