using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tonewise.Interop;

public sealed record AudioFrameFingerprint(
    [property: JsonPropertyName("audio_frame_blake3_hex")] string AudioFrameBlake3Hex,
    [property: JsonPropertyName("header_blake3_hex")] string HeaderBlake3Hex,
    [property: JsonPropertyName("header_size")] ulong HeaderSize,
    [property: JsonPropertyName("file_size")] ulong FileSize,
    [property: JsonPropertyName("footer_size")] ulong FooterSize = 0
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

public sealed record NativeTrackProbeDto(
    [property: JsonPropertyName("audio_frame_blake3_hex")] string AudioFrameBlake3Hex,
    [property: JsonPropertyName("header_blake3_hex")] string HeaderBlake3Hex,
    [property: JsonPropertyName("header_size")] ulong HeaderSize,
    [property: JsonPropertyName("footer_size")] ulong FooterSize,
    [property: JsonPropertyName("file_size")] ulong FileSize,
    [property: JsonPropertyName("padding_bytes")] ulong PaddingBytes,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("sample_rate_hz")] uint SampleRateHz,
    [property: JsonPropertyName("bit_depth")] uint? BitDepth,
    [property: JsonPropertyName("bitrate_kbps")] uint? BitrateKbps,
    [property: JsonPropertyName("duration_ms")] ulong DurationMs,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("album_artist")] string? AlbumArtist,
    [property: JsonPropertyName("artists")] List<string> Artists,
    [property: JsonPropertyName("composers")] List<string> Composers,
    [property: JsonPropertyName("conductors")] List<string> Conductors,
    [property: JsonPropertyName("work_name")] string? WorkName,
    [property: JsonPropertyName("movement_name")] string? MovementName,
    [property: JsonPropertyName("movement_number")] uint? MovementNumber,
    [property: JsonPropertyName("disc_number")] uint DiscNumber,
    [property: JsonPropertyName("track_number")] uint TrackNumber,
    [property: JsonPropertyName("publisher")] string? Publisher,
    [property: JsonPropertyName("genre")] string? Genre,
    [property: JsonPropertyName("year")] ushort? Year,
    [property: JsonPropertyName("original_date")] string? OriginalDate,
    [property: JsonPropertyName("release_type")] string? ReleaseType,
    [property: JsonPropertyName("replaygain_track_db")] float? ReplayGainTrackDb,
    [property: JsonPropertyName("replaygain_album_db")] float? ReplayGainAlbumDb,
    [property: JsonPropertyName("replaygain_peak")] float? ReplayGainPeak,
    [property: JsonPropertyName("dj_scene")] List<string> DjScene,
    [property: JsonPropertyName("dj_vocals")] string? DjVocals,
    [property: JsonPropertyName("dj_energy")] string? DjEnergy,
    [property: JsonPropertyName("tempo_bpm")] float? TempoBpm,
    [property: JsonPropertyName("tempo_bucket")] string? TempoBucket,
    [property: JsonPropertyName("moods")] List<string> Moods
);

public sealed record AutoEqBiquadDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("freq_hz")] float FreqHz,
    [property: JsonPropertyName("gain_db")] float GainDb,
    [property: JsonPropertyName("q")] float Q
);

public sealed record DspProfileDto(
    [property: JsonPropertyName("preamp_db")] float PreampDb,
    [property: JsonPropertyName("filters")] List<AutoEqBiquadDto> Filters
);

public sealed record AutoEqRuleOverrideDto(
    [property: JsonPropertyName("preamp_db")] float PreampDb,
    [property: JsonPropertyName("filters")] List<AutoEqBiquadDto> Filters,
    [property: JsonPropertyName("crossfeed_override")] bool? CrossfeedOverride = null
);

public sealed record AutoEqPolicyDto(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("classical_auto_crossfeed")] bool ClassicalAutoCrossfeed = true,
    [property: JsonPropertyName("lock_within_classical_work")] bool LockWithinClassicalWork = true,
    [property: JsonPropertyName("updated_at_ms")] ulong UpdatedAtMs = 0,
    [property: JsonPropertyName("custom_rules")] Dictionary<string, AutoEqRuleOverrideDto>? CustomRules = null
);

public sealed record TrackAutoEqInputDto(
    [property: JsonPropertyName("genres")] List<string> Genres,
    [property: JsonPropertyName("dj_vocals")] string? DjVocals = null,
    [property: JsonPropertyName("work_name")] string? WorkName = null,
    [property: JsonPropertyName("work_key")] string? WorkKey = null
);

public sealed record ResolvedAutoEqDto(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("genre_family")] string GenreFamily,
    [property: JsonPropertyName("vocal_class")] string VocalClass,
    [property: JsonPropertyName("matched_rule_key")] string MatchedRuleKey,
    [property: JsonPropertyName("display_label")] string DisplayLabel,
    [property: JsonPropertyName("is_custom_rule")] bool IsCustomRule,
    [property: JsonPropertyName("locked_to_previous_work")] bool LockedToPreviousWork,
    [property: JsonPropertyName("preamp_db")] float PreampDb,
    [property: JsonPropertyName("filters")] List<AutoEqBiquadDto> Filters,
    [property: JsonPropertyName("enable_crossfeed")] bool EnableCrossfeed,
    [property: JsonPropertyName("has_non_zero_eq")] bool HasNonZeroEq
);

public sealed record AutoEqCatalogEntryDto(
    [property: JsonPropertyName("slot_key")] string SlotKey,
    [property: JsonPropertyName("genre_family")] string GenreFamily,
    [property: JsonPropertyName("vocal_class")] string VocalClass,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("default_preamp_db")] float DefaultPreampDb,
    [property: JsonPropertyName("default_filters")] List<AutoEqBiquadDto> DefaultFilters,
    [property: JsonPropertyName("default_crossfeed")] bool DefaultCrossfeed
);

