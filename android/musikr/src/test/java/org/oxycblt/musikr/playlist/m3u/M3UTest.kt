/*
 * Copyright (c) 2026 Tonewise Project
 * M3UTest.kt is part of Tonewise.
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
 
package org.oxycblt.musikr.playlist.m3u

import android.content.Context
import io.mockk.every
import io.mockk.mockk
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.fs.Volume
import org.oxycblt.musikr.fs.path.VolumeManager
import org.oxycblt.musikr.playlist.ExportConfig
import org.oxycblt.musikr.tag.Name
import org.oxycblt.musikr.tag.interpret.Naming

class M3UTest {
    private val fakeVolume =
        object : Volume.Internal {
            override val mediaStoreName = "external_primary"
            override val components = Components.parseUnix("/storage/emulated/0")

            override fun resolveName(context: Context) = "Internal"
        }

    @Test
    fun isM3uFile_matchesExtensionsAndMimeTypes() {
        assertTrue(M3U.isM3uFile("focus.m3u", "application/octet-stream"))
        assertTrue(M3U.isM3uFile("Deep_Focus.M3U8", "text/plain"))
        assertTrue(M3U.isM3uFile("playlist", "audio/x-mpegurl"))
        assertTrue(M3U.isM3uFile("playlist", "application/vnd.apple.mpegurl"))
        assertFalse(M3U.isM3uFile(".hidden.m3u", "audio/x-mpegurl"))
        assertFalse(M3U.isM3uFile("track01.flac", "audio/flac"))
    }

    @Test
    fun isInPlaylistDirectory_matchesPlaylistAndPlaylistsFoldersOnly() {
        val inPlaylist = Path(fakeVolume, Components.parseUnix("Music/playlist/jazz.m3u"))
        val inPlaylistsNested =
            Path(fakeVolume, Components.parseUnix("Music/Playlists/2026/late_night.m3u8"))
        val inRootPlaylist = Path(fakeVolume, Components.parseUnix("Playlist/commute.m3u"))
        val inAlbumDir = Path(fakeVolume, Components.parseUnix("Music/Bach/00 - Bach.m3u"))

        assertTrue(M3U.isInPlaylistDirectory(inPlaylist))
        assertTrue(M3U.isInPlaylistDirectory(inPlaylistsNested))
        assertTrue(M3U.isInPlaylistDirectory(inRootPlaylist))
        assertFalse(M3U.isInPlaylistDirectory(inAlbumDir))
    }

    @Test
    fun read_handlesUtf8BomAndRelativePaths() {
        val volumeManager = mockk<VolumeManager>()
        every { volumeManager.getVolumes() } returns listOf(fakeVolume)
        val m3u = M3U.from(volumeManager)
        val workingDir = Path(fakeVolume, Components.parseUnix("Music/playlist"))
        val content =
            "\uFEFF#EXTM3U\n" +
                "#PLAYLIST:深夜古典\n" +
                "#EXTINF:210,Prelude\n" +
                "../Bach/BWV1007/01_Prelude.flac\n"

        val imported =
            m3u.read(ByteArrayInputStream(content.toByteArray(Charsets.UTF_8)), workingDir)

        assertNotNull(imported)
        assertEquals("深夜古典", imported!!.name)
        assertEquals(1, imported.paths.size)
        val expectedPath =
            Path(fakeVolume, Components.parseUnix("Music/Bach/BWV1007/01_Prelude.flac"))
        assertTrue(imported.paths[0].contains(expectedPath))
    }

    @Test
    fun write_formatsExtinfDurationInSecondsWithArtists() {
        val volumeManager = mockk<VolumeManager>()
        every { volumeManager.getVolumes() } returns listOf(fakeVolume)
        val m3u = M3U.from(volumeManager)
        val naming = Naming.simple()

        val artist = mockk<Artist>()
        every { artist.name } returns naming.name("Glenn Gould", null)

        val album = mockk<Album>()
        every { album.name } returns naming.name("Goldberg Variations", null)

        val song = mockk<Song>()
        every { song.name } returns naming.name("Aria", null)
        every { song.durationMs } returns 210_450L
        every { song.artists } returns listOf(artist)
        every { song.album } returns album
        every { song.genres } returns emptyList()
        every { song.path } returns Path(fakeVolume, Components.parseUnix("Music/Bach/01_Aria.flac"))

        val playlist = mockk<Playlist>()
        every { playlist.name } returns naming.name("Midnight Bach", null)
        every { playlist.songs } returns listOf(song)

        val out = ByteArrayOutputStream()
        val workingDir = Path(fakeVolume, Components.parseUnix("Music/Playlists"))
        m3u.write(
            playlist = playlist,
            outputStream = out,
            workingDirectory = workingDir,
            config = ExportConfig(absolute = false, windowsPaths = false),
        )

        val text = out.toString(Charsets.UTF_8)
        val lines = text.lines()
        assertTrue("Expected #EXTINF:210,Glenn Gould - Aria in:\n$text", lines.contains("#EXTINF:210,Glenn Gould - Aria"))
        assertFalse("Duration should not be written in milliseconds", text.contains("#EXTINF:210450,"))
        assertTrue(
            "Expected relative track path in:\n$text",
            lines.any { it.replace('\\', '/') == "./../Bach/01_Aria.flac" },
        )
    }

    @Test
    fun read_handlesGb18030PercentEncodedAndExttwUnmatchedEntries() {
        val volumeManager = mockk<VolumeManager>()
        every { volumeManager.getVolumes() } returns listOf(fakeVolume)
        val m3u = M3U.from(volumeManager)
        val workingDir = Path(fakeVolume, Components.parseUnix("Music/Playlists"))

        val gbContent =
            "#EXTM3U\n" +
                "#PLAYLIST:华语经典\n" +
                "#EXTINF:269,周杰伦 - 晴天\n" +
                "../Jay/晴天.flac\n"
        val gbBytes = gbContent.toByteArray(java.nio.charset.Charset.forName("GB18030"))
        val gbImported = m3u.read(ByteArrayInputStream(gbBytes), workingDir)
        assertNotNull(gbImported)
        assertEquals("华语经典", gbImported!!.name)
        assertEquals(1, gbImported.entries.size)
        assertEquals("周杰伦", gbImported.entries[0].artist)
        assertEquals("晴天", gbImported.entries[0].title)
        assertEquals(269, gbImported.entries[0].durationSec)

        val utfContent =
            "#EXTM3U\n" +
                "#EXTINF:269,周杰伦 - 晴天\n" +
                "file:///storage/emulated/0/Music/Jay/%E6%99%B4%E5%A4%A9.flac\n" +
                "#EXTINF:0,陈奕迅 - 十年\n" +
                "#EXTTW-UNMATCHED:陈奕迅\t十年\t黑白灰\n"
        val utfImported =
            m3u.read(ByteArrayInputStream(utfContent.toByteArray(Charsets.UTF_8)), workingDir)
        assertNotNull(utfImported)
        assertEquals(2, utfImported!!.entries.size)
        val expectedDecoded =
            Path(fakeVolume, Components.parseUnix("/storage/emulated/0/Music/Jay/晴天.flac"))
        assertTrue(utfImported.entries[0].paths.contains(expectedDecoded))
        assertTrue(utfImported.entries[1].paths.isEmpty())
        assertEquals("陈奕迅", utfImported.entries[1].artist)
        assertEquals("十年", utfImported.entries[1].title)
        assertEquals("黑白灰", utfImported.entries[1].album)
    }
}
