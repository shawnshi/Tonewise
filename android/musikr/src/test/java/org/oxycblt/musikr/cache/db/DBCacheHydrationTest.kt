/*
 * Copyright (c) 2026 Tonewise Project
 * DBCacheHydrationTest.kt is part of Tonewise.
 */

package org.oxycblt.musikr.cache.db

import android.content.Context
import android.net.Uri
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.just
import io.mockk.mockk
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.oxycblt.musikr.cache.Audio
import org.oxycblt.musikr.cache.CacheResult
import org.oxycblt.musikr.cache.CachedFile
import org.oxycblt.musikr.fs.AddedMs
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.fs.File
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.fs.Volume
import org.oxycblt.musikr.metadata.Properties
import org.oxycblt.musikr.tag.Date
import org.oxycblt.musikr.tag.interpret.Naming
import org.oxycblt.musikr.tag.parse.ParsedTags
import org.oxycblt.musikr.util.update
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class DBCacheHydrationTest {
    private val testVolume =
        object : Volume.Internal {
            override val mediaStoreName: String = "external_primary"
            override val components: Components = Components.parseUnix("storage/emulated/0")

            override fun resolveName(context: Context) = "Internal"
        }

    private fun makeCachedFileData(
        uriString: String,
        filePathVolume: String?,
        filePathComponents: String?,
        fileSize: Long?,
        fileMimeType: String?,
    ) =
        CachedFileData(
            uri = Uri.parse(uriString),
            modifiedMs = 1_763_000_000_000L,
            addedMs = 1_763_000_000_000L,
            mimeType = "audio/flac",
            durationMs = 215_000L,
            bitrateKbps = 980,
            sampleRateHz = 44_100,
            musicBrainzId = null,
            name = "Goldberg Aria",
            sortName = null,
            track = 1,
            disc = 1,
            subtitle = null,
            date = Date.from("1981"),
            albumMusicBrainzId = null,
            albumName = "Goldberg Variations",
            albumSortName = null,
            releaseTypes = emptyList(),
            artistMusicBrainzIds = emptyList(),
            artistNames = listOf("Glenn Gould"),
            artistSortNames = emptyList(),
            albumArtistMusicBrainzIds = emptyList(),
            albumArtistNames = listOf("Glenn Gould"),
            albumArtistSortNames = emptyList(),
            composerNames = listOf("Johann Sebastian Bach"),
            conductorNames = emptyList(),
            genreNames = listOf("Classical"),
            replayGainTrackAdjustment = -4.2f,
            replayGainAlbumAdjustment = -3.8f,
            coverId = "mcs:cover_1",
            djVocals = "instrumental",
            djEnergy = "low",
            djScenes = listOf("focus"),
            workName = "Goldberg Variations, BWV 988",
            movementName = "Aria",
            movementNumber = 1,
            musicBrainzWorkId = null,
            moods = listOf("Calm"),
            tempoBpm = 72.0f,
            filePathVolume = filePathVolume,
            filePathComponents = filePathComponents,
            fileSize = fileSize,
            fileMimeType = fileMimeType,
        )

    @Test
    fun readAll_hydratesCompleteEntriesAndSkipsLegacyIncompleteRows() = runTest {
        val complete =
            makeCachedFileData(
                uriString = "content://com.android.externalstorage.documents/tree/primary/document/aria.flac",
                filePathVolume = "internal",
                filePathComponents = "Music/Bach/01 Aria.flac",
                fileSize = 24_500_123L,
                fileMimeType = "audio/flac",
            )
        val legacyWithoutPath =
            makeCachedFileData(
                uriString = "content://com.android.externalstorage.documents/tree/primary/document/legacy.mp3",
                filePathVolume = null,
                filePathComponents = null,
                fileSize = null,
                fileMimeType = null,
            )

        val mockReadDao = mockk<CacheReadDao>()
        coEvery { mockReadDao.selectAllSongs() } returns listOf(complete, legacyWithoutPath)
        val dbCache =
            DBCache.from(mockReadDao) { encoded ->
                if (encoded == "internal") testVolume else null
            }

        val hydrated = dbCache.readAll()

        assertEquals(1, hydrated.size)
        val item = hydrated[0]
        assertEquals(complete.uri, item.file.uri)
        assertEquals(listOf("Music", "Bach", "01 Aria.flac"), item.file.path.components.components)
        assertEquals(testVolume, item.file.path.volume)
        assertEquals(24_500_123L, item.file.size)
        assertEquals("audio/flac", item.file.mimeType)
        assertEquals(complete.addedMs, item.file.addedMs.resolve())
        assertNotNull(item.audio)
        assertEquals("Goldberg Aria", item.audio?.tags?.name)
        assertEquals("mcs:cover_1", item.audio?.coverId)
    }

    @Test
    fun cleanup_backfillsLegacyEntriesMissingPathMetadataAndInvalidatesMapping() = runTest {
        val uri =
            Uri.parse("content://com.android.externalstorage.documents/tree/primary/document/legacy.flac")
        val legacyRow =
            makeCachedFileData(
                uriString = uri.toString(),
                filePathVolume = null,
                filePathComponents = null,
                fileSize = null,
                fileMimeType = null,
            )
        val updatedRow =
            legacyRow.copy(
                filePathVolume = "internal",
                filePathComponents = "Music/Bach/Legacy.flac",
                fileSize = 18_000_000L,
                fileMimeType = "audio/flac",
            )

        val mockReadDao = mockk<CacheReadDao>()
        coEvery { mockReadDao.selectAllSongs() } returnsMany listOf(listOf(legacyRow), listOf(updatedRow))

        val capturedWrites = mutableListOf<List<CachedFileData>>()
        val mockWriteDao = mockk<CacheWriteDao>()
        coEvery { mockWriteDao.updateSongs(capture(capturedWrites)) } just Runs
        coEvery { mockWriteDao.deleteExcludingUris(any()) } just Runs

        val dbCache =
            DBCache.from(mockReadDao) { encoded ->
                if (encoded == "internal") testVolume else null
            }
        val mutableCache = MutableDBCache.from(dbCache, mockWriteDao)

        // Before cleanup: legacy row lacks path metadata so readAll returns empty
        assertTrue(mutableCache.readAll().isEmpty())

        val excluding =
            listOf(
                CachedFile(
                    file =
                        File(
                            uri = uri,
                            path = Path(testVolume, Components.parseUnix("Music/Bach/Legacy.flac")),
                            addedMs =
                                object : AddedMs {
                                    override suspend fun resolve(): Long = 1_763_000_000_000L
                                },
                            modifiedMs = 1_763_000_000_000L,
                            mimeType = "audio/flac",
                            size = 18_000_000L,
                            parent = null,
                        ),
                    audio =
                        Audio(
                            Properties("audio/flac", 215_000L, 980, 44_100),
                            ParsedTags(durationMs = 215_000L, name = "Goldberg Aria"),
                            coverId = "mcs:cover_1",
                        ),
                    addedMs = 1_763_000_000_000L,
                )
            )

        mutableCache.cleanup(excluding)

        assertEquals(1, capturedWrites.size)
        assertEquals(1, capturedWrites[0].size)
        assertEquals("internal", capturedWrites[0][0].filePathVolume)
        assertEquals("Music/Bach/Legacy.flac", capturedWrites[0][0].filePathComponents)
        assertEquals(18_000_000L, capturedWrites[0][0].fileSize)
        assertEquals("audio/flac", capturedWrites[0][0].fileMimeType)
        coVerify(exactly = 1) { mockWriteDao.deleteExcludingUris(setOf(uri.toString())) }

        // After cleanup invalidated mapping, second readAll sees updatedRow
        val afterCleanup = mutableCache.readAll()
        assertEquals(1, afterCleanup.size)
        assertEquals("Music/Bach/Legacy.flac", afterCleanup[0].file.path.components.components.joinToString("/"))
    }

    @Test
    fun read_returnsStaleWhenModifiedMsMatchesButFileSizeChanges() = runTest {
        val uriString = "content://com.android.externalstorage.documents/tree/primary/document/aria.flac"
        val row =
            makeCachedFileData(
                uriString = uriString,
                filePathVolume = "internal",
                filePathComponents = "Music/Bach/01 Aria.flac",
                fileSize = 24_500_000L,
                fileMimeType = "audio/flac",
            )
        val mockReadDao = mockk<CacheReadDao>()
        coEvery { mockReadDao.selectAllSongs() } returns listOf(row)
        val dbCache =
            DBCache.from(mockReadDao) { encoded ->
                if (encoded == "internal") testVolume else null
            }

        val diskFile =
            File(
                uri = Uri.parse(uriString),
                path = Path(testVolume, Components.parseUnix("Music/Bach/01 Aria.flac")),
                addedMs =
                    object : AddedMs {
                        override suspend fun resolve(): Long = row.addedMs
                    },
                modifiedMs = row.modifiedMs,
                mimeType = "audio/flac",
                size = 24_500_512L,
                parent = null,
            )

        val result = dbCache.read(diskFile)
        assertTrue(result is CacheResult.Stale)
        assertEquals(row.addedMs, (result as CacheResult.Stale).addedMs)
    }

    @Test
    fun musicGraph_keepsDistinctUrisOnV363UidCollision() {
        val baseUri = Uri.parse("content://test/track1.flac")
        val dupUri = Uri.parse("content://test/track2.mp3")
        val songUid = org.oxycblt.musikr.Music.UID.auxio(org.oxycblt.musikr.Music.UID.Item.SONG) {
            update("Same Title")
        }
        val genre = org.oxycblt.musikr.tag.interpret.PreGenre(
            Naming.simple().name("Classical", null),
            "Classical",
        )
        val artist = org.oxycblt.musikr.tag.interpret.PreArtist(
            null,
            Naming.simple().name("Glenn Gould", null),
            "Glenn Gould",
        )
        val album = org.oxycblt.musikr.tag.interpret.PreAlbum(
            null,
            Naming.simple().name("Goldberg", null),
            "Goldberg",
            org.oxycblt.musikr.tag.ReleaseType.Album(null),
            org.oxycblt.musikr.tag.interpret.PreArtistsFrom.Individual(listOf(artist)),
        )
        fun makePre(uri: Uri) =
            org.oxycblt.musikr.tag.interpret.PreSong(
                v363Uid = songUid,
                v400Uid = songUid,
                v401Uid = songUid,
                musicBrainzId = null,
                name = Naming.simple().name("Same Title", null),
                rawName = "Same Title",
                track = 1,
                disc = null,
                date = null,
                uri = uri,
                path = Path(testVolume, Components.parseUnix("Music/${uri.lastPathSegment}")),
                format = org.oxycblt.musikr.fs.Format.FLAC,
                size = 1000L,
                durationMs = 10000L,
                bitrateKbps = 320,
                sampleRateHz = 44100,
                replayGainAdjustment = org.oxycblt.musikr.tag.ReplayGainAdjustment(null, null),
                modifiedMs = 1L,
                addedMs = 1L,
                cover = null,
                preAlbum = album,
                preArtists = listOf(artist),
                preGenres = listOf(genre),
                composers = emptyList(),
                conductors = emptyList(),
            )

        val builder = org.oxycblt.musikr.graph.MusicGraph.builder()
        builder.add(makePre(baseUri))
        builder.add(makePre(dupUri))
        val graph = builder.build()

        assertEquals(2, graph.songVertex.size)
        assertEquals(setOf(baseUri, dupUri), graph.songVertex.map { it.preSong.uri }.toSet())
    }
}
