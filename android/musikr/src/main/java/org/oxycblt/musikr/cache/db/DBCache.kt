/*
 * Copyright (c) 2025 Auxio Project
 * DBCache.kt is part of Auxio.
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
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.oxycblt.musikr.cache.Audio
import org.oxycblt.musikr.cache.Cache
import org.oxycblt.musikr.cache.CacheResult
import org.oxycblt.musikr.cache.CachedFile
import org.oxycblt.musikr.cache.MutableCache
import org.oxycblt.musikr.fs.AddedMs
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.fs.File
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.fs.Volume
import org.oxycblt.musikr.fs.path.VolumeManager
import org.oxycblt.musikr.metadata.Properties
import org.oxycblt.musikr.tag.parse.ParsedTags

private const val VOLUME_INTERNAL = "internal"
private const val VOLUME_EXTERNAL_PREFIX = "external:"
private const val VOLUME_THIRDPARTY_PREFIX = "thirdparty:"

internal fun serializeVolume(volume: Any?): String? =
    when (volume) {
        is Volume.Internal -> VOLUME_INTERNAL
        is Volume.External -> "$VOLUME_EXTERNAL_PREFIX${volume.id ?: ""}"
        is Volume.ThirdParty -> "$VOLUME_THIRDPARTY_PREFIX${volume.uri}"
        else -> null
    }

internal fun deserializeVolume(
    encoded: String?,
    volumeManager: VolumeManager?,
    knownVolumes: List<Volume>? = null,
): Volume? {
    if (encoded.isNullOrBlank()) return null
    return when {
        encoded == VOLUME_INTERNAL -> volumeManager?.getInternalVolume()
        encoded.startsWith(VOLUME_EXTERNAL_PREFIX) -> {
            val id = encoded.removePrefix(VOLUME_EXTERNAL_PREFIX)
            val volumes = knownVolumes ?: volumeManager?.getVolumes().orEmpty()
            volumes.filterIsInstance<Volume.External>().firstOrNull { (it.id ?: "") == id }
        }
        encoded.startsWith(VOLUME_THIRDPARTY_PREFIX) -> {
            val uriStr = encoded.removePrefix(VOLUME_THIRDPARTY_PREFIX)
            if (uriStr.isBlank()) null else Volume.ThirdParty(Uri.parse(uriStr))
        }
        else -> null
    }
}

private class StaticAddedMs(private val value: Long) : AddedMs {
    override suspend fun resolve(): Long = value
}

/**
 * An immutable [Cache] backed by an internal Room database.
 *
 * Create an instance with [from].
 */
