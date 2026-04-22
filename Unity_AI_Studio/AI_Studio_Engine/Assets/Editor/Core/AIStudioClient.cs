using System;
using System.Net.Http;
using System.Threading;

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