public static class TonewiseCoreClient
{
    private const uint ExpectedAbiVersion = 1;
    private static int _abiVerified;

    private static void EnsureAbiVersion()
    {
        if (Volatile.Read(ref _abiVerified) == 1) return;
        uint actual = TonewiseCoreNative.tonewise_core_abi_version();
        if (actual != ExpectedAbiVersion)
        {
            throw new InvalidOperationException(
                $"tonewise-core ABI version mismatch: expected {ExpectedAbiVersion}, got {actual}");
        }
        Volatile.Write(ref _abiVerified, 1);
    }

    public static uint AbiVersion() => TonewiseCoreNative.tonewise_core_abi_version();

    public static List<string> SplitArtists(string raw)
    {
        EnsureAbiVersion();
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
        EnsureAbiVersion();
        IntPtr pScene = IntPtr.Zero;
        IntPtr pTracks = IntPtr.Zero;
        try
        {
            pScene = TonewiseCoreNative.AllocUtf8(sceneJson);
            pTracks = TonewiseCoreNative.AllocUtf8(tracksJson);
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_evaluate_scene_json(pScene, pTracks)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        finally
        {
            if (pScene != IntPtr.Zero) Marshal.FreeHGlobal(pScene);
            if (pTracks != IntPtr.Zero) Marshal.FreeHGlobal(pTracks);
        }
    }

    public static M3u8MergeResult MergeM3u8(
        IReadOnlyList<string> @base,
        IReadOnlyList<string> ours,
        IReadOnlyList<string> theirs
    )
    {
        EnsureAbiVersion();
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
        EnsureAbiVersion();
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
        EnsureAbiVersion();
        IntPtr pArchive = IntPtr.Zero;
        IntPtr pHex = IntPtr.Zero;
        try
        {
            pArchive = TonewiseCoreNative.AllocUtf8(archivePath);
            pHex = TonewiseCoreNative.AllocUtf8(blake3Hex);
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
            if (pArchive != IntPtr.Zero) Marshal.FreeHGlobal(pArchive);
            if (pHex != IntPtr.Zero) Marshal.FreeHGlobal(pHex);
        }
    }

    public static List<SessionSlotDto> MergeSessions(
        IReadOnlyList<SessionSlotDto> local,
        IReadOnlyList<SessionSlotDto> remote
    )
    {
        EnsureAbiVersion();
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
        EnsureAbiVersion();
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
        EnsureAbiVersion();
        IntPtr pPath = IntPtr.Zero;
        IntPtr pHex = IntPtr.Zero;
        try
        {
            pPath = TonewiseCoreNative.AllocUtf8(filePath);
            pHex = TonewiseCoreNative.AllocUtf8(expectedOldHex);
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
            if (pPath != IntPtr.Zero) Marshal.FreeHGlobal(pPath);
            if (pHex != IntPtr.Zero) Marshal.FreeHGlobal(pHex);
        }
    }

    public static NativeAudioAnalysisDto AnalyzeAudioFile(string filePath)
    {
        EnsureAbiVersion();
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

    public static NativeTrackProbeDto ProbeTrack(string filePath)
    {
        EnsureAbiVersion();
        IntPtr pPath = TonewiseCoreNative.AllocUtf8(filePath);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_probe_track_path(pPath)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<NativeTrackProbeDto>(json)
                ?? throw new InvalidOperationException("Empty track probe result from tonewise_core");
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
        EnsureAbiVersion();
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

    public static ResolvedAutoEqDto ResolveAutoEq(
        AutoEqPolicyDto policy,
        TrackAutoEqInputDto track,
        string? previousWorkKey = null,
        ResolvedAutoEqDto? previousResolved = null)
    {
        EnsureAbiVersion();
        string req = JsonSerializer.Serialize(new
        {
            policy = policy with { CustomRules = policy.CustomRules ?? [] },
            track,
            previous_work_key = previousWorkKey,
            previous_resolved = previousResolved,
        });
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(req);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_resolve_auto_eq_json(pReq)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<ResolvedAutoEqDto>(json)
                ?? throw new InvalidOperationException("Empty auto eq result from tonewise_core");
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    public static DspProfileDto ComposeDsp(DspProfileDto deviceProfile, DspProfileDto contentProfile)
    {
        EnsureAbiVersion();
        string req = JsonSerializer.Serialize(new
        {
            device_profile = deviceProfile,
            content_profile = contentProfile,
        });
        IntPtr pReq = TonewiseCoreNative.AllocUtf8(req);
        try
        {
            string json = TonewiseCoreNative.ConsumeOwnedUtf8(
                TonewiseCoreNative.tonewise_compose_dsp_json(pReq)
            );
            ThrowIfError(json);
            return JsonSerializer.Deserialize<DspProfileDto>(json)
                ?? throw new InvalidOperationException("Empty compose dsp result from tonewise_core");
        }
        finally
        {
            Marshal.FreeHGlobal(pReq);
        }
    }

    public static List<AutoEqCatalogEntryDto> GetDefaultAutoEqCatalog()
    {
        EnsureAbiVersion();
        string json = TonewiseCoreNative.ConsumeOwnedUtf8(
            TonewiseCoreNative.tonewise_default_auto_eq_catalog_json()
        );
        ThrowIfError(json);
        return JsonSerializer.Deserialize<List<AutoEqCatalogEntryDto>>(json) ?? [];
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
