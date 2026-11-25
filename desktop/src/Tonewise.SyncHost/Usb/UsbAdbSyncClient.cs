using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Tonewise.SyncHost.Curation;

namespace Tonewise.SyncHost.Usb;

public sealed record UsbDirectPushTrackSpec(
    string TrackUid,
    string RelPath,
    string LocalPath,
    long ExpectedSize,
    long ExpectedMtimeUnixMs
);

public sealed record UsbDirectPushCoverSpec(
    string AlbumDirRelPath,
    string LocalCoverPath
);

public sealed record UsbDirectPushProgress(
    string Stage,
    int CompletedTracks,
    int TotalTracks,
    long BytesTransferred,
    long TotalBytes,
    double SpeedMbps,
    string? CurrentRelPath
);

public sealed record UsbDirectPushedTrackResult(
    string TrackUid,
    string RelPath,
    long FileSize
);

public sealed record UsbDirectPushSummary(
    bool UsedDirectPush,
    IReadOnlyList<UsbDirectPushedTrackResult> PushedTracks,
    IReadOnlyList<string> FailedTrackUids,
    int CoversPushed,
    int CoversSkipped,
    long TotalBytesTransferred,
    double ElapsedSeconds,
    double AverageMbps,
    string? Error
);

/// <summary>
/// Native ADB wire-protocol client talking directly to 127.0.0.1:5037 over TCP
/// (`host:transport:&lt;serial&gt;` + `sync:` binary v1 + `shell:` batch commits),
/// bypassing per-track ADB process spawns and loopback HTTP tunneling overhead.
/// </summary>
public sealed class UsbAdbSyncClient
{
    public const int DefaultAdbPort = 5037;
    public const string DefaultRemoteMusicRoot = "/storage/emulated/0/Music";
    private const int SyncDataMaxChunk = 64 * 1024;
    private const int WriteCoalesceBufferSize = 4 * (8 + SyncDataMaxChunk);
    private const int FileReadBufferSize = 256 * 1024;
    private const int SocketBufferSize = 256 * 1024;
    private const int MaxShellBatchCommandChars = 12 * 1024;
    private const int CommitMiniBatchSize = 32;
    private const string SyncModeRegular0664 = "33204"; // S_IFREG | 0664 in unambiguous decimal

    private static readonly byte[] TagStat = "STAT"u8.ToArray();
    private static readonly byte[] TagSend = "SEND"u8.ToArray();
    private static readonly byte[] TagData = "DATA"u8.ToArray();
    private static readonly byte[] TagDone = "DONE"u8.ToArray();
    private static readonly byte[] TagQuit = "QUIT"u8.ToArray();
    private static readonly byte[] TagOkay = "OKAY"u8.ToArray();
    private static readonly byte[] TagFail = "FAIL"u8.ToArray();

    private readonly int _adbPort;
    private readonly int _workerCount;
    private readonly Func<CancellationToken, Task>? _ensureAdbServerAsync;

    public UsbAdbSyncClient(
        int adbPort = DefaultAdbPort,
        Func<CancellationToken, Task>? ensureAdbServerAsync = null,
        int workerCount = 2)
    {
        _adbPort = adbPort > 0 ? adbPort : DefaultAdbPort;
        _ensureAdbServerAsync = ensureAdbServerAsync;
        _workerCount = Math.Clamp(workerCount, 1, 4);
    }

    public async Task<UsbDirectPushSummary> PushAsync(
        string serial,
        IReadOnlyList<UsbDirectPushTrackSpec> tracks,
        IReadOnlyList<UsbDirectPushCoverSpec> covers,
        string? remoteMusicRoot,
        Func<UsbDirectPushProgress, Task>? onProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return new UsbDirectPushSummary(false, [], tracks.Select(t => t.TrackUid).ToArray(), 0, 0, 0, 0, 0, "Empty serial");
        }

        string normalizedRoot = NormalizeRemoteMusicRoot(remoteMusicRoot);
        var stopwatch = Stopwatch.StartNew();

        var validTracks = new List<ValidatedTrackWork>(tracks.Count);
        var failedUids = new List<string>();
        var parentDirs = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;

        foreach (UsbDirectPushTrackSpec item in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!UsbAdbBridge.TryNormalizeSafeAudioRelPath(item.RelPath, out string relPath) ||
                string.IsNullOrWhiteSpace(item.LocalPath))
            {
                failedUids.Add(item.TrackUid);
                continue;
            }

