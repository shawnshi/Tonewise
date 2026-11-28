using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Tonewise.SyncHost.Usb;

public sealed record AdbDeviceInfo(string Serial, string State);

public interface IUsbAdbRunner
{
    string? ResolveAdbPath(string? customPath = null);
    Task TrackDevicesAsync(Action<IReadOnlyList<AdbDeviceInfo>> onDevicesChanged, Action<string>? onStatusLog, CancellationToken ct);
    Task<string?> GetDeviceModelAsync(string serial, CancellationToken ct);
    Task<string?> DetectTonewisePackageAsync(string serial, CancellationToken ct);
    Task<bool> SetupReverseTunnelAsync(string serial, int remotePort, int localPort, CancellationToken ct);
    Task RemoveReverseTunnelAsync(string serial, int remotePort, CancellationToken ct);
    Task<(bool Ok, string Detail)> TriggerPhoneUsbSyncAsync(
        string serial,
        string packageName,
        int port,
        string nonce,
        string hostId,
        bool allowAutoPair,
        CancellationToken ct);
    Task<bool> TriggerPhoneLibraryRescanAsync(string serial, string packageName, string nonce, CancellationToken ct);
    Task<int> DeleteForeignMusicFilesAsync(string serial, IReadOnlyList<string> validatedRelPaths, CancellationToken ct);
    Task<UsbDirectPushSummary> PushFilesDirectAsync(
        string serial,
        IReadOnlyList<UsbDirectPushTrackSpec> tracks,
        IReadOnlyList<UsbDirectPushCoverSpec> covers,
        string? remoteMusicRoot,
        Func<UsbDirectPushProgress, Task>? onProgress,
        CancellationToken ct);
}

