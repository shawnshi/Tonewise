using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tonewise.Interop;

public sealed record AudioFrameFingerprint(
    [property: JsonPropertyName("audio_frame_blake3_hex")] string AudioFrameBlake3Hex,
    [property: JsonPropertyName("header_blake3_hex")] string HeaderBlake3Hex,
    [property: JsonPropertyName("header_size")] ulong HeaderSize,
    [property: JsonPropertyName("file_size")] ulong FileSize
);

public sealed record M3u8MergeConflict(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("ours")] List<string> Ours,
    [property: JsonPropertyName("theirs")] List<string> Theirs
);

public sealed record M3u8MergeResult(
    [property: JsonPropertyName("merged")] List<string> Merged,
    [property: JsonPropertyName("conflicts")] List<M3u8MergeConflict> Conflicts
);

public sealed record SessionSlotDto(
    [property: JsonPropertyName("slot_index")] int SlotIndex,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scene_id")] string? SceneId,
    [property: JsonPropertyName("updated_at_ms")] ulong UpdatedAtMs,
    [property: JsonPropertyName("queue_track_uids")] List<string> QueueTrackUids,
    [property: JsonPropertyName("current_index")] int CurrentIndex,
    [property: JsonPropertyName("position_ms")] ulong PositionMs
);

public sealed record NativeAudioAnalysisDto(
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("duration_ms")] ulong DurationMs,
    [property: JsonPropertyName("sample_rate_hz")] uint SampleRateHz,
    [property: JsonPropertyName("bit_depth")] uint? BitDepth,
    [property: JsonPropertyName("bitrate_kbps")] uint? BitrateKbps,
    [property: JsonPropertyName("waveform_150")] List<byte> Waveform150,
    [property: JsonPropertyName("estimated_bpm")] float? EstimatedBpm,
    [property: JsonPropertyName("tempo_bucket")] string? TempoBucket,
    [property: JsonPropertyName("replaygain_track_db")] float? ReplayGainTrackDb,
    [property: JsonPropertyName("peak_linear")] float PeakLinear
);

public static class TonewiseCoreClient
{
    public static uint AbiVersion() => TonewiseCoreNative.tonewise_core_abi_version();

    public static List<string> SplitArtists(string raw)
    {
        IntPtr arg = TonewiseCoreNative.AllocUtf8(raw);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_split_artists_json(arg)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        finally
        {
            Marshal.FreeHGlobal(arg);
        }
    }

    public static List<string> EvaluateScene(string sceneJson, string tracksJson)
    {
        IntPtr pScene = TonewiseCoreNative.AllocUtf8(sceneJson);
        IntPtr pTracks = TonewiseCoreNative.AllocUtf8(tracksJson);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_evaluate_scene_json(pScene, pTracks)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        finally
        {
            Marshal.FreeHGlobal(pScene);
            Marshal.FreeHGlobal(pTracks);
        }
    }

    public static M3u8MergeResult MergeM3u8(
        IReadOnlyList<string> @base,
        IReadOnlyList<string> ours,
        IReadOnlyList<string> theirs
    )
    {
        string requestJson = JsonSerializer.Serialize(new { @base, ours, theirs });
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(requestJson);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_merge_m3u8_json(pReq)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<M3u8MergeResult>(json)
                ?? throw new InvalidOperationException("Empty merge result from tonewise_core");
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    public static AudioFrameFingerprint ComputeFingerprint(string filePath)
    {
        IntPtr pPath = TonewiseCoreNative.AllocUtf8(filePath);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_audio_fingerprint_path(pPath)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<AudioFrameFingerprint>(json)
                ?? throw new InvalidOperationException("Empty fingerprint result from tonewise_core");
        }
        finally
        {
            Marshal.FreeHGlobal(pPath);
        }
    }

    public static float[]? LookupWaveform(string archivePath, string blake3Hex)
    {
        IntPtr pArchive = TonewiseCoreNative.AllocUtf8(archivePath);
        IntPtr pHex = TonewiseCoreNative.AllocUtf8(blake3Hex);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_lookup_waveform_json(pArchive, pHex)
            );
            if (json == "null")
            {
                return null;
            }
            ThrowIfError(json);
            byte[]? raw = JsonSerializer.Deserialize<byte[]>(json);
            if (raw is null) return null;
            float[] result = new float[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                result[i] = raw[i] / 255f;
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pArchive);
            Marshal.FreeHGlobal(pHex);
        }
    }

    public static List<SessionSlotDto> MergeSessions(
        IReadOnlyList<SessionSlotDto> local,
        IReadOnlyList<SessionSlotDto> remote
    )
    {
        string req = JsonSerializer.Serialize(new { local, remote });
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(req);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_merge_sessions_json(pReq)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<List<SessionSlotDto>>(json) ?? [];
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    public static string ApplyLwwMutationsJson(string requestJson)
    {
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(requestJson);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_apply_lww_mutations_json(pReq)
            );
            ThrowIfError(json);
            return json;
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    public static string ApplyHeaderPatch(string filePath, string expectedOldHex, byte[] newHeaderBytes)
    {
        IntPtr pPath = TonewiseCoreNative.AllocUtf8(filePath);
        IntPtr pHex = TonewiseCoreNative.AllocUtf8(expectedOldHex);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_apply_header_patch_path(
                    pPath,
                    pHex,
                    newHeaderBytes,
                    (nuint)newHeaderBytes.Length
                )
            );
            ThrowIfError(json);
            return json;
        }
        finally
        {
            Marshal.FreeHGlobal(pPath);
            Marshal.FreeHGlobal(pHex);
        }
    }

    public static NativeAudioAnalysisDto AnalyzeAudioFile(string filePath)
    {
        IntPtr pPath = TonewiseCoreNative.AllocUtf8(filePath);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_analyze_audio_path(pPath)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<NativeAudioAnalysisDto>(json)
                ?? throw new InvalidOperationException("Empty audio analysis result from tonewise_core");
        }
        finally
        {
            Marshal.FreeHGlobal(pPath);
        }
    }

    public static AudioFrameFingerprint WriteAudioTags(
        string filePath,
        IReadOnlyDictionary<string, string?> updates)
    {
        string req = JsonSerializer.Serialize(new { path = filePath, updates });
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(req);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_write_audio_tags_json(pReq)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<AudioFrameFingerprint>(json)
                ?? throw new InvalidOperationException("Empty fingerprint result after writing audio tags");
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    private static void ThrowIfError(string json)
    {
        if (json.StartsWith("{\"error\"", StringComparison.Ordinal))
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                throw new InvalidOperationException($"tonewise-core error: {err.GetString()}");
            }
        }
    }
}
