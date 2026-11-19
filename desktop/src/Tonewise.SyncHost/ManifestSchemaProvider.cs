using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Tonewise.SyncHost;

/// <summary>
/// Single source of truth provider for <c>protocol/schemas/manifest.sql</c>, embedded
/// from the repository root schema file at compile time.
/// </summary>
public static class ManifestSchemaProvider
{
    public const int CurrentSchemaVersion = 2;

    private const string ResourceName = "Tonewise.SyncHost.protocol.manifest.sql";

    private static readonly Lazy<string> LazySql = new(() =>
    {
        Assembly asm = typeof(ManifestSchemaProvider).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded schema resource '{ResourceName}' was not found in assembly '{asm.FullName}'.");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string ManifestSql => LazySql.Value;

    /// <summary>
    /// Idempotently upgrades an existing v1/partial <c>manifest.db</c> to schema version 2:
    /// ensures v2 <c>tracks</c> columns, creates catalog indexes, and stamps <c>schema_meta</c>.
    /// </summary>
    public static void EnsureSchemaV2(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT name FROM pragma_table_info('tracks');";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        if (existing.Count == 0) return;

        (string Name, string AlterSql)[] v2Cols =
        [
            ("disc_number", "ALTER TABLE tracks ADD COLUMN disc_number INTEGER NOT NULL DEFAULT 0;"),
            ("track_number", "ALTER TABLE tracks ADD COLUMN track_number INTEGER NOT NULL DEFAULT 0;"),
            ("publisher", "ALTER TABLE tracks ADD COLUMN publisher TEXT;"),
            ("legacy_play_count", "ALTER TABLE tracks ADD COLUMN legacy_play_count INTEGER NOT NULL DEFAULT 0;"),
            ("lastfm_pulled_count", "ALTER TABLE tracks ADD COLUMN lastfm_pulled_count INTEGER NOT NULL DEFAULT 0;"),
            ("local_unsynced_expired_count", "ALTER TABLE tracks ADD COLUMN local_unsynced_expired_count INTEGER NOT NULL DEFAULT 0;"),
        ];

        foreach (var (name, sql) in v2Cols)
        {
            if (!existing.Contains(name))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
        }

        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks (album);
                CREATE INDEX IF NOT EXISTS idx_tracks_genre ON tracks (genre);
                CREATE INDEX IF NOT EXISTS idx_tracks_work_name ON tracks (work_name);
                CREATE TABLE IF NOT EXISTS schema_meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO schema_meta (key, value) VALUES ('schema_version', '2')
                ON CONFLICT(key) DO UPDATE SET value = '2';
                """;
            idxCmd.ExecuteNonQuery();
        }
    }
}