class DBCache
private constructor(
    private val readDao: CacheReadDao,
    private val volumeResolverFactory: () -> ((String?) -> Volume?) = { { null } },
) : Cache {
    private var mapping: Map<Uri, CachedFileData>? = null
    private val mappingLock = Mutex()

    private suspend fun getOrLoadMapping(): Map<Uri, CachedFileData> =
        mappingLock.withLock {
            mapping ?: readDao.selectAllSongs().associateBy { it.uri }.also { mapping = it }
        }

    override suspend fun read(file: File): CacheResult {
        val currentMapping = getOrLoadMapping()
        val dbSong = currentMapping[file.uri] ?: return CacheResult.Miss(file)
        val sizeChanged =
            dbSong.fileSize != null &&
                dbSong.fileSize > 0L &&
                file.size > 0L &&
                dbSong.fileSize != file.size
        if (dbSong.modifiedMs != file.modifiedMs || sizeChanged) {
            return CacheResult.Stale(file, dbSong.addedMs)
        }
        val song = CachedFile(file, dbSong.toAudio(), addedMs = dbSong.addedMs)
        return CacheResult.Hit(song)
    }

    override suspend fun readAll(): List<CachedFile> {
        val currentMapping = getOrLoadMapping()
        if (currentMapping.isEmpty()) return emptyList()
        val resolveVolume = volumeResolverFactory()
        val result = ArrayList<CachedFile>(currentMapping.size)
        for (dbSong in currentMapping.values) {
            val audio = dbSong.toAudio() ?: continue
            val rawComponents = dbSong.filePathComponents?.takeIf { it.isNotBlank() } ?: continue
            val fileMimeType = dbSong.fileMimeType?.takeIf { it.isNotBlank() } ?: continue
            val volume = resolveVolume(dbSong.filePathVolume) ?: continue
            val path = Path(volume, Components.parseUnix(rawComponents))
            val file =
                File(
                    uri = dbSong.uri,
                    path = path,
                    addedMs = StaticAddedMs(dbSong.addedMs),
                    modifiedMs = dbSong.modifiedMs,
                    mimeType = fileMimeType,
                    size = dbSong.fileSize ?: 0L,
                    parent = null,
                )
            result.add(CachedFile(file = file, audio = audio, addedMs = dbSong.addedMs))
        }
        return result
    }

    internal suspend fun invalidate() {
        mappingLock.withLock { mapping = null }
    }

    internal suspend fun findEntriesNeedingMetadataBackfill(
        excluding: List<CachedFile>
    ): List<CachedFile> {
        if (excluding.isEmpty()) return emptyList()
        val currentMapping = getOrLoadMapping()
        val backfill = mutableListOf<CachedFile>()
        for (cf in excluding) {
            val existing = currentMapping[cf.file.uri] ?: continue
            val encodedVolume = serializePathVolumeSafe(cf.file)
            val encodedComponents = serializePathComponentsSafe(cf.file)
            if (
                existing.filePathVolume == null ||
                    existing.filePathComponents == null ||
                    existing.fileSize == null ||
                    existing.fileMimeType == null ||
                    existing.filePathVolume != encodedVolume ||
                    existing.filePathComponents != encodedComponents ||
                    existing.fileSize != cf.file.size ||
                    existing.fileMimeType != cf.file.mimeType
            ) {
                backfill.add(cf)
            }
        }
        return backfill
    }

    companion object {
        /**
         * Create a new instance of [DBCache] from the given [context].
         *
         * This instance should be a singleton, since it implicitly holds a Room database. As a
         * result, you should only create EITHER a [DBCache] or a [MutableDBCache].
         *
         * @param context The context to use to create the Room database.
         * @return A new instance of [DBCache].
         */
        fun from(context: Context) = from(context, CacheDatabase.from(context))

        internal fun from(context: Context, db: CacheDatabase): DBCache {
            val volumeManager = VolumeManager.from(context)
            return DBCache(db.readDao()) {
                val knownVolumes = volumeManager.getVolumes()
                val internalVolume = volumeManager.getInternalVolume()
                val resolver: (String?) -> Volume? = { encoded ->
                    if (encoded == VOLUME_INTERNAL) {
                        internalVolume
                    } else {
                        deserializeVolume(encoded, volumeManager, knownVolumes)
                    }
                }
                resolver
            }
        }

        internal fun from(db: CacheDatabase) = DBCache(db.readDao())

        internal fun from(
            readDao: CacheReadDao,
            volumeResolver: (String?) -> Volume? = { null },
        ) = DBCache(readDao) { volumeResolver }
    }
}

/**
 * A mutable [Cache] backed by an internal Room database.
 *
 * Create an instance with [from].
 */
class MutableDBCache
private constructor(private val inner: DBCache, private val writeDao: CacheWriteDao) :
    MutableCache {
    override suspend fun read(file: File) = inner.read(file)

    override suspend fun readAll(): List<CachedFile> = inner.readAll()

    override suspend fun write(cachedFile: CachedFile) {
        writeDao.updateSong(cachedFile.toDbData())
        inner.invalidate()
    }

    override suspend fun writeAll(cachedFiles: List<CachedFile>) {
        if (cachedFiles.isEmpty()) return
        for (chunk in cachedFiles.chunked(BATCH_SIZE)) {
            writeDao.updateSongs(chunk.map { it.toDbData() })
        }
        inner.invalidate()
    }

    override suspend fun cleanup(excluding: List<CachedFile>) {
        val backfill = inner.findEntriesNeedingMetadataBackfill(excluding)
        if (backfill.isNotEmpty()) {
            writeAll(backfill)
        }
        writeDao.deleteExcludingUris(excluding.mapTo(mutableSetOf()) { it.file.uri.toString() })
        inner.invalidate()
    }

    companion object {
        /**
         * Create a new instance of [MutableDBCache] from the given [context].
         *
         * This instance should be a singleton, since it implicitly holds a Room database. As a
         * result, you should only create EITHER a [DBCache] or a [MutableDBCache].
         *
         * @param context The context to use to create the Room database.
         * @return A new instance of [MutableDBCache].
         */
        fun from(context: Context): MutableDBCache {
            val db = CacheDatabase.from(context)
            return MutableDBCache(DBCache.from(context, db), db.writeDao())
        }

        internal fun from(inner: DBCache, writeDao: CacheWriteDao) = MutableDBCache(inner, writeDao)

        private const val BATCH_SIZE = 500
    }
}

