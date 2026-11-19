/*
 * Copyright (c) 2026 Tonewise Project
 * PlaylistDatabasePruneTest.kt is part of Tonewise.
 */

package org.oxycblt.musikr.playlist.db

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.oxycblt.musikr.Music
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class PlaylistDatabasePruneTest {
    @Test
    fun deleteAndReplacePlaylist_prunesOrphanSongs_whilePreservingSharedSongs() = runTest {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val db =
            Room.inMemoryDatabaseBuilder(context, PlaylistDatabase::class.java)
                .allowMainThreadQueries()
                .build()
        try {
            val dao = db.playlistDao()
            val p1Uid = requireNotNull(Music.UID.fromString("uap11111111-1111-4111-8111-111111111111"))
            val p2Uid = requireNotNull(Music.UID.fromString("uap22222222-2222-4222-8222-222222222222"))
            val s1 = PlaylistSong(requireNotNull(Music.UID.fromString("uas11111111-1111-4111-8111-111111111111")))
            val s2 = PlaylistSong(requireNotNull(Music.UID.fromString("uas22222222-2222-4222-8222-222222222222")))
            val s3 = PlaylistSong(requireNotNull(Music.UID.fromString("uas33333333-3333-4333-8333-333333333333")))

            dao.insertPlaylist(RawPlaylist(PlaylistInfo(p1Uid, "Playlist One"), listOf(s1, s2)))
            dao.insertPlaylist(RawPlaylist(PlaylistInfo(p2Uid, "Playlist Two"), listOf(s2, s3)))

            // Deleting P1 must prune S1 (unreferenced) while preserving S2 (shared with P2) and S3.
            dao.deletePlaylist(p1Uid)
            val remainingAfterDelete = readAllSongUids(db)
            assertEquals(setOf(s2.songUid.toString(), s3.songUid.toString()), remainingAfterDelete)

            // Replacing P2 with [S3] must prune S2 while preserving S3.
            dao.replacePlaylistSongs(p2Uid, listOf(s3))
            val remainingAfterReplace = readAllSongUids(db)
            assertEquals(setOf(s3.songUid.toString()), remainingAfterReplace)
            assertTrue(dao.readRawPlaylists().single().songs.map { it.songUid } == listOf(s3.songUid))
        } finally {
            db.close()
        }
    }

    private fun readAllSongUids(db: PlaylistDatabase): Set<String> {
        val result = HashSet<String>()
        db.openHelper.readableDatabase.query("SELECT songUid FROM PlaylistSong").use { cursor ->
            while (cursor.moveToNext()) {
                result.add(cursor.getString(0))
            }
        }
        return result
    }
}
