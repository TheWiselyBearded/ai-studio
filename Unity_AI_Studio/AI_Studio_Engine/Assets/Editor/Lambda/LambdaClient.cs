using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AIStudio.Core;

namespace AIStudio.Lambda
{
    /// Thin wrapper around Lambda Cloud's REST API (https://cloud.lambda.ai/api/v1).
    /// Auth via Bearer token (the API also accepts Basic; Bearer is simpler).
    /// All Lambda success responses are wrapped as {"data": <payload>}; errors
    /// are {"error": {"code", "message", "suggestion"}}. This client unwraps both.
    public static class LambdaClient
    {
        public const string BaseUrl = "https://cloud.lambda.ai/api/v1";

        public class LambdaApiException : Exception
        {
            public int StatusCode { get; }
            public string Code { get; }
            public string Suggestion { get; }
            public LambdaApiException(int status, string code, string message, string suggestion)
                : base(message)
            {
                StatusCode = status;
                Code = code;
                Suggestion = suggestion;
            }
        }

        private static HttpRequestMessage Build(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, BaseUrl + path);
            var apiKey = AIStudioSettings.LambdaApiKey;
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("Lambda API key is not set. Configure it in AI Studio/Settings.");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            return req;
        }

        private static async Task<JToken> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await AIStudioClient.Http.SendAsync(req, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new LambdaApiException(0, "network", ex.Message, null);
            }

            string body = await resp.Content.ReadAsStringAsync();
            JObject parsed;
            try { parsed = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body); }
            catch (JsonReaderException)
            {
                throw new LambdaApiException((int)resp.StatusCode, "parse", $"Non-JSON response: {body}", null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var err = parsed["error"];
                throw new LambdaApiException(
                    (int)resp.StatusCode,
                    err?["code"]?.ToString() ?? "unknown",
                    err?["message"]?.ToString() ?? body,
                    err?["suggestion"]?.ToString());
            }

            return parsed["data"] ?? parsed;
        }

        // ---------------- Instance types ----------------

        public class RegionAvailability
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("description")] public string Description;
        }

        public class InstanceTypeSpec
        {
            [JsonProperty("gpus")] public int Gpus;
            [JsonProperty("memory_gib")] public int MemoryGib;
            [JsonProperty("vcpus")] public int Vcpus;
            [JsonProperty("storage_gib")] public int StorageGib;
        }

        public class InstanceType
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("price_cents_per_hour")] public int PriceCentsPerHour;
            [JsonProperty("description")] public string Description;
            [JsonProperty("specs")] public InstanceTypeSpec Specs;
            [JsonProperty("gpu_description")] public string GpuDescription;
            public List<RegionAvailability> AvailableRegions = new List<RegionAvailability>();
        }

        public static async Task<List<InstanceType>> ListInstanceTypesAsync(CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Get, "/instance-types");
            var data = await SendAsync(req, ct) as JObject ?? new JObject();
            var list = new List<InstanceType>();
            foreach (var prop in data.Properties())
            {
                var entry = prop.Value as JObject;
                if (entry == null) continue;
                var it = entry["instance_type"]?.ToObject<InstanceType>() ?? new InstanceType { Name = prop.Name };
                if (string.IsNullOrEmpty(it.Name)) it.Name = prop.Name;
                var regions = entry["regions_with_capacity_available"] as JArray;
                if (regions != null)
                {
                    foreach (var r in regions)
                        it.AvailableRegions.Add(r.ToObject<RegionAvailability>());
                }
                list.Add(it);
            }
            list.Sort((a, b) => a.PriceCentsPerHour.CompareTo(b.PriceCentsPerHour));
            return list;
        }

        // ---------------- Instances ----------------

        public class Instance
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("status")] public string Status;
            [JsonProperty("ip")] public string Ip;
            [JsonProperty("region")] public RegionAvailability Region;
            [JsonProperty("instance_type")] public InstanceType InstanceType;
            [JsonProperty("ssh_key_names")] public List<string> SshKeyNames;
            [JsonProperty("file_system_names")] public List<string> FileSystemNames;
        }

        public static async Task<List<Instance>> ListInstancesAsync(CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Get, "/instances");
            var arr = await SendAsync(req, ct) as JArray;
            return arr?.ToObject<List<Instance>>() ?? new List<Instance>();
        }

        public static async Task<Instance> GetInstanceAsync(string id, CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Get, $"/instances/{id}");
            var obj = await SendAsync(req, ct);
            return obj?.ToObject<Instance>();
        }

        // ---------------- Launch / Terminate ----------------

        public class LaunchRequest
        {
            [JsonProperty("region_name")] public string RegionName;
            [JsonProperty("instance_type_name")] public string InstanceTypeName;
            [JsonProperty("ssh_key_names")] public List<string> SshKeyNames;
            [JsonProperty("file_system_names", NullValueHandling = NullValueHandling.Ignore)] public List<string> FileSystemNames;
            [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name;
            [JsonProperty("user_data", NullValueHandling = NullValueHandling.Ignore)] public string UserData;
            [JsonProperty("quantity", NullValueHandling = NullValueHandling.Ignore)] public int? Quantity;
        }

        public static async Task<List<string>> LaunchAsync(LaunchRequest body, CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Post, "/instance-operations/launch");
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            var obj = await SendAsync(req, ct) as JObject;
            var ids = obj?["instance_ids"] as JArray;
            return ids?.ToObject<List<string>>() ?? new List<string>();
        }

        public static async Task<List<Instance>> TerminateAsync(IEnumerable<string> instanceIds, CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Post, "/instance-operations/terminate");
            var payload = new Dictionary<string, object> { ["instance_ids"] = new List<string>(instanceIds) };
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var obj = await SendAsync(req, ct) as JObject;
            var terminated = obj?["terminated_instances"] as JArray;
            return terminated?.ToObject<List<Instance>>() ?? new List<Instance>();
        }

        // ---------------- SSH keys ----------------

        public class SshKey
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("public_key")] public string PublicKey;
        }

        public static async Task<List<SshKey>> ListSshKeysAsync(CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Get, "/ssh-keys");
            var arr = await SendAsync(req, ct) as JArray;
            return arr?.ToObject<List<SshKey>>() ?? new List<SshKey>();
        }

        // ---------------- File systems ----------------

        public class FileSystem
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("region")] public RegionAvailability Region;
            [JsonProperty("mount_point", NullValueHandling = NullValueHandling.Ignore)] public string MountPoint;
        }

        public static async Task<List<FileSystem>> ListFileSystemsAsync(CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Get, "/file-systems");
            var arr = await SendAsync(req, ct) as JArray;
            return arr?.ToObject<List<FileSystem>>() ?? new List<FileSystem>();
        }

        public static async Task<FileSystem> CreateFileSystemAsync(string name, string regionName, CancellationToken ct = default)
        {
            using var req = Build(HttpMethod.Post, "/file-systems");
            var payload = new Dictionary<string, object>
            {
                ["name"] = name,
                ["region"] = new Dictionary<string, object> { ["name"] = regionName },
            };
            req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var obj = await SendAsync(req, ct);
            return obj?.ToObject<FileSystem>();
        }
    }
}