private fun CachedFileData.toAudio(): Audio? {
    val mime = mimeType ?: return null
    return Audio(
        Properties(mime, durationMs!!, bitrateKbps!!, sampleRateHz!!),
        ParsedTags(
            musicBrainzId = musicBrainzId,
            name = name,
            sortName = sortName,
            durationMs = durationMs,
            track = track,
            disc = disc,
            subtitle = subtitle,
            date = date,
            albumMusicBrainzId = albumMusicBrainzId,
            albumName = albumName,
            albumSortName = albumSortName,
            releaseTypes = releaseTypes!!,
            artistMusicBrainzIds = artistMusicBrainzIds!!,
            artistNames = artistNames!!,
            artistSortNames = artistSortNames!!,
            albumArtistMusicBrainzIds = albumArtistMusicBrainzIds!!,
            albumArtistNames = albumArtistNames!!,
            albumArtistSortNames = albumArtistSortNames!!,
            composerNames = composerNames ?: listOf(),
            conductorNames = conductorNames ?: listOf(),
            genreNames = genreNames!!,
            replayGainTrackAdjustment = replayGainTrackAdjustment,
            replayGainAlbumAdjustment = replayGainAlbumAdjustment,
            djVocals = djVocals,
            djEnergy = djEnergy,
            djScenes = djScenes ?: listOf(),
            workName = workName,
            movementName = movementName,
            movementNumber = movementNumber,
            musicBrainzWorkId = musicBrainzWorkId,
            moods = moods ?: listOf(),
            tempoBpm = tempoBpm,
        ),
        coverId = coverId,
    )
}

private fun serializePathVolumeSafe(file: File): String? =
    runCatching { serializeVolume(file.path.volume) }.getOrNull()

private fun serializePathComponentsSafe(file: File): String? =
    runCatching { file.path.components.components.takeIf { it.isNotEmpty() }?.joinToString("/") }
        .getOrNull()

private fun CachedFile.toDbData() =
    CachedFileData(
        uri = file.uri,
        modifiedMs = file.modifiedMs,
        addedMs = addedMs,
        mimeType = audio?.properties?.mimeType,
        durationMs = audio?.properties?.durationMs,
        bitrateKbps = audio?.properties?.bitrateKbps,
        sampleRateHz = audio?.properties?.sampleRateHz,
        musicBrainzId = audio?.tags?.musicBrainzId,
        name = audio?.tags?.name,
        sortName = audio?.tags?.sortName,
        track = audio?.tags?.track,
        disc = audio?.tags?.disc,
        subtitle = audio?.tags?.subtitle,
        date = audio?.tags?.date,
        albumMusicBrainzId = audio?.tags?.albumMusicBrainzId,
        albumName = audio?.tags?.albumName,
        albumSortName = audio?.tags?.albumSortName,
        releaseTypes = audio?.tags?.releaseTypes,
        artistMusicBrainzIds = audio?.tags?.artistMusicBrainzIds,
        artistNames = audio?.tags?.artistNames,
        artistSortNames = audio?.tags?.artistSortNames,
        albumArtistMusicBrainzIds = audio?.tags?.albumArtistMusicBrainzIds,
        albumArtistNames = audio?.tags?.albumArtistNames,
        albumArtistSortNames = audio?.tags?.albumArtistSortNames,
        composerNames = audio?.tags?.composerNames,
        conductorNames = audio?.tags?.conductorNames,
        genreNames = audio?.tags?.genreNames,
        replayGainTrackAdjustment = audio?.tags?.replayGainTrackAdjustment,
        replayGainAlbumAdjustment = audio?.tags?.replayGainAlbumAdjustment,
        coverId = audio?.coverId,
        djVocals = audio?.tags?.djVocals,
        djEnergy = audio?.tags?.djEnergy,
        djScenes = audio?.tags?.djScenes,
        workName = audio?.tags?.workName,
        movementName = audio?.tags?.movementName,
        movementNumber = audio?.tags?.movementNumber,
        musicBrainzWorkId = audio?.tags?.musicBrainzWorkId,
        moods = audio?.tags?.moods,
        tempoBpm = audio?.tags?.tempoBpm,
        filePathVolume = serializePathVolumeSafe(file),
        filePathComponents = serializePathComponentsSafe(file),
        fileSize = runCatching { file.size }.getOrNull(),
        fileMimeType = runCatching { file.mimeType }.getOrNull()?.takeIf { it.isNotEmpty() },
    )
