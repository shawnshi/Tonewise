using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Tonewise.SyncHost;

/// <summary>
/// Centralized SQLite connection and transaction factory enforcing WAL mode, busy timeouts,
/// immediate write transactions, and consistent <c>VACUUM INTO</c> snapshots across Desktop and SyncHost.
/// </summary>
public static class TonewiseSqlite
{
    public const int DefaultBusyTimeoutMs = 5000;
    private static readonly ConcurrentDictionary<string, byte> WalInitializedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static SqliteConnection OpenReadWrite(string dbPath, int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        int clampedMs = Math.Clamp(busyTimeoutMs, 500, 60_000);
        bool isMemory = string.Equals(dbPath, ":memory:", StringComparison.OrdinalIgnoreCase);
        string fullPath = isMemory ? dbPath : Path.GetFullPath(dbPath);
        if (!isMemory)
        {
            string? parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }
        }

        bool needsWalInit = isMemory || !File.Exists(fullPath) || !WalInitializedPaths.ContainsKey(fullPath);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = Math.Max(1, (clampedMs + 999) / 1000)
        };
        var conn = new SqliteConnection(csb.ToString());
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = needsWalInit
                ? $"""
                  PRAGMA journal_mode = WAL;
                  PRAGMA synchronous = NORMAL;
                  PRAGMA busy_timeout = {clampedMs};
                  PRAGMA foreign_keys = ON;
                  """
                : $"""
                  PRAGMA synchronous = NORMAL;
                  PRAGMA busy_timeout = {clampedMs};
                  PRAGMA foreign_keys = ON;
                  """;
            cmd.ExecuteNonQuery();
            if (needsWalInit && !isMemory)
            {
                WalInitializedPaths.TryAdd(fullPath, 0);
            }
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public static SqliteConnection OpenReadOnly(string dbPath, int busyTimeoutMs = DefaultBusyTimeoutMs)
    {
        int clampedMs = Math.Clamp(busyTimeoutMs, 500, 60_000);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = Math.Max(1, (clampedMs + 999) / 1000)
        };
        var conn = new SqliteConnection(csb.ToString());
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                PRAGMA busy_timeout = {clampedMs};
                PRAGMA query_only = ON;
                """;
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public static SqliteTransaction BeginImmediateTransaction(SqliteConnection conn) =>
        conn.BeginTransaction(deferred: false);

    public static void CreateConsistentSnapshot(string sourceDbPath, string snapshotFilePath)
    {
        string? snapDir = Path.GetDirectoryName(Path.GetFullPath(snapshotFilePath));
        if (!string.IsNullOrWhiteSpace(snapDir))
        {
            Directory.CreateDirectory(snapDir);
        }

        if (File.Exists(snapshotFilePath))
        {
            File.Delete(snapshotFilePath);
        }

        using var conn = OpenReadWrite(sourceDbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dst;";
        cmd.Parameters.AddWithValue("$dst", snapshotFilePath);
        cmd.ExecuteNonQuery();
    }
}
