using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tonewise.Interop;

namespace Tonewise.SyncHost;

public sealed record M3u8MergeApiRequest(
    [property: JsonPropertyName("playlist_name")] string PlaylistName,
    [property: JsonPropertyName("base")] List<string> Base,
    [property: JsonPropertyName("ours")] List<string> Ours,
    [property: JsonPropertyName("theirs")] List<string> Theirs
);

public sealed record HistoryPushRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("events")] List<JsonElement> Events
);

public sealed record MutationsPushRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("mutations")] List<JsonElement> Mutations
);

public sealed class SyncHostServer : IAsyncDisposable
{
    private readonly string _musicRoot;
    private readonly string _tonewiseDir;
    private readonly string _deviceId;
    private readonly Lock _fileLock = new();
    private WebApplication? _app;
    private MdnsAdvertiser? _mdns;

    public int ListeningPort { get; private set; }
    public bool IsRunning => _app is not null;

    public SyncHostServer(string musicRoot, string? deviceId = null)
    {
        _musicRoot = musicRoot;
        _tonewiseDir = Path.Combine(musicRoot, ".tonewise");
        _deviceId = deviceId ?? Environment.MachineName;
    }

    public async Task<int> StartAsync(int port = 45820, bool enableMdns = true)
    {
        if (_app is not null) return ListeningPort;
        Directory.CreateDirectory(_tonewiseDir);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        var app = builder.Build();
        MapRoutes(app);
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        string? firstAddr = addresses?.Addresses.FirstOrDefault();
        ListeningPort = firstAddr is not null && Uri.TryCreate(firstAddr, UriKind.Absolute, out var uri)
            ? uri.Port
            : port;

        _app = app;
        if (enableMdns && ListeningPort > 0)
        {
            _mdns = new MdnsAdvertiser($"Tonewise-{_deviceId}", ListeningPort);
            _mdns.Start();
        }
        return ListeningPort;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/v1/status", () =>
        {
            string wfPath = Path.Combine(_tonewiseDir, "waveforms.bin");
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            return Results.Json(new
            {
                schema_version = 1,
                device_id = _deviceId,
                has_manifest = File.Exists(dbPath),
                has_waveforms = File.Exists(wfPath),
                waveforms_bytes = File.Exists(wfPath) ? new FileInfo(wfPath).Length : 0L,
                manifest_sha256 = File.Exists(dbPath) ? ComputeSha256Hex(dbPath) : null,
            });
        });

        app.MapGet("/api/v1/scenes", () =>
        {
            string scenesPath = Path.Combine(_tonewiseDir, "scenes_5d.json");
            if (File.Exists(scenesPath))
            {
                return Results.Text(File.ReadAllText(scenesPath, Encoding.UTF8), "application/json", Encoding.UTF8);
            }
            return Results.Text(DefaultScenesJson(), "application/json", Encoding.UTF8);
        });

        app.MapGet("/api/v1/waveforms", () =>
        {
            string wfPath = Path.Combine(_tonewiseDir, "waveforms.bin");
            if (!File.Exists(wfPath)) return Results.NotFound();
            return Results.File(wfPath, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapGet("/api/v1/manifest", () =>
        {
            string dbPath = Path.Combine(_tonewiseDir, "manifest.db");
            if (!File.Exists(dbPath)) return Results.NotFound();
            return Results.File(dbPath, "application/x-sqlite3", enableRangeProcessing: true);
        });

        app.MapPost("/api/v1/playlists/merge", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<M3u8MergeApiRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.PlaylistName))
                return Results.BadRequest(new { error = "invalid playlist merge payload" });

            var result = TonewiseCoreClient.MergeM3u8(body.Base, body.Ours, body.Theirs);
            if (result.Conflicts.Count == 0)
            {
                string playlistsDir = Path.Combine(_musicRoot, "Playlists");
                Directory.CreateDirectory(playlistsDir);
                string safeName = Path.GetFileNameWithoutExtension(body.PlaylistName) + ".m3u8";
                string outPath = Path.Combine(playlistsDir, safeName);
                string m3uText = "#EXTM3U\n" + string.Join("\n", result.Merged) + "\n";
                lock (_fileLock)
                {
                    File.WriteAllText(outPath, m3uText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            return Results.Json(result);
        });

        app.MapPost("/api/v1/history/push", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<HistoryPushRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "invalid history payload" });

            string safeDevice = SanitizeId(body.DeviceId);
            string histPath = Path.Combine(_tonewiseDir, $"history.{safeDevice}.jsonl");
            var lines = body.Events.Select(e => e.GetRawText()).ToList();
            lock (_fileLock)
            {
                File.AppendAllLines(histPath, lines, new UTF8Encoding(false));
            }
            return Results.Json(new { accepted = lines.Count });
        });

