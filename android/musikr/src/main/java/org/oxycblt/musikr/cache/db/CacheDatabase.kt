/*
 * Copyright (c) 2023 Auxio Project
 * CacheDatabase.kt is part of Auxio.
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
 
package org.oxycblt.musikr.cache.db

import android.content.Context
import android.net.Uri
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.TypeConverter
import androidx.room.TypeConverters
import androidx.room.migration.Migration
import org.oxycblt.musikr.tag.Date
import org.oxycblt.musikr.util.correctWhitespace
import org.oxycblt.musikr.util.splitEscaped

@Database(entities = [CachedFileData::class], version = 75, exportSchema = true)
internal abstract class CacheDatabase : RoomDatabase() {
    abstract fun readDao(): CacheReadDao

    abstract fun writeDao(): CacheWriteDao

    companion object {
        val MIGRATION_70_73 =
            Migration(70, 73) { db ->
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN composerNames TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN conductorNames TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN djVocals TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN djEnergy TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN djScenes TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN workName TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN movementName TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN movementNumber INTEGER")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN musicBrainzWorkId TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN moods TEXT")
            }

        val MIGRATION_73_74 =
            Migration(73, 74) { db ->
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN tempoBpm REAL")
            }

        val MIGRATION_74_75 =
            Migration(74, 75) { db ->
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN filePathVolume TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN filePathComponents TEXT")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN fileSize INTEGER")
                db.execSQL("ALTER TABLE CachedFileData ADD COLUMN fileMimeType TEXT")
            }

        fun from(context: Context) =
            Room.databaseBuilder(
                    context.applicationContext,
                    CacheDatabase::class.java,
                    "music_cache.db",
                )
                .addMigrations(MIGRATION_70_73, MIGRATION_73_74, MIGRATION_74_75)
                .fallbackToDestructiveMigration(true)
                .build()
    }
}

@Dao
internal interface CacheReadDao {
    @Query("SELECT * FROM CachedFileData") suspend fun selectAllSongs(): List<CachedFileData>
}

@Dao
internal interface CacheWriteDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE) suspend fun updateSong(data: CachedFileData)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun updateSongs(data: List<CachedFileData>)

    @Transaction
    suspend fun deleteExcludingUris(uris: Set<String>) {
        val delete = selectAllUris().toSet() - uris
        for (chunk in delete.chunked(999)) {
            deleteExcludingUriChunk(chunk)
        }
    }

    @Query("SELECT uri FROM CachedFileData") suspend fun selectAllUris(): List<String>

    @Query("DELETE FROM CachedFileData WHERE uri IN (:uris)")
    suspend fun deleteExcludingUriChunk(uris: List<String>)
}

@Entity
@TypeConverters(CachedFileData.Converters::class)
internal data class CachedFileData(
    @PrimaryKey val uri: Uri,
    val modifiedMs: Long,
    val addedMs: Long,
    val mimeType: String?,
    val durationMs: Long?,
    val bitrateKbps: Int?,
    val sampleRateHz: Int?,
    val musicBrainzId: String?,
    val name: String?,
    val sortName: String?,
    val track: Int?,
    val disc: Int?,
    val subtitle: String?,
    val date: Date?,
    val albumMusicBrainzId: String?,
    val albumName: String?,
    val albumSortName: String?,
    val releaseTypes: List<String>?,
    val artistMusicBrainzIds: List<String>?,
    val artistNames: List<String>?,
    val artistSortNames: List<String>?,
    val albumArtistMusicBrainzIds: List<String>?,
    val albumArtistNames: List<String>?,
    val albumArtistSortNames: List<String>?,
    val composerNames: List<String>?,
    val conductorNames: List<String>?,
    val genreNames: List<String>?,
    val replayGainTrackAdjustment: Float?,
    val replayGainAlbumAdjustment: Float?,
    val coverId: String?,
    val djVocals: String? = null,
    val djEnergy: String? = null,
    val djScenes: List<String>? = null,
    val workName: String? = null,
    val movementName: String? = null,
    val movementNumber: Int? = null,
    val musicBrainzWorkId: String? = null,
    val moods: List<String>? = null,
    val tempoBpm: Float? = null,
    val filePathVolume: String? = null,
    val filePathComponents: String? = null,
    val fileSize: Long? = null,
    val fileMimeType: String? = null,
) {
    object Converters {
        @TypeConverter
        fun fromMultiValue(values: List<String>) =
            values.joinToString(";") { it.replace("\\", "\\\\").replace(";", "\\;") }

        @TypeConverter
        fun toMultiValue(string: String) = string.splitEscaped { it == ';' }.correctWhitespace()

        @TypeConverter fun fromDate(date: Date?) = date?.toString()

        @TypeConverter fun toDate(string: String?) = string?.let(Date::from)

        @TypeConverter fun toUri(string: String) = Uri.parse(string)

        @TypeConverter fun fromUri(uri: Uri) = uri.toString()
    }
}
