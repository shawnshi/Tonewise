/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseSyncDatabase.kt is part of Tonewise.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

package com.shawnshi.tonewise.sync

import android.content.Context
import androidx.core.content.edit
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton
import org.json.JSONArray
import org.json.JSONObject

@Entity(tableName = "track_play_ledger")
data class TrackPlayLedgerEntity(
    @PrimaryKey val trackUid: String,
    val playCount: Int = 0,
    val lastPlayedMs: Long = 0L,
    val loved: Boolean = false,
    val syncedCount: Int = 0,
    val pulledCloudCount: Int = 0,
)

@Entity(tableName = "lww_overrides", primaryKeys = ["trackUid", "field"])
data class LwwOverrideEntity(
    val trackUid: String,
    val field: String,
    val value: String,
)

@Entity(tableName = "pending_tag_mutations", primaryKeys = ["trackUid", "field"])
data class PendingTagMutationEntity(
    val trackUid: String,
    val field: String,
    val value: String,
    val updatedAtMs: Long,
    val deviceId: String,
)

@Entity(tableName = "pending_scrobbles")
data class PendingScrobbleEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0L,
    val songUid: String,
    val artist: String,
    val track: String,
    val album: String,
    val durationSec: Int,
    val timestampSec: Long,
)

@Entity(tableName = "seen_cloud_stamps")
data class SeenCloudStampEntity(
    @PrimaryKey val stamp: String,
    val createdAtMs: Long,
)

@Entity(tableName = "bio_cache")
data class BioCacheEntity(
    @PrimaryKey val cacheKey: String,
    val summary: String,
    val updatedAtMs: Long,
)

@Entity(tableName = "sync_kv")
data class SyncKvEntity(
    @PrimaryKey val key: String,
    val value: String,
)

@Entity(tableName = "audio_fingerprint_cache")
data class AudioFingerprintCacheEntity(
    @PrimaryKey val trackUid: String,
    val fileSize: Long,
    val audioFrameBlake3Hex: String,
    val headerBlake3Hex: String,
    val headerSize: Long,
    val updatedAtMs: Long,
)

@Dao
interface TrackPlayLedgerDao {
    @Query("SELECT * FROM track_play_ledger WHERE trackUid = :uid LIMIT 1")
    fun getByUid(uid: String): TrackPlayLedgerEntity?

    @Query("SELECT playCount FROM track_play_ledger WHERE trackUid = :uid LIMIT 1")
    fun getPlayCount(uid: String): Int?

