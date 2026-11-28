/*
 * Copyright (c) 2024 Auxio Project
 * PlaybackCommand.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.playback.state

import javax.inject.Inject
import com.shawnshi.tonewise.list.ListSettings
import com.shawnshi.tonewise.list.sort.Sort
import com.shawnshi.tonewise.music.ClassicalWorkKey
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.PlaybackSettings
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.MusicParent
import org.oxycblt.musikr.PersonArtist
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song

/**
 * A playback command that can be passed to [PlaybackStateManager] to start new playback.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
interface PlaybackCommand {
    /** A particular [Song] to play, or null to play the first [Song] in the new queue. * */
    val song: Song?
    /**
     * The [MusicParent] to play from, or null if to play from an non-specific collection of "All
     * [Song]s". *
     */
    val parent: MusicParent?
    /** The queue of [Song]s to play from. * */
    val queue: List<Song>
    /** Whether to shuffle or not. * */
    val shuffled: Boolean
    /** Session-scoped shuffle lock for sequential classical work/movement playback. */
    val suppressGlobalShuffle: Boolean
        get() = false
    /** Restore previously locked global shuffle if [ShuffleMode.IMPLICIT] started non-classical playback. */
    val restoreGlobalShuffleOnUnlock: Boolean
        get() = false

    interface Factory {
        fun song(song: Song, shuffle: ShuffleMode): PlaybackCommand?

        fun songFromAll(song: Song, shuffle: ShuffleMode): PlaybackCommand?

        fun songFromAlbum(song: Song, shuffle: ShuffleMode): PlaybackCommand?

        fun songFromArtist(song: Song, artist: Artist?, shuffle: ShuffleMode): PlaybackCommand?

        fun songFromGenre(song: Song, genre: Genre?, shuffle: ShuffleMode): PlaybackCommand?

        fun songFromPlaylist(song: Song, playlist: Playlist, shuffle: ShuffleMode): PlaybackCommand?

        fun all(shuffle: ShuffleMode): PlaybackCommand?

        fun songs(songs: List<Song>, shuffle: ShuffleMode): PlaybackCommand?

        fun album(album: Album, shuffle: ShuffleMode): PlaybackCommand?

        fun artist(artist: Artist, shuffle: ShuffleMode): PlaybackCommand?

        fun genre(genre: Genre, shuffle: ShuffleMode): PlaybackCommand?

        fun playlist(playlist: Playlist, shuffle: ShuffleMode): PlaybackCommand?

        fun classicalRecording(movements: List<Song>, startIndex: Int = 0): PlaybackCommand?
    }
}

/**
 * Pure session-scoped shuffle lock state machine for classical work/movement playback.
 * Never mutates persisted user global shuffle settings.
 */
class ClassicalSessionShuffleLock {
    private var lockActive: Boolean = false
    private var savedGlobalShuffle: Boolean? = null

    val isLocked: Boolean
        get() = lockActive

    fun resolveForNewPlayback(
        currentShuffleEnabled: Boolean,
        commandShuffled: Boolean,
        suppressGlobalShuffle: Boolean,
        restoreGlobalShuffleOnUnlock: Boolean,
    ): Boolean {
        if (suppressGlobalShuffle) {
            if (!lockActive) {
                savedGlobalShuffle = currentShuffleEnabled
                lockActive = true
            }
            return false
        }
        val wasLocked = lockActive
        val priorGlobal = savedGlobalShuffle
        lockActive = false
        savedGlobalShuffle = null
        return if (wasLocked && restoreGlobalShuffleOnUnlock && priorGlobal == true) {
            true
        } else {
            commandShuffled
        }
    }

    fun clearOnUserToggle() {
        lockActive = false
        savedGlobalShuffle = null
    }
}

enum class ShuffleMode {
    ON,
    OFF,
    IMPLICIT,
}

