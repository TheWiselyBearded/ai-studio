using UnityEditor;
using UnityEngine;
using System;

namespace AIStudio.Lambda
{
    /// Persistent state for the currently-tracked Lambda instance. Survives
    /// domain reloads (script recompiles) so uptime/cost readouts don't reset.
    /// Stored in EditorPrefs, never in the repo.
    public static class LambdaInstanceState
    {
        private const string Prefix = "AIStudio.Lambda.";
        private const string KeyInstanceId = Prefix + "InstanceId";
        private const string KeyLaunchedAt = Prefix + "LaunchedAtUnix";
        private const string KeyPriceCents = Prefix + "PriceCentsPerHour";
        private const string KeyInstanceType = Prefix + "InstanceTypeName";
        private const string KeyPublicIp = Prefix + "PublicIp";
        private const string KeyRegion = Prefix + "RegionName";
        private const string KeyTunnelUrl = Prefix + "TunnelUrl";
        private const string KeyFileSystemId = Prefix + "FileSystemId";

        public static event Action Changed;

        public static bool HasActiveInstance => !string.IsNullOrEmpty(ActiveInstanceId);

        public static string ActiveInstanceId
        {
            get => EditorPrefs.GetString(KeyInstanceId, string.Empty);
            set { EditorPrefs.SetString(KeyInstanceId, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static long LaunchedAtUnix
        {
            get => long.TryParse(EditorPrefs.GetString(KeyLaunchedAt, "0"), out var v) ? v : 0;
            set { EditorPrefs.SetString(KeyLaunchedAt, value.ToString()); Changed?.Invoke(); }
        }

        /// Stored as integer cents to sidestep float precision issues.
        public static int PriceCentsPerHour
        {
            get => EditorPrefs.GetInt(KeyPriceCents, 0);
            set { EditorPrefs.SetInt(KeyPriceCents, value); Changed?.Invoke(); }
        }

        public static string InstanceTypeName
        {
            get => EditorPrefs.GetString(KeyInstanceType, string.Empty);
            set { EditorPrefs.SetString(KeyInstanceType, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string PublicIp
        {
            get => EditorPrefs.GetString(KeyPublicIp, string.Empty);
            set { EditorPrefs.SetString(KeyPublicIp, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string RegionName
        {
            get => EditorPrefs.GetString(KeyRegion, string.Empty);
            set { EditorPrefs.SetString(KeyRegion, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string TunnelUrl
        {
            get => EditorPrefs.GetString(KeyTunnelUrl, string.Empty);
            set { EditorPrefs.SetString(KeyTunnelUrl, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static string FileSystemId
        {
            get => EditorPrefs.GetString(KeyFileSystemId, string.Empty);
            set { EditorPrefs.SetString(KeyFileSystemId, value ?? string.Empty); Changed?.Invoke(); }
        }

        public static TimeSpan Uptime
        {
            get
            {
                var launched = LaunchedAtUnix;
                if (launched == 0) return TimeSpan.Zero;
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return TimeSpan.FromSeconds(Math.Max(0, nowUnix - launched));
            }
        }

        /// Rolling cost estimate in USD.
        public static double EstimatedCostUsd
        {
            get
            {
                var cents = PriceCentsPerHour;
                if (cents <= 0) return 0.0;
                var hours = Uptime.TotalHours;
                return cents * hours / 100.0;
            }
        }

        public static void Clear()
        {
            EditorPrefs.DeleteKey(KeyInstanceId);
            EditorPrefs.DeleteKey(KeyLaunchedAt);
            EditorPrefs.DeleteKey(KeyPriceCents);
            EditorPrefs.DeleteKey(KeyInstanceType);
            EditorPrefs.DeleteKey(KeyPublicIp);
            EditorPrefs.DeleteKey(KeyRegion);
            EditorPrefs.DeleteKey(KeyTunnelUrl);
            Changed?.Invoke();
        }
    }
}