            string ioPath = LongPathHelper.ToExtendedIfLong(item.LocalPath);
            if (!File.Exists(ioPath))
            {
                failedUids.Add(item.TrackUid);
                continue;
            }

            var fileInfo = new FileInfo(ioPath);
            long actualSize = fileInfo.Length;
            uint mtimeSec = ResolveUnixSeconds(item.ExpectedMtimeUnixMs, fileInfo.LastWriteTimeUtc);
            string tempRelPath = BuildTempRelPath(relPath, item.TrackUid, ".part");
            string remoteTempPath = $"{normalizedRoot}/{tempRelPath}";
            string remoteFinalPath = $"{normalizedRoot}/{relPath}";

            int lastSlash = relPath.LastIndexOf('/');
            if (lastSlash > 0)
            {
                parentDirs.Add($"{normalizedRoot}/{relPath[..lastSlash]}");
            }
            else
            {
                parentDirs.Add(normalizedRoot);
            }

            totalBytes += actualSize;
            validTracks.Add(new ValidatedTrackWork(
                item.TrackUid,
                relPath,
                ioPath,
                actualSize,
                mtimeSec,
                remoteTempPath,
                remoteFinalPath));
        }

        var validCovers = new List<ValidatedCoverWork>(covers.Count);
        var seenCoverDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UsbDirectPushCoverSpec cover in covers)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(cover.LocalCoverPath))
                continue;

            string? subDir = null;
            if (!string.IsNullOrWhiteSpace(cover.AlbumDirRelPath))
            {
                if (!UsbAdbBridge.TryNormalizeSafeSubDirRelPath(cover.AlbumDirRelPath, out string normSubDir))
                    continue;
                subDir = normSubDir;
            }

            string dedupKey = subDir ?? "";
            if (!seenCoverDirs.Add(dedupKey))
                continue;

            string coverIoPath = LongPathHelper.ToExtendedIfLong(cover.LocalCoverPath);
            if (!File.Exists(coverIoPath))
                continue;

            string coverRelPath = string.IsNullOrEmpty(subDir) ? "cover.jpg" : $"{subDir}/cover.jpg";
            string tempCoverRelPath = BuildTempRelPath(coverRelPath, dedupKey, ".cover.part");
            string remoteDir = string.IsNullOrEmpty(subDir) ? normalizedRoot : $"{normalizedRoot}/{subDir}";
            parentDirs.Add(remoteDir);

            var coverInfo = new FileInfo(coverIoPath);
            validCovers.Add(new ValidatedCoverWork(
                coverIoPath,
                coverInfo.Length,
                ResolveUnixSeconds(0, coverInfo.LastWriteTimeUtc),
                $"{normalizedRoot}/{tempCoverRelPath}",
                $"{remoteDir}/cover.jpg"));
        }

        if (validTracks.Count == 0 && validCovers.Count == 0)
        {
            return new UsbDirectPushSummary(
                UsedDirectPush: true,
                PushedTracks: [],
                FailedTrackUids: failedUids,
                CoversPushed: 0,
                CoversSkipped: 0,
                TotalBytesTransferred: 0,
                ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
                AverageMbps: 0,
                Error: null);
        }

        if (_ensureAdbServerAsync is not null)
        {
            await _ensureAdbServerAsync(ct).ConfigureAwait(false);
        }

        try
        {
            if (parentDirs.Count > 0)
            {
                await EnsureRemoteDirectoriesAsync(serial.Trim(), parentDirs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UsbDirectPushSummary(
                UsedDirectPush: false,
                PushedTracks: [],
                FailedTrackUids: tracks.Select(t => t.TrackUid).ToArray(),
                CoversPushed: 0,
                CoversSkipped: 0,
                TotalBytesTransferred: 0,
                ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
                AverageMbps: 0,
                Error: ex.Message);
        }

        long bytesTransferred = 0;
        int completedTrackUploads = 0;
        string? currentRelPath = null;
        var stagedTracks = new ConcurrentQueue<ValidatedTrackWork>();
        var committedTracks = new ConcurrentBag<UsbDirectPushedTrackResult>();
        var uncommittedCleanupPaths = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var failedBag = new ConcurrentBag<string>(failedUids);
        var commitLock = new SemaphoreSlim(1, 1);

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = RunProgressTickerAsync(
            validTracks.Count,
            totalBytes,
            stopwatch,
            () => (Volatile.Read(ref completedTrackUploads), Interlocked.Read(ref bytesTransferred), Volatile.Read(ref currentRelPath)),
            onProgress,
            progressCts.Token);

        int coversPushed = 0;
        int coversSkipped = 0;

        try
        {
            if (validTracks.Count > 0)
            {
                var trackChannel = Channel.CreateUnbounded<ValidatedTrackWork>(new UnboundedChannelOptions
                {
                    SingleWriter = true,
                    SingleReader = false
                });
                foreach (ValidatedTrackWork track in validTracks)
                {
                    trackChannel.Writer.TryWrite(track);
                }
                trackChannel.Writer.Complete();

                int actualWorkers = Math.Min(_workerCount, validTracks.Count);
                Task[] workers = new Task[actualWorkers];
                for (int w = 0; w < actualWorkers; w++)
                {
                    workers[w] = RunTrackWorkerAsync(trackChannel.Reader);
                }
                await Task.WhenAll(workers).ConfigureAwait(false);
            }

            await FlushPendingTrackCommitsAsync(ct).ConfigureAwait(false);

            if (validCovers.Count > 0)
            {
                (int pushed, int skipped) = await SyncMissingCoversAsync(
                    serial.Trim(),
                    validCovers,
                    uncommittedCleanupPaths,
                    ct).ConfigureAwait(false);
                coversPushed = pushed;
                coversSkipped = skipped;
            }
        }
        finally
        {
            await progressCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await progressTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (!uncommittedCleanupPaths.IsEmpty)
            {
                await BestEffortDeleteRemotePathsAsync(serial.Trim(), uncommittedCleanupPaths.Keys.ToArray())
                    .ConfigureAwait(false);
            }
        }

        double elapsed = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        long finalBytes = Interlocked.Read(ref bytesTransferred);
        double avgMbps = finalBytes / elapsed / (1024.0 * 1024.0);

        if (onProgress is not null)
        {
            try
            {
                await onProgress(new UsbDirectPushProgress(
                    "complete",
                    committedTracks.Count,
                    validTracks.Count,
                    finalBytes,
                    totalBytes,
                    avgMbps,
                    null)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        return new UsbDirectPushSummary(
            UsedDirectPush: true,
            PushedTracks: committedTracks.ToArray(),
            FailedTrackUids: failedBag.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CoversPushed: coversPushed,
            CoversSkipped: coversSkipped,
            TotalBytesTransferred: finalBytes,
            ElapsedSeconds: elapsed,
            AverageMbps: avgMbps,
            Error: null);

        async Task RunTrackWorkerAsync(ChannelReader<ValidatedTrackWork> reader)
        {
            PersistentSyncSession? session = null;
            byte[] writeBuf = ArrayPool<byte>.Shared.Rent(WriteCoalesceBufferSize);
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out ValidatedTrackWork? work))
                    {
                        ct.ThrowIfCancellationRequested();
                        Volatile.Write(ref currentRelPath, work.RelPath);
                        uncommittedCleanupPaths.TryAdd(work.RemoteTempPath, 0);

                        bool uploaded = false;

                        for (int attempt = 0; attempt < 2 && !uploaded; attempt++)
                        {
                            long attemptSent = 0;
                            try
                            {
                                session ??= await OpenSyncSessionAsync(serial.Trim(), ct).ConfigureAwait(false);
                                await SendFileAsync(
                                    session.Stream,
                                    work.LocalPath,
                                    work.RemoteTempPath,
                                    work.MtimeUnixSec,
                                    writeBuf,
                                    delta =>
                                    {
                                        attemptSent += delta;
                                        Interlocked.Add(ref bytesTransferred, delta);
                                    },
                                    ct).ConfigureAwait(false);
                                uploaded = true;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                if (attemptSent > 0)
                                {
                                    Interlocked.Add(ref bytesTransferred, -attemptSent);
                                }
                                if (session is not null)
                                {
                                    await session.DisposeAsync().ConfigureAwait(false);
                                    session = null;
                                }
                            }
                        }

                        if (!uploaded)
                        {
                            failedBag.Add(work.TrackUid);
                            continue;
                        }

                        Interlocked.Increment(ref completedTrackUploads);
                        stagedTracks.Enqueue(work);

                        if (stagedTracks.Count >= CommitMiniBatchSize && await commitLock.WaitAsync(0, ct).ConfigureAwait(false))
                        {
                            try
                            {
                                await DrainAndCommitStagedTracksCoreAsync(ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                commitLock.Release();
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(writeBuf);
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async Task FlushPendingTrackCommitsAsync(CancellationToken token)
        {
            await commitLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await DrainAndCommitStagedTracksCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                commitLock.Release();
            }
        }

        async Task DrainAndCommitStagedTracksCoreAsync(CancellationToken token)
        {
            var batch = new List<ValidatedTrackWork>(CommitMiniBatchSize);
            while (stagedTracks.TryDequeue(out ValidatedTrackWork? next))
            {
                batch.Add(next);
                if (batch.Count >= CommitMiniBatchSize)
                {
                    await CommitTrackBatchAsync(batch, token).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await CommitTrackBatchAsync(batch, token).ConfigureAwait(false);
            }
        }

        async Task CommitTrackBatchAsync(List<ValidatedTrackWork> batch, CancellationToken token)
        {
            var moveSpecs = batch.Select(x => (x.RemoteTempPath, x.RemoteFinalPath)).ToArray();
            bool[] moved = await BatchMoveRemoteFilesAsync(serial.Trim(), moveSpecs, token).ConfigureAwait(false);
            for (int i = 0; i < batch.Count; i++)
            {
                ValidatedTrackWork item = batch[i];
                if (moved[i])
                {
                    uncommittedCleanupPaths.TryRemove(item.RemoteTempPath, out _);
                    committedTracks.Add(new UsbDirectPushedTrackResult(item.TrackUid, item.RelPath, item.FileSize));
                }
                else
                {
                    failedBag.Add(item.TrackUid);
                }
            }
        }
    }

    private async Task<(int Pushed, int Skipped)> SyncMissingCoversAsync(
        string serial,
        List<ValidatedCoverWork> covers,
        ConcurrentDictionary<string, byte> uncommittedCleanupPaths,
        CancellationToken ct)
    {
        int pushed = 0;
        int skipped = 0;
        var stagedMoves = new List<(string TempPath, string FinalPath)>();
        byte[] writeBuf = ArrayPool<byte>.Shared.Rent(WriteCoalesceBufferSize);
        PersistentSyncSession? session = null;

        try
        {
            session = await OpenSyncSessionAsync(serial, ct).ConfigureAwait(false);
            foreach (ValidatedCoverWork cover in covers)
            {
                ct.ThrowIfCancellationRequested();
                bool ok = false;
                for (int attempt = 0; attempt < 2 && !ok; attempt++)
                {
                    try
                    {
                        session ??= await OpenSyncSessionAsync(serial, ct).ConfigureAwait(false);
                        RemoteStatResult stat = await StatRemoteAsync(session.Stream, cover.RemoteFinalPath, ct)
                            .ConfigureAwait(false);
                        if (stat.Exists && stat.Size > 0)
                        {
                            skipped++;
                            ok = true;
                            break;
                        }

                        uncommittedCleanupPaths.TryAdd(cover.RemoteTempPath, 0);
                        await SendFileAsync(
                            session.Stream,
                            cover.LocalPath,
                            cover.RemoteTempPath,
                            cover.MtimeUnixSec,
                            writeBuf,
                            deltaBytes: null,
                            ct).ConfigureAwait(false);
                        stagedMoves.Add((cover.RemoteTempPath, cover.RemoteFinalPath));
                        ok = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        if (session is not null)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            session = null;
                        }
                    }
                }
            }

            if (stagedMoves.Count > 0)
            {
                bool[] moved = await BatchMoveRemoteFilesAsync(serial, stagedMoves, ct).ConfigureAwait(false);
                for (int i = 0; i < stagedMoves.Count; i++)
                {
                    if (moved[i])
                    {
                        uncommittedCleanupPaths.TryRemove(stagedMoves[i].TempPath, out _);
                        pushed++;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(writeBuf);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        return (pushed, skipped);
    }

    private async Task EnsureRemoteDirectoriesAsync(string serial, HashSet<string> dirs, CancellationToken ct)
    {
        string[] ordered = dirs.OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal).ToArray();
        var batch = new StringBuilder();
        int batchCount = 0;

        foreach (string dir in ordered)
        {
            string quoted = UsbAdbBridge.PosixSingleQuote(dir);
            if (batchCount > 0 && batch.Length + 1 + quoted.Length > MaxShellBatchCommandChars)
            {
                await RunMkdirBatchAsync(serial, batch.ToString(), ct).ConfigureAwait(false);
                batch.Clear();
                batchCount = 0;
            }

            if (batchCount == 0)
            {
                batch.Append("mkdir -p -- ");
            }
            else
            {
                batch.Append(' ');
            }
            batch.Append(quoted);
            batchCount++;
        }

        if (batchCount > 0)
        {
            await RunMkdirBatchAsync(serial, batch.ToString(), ct).ConfigureAwait(false);
        }
    }

    private async Task RunMkdirBatchAsync(string serial, string mkdirCmd, CancellationToken ct)
    {
        string output = await RunShellCommandAsync(serial, $"{mkdirCmd}; echo __TW_EXIT:$?", ct).ConfigureAwait(false);
        if (!TryParseExitCode(output, out int exitCode) || exitCode != 0)
        {
            throw new IOException($"Remote mkdir failed: {output.Trim()}");
        }
    }

    private async Task<bool[]> BatchMoveRemoteFilesAsync(
        string serial,
        IReadOnlyList<(string TempPath, string FinalPath)> pairs,
        CancellationToken ct)
    {
        var results = new bool[pairs.Count];
        int index = 0;

        while (index < pairs.Count)
        {
            var sb = new StringBuilder();
            int start = index;
            while (index < pairs.Count)
            {
                (string tempPath, string finalPath) = pairs[index];
                string snippet =
                    $"mv -f -- {UsbAdbBridge.PosixSingleQuote(tempPath)} {UsbAdbBridge.PosixSingleQuote(finalPath)} && echo __TWOK:{index}";
                if (index > start && sb.Length + 2 + snippet.Length > MaxShellBatchCommandChars)
                {
                    break;
                }
                if (index > start)
                {
                    sb.Append("; ");
                }
                sb.Append(snippet);
                index++;
            }

            try
            {
                string output = await RunShellCommandAsync(serial, sb.ToString(), ct).ConfigureAwait(false);
                foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    const string prefix = "__TWOK:";
                    int pos = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (pos >= 0 && int.TryParse(line.AsSpan(pos + prefix.Length).Trim(), out int okIdx) &&
                        okIdx >= start && okIdx < index)
                    {
                        results[okIdx] = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep batch items false so caller cleans up temp files and falls back to HTTP
            }
        }

        return results;
    }

    private async Task BestEffortDeleteRemotePathsAsync(string serial, IReadOnlyList<string> paths)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            int index = 0;
            while (index < paths.Count)
            {
                var sb = new StringBuilder("rm -f -- ");
                int start = index;
                while (index < paths.Count)
                {
                    string quoted = UsbAdbBridge.PosixSingleQuote(paths[index]);
                    if (index > start && sb.Length + 1 + quoted.Length > MaxShellBatchCommandChars)
                        break;
                    if (index > start) sb.Append(' ');
                    sb.Append(quoted);
                    index++;
                }

                await RunShellCommandAsync(serial, sb.ToString(), cleanupCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort cleanup only
        }
    }

    private static async Task RunProgressTickerAsync(
        int totalTracks,
        long totalBytes,
        Stopwatch stopwatch,
        Func<(int Completed, long Bytes, string? CurrentRelPath)> snapshotProvider,
        Func<UsbDirectPushProgress, Task>? onProgress,
        CancellationToken ct)
    {
        if (onProgress is null) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        long prevBytes = 0;
        double prevSec = 0;
        int prevCompleted = -1;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            (int completed, long bytes, string? current) = snapshotProvider();
            double nowSec = stopwatch.Elapsed.TotalSeconds;
            if (bytes == prevBytes && completed == prevCompleted)
                continue;

            double dt = Math.Max(0.05, nowSec - prevSec);
            double mbps = bytes >= prevBytes
                ? (bytes - prevBytes) / dt / (1024.0 * 1024.0)
                : 0.0;
            prevBytes = bytes;
            prevSec = nowSec;
            prevCompleted = completed;

            try
            {
                await onProgress(new UsbDirectPushProgress(
                    "pushing",
                    completed,
                    totalTracks,
                    bytes,
                    totalBytes,
                    mbps,
                    current)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Do not break workers on UI progress emission hiccup
            }
        }
    }

    internal async Task<RemoteStatResult> StatRemotePathForProbeAsync(
        string serial,
        string remotePath,
        CancellationToken ct)
    {
        if (_ensureAdbServerAsync is not null)
        {
            await _ensureAdbServerAsync(ct).ConfigureAwait(false);
        }

        await using PersistentSyncSession session = await OpenSyncSessionAsync(serial.Trim(), ct).ConfigureAwait(false);
        return await StatRemoteAsync(session.Stream, remotePath, ct).ConfigureAwait(false);
    }

    private async Task<PersistentSyncSession> OpenSyncSessionAsync(string serial, CancellationToken ct)
    {
        TcpClient client = await OpenTransportedTcpClientAsync(serial, ct).ConfigureAwait(false);
        try
        {
            NetworkStream stream = client.GetStream();
            await SendAdbServerCommandAsync(stream, "sync:", ct).ConfigureAwait(false);
            await ReadAdbStatusAsync(stream, "sync:", ct).ConfigureAwait(false);
            return new PersistentSyncSession(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<string> RunShellCommandAsync(string serial, string command, CancellationToken ct)
    {
        using TcpClient client = await OpenTransportedTcpClientAsync(serial, ct).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();
        string adbCmd = $"shell:{command}";
        await SendAdbServerCommandAsync(stream, adbCmd, ct).ConfigureAwait(false);
        await ReadAdbStatusAsync(stream, "shell", ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        byte[] buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                ms.Write(buf, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async Task<TcpClient> OpenTransportedTcpClientAsync(string serial, CancellationToken ct)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient
            {
                NoDelay = true,
                SendBufferSize = SocketBufferSize,
                ReceiveBufferSize = SocketBufferSize
            };
            await client.ConnectAsync("127.0.0.1", _adbPort, ct).ConfigureAwait(false);
        }
        catch (SocketException) when (_ensureAdbServerAsync is not null)
        {
            client?.Dispose();
            await _ensureAdbServerAsync(ct).ConfigureAwait(false);
            client = new TcpClient
            {
                NoDelay = true,
                SendBufferSize = SocketBufferSize,
                ReceiveBufferSize = SocketBufferSize
            };
            await client.ConnectAsync("127.0.0.1", _adbPort, ct).ConfigureAwait(false);
        }

        try
        {
            NetworkStream stream = client.GetStream();
            string transportCmd = $"host:transport:{serial}";
            await SendAdbServerCommandAsync(stream, transportCmd, ct).ConfigureAwait(false);
            await ReadAdbStatusAsync(stream, transportCmd, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task SendAdbServerCommandAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        int payloadLen = Encoding.UTF8.GetByteCount(command);
        if (payloadLen > 0xFFFF)
        {
            throw new ArgumentException($"ADB command exceeds 64 KB limit ({payloadLen} bytes).");
        }

        int totalLen = 4 + payloadLen;
        byte[] packet = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            Encoding.ASCII.GetBytes($"{payloadLen:X4}", packet.AsSpan(0, 4));
            Encoding.UTF8.GetBytes(command, packet.AsSpan(4, payloadLen));
            await stream.WriteAsync(packet.AsMemory(0, totalLen), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private static async Task ReadAdbStatusAsync(NetworkStream stream, string context, CancellationToken ct)
    {
        byte[] status = new byte[4];
        await stream.ReadExactlyAsync(status.AsMemory(0, 4), ct).ConfigureAwait(false);
        if (status.AsSpan().SequenceEqual(TagOkay))
        {
            return;
        }

        if (status.AsSpan().SequenceEqual(TagFail))
        {
            string msg = await UsbAdbBridge.ReadLengthPrefixedUtf8Async(stream, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"ADB {context} FAIL: {msg}");
        }

        string raw = Encoding.ASCII.GetString(status);
        throw new InvalidDataException($"Unexpected ADB status '{raw}' for {context}.");
    }

    internal static async Task<RemoteStatResult> StatRemoteAsync(Stream stream, string remotePath, CancellationToken ct)
    {
        int pathLen = Encoding.UTF8.GetByteCount(remotePath);
        int packetLen = 8 + pathLen;
        byte[] packet = ArrayPool<byte>.Shared.Rent(packetLen);
        try
        {
            TagStat.CopyTo(packet, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)pathLen);
            Encoding.UTF8.GetBytes(remotePath, packet.AsSpan(8, pathLen));
            await stream.WriteAsync(packet.AsMemory(0, packetLen), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }

        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header.AsMemory(0, 8), ct).ConfigureAwait(false);
        if (header.AsSpan(0, 4).SequenceEqual(TagStat))
        {
            uint mode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            byte[] rest = new byte[8];
            await stream.ReadExactlyAsync(rest.AsMemory(0, 8), ct).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(0, 4));
            uint mtime = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(4, 4));
            return new RemoteStatResult(Exists: mode != 0, Mode: mode, Size: size, MtimeSeconds: mtime);
        }

        if (header.AsSpan(0, 4).SequenceEqual(TagFail))
        {
            uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            string msg = await ReadSyncMessageAsync(stream, msgLen, ct).ConfigureAwait(false);
            throw new IOException($"ADB sync STAT failed: {msg}");
        }

        throw new InvalidDataException($"Unexpected ADB sync STAT response tag '{Encoding.ASCII.GetString(header, 0, 4)}'.");
    }

    internal static async Task SendFileAsync(
        Stream stream,
        string localIoPath,
        string remoteTempPath,
        uint mtimeUnixSec,
        byte[] writeBuf,
        Action<long>? deltaBytes,
        CancellationToken ct)
    {
        string spec = $"{remoteTempPath},{SyncModeRegular0664}";
        int specLen = Encoding.UTF8.GetByteCount(spec);
        int sendHeaderLen = 8 + specLen;
        byte[] sendHeader = ArrayPool<byte>.Shared.Rent(sendHeaderLen);
        try
        {
            TagSend.CopyTo(sendHeader, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(sendHeader.AsSpan(4, 4), (uint)specLen);
            Encoding.UTF8.GetBytes(spec, sendHeader.AsSpan(8, specLen));
            await stream.WriteAsync(sendHeader.AsMemory(0, sendHeaderLen), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sendHeader);
        }

        await using var fileStream = new FileStream(
            localIoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: FileReadBufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        int writeOffset = 0;
        while (true)
        {
            if (writeBuf.Length - writeOffset < 8 + SyncDataMaxChunk)
            {
                await stream.WriteAsync(writeBuf.AsMemory(0, writeOffset), ct).ConfigureAwait(false);
                writeOffset = 0;
            }

            int payloadStart = writeOffset + 8;
            int read = await fileStream.ReadAsync(
                writeBuf.AsMemory(payloadStart, SyncDataMaxChunk),
                ct).ConfigureAwait(false);

            if (read <= 0)
                break;

            TagData.CopyTo(writeBuf, writeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(writeBuf.AsSpan(writeOffset + 4, 4), (uint)read);
            writeOffset += 8 + read;
            deltaBytes?.Invoke(read);
        }

        if (writeBuf.Length - writeOffset < 8)
        {
            await stream.WriteAsync(writeBuf.AsMemory(0, writeOffset), ct).ConfigureAwait(false);
            writeOffset = 0;
        }

        TagDone.CopyTo(writeBuf, writeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(writeBuf.AsSpan(writeOffset + 4, 4), mtimeUnixSec);
        writeOffset += 8;

        await stream.WriteAsync(writeBuf.AsMemory(0, writeOffset), ct).ConfigureAwait(false);

        byte[] status = new byte[8];
        await stream.ReadExactlyAsync(status.AsMemory(0, 8), ct).ConfigureAwait(false);
        uint msgLen = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4, 4));
        string msg = msgLen > 0
            ? await ReadSyncMessageAsync(stream, msgLen, ct).ConfigureAwait(false)
            : string.Empty;

        if (!status.AsSpan(0, 4).SequenceEqual(TagOkay))
        {
            string tag = Encoding.ASCII.GetString(status, 0, 4);
            throw new IOException($"ADB sync SEND failed ({tag}): {msg}");
        }
    }

    private static async Task<string> ReadSyncMessageAsync(Stream stream, uint msgLen, CancellationToken ct)
    {
        if (msgLen == 0) return string.Empty;
        int capped = (int)Math.Min(msgLen, 64 * 1024);
        byte[] buf = ArrayPool<byte>.Shared.Rent(capped);
        try
        {
            await stream.ReadExactlyAsync(buf.AsMemory(0, capped), ct).ConfigureAwait(false);
            int remaining = (int)(msgLen - (uint)capped);
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, buf.Length);
                await stream.ReadExactlyAsync(buf.AsMemory(0, chunk), ct).ConfigureAwait(false);
                remaining -= chunk;
            }
            return Encoding.UTF8.GetString(buf, 0, capped);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static bool TryValidateRemoteMusicRoot(string? rawRoot, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(rawRoot))
        {
            normalized = DefaultRemoteMusicRoot;
            return true;
        }

        string candidate = rawRoot.Trim().Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(candidate)
            || candidate.IndexOf('\0') >= 0
            || candidate.IndexOf('\r') >= 0
            || candidate.IndexOf('\n') >= 0
            || !candidate.StartsWith('/'))
        {
            normalized = DefaultRemoteMusicRoot;
            return false;
        }

        string[] segments = candidate.Split('/', StringSplitOptions.None);
        if (segments.Length < 2)
        {
            normalized = DefaultRemoteMusicRoot;
            return false;
        }

        for (int i = 1; i < segments.Length; i++)
        {
            string seg = segments[i];
            if (string.IsNullOrEmpty(seg) || seg == "." || seg == ".."
                || seg.Any(c => char.IsControl(c) || c is ';' or '|' or '&' or '$' or '`' or '"' or '\'' or '<' or '>'))
            {
                normalized = DefaultRemoteMusicRoot;
                return false;
            }
        }

        string canonical = "/" + string.Join('/', segments.Skip(1));
        bool allowed =
            string.Equals(canonical, "/storage/emulated/0/Music", StringComparison.Ordinal)
            || canonical.StartsWith("/storage/emulated/0/Music/", StringComparison.Ordinal)
            || string.Equals(canonical, "/sdcard/Music", StringComparison.Ordinal)
            || canonical.StartsWith("/sdcard/Music/", StringComparison.Ordinal);

        normalized = allowed ? canonical : DefaultRemoteMusicRoot;
        return allowed;
    }

    private static string NormalizeRemoteMusicRoot(string? rawRoot)
    {
        if (!TryValidateRemoteMusicRoot(rawRoot, out string normalized))
        {
            throw new ArgumentException($"Invalid remote_music_root outside allowed Music directories: '{rawRoot}'", nameof(rawRoot));
        }
        return normalized;
    }

    private static string BuildTempRelPath(string finalRelPath, string uniqueSeed, string suffix)
    {
        string seedInput = string.IsNullOrWhiteSpace(uniqueSeed) ? finalRelPath : $"{uniqueSeed}|{finalRelPath}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedInput));
        string hex10 = Convert.ToHexString(hash.AsSpan(0, 5)).ToLowerInvariant();
        string tempName = $".twtmp_{hex10}{suffix}";

        int lastSlash = finalRelPath.LastIndexOf('/');
        return lastSlash > 0
            ? $"{finalRelPath[..lastSlash]}/{tempName}"
            : tempName;
    }

    private static uint ResolveUnixSeconds(long mtimeUnixMs, DateTime lastWriteUtc)
    {
        long sec = mtimeUnixMs > 0
            ? mtimeUnixMs / 1000L
            : new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        if (sec <= 0)
        {
            sec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        return (uint)Math.Clamp(sec, 1L, uint.MaxValue);
    }

    private static bool TryParseExitCode(string output, out int exitCode)
    {
        exitCode = -1;
        const string marker = "__TW_EXIT:";
        int idx = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return false;
        string tail = output[(idx + marker.Length)..].Trim();
        int end = 0;
        while (end < tail.Length && (char.IsDigit(tail[end]) || (end == 0 && tail[end] == '-')))
        {
            end++;
        }
        return end > 0 && int.TryParse(tail[..end], out exitCode);
    }

    internal sealed record RemoteStatResult(bool Exists, uint Mode, uint Size, uint MtimeSeconds);

    private sealed record ValidatedTrackWork(
        string TrackUid,
        string RelPath,
        string LocalPath,
        long FileSize,
        uint MtimeUnixSec,
        string RemoteTempPath,
        string RemoteFinalPath
    );

    private sealed record ValidatedCoverWork(
        string LocalPath,
        long FileSize,
        uint MtimeUnixSec,
        string RemoteTempPath,
        string RemoteFinalPath
    );

    private sealed class PersistentSyncSession : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private int _disposed;

        public NetworkStream Stream { get; }

        public PersistentSyncSession(TcpClient client, NetworkStream stream)
        {
            _client = client;
            Stream = stream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                byte[] quit = new byte[8];
                TagQuit.CopyTo(quit, 0);
                using var quitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await Stream.WriteAsync(quit, quitCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort QUIT before socket close
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}