class PlaybackCommandFactoryImpl
@Inject
constructor(
    val playbackManager: PlaybackStateManager,
    val playbackSettings: PlaybackSettings,
    val listSettings: ListSettings,
    val musicRepository: MusicRepository,
) : PlaybackCommand.Factory {
    data class PlaybackCommandImpl(
        override val song: Song?,
        override val parent: MusicParent?,
        override val queue: List<Song>,
        override val shuffled: Boolean,
        override val suppressGlobalShuffle: Boolean = false,
        override val restoreGlobalShuffleOnUnlock: Boolean = false,
    ) : PlaybackCommand {
        // Only show queue count to reduce memory use
        override fun toString() =
            "PlaybackCommand(song=$song, parent=$parent, queue=${queue.size} songs, shuffled=$shuffled, lock=$suppressGlobalShuffle)"
    }

    override fun song(song: Song, shuffle: ShuffleMode) =
        newCommand(song, null, listOf(song), shuffle)

    override fun songFromAll(song: Song, shuffle: ShuffleMode) = newCommand(song, shuffle)

    override fun songFromAlbum(song: Song, shuffle: ShuffleMode) =
        newCommand(song, song.album, listSettings.albumSongSort, shuffle)

    override fun songFromArtist(song: Song, artist: Artist?, shuffle: ShuffleMode) =
        newCommand(song, artist, song.artists, listSettings.artistSongSort, shuffle)

    override fun songFromGenre(song: Song, genre: Genre?, shuffle: ShuffleMode) =
        newCommand(song, genre, song.genres, listSettings.genreSongSort, shuffle)

    override fun songFromPlaylist(song: Song, playlist: Playlist, shuffle: ShuffleMode) =
        newCommand(song, playlist, playlist.songs, shuffle)

    override fun all(shuffle: ShuffleMode) = newCommand(null, shuffle)

    override fun songs(songs: List<Song>, shuffle: ShuffleMode) =
        newCommand(null, null, songs, shuffle)

    override fun album(album: Album, shuffle: ShuffleMode) =
        newCommand(null, album, listSettings.albumSongSort, shuffle)

    override fun artist(artist: Artist, shuffle: ShuffleMode) =
        newCommand(null, artist, listSettings.artistSongSort, shuffle)

    override fun genre(genre: Genre, shuffle: ShuffleMode) =
        newCommand(null, genre, listSettings.genreSongSort, shuffle)

    override fun playlist(playlist: Playlist, shuffle: ShuffleMode) =
        newCommand(null, playlist, playlist.songs, shuffle)

    override fun classicalRecording(movements: List<Song>, startIndex: Int): PlaybackCommand? {
        if (movements.isEmpty()) return null
        val safeStart = startIndex.coerceIn(0, movements.lastIndex)
        val slice = movements.subList(safeStart, movements.size)
        return PlaybackCommandImpl(
            song = slice.first(),
            parent = null,
            queue = slice,
            shuffled = false,
            suppressGlobalShuffle = true,
            restoreGlobalShuffleOnUnlock = false,
        )
    }

    private fun <T : MusicParent> newCommand(
        song: Song,
        parent: T?,
        parents: List<T>,
        sort: Sort,
        shuffle: ShuffleMode,
    ): PlaybackCommand? {
        return if (parent != null) {
            newCommand(song, parent, sort, shuffle)
        } else if (parents.size == 1) {
            newCommand(song, parents.first(), sort, shuffle)
        } else {
            null
        }
    }

    private fun newCommand(song: Song?, shuffle: ShuffleMode): PlaybackCommand? {
        val library = musicRepository.library ?: return null
        return newCommand(song, null, library.songs, listSettings.songSort, shuffle)
    }

    private fun newCommand(
        song: Song?,
        parent: MusicParent,
        sort: Sort,
        shuffle: ShuffleMode,
    ): PlaybackCommand? {
        val sorted = sort.songs(parent.songs)
        val songs = if (parent is PersonArtist) ClassicalWorkKey.orderPreservingWorks(sorted) else sorted
        return newCommand(song, parent, songs, shuffle)
    }

    private fun newCommand(
        song: Song?,
        parent: MusicParent?,
        queue: Collection<Song>,
        sort: Sort,
        shuffle: ShuffleMode,
    ): PlaybackCommand? {
        if (queue.isEmpty() || (song != null && song !in queue)) {
            return null
        }
        return newCommand(song, parent, sort.songs(queue), shuffle)
    }

    private fun newCommand(
        song: Song?,
        parent: MusicParent?,
        queue: List<Song>,
        shuffle: ShuffleMode,
    ): PlaybackCommand {
        return PlaybackCommandImpl(
            song = song,
            parent = parent,
            queue = queue,
            shuffled = isShuffled(shuffle),
            suppressGlobalShuffle = false,
            restoreGlobalShuffleOnUnlock =
                shuffle == ShuffleMode.IMPLICIT && playbackSettings.keepShuffle,
        )
    }

    private fun isShuffled(shuffle: ShuffleMode) =
        when (shuffle) {
            ShuffleMode.ON -> true
            ShuffleMode.OFF -> false
            ShuffleMode.IMPLICIT -> playbackSettings.keepShuffle && playbackManager.isShuffled
        }
}