public sealed class UsbAdbBridge : IUsbAdbRunner
{
    private const int AdbServerPort = 5037;
    private const string TriggerServiceClass = "com.shawnshi.tonewise.sync.UsbSyncTriggerService";
    private const string ActionStartUsbSync = "com.shawnshi.tonewise.sync.action.START_USB_SYNC";
    private const string ActionRescanLibrary = "com.shawnshi.tonewise.sync.action.RESCAN_LIBRARY";

    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".alac", ".aac", ".wav",
        ".ogg", ".opus", ".aiff", ".aif", ".dsf", ".dff",
        ".ape", ".wv", ".wma",
    };

    public string? CustomAdbPath { get; set; }

    public string? ResolveAdbPath(string? customPath = null)
    {
        string adbExe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

        string? explicitPath = !string.IsNullOrWhiteSpace(customPath) ? customPath.Trim() : CustomAdbPath?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        string[] bundledCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", adbExe),
            Path.Combine(AppContext.BaseDirectory, adbExe),
        ];
        foreach (string candidate in bundledCandidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(rawDir.Trim('"'), adbExe);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // Ignore malformed PATH entries
                }
            }
        }

        foreach (string envVar in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            string? sdkRoot = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(sdkRoot))
            {
                string candidate = Path.Combine(sdkRoot.Trim(), "platform-tools", adbExe);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string sdkDefault = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", adbExe);
            if (File.Exists(sdkDefault)) return Path.GetFullPath(sdkDefault);
        }

        return null;
    }

    public async Task TrackDevicesAsync(
        Action<IReadOnlyList<AdbDeviceInfo>> onDevicesChanged,
        Action<string>? onStatusLog,
        CancellationToken ct)
    {
        int[] backoffMs = [2000, 5000, 10000];
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TrackDevicesSingleConnectionAsync(onDevicesChanged, onStatusLog, ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onDevicesChanged([]);
                string? adbPath = ResolveAdbPath();
                if (adbPath is null)
                {
                    onStatusLog?.Invoke("未检测到 adb.exe（请安装 Android SDK Platform-Tools 或放入 tools\\adb.exe）");
                }
                else
                {
                    onStatusLog?.Invoke($"ADB 设备监听重连中：{ex.Message}");
                }

                int delayMs = backoffMs[Math.Min(attempt, backoffMs.Length - 1)];
                attempt++;
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task TrackDevicesSingleConnectionAsync(
        Action<IReadOnlyList<AdbDeviceInfo>> onDevicesChanged,
        Action<string>? onStatusLog,
        CancellationToken ct)
    {
        using var client = await ConnectToAdbServerWithAutoStartAsync(onStatusLog, ct).ConfigureAwait(false);
        using var reg = ct.Register(() =>
        {
            try { client.Dispose(); } catch { }
        });

        NetworkStream stream = client.GetStream();
        const string cmd = "host:track-devices";
        byte[] cmdBytes = Encoding.ASCII.GetBytes($"{cmd.Length:x4}{cmd}");
        await stream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        byte[] statusBuf = new byte[4];
        await ReadExactlyAsync(stream, statusBuf, ct).ConfigureAwait(false);
        string status = Encoding.ASCII.GetString(statusBuf);
        if (!string.Equals(status, "OKAY", StringComparison.Ordinal))
        {
            string errReason = await ReadLengthPrefixedUtf8Async(stream, ct).ConfigureAwait(false);
            throw new IOException($"ADB host:track-devices rejected ({status}): {errReason}");
        }

        onStatusLog?.Invoke("ADB USB 设备监听已就绪（等待手机插线连接）");

        while (!ct.IsCancellationRequested)
        {
            string payload = await ReadLengthPrefixedUtf8Async(stream, ct).ConfigureAwait(false);
            var devices = ParseTrackDevicesPayload(payload);
            onDevicesChanged(devices);
        }
    }

    internal async Task EnsureAdbServerRunningAsync(CancellationToken ct)
    {
        using var client = await ConnectToAdbServerWithAutoStartAsync(null, ct).ConfigureAwait(false);
    }

    public Task<UsbDirectPushSummary> PushFilesDirectAsync(
        string serial,
        IReadOnlyList<UsbDirectPushTrackSpec> tracks,
        IReadOnlyList<UsbDirectPushCoverSpec> covers,
        string? remoteMusicRoot,
        Func<UsbDirectPushProgress, Task>? onProgress,
        CancellationToken ct)
    {
        var syncClient = new UsbAdbSyncClient(AdbServerPort, EnsureAdbServerRunningAsync);
        return syncClient.PushAsync(serial, tracks, covers, remoteMusicRoot, onProgress, ct);
    }

    private async Task<TcpClient> ConnectToAdbServerWithAutoStartAsync(Action<string>? onStatusLog, CancellationToken ct)
    {
        try
        {
            var direct = new TcpClient();
            await direct.ConnectAsync(System.Net.IPAddress.Loopback, AdbServerPort, ct).ConfigureAwait(false);
            return direct;
        }
        catch (SocketException)
        {
            string? adbPath = ResolveAdbPath();
            if (adbPath is null)
            {
                throw new FileNotFoundException("未找到 adb.exe");
            }

            onStatusLog?.Invoke("正在启动本地 ADB 服务...");
            var startRes = await RunAdbCommandAsync(adbPath, ["start-server"], TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
            if (startRes.ExitCode != 0)
            {
                throw new IOException($"adb start-server 失败：{startRes.CombinedErrorOrOutput}");
            }

            var retry = new TcpClient();
            await retry.ConnectAsync(System.Net.IPAddress.Loopback, AdbServerPort, ct).ConfigureAwait(false);
            return retry;
        }
    }

    public static IReadOnlyList<AdbDeviceInfo> ParseTrackDevicesPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<AdbDeviceInfo>();
        string[] lines = payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines)
        {
            int sepIdx = line.IndexOfAny(['\t', ' ']);
            if (sepIdx <= 0) continue;
            string serial = line[..sepIdx].Trim();
            string state = line[(sepIdx + 1)..].Trim();
            int nextSpace = state.IndexOfAny(['\t', ' ']);
            if (nextSpace >= 0) state = state[..nextSpace].Trim();
            if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(state) && seen.Add(serial))
            {
                list.Add(new AdbDeviceInfo(serial, state));
            }
        }
        return list;
    }

    public async Task<string?> GetDeviceModelAsync(string serial, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial)) return null;

        var res = await RunAdbCommandAsync(adb, ["-s", serial, "shell", "getprop", "ro.product.model"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        string model = res.StdOut.Trim();
        if (!string.IsNullOrEmpty(model)) return model;

        var fallback = await RunAdbCommandAsync(adb, ["-s", serial, "shell", "getprop", "ro.product.device"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        string dev = fallback.StdOut.Trim();
        return string.IsNullOrEmpty(dev) ? null : dev;
    }

    public async Task<string?> DetectTonewisePackageAsync(string serial, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial)) return null;

        foreach (string pkg in new[] { "com.shawnshi.tonewise", "com.shawnshi.tonewise.debug" })
        {
            var res = await RunAdbCommandAsync(adb, ["-s", serial, "shell", "pm", "path", pkg], TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            if (res.ExitCode == 0 && res.StdOut.Contains("package:", StringComparison.OrdinalIgnoreCase))
            {
                return pkg;
            }
        }
        return null;
    }

    public async Task<bool> SetupReverseTunnelAsync(string serial, int remotePort, int localPort, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial)) return false;

        var res = await RunAdbCommandAsync(
            adb,
            ["-s", serial, "reverse", $"tcp:{remotePort}", $"tcp:{localPort}"],
            TimeSpan.FromSeconds(6),
            ct).ConfigureAwait(false);
        return res.ExitCode == 0;
    }

    public async Task RemoveReverseTunnelAsync(string serial, int remotePort, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial)) return;

        try
        {
            await RunAdbCommandAsync(
                adb,
                ["-s", serial, "reverse", "--remove", $"tcp:{remotePort}"],
                TimeSpan.FromSeconds(4),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort tunnel teardown
        }
    }

    public async Task<(bool Ok, string Detail)> TriggerPhoneUsbSyncAsync(
        string serial,
        string packageName,
        int port,
        string nonce,
        string hostId,
        bool allowAutoPair,
        CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null) return (false, "未找到 adb.exe");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(packageName))
            return (false, "设备序列号或包名无效");

        string component = $"{packageName}/{TriggerServiceClass}";
        string[] buildArgs(string amVerb) =>
        [
            "-s", serial, "shell", "am", amVerb,
            "-n", component,
            "-a", ActionStartUsbSync,
            "--ei", "extra_usb_port", port.ToString(CultureInfo.InvariantCulture),
            "--es", "extra_usb_nonce", nonce,
            "--es", "extra_host_id", hostId,
            "--ez", "extra_allow_auto_pair", allowAutoPair ? "true" : "false",
        ];

        var res = await RunAdbCommandAsync(adb, buildArgs("start-foreground-service"), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        string combined = res.CombinedErrorOrOutput;
        if (res.ExitCode != 0 || combined.Contains("Error:", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback for Android 7.x (API 24/25) where `am start-foreground-service` does not exist
            if (combined.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("start-foreground-service", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await RunAdbCommandAsync(adb, buildArgs("startservice"), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (fallback.ExitCode == 0 && !fallback.CombinedErrorOrOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "ok");
                }
                return (false, fallback.CombinedErrorOrOutput);
            }
            return (false, combined);
        }

        return (true, "ok");
    }

    public async Task<bool> TriggerPhoneLibraryRescanAsync(string serial, string packageName, string nonce, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(packageName))
            return false;

        string component = $"{packageName}/{TriggerServiceClass}";
        string[] buildArgs(string amVerb) =>
        [
            "-s", serial, "shell", "am", amVerb,
            "-n", component,
            "-a", ActionRescanLibrary,
            "--es", "extra_usb_nonce", nonce,
        ];

        var res = await RunAdbCommandAsync(adb, buildArgs("start-foreground-service"), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        if (res.ExitCode == 0 && !res.CombinedErrorOrOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (res.CombinedErrorOrOutput.Contains("Unknown command", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = await RunAdbCommandAsync(adb, buildArgs("startservice"), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            return fallback.ExitCode == 0 && !fallback.CombinedErrorOrOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public async Task<int> DeleteForeignMusicFilesAsync(string serial, IReadOnlyList<string> validatedRelPaths, CancellationToken ct)
    {
        string? adb = ResolveAdbPath();
        if (adb is null || string.IsNullOrWhiteSpace(serial) || validatedRelPaths.Count == 0)
            return 0;

        var safeRelPaths = FilterSafeForeignMusicRelPaths(validatedRelPaths, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (safeRelPaths.Count == 0)
            return 0;

        int deletedCount = 0;
        var touchedSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string[] chunk in safeRelPaths.Chunk(80))
        {
            ct.ThrowIfCancellationRequested();
            var remoteToRel = new Dictionary<string, string>(chunk.Length, StringComparer.Ordinal);
            var remoteFiles = new List<string>(chunk.Length);
            foreach (string relPath in chunk)
            {
                string remoteFile = $"/storage/emulated/0/Music/{relPath}";
                remoteToRel[remoteFile] = relPath;
                remoteFiles.Add(remoteFile);
            }

            string shellCmd = BuildBatchDeleteFilesShellCommand(remoteFiles);
            var res = await RunAdbCommandAsync(adb, ["-s", serial, "shell", shellCmd], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            foreach (string line in res.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("TWDEL\t", StringComparison.Ordinal)) continue;
                string deletedRemote = line["TWDEL\t".Length..];
                deletedCount++;
                if (remoteToRel.TryGetValue(deletedRemote, out string? rel))
                {
                    CollectParentSubDirs(rel, touchedSubDirs);
                }
            }
        }

        if (touchedSubDirs.Count > 0)
        {
            // Prune empty (or cover-only) subdirectories deepest-first, never touching /storage/emulated/0/Music itself
            var orderedDirs = touchedSubDirs
                .OrderByDescending(s => s.Count(c => c == '/'))
                .ThenByDescending(s => s.Length)
                .Select(subDir => $"/storage/emulated/0/Music/{subDir}");

            foreach (string[] dirChunk in orderedDirs.Chunk(60))
            {
                ct.ThrowIfCancellationRequested();
                string rmdirCmd = BuildBatchPruneEmptyDirsShellCommand(dirChunk);
                await RunAdbCommandAsync(adb, ["-s", serial, "shell", rmdirCmd], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
        }

        return deletedCount;
    }

    public static string BuildBatchDeleteFilesShellCommand(IEnumerable<string> remoteFilePaths)
    {
        var quoted = string.Join(' ', remoteFilePaths.Select(PosixSingleQuote));
        if (quoted.Length == 0) return "true";
        return $"for f in {quoted}; do if [ -f \"$f\" ]; then rm -f -- \"$f\" && printf 'TWDEL\\t%s\\n' \"$f\"; fi; done";
    }

    public static string BuildBatchPruneEmptyDirsShellCommand(IEnumerable<string> remoteDirPaths)
    {
        var quoted = string.Join(' ', remoteDirPaths.Select(PosixSingleQuote));
        if (quoted.Length == 0) return "true";
        return $"for d in {quoted}; do if [ -d \"$d\" ]; then has_other=0; for c in \"$d\"/* \"$d\"/.*; do [ -e \"$c\" ] || continue; bn=\"${{c##*/}}\"; case \"$bn\" in .|..) continue ;; [Cc][Oo][Vv][Ee][Rr].[Jj][Pp][Gg]|[Cc][Oo][Vv][Ee][Rr].[Jj][Pp][Ee][Gg]|[Cc][Oo][Vv][Ee][Rr].[Pp][Nn][Gg]|[Ff][Oo][Ll][Dd][Ee][Rr].[Jj][Pp][Gg]|[Ff][Oo][Ll][Dd][Ee][Rr].[Pp][Nn][Gg]|[Ff][Rr][Oo][Nn][Tt].[Jj][Pp][Gg]|[Ff][Rr][Oo][Nn][Tt].[Pp][Nn][Gg]) [ -d \"$c\" ] && {{ has_other=1; break; }} ;; *) has_other=1; break ;; esac; done; if [ \"$has_other\" -eq 0 ]; then for c in \"$d\"/*; do [ -f \"$c\" ] && rm -f -- \"$c\"; done; rmdir -- \"$d\" 2>/dev/null || true; fi; fi; done";
    }

    public static IReadOnlyList<string> FilterSafeForeignMusicRelPaths(
        IEnumerable<string> rawPaths,
        ISet<string> desiredNormRelPaths)
    {
        var normalizedDesired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string desired in desiredNormRelPaths)
        {
            if (TryNormalizeSafeAudioRelPath(desired, out string normDesired))
            {
                normalizedDesired.Add(normDesired);
            }
            else if (!string.IsNullOrWhiteSpace(desired))
            {
                normalizedDesired.Add(desired.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (string raw in rawPaths)
        {
            if (!TryNormalizeSafeAudioRelPath(raw, out string norm))
                continue;
            if (normalizedDesired.Contains(norm))
                continue;
            if (seen.Add(norm))
            {
                result.Add(norm);
            }
        }
        return result;
    }

    internal static bool TryNormalizeSafeAudioRelPath(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim().Normalize(NormalizationForm.FormC);
        foreach (char c in trimmed)
        {
            if (c < 0x20) return false;
        }
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
            return false;

        string unified = trimmed.Replace('\\', '/');
        string[] segments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (string seg in segments)
        {
            if (seg is "." or "..") return false;
            if (seg.Trim().Length == 0) return false;
        }

        string joined = string.Join('/', segments);
        if (joined.StartsWith(".tonewise/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(joined, ".tonewise", StringComparison.OrdinalIgnoreCase)
            || joined.StartsWith("Tonewise/Playlists/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string ext = Path.GetExtension(segments[^1]);
        if (string.IsNullOrEmpty(ext) || !AllowedAudioExtensions.Contains(ext))
            return false;

        normalized = joined;
        return true;
    }

    internal static bool TryNormalizeSafeSubDirRelPath(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string trimmed = raw.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(trimmed))
            return false;

        const string dummyLeaf = "/__tw_probe.mp3";
        if (!TryNormalizeSafeAudioRelPath($"{trimmed}{dummyLeaf}", out string probed))
            return false;

        if (!probed.EndsWith(dummyLeaf, StringComparison.Ordinal))
            return false;

        normalized = probed[..^dummyLeaf.Length];
        return !string.IsNullOrEmpty(normalized);
    }

    private static void CollectParentSubDirs(string relPath, HashSet<string> subDirs)
    {
        string[] parts = relPath.Split('/');
        if (parts.Length <= 1) return;
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(parts[i]);
            subDirs.Add(sb.ToString());
        }
    }

    internal static string PosixSingleQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static async Task<string> ReadLengthPrefixedUtf8Async(NetworkStream stream, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        await ReadExactlyAsync(stream, lenBuf, ct).ConfigureAwait(false);
        string lenHex = Encoding.ASCII.GetString(lenBuf);
        if (!int.TryParse(lenHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int len) || len < 0 || len > 4 * 1024 * 1024)
        {
            throw new IOException($"Invalid ADB frame length '{lenHex}'");
        }
        if (len == 0) return "";
        byte[] payloadBuf = new byte[len];
        await ReadExactlyAsync(stream, payloadBuf, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payloadBuf);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException("ADB connection closed");
            }
            offset += n;
        }
    }

    private sealed record AdbCommandResult(int ExitCode, string StdOut, string StdErr)
    {
        public string CombinedErrorOrOutput =>
            string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : $"{StdErr.Trim()} {StdOut.Trim()}".Trim();
    }

    private static async Task<AdbCommandResult> RunAdbCommandAsync(
        string adbPath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {adbPath}");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return new AdbCommandResult(proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill errors
            }
            if (ct.IsCancellationRequested) throw;
            return new AdbCommandResult(-1, "", "ADB command timed out");
        }
    }
}