        app.MapPost("/api/v1/mutations/push", async (HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<MutationsPushRequest>(request.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.DeviceId))
                return Results.BadRequest(new { error = "invalid mutations payload" });

            string safeDevice = SanitizeId(body.DeviceId);
            string mutPath = Path.Combine(_tonewiseDir, $"mutations.{safeDevice}.jsonl");
            var lines = body.Mutations.Select(m => m.GetRawText()).ToList();
            lock (_fileLock)
            {
                File.AppendAllLines(mutPath, lines, new UTF8Encoding(false));
            }
            return Results.Json(new { accepted = lines.Count });
        });

        app.MapGet("/api/v1/handoff", () =>
        {
            var slots = ReadSessions();
            return Results.Json(slots);
        });

        app.MapPost("/api/v1/handoff", async (HttpRequest request) =>
        {
            var incoming = await JsonSerializer.DeserializeAsync<List<SessionSlotDto>>(request.Body);
            if (incoming is null) return Results.BadRequest(new { error = "invalid sessions payload" });

            List<SessionSlotDto> merged;
            lock (_fileLock)
            {
                var current = ReadSessions();
                merged = TonewiseCoreClient.MergeSessions(current, incoming);
                WriteSessions(merged);
            }
            return Results.Json(merged);
        });
    }

    private List<SessionSlotDto> ReadSessions()
    {
        string path = Path.Combine(_tonewiseDir, "sessions.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SessionSlotDto>>(File.ReadAllText(path, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteSessions(List<SessionSlotDto> slots)
    {
        string path = Path.Combine(_tonewiseDir, "sessions.json");
        string json = JsonSerializer.Serialize(slots, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
    }

    private static string SanitizeId(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));

    private static string ComputeSha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string DefaultScenesJson() => """
        {
          "schema_version": 1,
          "scenes": [
            {
              "id": "11111111-1111-4111-8111-111111111111",
              "title": "深度专注 · 纯器乐",
              "subtitle": "低/中能量纯器乐，Tempo 空值非阻断保留",
              "accent_hex": "#005EB8",
              "updated_at_ms": 1763251200000,
              "sort_arc": "flat",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "dj_vocals", "op": "eq", "values": ["instrumental"] } },
                  { "predicate": { "field": "dj_energy", "op": "in", "values": ["low", "medium"] } }
                ]
              }
            },
            {
              "id": "33333333-3333-4333-8333-333333333333",
              "title": "夜间降噪 · 松弛助眠",
              "subtitle": "静谧松弛 Mood 簇 + 阶梯能量衰减弧线",
              "accent_hex": "#2A6B54",
              "updated_at_ms": 1763251200000,
              "sort_arc": "energy_desc_sleep",
              "tempo_null_passthrough": true,
              "rule": {
                "any": [
                  { "predicate": { "field": "mood_cluster", "op": "in", "values": ["calm_relaxed"] } },
                  { "predicate": { "field": "dj_energy", "op": "eq", "values": ["low"] } }
                ]
              }
            },
            {
              "id": "44444444-4444-4444-8444-444444444444",
              "title": "沉睡馆藏唤醒",
              "subtitle": "超 90 天未播高分馆藏曲目重温",
              "accent_hex": "#8B5E3C",
              "updated_at_ms": 1763251200000,
              "sort_arc": "rediscovery_90d",
              "tempo_null_passthrough": true,
              "rule": {
                "all": [
                  { "predicate": { "field": "unplayed_days_gte", "op": "gte", "values": [90] } },
                  { "predicate": { "field": "min_rating", "op": "gte", "values": [4] } }
                ]
              }
            }
          ]
        }
        """;

    public async ValueTask DisposeAsync()
    {
        if (_mdns is not null)
        {
            await _mdns.DisposeAsync();
            _mdns = null;
        }
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
