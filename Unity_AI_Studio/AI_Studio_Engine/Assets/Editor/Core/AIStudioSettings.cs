using UnityEditor;
using UnityEngine;
using System;

namespace AIStudio.Core
{
    public enum EndpointMode
    {
        Local,
        Remote
    }

    /// Persistent editor settings for the AI Studio toolchain.
    /// Values live in EditorPrefs (per-user, never in the repo).
    public static class AIStudioSettings
    {
        private const string PrefPrefix = "AIStudio.";
        private const string KeyLocalBase = PrefPrefix + "LocalBaseUrl";
        private const string KeyRemoteBase = PrefPrefix + "RemoteBaseUrl";
        private const string KeyMode = PrefPrefix + "ActiveMode";
        private const string KeyAuthToken = PrefPrefix + "AuthToken";
        private const string KeyLambdaApiKey = PrefPrefix + "LambdaApiKey";
        private const string KeySshKeyName = PrefPrefix + "SshKeyName";
        private const string KeySshPrivateKeyPath = PrefPrefix + "SshPrivateKeyPath";
        private const string KeyMaxSessionCost = PrefPrefix + "MaxSessionCostUsd";
        private const string KeyMaxSessionHours = PrefPrefix + "MaxSessionHours";
        private const string KeyHfToken = PrefPrefix + "HfToken";

        public const string DefaultLocalBaseUrl = "http://127.0.0.1:5001";
        public const float DefaultMaxSessionCostUsd = 5.0f;
        public const float DefaultMaxSessionHours = 15.0f;

        public static event Action Changed;

        public static string LocalBaseUrl
        {
            get => EditorPrefs.GetString(KeyLocalBase, DefaultLocalBaseUrl);
            set { EditorPrefs.SetString(KeyLocalBase, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string RemoteBaseUrl
        {
            get
            {
                // Project-local manifest is source of truth when present, so multiple
                // developers / tabs see the same endpoint. Fall back to EditorPrefs.
                var manifest = AIStudioEndpointManifest.Read();
                if (!string.IsNullOrEmpty(manifest.remoteBaseUrl))
                    return manifest.remoteBaseUrl;
                return EditorPrefs.GetString(KeyRemoteBase, string.Empty);
            }
            set
            {
                EditorPrefs.SetString(KeyRemoteBase, value ?? string.Empty);
                AIStudioEndpointManifest.Write(value ?? string.Empty);
                Changed?.Invoke();
            }
        }

        public static EndpointMode ActiveMode
        {
            get => (EndpointMode)EditorPrefs.GetInt(KeyMode, (int)EndpointMode.Local);
            set { EditorPrefs.SetInt(KeyMode, (int)value); Changed?.Invoke(); }
        }

        public static string AuthToken
        {
            get => EditorPrefs.GetString(KeyAuthToken, string.Empty);
            set { EditorPrefs.SetString(KeyAuthToken, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string LambdaApiKey
        {
            get => EditorPrefs.GetString(KeyLambdaApiKey, string.Empty);
            set { EditorPrefs.SetString(KeyLambdaApiKey, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string SshKeyName
        {
            get => EditorPrefs.GetString(KeySshKeyName, string.Empty);
            set { EditorPrefs.SetString(KeySshKeyName, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string SshPrivateKeyPath
        {
            get => EditorPrefs.GetString(KeySshPrivateKeyPath, string.Empty);
            set { EditorPrefs.SetString(KeySshPrivateKeyPath, value ?? string.Empty); Changed?.Invoke(); }
        }

        // HF_TOKEN: threaded through user_data so gated models (DINOv3, BiRefNet,
        // some Llama weights) auto-fetch on the instance. Falls back to the
        // editor process env var if the EditorPref is empty.
        public static string HuggingFaceToken
        {
            get
            {
                var pref = EditorPrefs.GetString(KeyHfToken, string.Empty);
                if (!string.IsNullOrEmpty(pref)) return pref;
                return System.Environment.GetEnvironmentVariable("HF_TOKEN")
                       ?? System.Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_TOKEN")
                       ?? string.Empty;
            }
            set { EditorPrefs.SetString(KeyHfToken, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static float MaxSessionCostUsd
        {
            get => EditorPrefs.GetFloat(KeyMaxSessionCost, DefaultMaxSessionCostUsd);
            set { EditorPrefs.SetFloat(KeyMaxSessionCost, value); Changed?.Invoke(); }
        }

        public static float MaxSessionHours
        {
            get => EditorPrefs.GetFloat(KeyMaxSessionHours, DefaultMaxSessionHours);
            set { EditorPrefs.SetFloat(KeyMaxSessionHours, Mathf.Max(0f, value)); Changed?.Invoke(); }
        }

        public static string ActiveBaseUrl
        {
            get
            {
                var url = ActiveMode == EndpointMode.Remote ? RemoteBaseUrl : LocalBaseUrl;
                return string.IsNullOrWhiteSpace(url) ? DefaultLocalBaseUrl : url.TrimEnd('/');
            }
        }

        public static string BuildUrl(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return ActiveBaseUrl;
            return ActiveBaseUrl + (relativePath.StartsWith("/") ? relativePath : "/" + relativePath);
        }
    }
}