    @Query("SELECT * FROM track_play_ledger")
    fun getAll(): List<TrackPlayLedgerEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsert(entity: TrackPlayLedgerEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertAll(entities: List<TrackPlayLedgerEntity>)
}

@Dao
interface LwwMutationDao {
    @Query("SELECT * FROM lww_overrides")
    fun getAllOverrides(): List<LwwOverrideEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertOverride(entity: LwwOverrideEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertOverrides(entities: List<LwwOverrideEntity>)

    @Query("SELECT * FROM pending_tag_mutations ORDER BY updatedAtMs ASC")
    fun getAllPendingMutations(): List<PendingTagMutationEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertPendingMutation(entity: PendingTagMutationEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertPendingMutations(entities: List<PendingTagMutationEntity>)

    @Query("DELETE FROM pending_tag_mutations WHERE trackUid = :uid AND field = :field")
    fun deletePendingMutation(uid: String, field: String)
}

@Dao
interface LastFmLedgerDao {
    @Query("SELECT * FROM pending_scrobbles ORDER BY timestampSec ASC, id ASC")
    fun getAllPendingScrobbles(): List<PendingScrobbleEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun insertScrobble(entity: PendingScrobbleEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun insertScrobbles(entities: List<PendingScrobbleEntity>)

    @Query("DELETE FROM pending_scrobbles")
    fun clearPendingScrobbles()

    @Query("SELECT stamp FROM seen_cloud_stamps ORDER BY createdAtMs ASC")
    fun getAllSeenStamps(): List<String>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    fun insertSeenStamps(entities: List<SeenCloudStampEntity>)

    @Query("DELETE FROM seen_cloud_stamps WHERE stamp NOT IN (SELECT stamp FROM seen_cloud_stamps ORDER BY createdAtMs DESC LIMIT 2000)")
    fun pruneOldSeenStamps()
}

@Dao
interface BioCacheDao {
    @Query("SELECT summary FROM bio_cache WHERE cacheKey = :key LIMIT 1")
    fun getSummary(key: String): String?

    @Query("SELECT * FROM bio_cache")
    fun getAll(): List<BioCacheEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertAll(entities: List<BioCacheEntity>)
}

@Dao
interface SyncKvDao {
    @Query("SELECT value FROM sync_kv WHERE `key` = :key LIMIT 1")
    fun getValue(key: String): String?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun putValue(entity: SyncKvEntity)
}

@Dao
interface AudioFingerprintDao {
    @Query("SELECT * FROM audio_fingerprint_cache WHERE trackUid = :uid LIMIT 1")
    fun getByUid(uid: String): AudioFingerprintCacheEntity?

    @Query("SELECT * FROM audio_fingerprint_cache")
    fun getAll(): List<AudioFingerprintCacheEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsert(entity: AudioFingerprintCacheEntity)
}

@Database(
    entities = [
        TrackPlayLedgerEntity::class,
        LwwOverrideEntity::class,
        PendingTagMutationEntity::class,
        PendingScrobbleEntity::class,
        SeenCloudStampEntity::class,
        BioCacheEntity::class,
        SyncKvEntity::class,
        AudioFingerprintCacheEntity::class,
    ],
    version = 2,
    exportSchema = false,
)
abstract class TonewiseSyncDatabase : RoomDatabase() {
    abstract fun playLedgerDao(): TrackPlayLedgerDao

    abstract fun lwwMutationDao(): LwwMutationDao

    abstract fun lastFmLedgerDao(): LastFmLedgerDao

    abstract fun bioCacheDao(): BioCacheDao

    abstract fun syncKvDao(): SyncKvDao

    abstract fun audioFingerprintDao(): AudioFingerprintDao

    companion object {
        private const val KEY_MIGRATED_PREFS_V1 = "migrated_prefs_v1"
        private const val KEY_MIGRATED_SESSION_SLOTS_V1 = "migrated_session_slots_v1"

        private val MIGRATION_1_2 =
            object : Migration(1, 2) {
                override fun migrate(db: SupportSQLiteDatabase) {
                    db.execSQL(
                        """
                        CREATE TABLE IF NOT EXISTS `audio_fingerprint_cache` (
                            `trackUid` TEXT NOT NULL,
                            `fileSize` INTEGER NOT NULL,
                            `audioFrameBlake3Hex` TEXT NOT NULL,
                            `headerBlake3Hex` TEXT NOT NULL,
                            `headerSize` INTEGER NOT NULL,
                            `updatedAtMs` INTEGER NOT NULL,
                            PRIMARY KEY(`trackUid`)
                        )
                        """.trimIndent()
                    )
                }
            }

        @Volatile private var instance: TonewiseSyncDatabase? = null

        fun getInstance(context: Context): TonewiseSyncDatabase {
            instance?.let { return it }
            return synchronized(this) {
                instance
                    ?: Room.databaseBuilder(
                            context.applicationContext,
                            TonewiseSyncDatabase::class.java,
                            "tonewise_sync.db",
                        )
                        .addMigrations(MIGRATION_1_2)
                        .allowMainThreadQueries()
                        .build()
                        .also { db ->
                            db.migrateLegacyPrefsIfNeeded(context.applicationContext)
                            db.migrateLegacySessionSlotsIfNeeded(context.applicationContext)
                            instance = db
                        }
            }
        }
    }

    private fun migrateLegacyPrefsIfNeeded(context: Context) {
        if (syncKvDao().getValue(KEY_MIGRATED_PREFS_V1) == "1") return

        runInTransaction {
            if (syncKvDao().getValue(KEY_MIGRATED_PREFS_V1) == "1") return@runInTransaction

            val ledgerMap = HashMap<String, TrackPlayLedgerEntity>()
            fun updateLedger(uid: String, block: (TrackPlayLedgerEntity) -> TrackPlayLedgerEntity) {
                val prev = ledgerMap[uid] ?: TrackPlayLedgerEntity(trackUid = uid)
                ledgerMap[uid] = block(prev)
            }

            // 1. tonewise_play_counts
            val playPrefs = context.getSharedPreferences("tonewise_play_counts", Context.MODE_PRIVATE)
            for ((k, v) in playPrefs.all) {
                when {
                    k.startsWith("last_played_ms_") && v is Long -> {
                        val uid = k.removePrefix("last_played_ms_")
                        updateLedger(uid) { it.copy(lastPlayedMs = v) }
                    }
                    k.startsWith("loved_") && v is Boolean -> {
                        val uid = k.removePrefix("loved_")
                        updateLedger(uid) { it.copy(loved = v) }
                    }
                    v is Int -> {
                        updateLedger(k) { it.copy(playCount = v) }
                    }
                }
            }

            // 2. tonewise_lastfm (ledger collections only; credentials stay in tonewise_lastfm)
            val lfmPrefs = context.getSharedPreferences("tonewise_lastfm", Context.MODE_PRIVATE)
            (lfmPrefs.getString("synced_counts_json", null))?.let { raw ->
                runCatching {
                    val obj = JSONObject(raw)
                    val keys = obj.keys()
                    while (keys.hasNext()) {
                        val uid = keys.next()
                        val cnt = obj.optInt(uid, 0)
                        updateLedger(uid) { it.copy(syncedCount = cnt) }
                    }
                }
            }
            (lfmPrefs.getString("pulled_counts_json", null))?.let { raw ->
                runCatching {
                    val obj = JSONObject(raw)
                    val keys = obj.keys()
                    while (keys.hasNext()) {
                        val uid = keys.next()
                        val cnt = obj.optInt(uid, 0)
                        updateLedger(uid) { it.copy(pulledCloudCount = cnt) }
                    }
                }
            }
            if (ledgerMap.isNotEmpty()) {
                playLedgerDao().upsertAll(ledgerMap.values.toList())
            }

            (lfmPrefs.getString("pending_scrobbles_json", null))?.let { raw ->
                runCatching {
                    val arr = JSONArray(raw)
                    val list =
                        List(arr.length()) { i ->
                            val o = arr.getJSONObject(i)
                            PendingScrobbleEntity(
                                songUid = o.getString("uid"),
                                artist = o.getString("artist"),
                                track = o.getString("track"),
                                album = o.optString("album", ""),
                                durationSec = o.optInt("duration", 0),
                                timestampSec = o.getLong("ts"),
                            )
                        }
                    if (list.isNotEmpty()) lastFmLedgerDao().insertScrobbles(list)
                }
            }
            (lfmPrefs.getString("seen_cloud_stamps_json", null))?.let { raw ->
                runCatching {
                    val arr = JSONArray(raw)
                    val now = System.currentTimeMillis()
                    val list =
                        List(arr.length()) { i ->
                            SeenCloudStampEntity(stamp = arr.getString(i), createdAtMs = now + i)
                        }
                    if (list.isNotEmpty()) lastFmLedgerDao().insertSeenStamps(list)
                }
            }
            val lastPullFrom = lfmPrefs.getLong("last_pull_from_sec", 0L)
            if (lastPullFrom > 0L) {
                syncKvDao().putValue(SyncKvEntity("last_pull_from_sec", lastPullFrom.toString()))
            }

            // 3. tonewise_lww_mutations
            val mutPrefs = context.getSharedPreferences("tonewise_lww_mutations", Context.MODE_PRIVATE)
            (mutPrefs.getString("lww_overrides", null))?.let { raw ->
                runCatching {
                    val obj = JSONObject(raw)
                    val overrides = mutableListOf<LwwOverrideEntity>()
                    val keys = obj.keys()
                    while (keys.hasNext()) {
                        val k = keys.next()
                        val parts = k.split("||", limit = 2)
                        if (parts.size == 2) {
                            overrides.add(LwwOverrideEntity(parts[0], parts[1], obj.optString(k, "")))
                        }
                    }
                    if (overrides.isNotEmpty()) lwwMutationDao().upsertOverrides(overrides)
                }
            }
            (mutPrefs.getString("pending_mutations", null))?.let { raw ->
                runCatching {
                    val arr = JSONArray(raw)
                    val muts =
                        List(arr.length()) { i ->
                            val o = arr.getJSONObject(i)
                            PendingTagMutationEntity(
                                trackUid = o.getString("track_uid"),
                                field = o.getString("field"),
                                value = o.getString("value"),
                                updatedAtMs = o.getLong("updated_at_ms"),
                                deviceId = o.optString("device_id", "android"),
                            )
                        }
                    if (muts.isNotEmpty()) lwwMutationDao().upsertPendingMutations(muts)
                }
            }

            // 4. tonewise_scene_order
            val scenePrefs = context.getSharedPreferences("tonewise_scene_order", Context.MODE_PRIVATE)
            for ((k, v) in scenePrefs.all) {
                if (v is String) {
                    syncKvDao().putValue(SyncKvEntity(k, v))
                }
            }

            // 5. tonewise_bio_cache
            val bioPrefs = context.getSharedPreferences("tonewise_bio_cache", Context.MODE_PRIVATE)
            val now = System.currentTimeMillis()
            val bios =
                bioPrefs.all.entries.mapNotNull { (k, v) ->
                    (v as? String)?.let { BioCacheEntity(cacheKey = k, summary = it, updatedAtMs = now) }
                }
            if (bios.isNotEmpty()) {
                bioCacheDao().upsertAll(bios)
            }

            syncKvDao().putValue(SyncKvEntity(KEY_MIGRATED_PREFS_V1, "1"))

            // Clean migrated JSON blobs from tonewise_lastfm while keeping credentials intact
            lfmPrefs.edit {
                remove("pending_scrobbles_json")
                remove("synced_counts_json")
                remove("pulled_counts_json")
                remove("seen_cloud_stamps_json")
                remove("last_pull_from_sec")
            }
        }
    }

    private fun migrateLegacySessionSlotsIfNeeded(context: Context) {
        if (syncKvDao().getValue(KEY_MIGRATED_SESSION_SLOTS_V1) == "1") return

        runInTransaction {
            if (syncKvDao().getValue(KEY_MIGRATED_SESSION_SLOTS_V1) == "1") return@runInTransaction

            val slotPrefs = context.getSharedPreferences("tonewise_session_slots", Context.MODE_PRIVATE)
            slotPrefs.getString("slots_json", null)?.let { raw ->
                syncKvDao().putValue(SyncKvEntity("session_slots_json", raw))
            }
            slotPrefs.getString("active_scene_label", null)?.let { label ->
                syncKvDao().putValue(SyncKvEntity("session_active_scene_label", label))
            }
            if (slotPrefs.contains("active_slot_index")) {
                val idx = slotPrefs.getInt("active_slot_index", 1).coerceIn(1, 5)
                syncKvDao().putValue(SyncKvEntity("session_active_slot_index", idx.toString()))
            }

            syncKvDao().putValue(SyncKvEntity(KEY_MIGRATED_SESSION_SLOTS_V1, "1"))
            slotPrefs.edit { clear() }
        }
    }
}

@Module
@InstallIn(SingletonComponent::class)
class SyncRoomModule {
    @Singleton
    @Provides
    fun provideTonewiseSyncDatabase(@ApplicationContext context: Context): TonewiseSyncDatabase =
        TonewiseSyncDatabase.getInstance(context)
}
