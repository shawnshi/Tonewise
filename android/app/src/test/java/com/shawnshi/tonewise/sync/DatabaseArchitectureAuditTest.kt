/*
 * Copyright (c) 2026 Tonewise Project
 * DatabaseArchitectureAuditTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import androidx.sqlite.db.SupportSQLiteDatabase
import java.io.File
import java.lang.reflect.Proxy
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.core.TonewiseCore

class DatabaseArchitectureAuditTest {
    private fun findRepoRoot(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").canonicalFile
        while (dir != null) {
            if (File(dir, "protocol/schemas/manifest.sql").isFile && File(dir, "android").isDirectory) {
                return dir
            }
            dir = dir.parentFile
        }
        error("Could not locate Tonewise repo root from ${System.getProperty("user.dir")}")
    }

    @Suppress("UNCHECKED_CAST")
    private fun readExportedSchemaVersion(schemaFile: File): Int {
        val parsed = TonewiseCore.parseJson(schemaFile.readText(Charsets.UTF_8)) as Map<String, Any?>
        val dbObj = parsed["database"] as Map<String, Any?>
        return (dbObj["version"] as Number).toInt()
    }

    @Test
    fun roomDatabases_enableSchemaExport_andEmitSchemasOnDisk() {
        val repoRoot = findRepoRoot()
        val syncSchema =
            File(
                repoRoot,
                "android/app/schemas/com.shawnshi.tonewise.sync.TonewiseSyncDatabase/7.json",
            )
        val persistSchema =
            File(
                repoRoot,
                "android/app/schemas/com.shawnshi.tonewise.playback.persist.PersistenceDatabase/38.json",
            )
        val cacheSchema =
            File(repoRoot, "android/musikr/schemas/org.oxycblt.musikr.cache.db.CacheDatabase/75.json")
        val playlistSchema =
            File(
                repoRoot,
                "android/musikr/schemas/org.oxycblt.musikr.playlist.db.PlaylistDatabase/75.json",
            )

        assertTrue("Expected exported sync schema at ${syncSchema.absolutePath}", syncSchema.isFile)
        assertTrue("Expected exported persist schema at ${persistSchema.absolutePath}", persistSchema.isFile)
        assertTrue("Expected exported cache schema at ${cacheSchema.absolutePath}", cacheSchema.isFile)
        assertTrue("Expected exported playlist schema at ${playlistSchema.absolutePath}", playlistSchema.isFile)

        assertEquals(7, readExportedSchemaVersion(syncSchema))
        assertEquals(38, readExportedSchemaVersion(persistSchema))
        assertEquals(75, readExportedSchemaVersion(cacheSchema))
        assertEquals(75, readExportedSchemaVersion(playlistSchema))

        val syncSchemaText = syncSchema.readText(Charsets.UTF_8)
        assertTrue(syncSchemaText.contains("audioFrameBlake3Hex"))
        assertTrue(syncSchemaText.contains("index_track_play_ledger_audioFrameBlake3Hex"))
        assertTrue(syncSchemaText.contains("index_lww_overrides_audioFrameBlake3Hex"))
        assertTrue(syncSchemaText.contains("index_pending_tag_mutations_updatedAtMs"))
        assertTrue(syncSchemaText.contains("index_pending_scrobbles_timestampSec"))
        assertTrue(syncSchemaText.contains("index_seen_cloud_stamps_createdAtMs"))
        assertTrue(syncSchemaText.contains("index_audio_fingerprint_cache_audioFrameBlake3Hex"))
        assertTrue(syncSchemaText.contains("index_synced_tracks_relativePath"))
        assertTrue(syncSchemaText.contains("index_synced_tracks_audioFrameBlake3Hex"))
    }

    @Test
    fun syncDatabaseMigration6To7_createsFingerprintColumnsAndAllSecondaryIndexes() {
        assertEquals(6, TonewiseSyncDatabase.MIGRATION_6_7.startVersion)
        assertEquals(7, TonewiseSyncDatabase.MIGRATION_6_7.endVersion)

        val executedSql = mutableListOf<String>()
        val proxyDb =
            Proxy.newProxyInstance(
                SupportSQLiteDatabase::class.java.classLoader,
                arrayOf(SupportSQLiteDatabase::class.java),
            ) { _, method, args ->
                if (method.name == "execSQL" && args != null && args.isNotEmpty() && args[0] is String) {
                    executedSql.add(args[0] as String)
                }
                when (method.returnType) {
                    Boolean::class.javaPrimitiveType -> false
                    Int::class.javaPrimitiveType -> 0
                    Long::class.javaPrimitiveType -> 0L
                    else -> null
                }
            } as SupportSQLiteDatabase

        TonewiseSyncDatabase.MIGRATION_6_7.migrate(proxyDb)

        val joined = executedSql.joinToString("\n")
        assertTrue(joined.contains("ALTER TABLE `track_play_ledger` ADD COLUMN `audioFrameBlake3Hex`"))
        assertTrue(joined.contains("ALTER TABLE `lww_overrides` ADD COLUMN `audioFrameBlake3Hex`"))
        assertTrue(joined.contains("index_track_play_ledger_audioFrameBlake3Hex"))
        assertTrue(joined.contains("index_lww_overrides_audioFrameBlake3Hex"))
        assertTrue(joined.contains("index_pending_tag_mutations_updatedAtMs"))
        assertTrue(joined.contains("index_pending_scrobbles_timestampSec"))
        assertTrue(joined.contains("index_seen_cloud_stamps_createdAtMs"))
        assertTrue(joined.contains("index_audio_fingerprint_cache_audioFrameBlake3Hex"))
        assertTrue(joined.contains("index_synced_tracks_relativePath"))
        assertTrue(joined.contains("index_synced_tracks_audioFrameBlake3Hex"))
    }
}
