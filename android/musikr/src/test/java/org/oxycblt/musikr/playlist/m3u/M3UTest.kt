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
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.fs.Volume
import org.oxycblt.musikr.fs.path.VolumeManager

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
}
