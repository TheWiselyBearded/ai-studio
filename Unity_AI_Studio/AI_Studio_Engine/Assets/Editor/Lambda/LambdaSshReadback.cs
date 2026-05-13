using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using AIStudio.Core;
using Debug = UnityEngine.Debug;

namespace AIStudio.Lambda
{
    /// Reads the Cloudflare tunnel URL back from a launched instance.
    ///
    /// bootstrap.sh writes the URL to /var/ai-studio/tunnel.url once cloudflared
    /// prints its trycloudflare hostname, and flips /var/ai-studio/ready once
    /// both the Flask server and the tunnel are up. This helper shells out to
    /// OpenSSH (available natively on Win10+, macOS, Linux) and polls for both.
    public static class LambdaSshReadback
    {
        public class TunnelReadResult
        {
            public bool Success;
            public string TunnelUrl;
            public string Error;
        }

        /// Poll the instance until both /var/ai-studio/ready exists and tunnel.url
        /// is non-empty. Returns the tunnel URL, or an error description.
        /// Quick non-blocking fetch of the current tunnel URL. Used by the
        /// periodic refresh button / window refresh. Returns empty Success=false
        /// if bootstrap isn't ready yet rather than polling.
        public static async Task<TunnelReadResult> FetchCurrentTunnelAsync(
            string publicIp,
            string sshPrivateKeyPath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(publicIp))
                return new TunnelReadResult { Success = false, Error = "Instance has no public IP." };
            if (string.IsNullOrWhiteSpace(sshPrivateKeyPath) || !File.Exists(sshPrivateKeyPath))
                return new TunnelReadResult { Success = false, Error = "SSH private key not configured." };

            // Wrap the read in an `if -f … else echo SENTINEL` so the inner
            // shell exits 0 whether or not the file is on disk. That keeps the
            // exit-code channel reserved exclusively for ssh-level failures
            // (auth, network, host down) — otherwise a missing file looks
            // identical to a dropped connection.
            const string remoteCmd =
                "if [ -f /var/ai-studio/tunnel.url ]; then cat /var/ai-studio/tunnel.url; " +
                "else echo __AISTUDIO_MISSING__; fi";

            var probe = await RunSshAsync(
                publicIp, sshPrivateKeyPath, remoteCmd,
                TimeSpan.FromSeconds(12), ct);

            if (probe.ExitCode != 0)
            {
                var sshErr = string.IsNullOrWhiteSpace(probe.Stderr)
                    ? $"ssh exited {probe.ExitCode} with no message."
                    : probe.Stderr.Trim();
                return new TunnelReadResult { Success = false, Error = "SSH to instance failed: " + sshErr };
            }

            var stdout = probe.Stdout?.Trim() ?? string.Empty;

            if (stdout.StartsWith("__AISTUDIO_MISSING__"))
                return new TunnelReadResult
                {
                    Success = false,
                    Error = "Bootstrap hasn't written /var/ai-studio/tunnel.url yet. " +
                            "First-time bootstrap takes ~25–35 minutes; " +
                            "tail /var/log/ai-studio-user-data.log on the instance for progress."
                };

            if (stdout.StartsWith("https://"))
                return new TunnelReadResult { Success = true, TunnelUrl = stdout };

            return new TunnelReadResult
            {
                Success = false,
                Error = "tunnel.url contents look wrong (expected an https:// URL, got: " +
                        Truncate(stdout, 200) + ")"
            };
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        public static async Task<TunnelReadResult> WaitForTunnelAsync(
            string publicIp,
            string sshPrivateKeyPath,
            TimeSpan overallTimeout,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(publicIp))
                return new TunnelReadResult { Success = false, Error = "Instance has no public IP yet." };
            if (string.IsNullOrWhiteSpace(sshPrivateKeyPath))
                return new TunnelReadResult { Success = false, Error = "SSH private key path is not configured in AI Studio/Settings." };
            if (!File.Exists(sshPrivateKeyPath))
                return new TunnelReadResult { Success = false, Error = $"SSH private key not found: {sshPrivateKeyPath}" };

            var deadline = DateTime.UtcNow + overallTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                var probe = await RunSshAsync(
                    publicIp, sshPrivateKeyPath,
                    "test -f /var/ai-studio/ready && cat /var/ai-studio/tunnel.url",
                    TimeSpan.FromSeconds(15), ct);

                if (probe.ExitCode == 0)
                {
                    var url = probe.Stdout?.Trim();
                    if (!string.IsNullOrEmpty(url) && url.StartsWith("https://"))
                        return new TunnelReadResult { Success = true, TunnelUrl = url };
                }

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            return new TunnelReadResult { Success = false, Error = "Timed out waiting for bootstrap to finish." };
        }

        public class SshResult
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
        }

        public static Task<SshResult> RunSshAsync(
            string host,
            string privateKeyPath,
            string remoteCommand,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<SshResult>();

            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(privateKeyPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ConnectTimeout=10");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add($"ubuntu@{host}");
            psi.ArgumentList.Add(remoteCommand);

            Process proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new SshResult { ExitCode = -1, Stderr = $"Could not start ssh: {ex.Message}" });
                return tcs.Task;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Timeout + cancellation watcher on a thread-pool thread.
            _ = Task.Run(async () =>
            {
                var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchCts.CancelAfter(timeout);
                try
                {
                    while (!proc.HasExited)
                    {
                        if (watchCts.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            break;
                        }
                        await Task.Delay(100, CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    proc.WaitForExit();
                    tcs.TrySetResult(new SshResult
                    {
                        ExitCode = proc.ExitCode,
                        Stdout = stdout.ToString(),
                        Stderr = stderr.ToString(),
                    });
                    proc.Dispose();
                }
            });

            return tcs.Task;
        }
    }
}
