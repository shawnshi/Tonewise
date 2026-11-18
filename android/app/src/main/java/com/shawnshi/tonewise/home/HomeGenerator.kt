/*
 * Copyright (c) 2024 Auxio Project
 * HomeGenerator.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.home

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import com.shawnshi.tonewise.home.tabs.Tab
import com.shawnshi.tonewise.list.ListSettings
import com.shawnshi.tonewise.list.adapter.UpdateInstructions
import com.shawnshi.tonewise.list.sort.Sort
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.MusicType
import com.shawnshi.tonewise.sync.SyncKvEntity
import com.shawnshi.tonewise.sync.TonewiseSyncDatabase
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song
import timber.log.Timber as L

interface HomeGenerator {
    fun attach()

    fun release()

    fun empty(): Boolean

    fun songs(): List<Song>

    fun albums(): List<Album>

    fun artists(): List<Artist>

    fun composers(): List<Artist>

    fun conductors(): List<Artist>

    fun hasCustomComposerOrder(): Boolean

    fun hasCustomConductorOrder(): Boolean

    fun saveComposerOrder(items: List<Artist>)

    fun saveConductorOrder(items: List<Artist>)

    fun clearComposerOrder()

    fun clearConductorOrder()

    fun genres(): List<Genre>

    fun playlists(): List<Playlist>

    fun tabs(): List<MusicType>

    interface Invalidator {
        fun invalidateEmpty() {}

        fun invalidateMusic(type: MusicType, instructions: UpdateInstructions)

        fun invalidateTabs()
    }

    interface Factory {
        fun create(invalidator: Invalidator): HomeGenerator
    }
}

class HomeGeneratorFactoryImpl
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val homeSettings: HomeSettings,
    private val listSettings: ListSettings,
    private val musicRepository: MusicRepository,
) : HomeGenerator.Factory {
    override fun create(invalidator: HomeGenerator.Invalidator): HomeGenerator =
        HomeGeneratorImpl(context, invalidator, homeSettings, listSettings, musicRepository)
}

internal object PersonOrderHelper {
    const val KEY_COMPOSER_ORDER = "composer_order_uids"
    const val KEY_CONDUCTOR_ORDER = "conductor_order_uids"

    fun decodeUids(raw: String?): List<String> {
        if (raw.isNullOrBlank()) return emptyList()
        return raw.lineSequence().map { it.trim() }.filter { it.isNotEmpty() }.toList()
    }

    fun encodeUids(uids: List<String>): String =
        uids.asSequence().map { it.trim() }.filter { it.isNotEmpty() }.joinToString("\n")

    fun applyCustomOrder(sorted: List<Artist>, orderedUids: List<String>): List<Artist> {
        if (orderedUids.isEmpty() || sorted.isEmpty()) return sorted
        val rankMap = HashMap<String, Int>(orderedUids.size)
        orderedUids.forEachIndexed { index, uid -> rankMap.putIfAbsent(uid, index) }
        val known = mutableListOf<Pair<Int, Artist>>()
        val unknown = mutableListOf<Artist>()
        for (artist in sorted) {
            val rank = rankMap[artist.uid.toString()]
            if (rank != null) {
                known += rank to artist
            } else {
                unknown += artist
            }
        }
        if (known.isEmpty()) return sorted
        known.sortBy { it.first }
        return buildList(sorted.size) {
            known.forEach { add(it.second) }
            addAll(unknown)
        }
    }
}

private class HomeGeneratorImpl(
    private val context: Context,
    private val invalidator: HomeGenerator.Invalidator,
    private val homeSettings: HomeSettings,
    private val listSettings: ListSettings,
    private val musicRepository: MusicRepository,
) : HomeGenerator, HomeSettings.Listener, ListSettings.Listener, MusicRepository.UpdateListener {
    private val syncKvDao by lazy { TonewiseSyncDatabase.getInstance(context).syncKvDao() }
    override fun attach() {
        homeSettings.registerListener(this)
        listSettings.registerListener(this)
        musicRepository.addUpdateListener(this)
    }

    override fun onTabsChanged() {
        invalidator.invalidateTabs()
    }

    override fun onHideCollaboratorsChanged() {
        // Changes in the hide collaborator setting will change the artist contents
        // of the library, consider it a library update.
        L.d("Collaborator setting changed, forwarding update")
        invalidator.invalidateMusic(MusicType.ARTISTS, UpdateInstructions.Diff)
    }

    override fun onSongSortChanged() {
        super.onSongSortChanged()
        invalidator.invalidateMusic(MusicType.SONGS, UpdateInstructions.Replace(0))
    }

    override fun onAlbumSortChanged() {
        super.onAlbumSortChanged()
        invalidator.invalidateMusic(MusicType.ALBUMS, UpdateInstructions.Replace(0))
    }

    override fun onArtistSortChanged() {
        super.onArtistSortChanged()
        invalidator.invalidateMusic(MusicType.ARTISTS, UpdateInstructions.Replace(0))
    }

    override fun onComposerSortChanged() {
        super.onComposerSortChanged()
        clearComposerOrder()
        invalidator.invalidateMusic(MusicType.COMPOSERS, UpdateInstructions.Replace(0))
    }

    override fun onConductorSortChanged() {
        super.onConductorSortChanged()
        clearConductorOrder()
        invalidator.invalidateMusic(MusicType.CONDUCTORS, UpdateInstructions.Replace(0))
    }

    override fun onGenreSortChanged() {
        super.onGenreSortChanged()
        invalidator.invalidateMusic(MusicType.GENRES, UpdateInstructions.Replace(0))
    }

    override fun onPlaylistSortChanged() {
        super.onPlaylistSortChanged()
        invalidator.invalidateMusic(MusicType.PLAYLISTS, UpdateInstructions.Replace(0))
    }

    override fun onMusicChanges(changes: MusicRepository.Changes) {
        invalidator.invalidateEmpty()

        val library = musicRepository.library
        if (changes.deviceLibrary && library != null) {
            L.d("Refreshing library")
            // Get the each list of items in the library to use as our list data.
            // Applying the preferred sorting to them.
            invalidator.invalidateMusic(MusicType.SONGS, UpdateInstructions.Diff)
            invalidator.invalidateMusic(MusicType.ALBUMS, UpdateInstructions.Diff)
            invalidator.invalidateMusic(MusicType.ARTISTS, UpdateInstructions.Diff)
            invalidator.invalidateMusic(MusicType.COMPOSERS, UpdateInstructions.Diff)
            invalidator.invalidateMusic(MusicType.CONDUCTORS, UpdateInstructions.Diff)
            invalidator.invalidateMusic(MusicType.GENRES, UpdateInstructions.Diff)
        }

        if (changes.userLibrary && library != null) {
            L.d("Refreshing playlists")
            invalidator.invalidateMusic(MusicType.PLAYLISTS, UpdateInstructions.Diff)
        }
    }

    override fun release() {
        musicRepository.removeUpdateListener(this)
        listSettings.unregisterListener(this)
        homeSettings.unregisterListener(this)
    }

    override fun empty() = musicRepository.library?.empty() ?: true

    override fun songs() =
        musicRepository.library?.let { listSettings.songSort.songs(it.songs) } ?: emptyList()

    override fun albums() =
        musicRepository.library?.let { listSettings.albumSort.albums(it.albums) } ?: emptyList()

    override fun artists() =
        musicRepository.library?.let { deviceLibrary ->
            val sorted = listSettings.artistSort.artists(deviceLibrary.artists)
            if (homeSettings.shouldHideCollaborators) {
                sorted.filter { it.explicitAlbums.isNotEmpty() }
            } else {
                sorted
            }
        } ?: emptyList()

    override fun composers(): List<Artist> =
        orderedPeople(
            musicRepository.composers,
            listSettings.composerSort,
            PersonOrderHelper.KEY_COMPOSER_ORDER,
        )

    override fun conductors(): List<Artist> =
        orderedPeople(
            musicRepository.conductors,
            listSettings.conductorSort,
            PersonOrderHelper.KEY_CONDUCTOR_ORDER,
        )

    override fun hasCustomComposerOrder(): Boolean =
        PersonOrderHelper.decodeUids(syncKvDao.getValue(PersonOrderHelper.KEY_COMPOSER_ORDER))
            .isNotEmpty()

    override fun hasCustomConductorOrder(): Boolean =
        PersonOrderHelper.decodeUids(syncKvDao.getValue(PersonOrderHelper.KEY_CONDUCTOR_ORDER))
            .isNotEmpty()

    override fun saveComposerOrder(items: List<Artist>) {
        val encoded = PersonOrderHelper.encodeUids(items.map { it.uid.toString() })
        syncKvDao.putValue(SyncKvEntity(PersonOrderHelper.KEY_COMPOSER_ORDER, encoded))
    }

    override fun saveConductorOrder(items: List<Artist>) {
        val encoded = PersonOrderHelper.encodeUids(items.map { it.uid.toString() })
        syncKvDao.putValue(SyncKvEntity(PersonOrderHelper.KEY_CONDUCTOR_ORDER, encoded))
    }

    override fun clearComposerOrder() {
        syncKvDao.putValue(SyncKvEntity(PersonOrderHelper.KEY_COMPOSER_ORDER, ""))
    }

    override fun clearConductorOrder() {
        syncKvDao.putValue(SyncKvEntity(PersonOrderHelper.KEY_CONDUCTOR_ORDER, ""))
    }

    private fun orderedPeople(
        raw: Collection<Artist>,
        sort: Sort,
        orderKey: String,
    ): List<Artist> {
        val sorted = sort.artists(raw)
        val savedUids = PersonOrderHelper.decodeUids(syncKvDao.getValue(orderKey))
        return PersonOrderHelper.applyCustomOrder(sorted, savedUids)
    }

    override fun genres() =
        musicRepository.library?.let { listSettings.genreSort.genres(it.genres) } ?: emptyList()

    override fun playlists() =
        musicRepository.library?.let { listSettings.playlistSort.playlists(it.playlists) }
            ?: emptyList()

    override fun tabs() = homeSettings.homeTabs.filterIsInstance<Tab.Visible>().map { it.type }
}
