/*
 * Copyright (c) 2021 Auxio Project
 * HomeViewModel.kt is part of Auxio.
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
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.home.collection.CollectionPivot
import com.shawnshi.tonewise.home.quickplay.QuickplayAggregator
import com.shawnshi.tonewise.home.quickplay.QuickplayPinKind
import com.shawnshi.tonewise.home.quickplay.QuickplayPinStore
import com.shawnshi.tonewise.home.quickplay.QuickplaySections
import com.shawnshi.tonewise.home.quickplay.QuickplaySectionsCodec
import com.shawnshi.tonewise.home.quickplay.QuickplayTrackCandidate
import com.shawnshi.tonewise.home.quickplay.primaryCoverIdForTarget
import com.shawnshi.tonewise.home.quickplay.prominentAlbumCoverId
import com.shawnshi.tonewise.sync.SyncKvEntity
import com.shawnshi.tonewise.sync.TonewiseSyncDatabase
import com.shawnshi.tonewise.list.ListSettings
import com.shawnshi.tonewise.list.adapter.UpdateInstructions
import com.shawnshi.tonewise.list.sort.Sort
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.MusicType
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.PlaySong
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.util.Event
import com.shawnshi.tonewise.util.MutableEvent
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Genre
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song
import timber.log.Timber as L

/**
 * The ViewModel for managing the tab data and lists of the home view.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@HiltViewModel
class HomeViewModel
@Inject
constructor(
    @ApplicationContext private val appContext: Context,
    private val listSettings: ListSettings,
    private val playbackSettings: PlaybackSettings,
    private val musicRepository: MusicRepository,
    private val playCountRepository: PlayCountRepository,
    private val quickplayPinStore: QuickplayPinStore,
    homeGeneratorFactory: HomeGenerator.Factory,
) : ViewModel(), HomeGenerator.Invalidator {
    private var quickplayJob: Job? = null
    @Volatile private var hasComputedFromLibrary = false
    private var lastComputedLibraryRevision = 0L
    private var lastComputedRevision: Pair<Long, Long>? = null
    val quickplaySections: StateFlow<QuickplaySections>
        field = MutableStateFlow(QuickplaySections(emptyList(), emptyList(), emptyList()))

    private fun startQuickplayObserver() {
        viewModelScope.launch {
            val cached =
                withContext(Dispatchers.IO) {
                    runCatching {
                            val raw =
                                TonewiseSyncDatabase.getInstance(appContext)
                                    .syncKvDao()
                                    .getValue(KEY_QUICKPLAY_SECTIONS_SNAPSHOT)
                            QuickplaySectionsCodec.deserializeSections(raw)
                        }
                        .getOrNull()
                }
            if (cached != null && !cached.isEmpty && !hasComputedFromLibrary && quickplaySections.value.isEmpty) {
                quickplaySections.value = cached
            }
        }

        viewModelScope.launch {
            combine(musicRepository.libraryRevision, playCountRepository.updates) { libRev, playRev ->
                    libRev to playRev
                }
                .distinctUntilChanged()
                .collect { (libRev, playRev) ->
                    scheduleQuickplayCompute(libRev, playRev)
                }
        }
    }

    private fun scheduleQuickplayCompute(libRev: Long, playRev: Long) {
        if (libRev <= 0L) return
        val key = libRev to playRev
        if (hasComputedFromLibrary && lastComputedRevision == key) {
            return
        }
        val shouldDebounce = hasComputedFromLibrary && lastComputedLibraryRevision == libRev
        quickplayJob?.cancel()
        quickplayJob =
            viewModelScope.launch {
                // First hydration computes immediately (0ms); subsequent play-count ticks coalesce.
                if (shouldDebounce) {
                    delay(180L)
                }
                val (computed, resolvedPinCovers) =
                    withContext(Dispatchers.Default) {
                        val library = musicRepository.library
                        val songs = library?.songs.orEmpty()
                        val albumCoverCache = HashMap<String, String>()
                        val candidates =
                            songs.map { song ->
                                val snap = playCountRepository.getSnapshot(song)
                                val albumUid = song.album.uid.toString()
                                val coverId =
                                    albumCoverCache
                                        .getOrPut(albumUid) { prominentAlbumCoverId(song.album) }
                                        .ifEmpty { song.cover?.id.orEmpty() }
                                QuickplayTrackCandidate(
                                    songUid = song.uid.toString(),
                                    title = song.name.resolve(appContext),
                                    artist = song.artists.resolveNames(appContext),
                                    albumUid = albumUid,
                                    albumTitle = song.album.name.resolve(appContext),
                                    addedMs = song.addedMs,
                                    playCount = snap.playCount,
                                    lastPlayedMs = snap.lastPlayedMs,
                                    coverId = coverId,
                                )
                            }
                        val sections =
                            QuickplayAggregator.computeSections(
                                candidates = candidates,
                                nowMs = System.currentTimeMillis(),
                                limit = 12,
                            )
                        val pinCovers =
                            if (library != null) {
                                val pins = quickplayPinStore.pins.value
                                buildMap(pins.size) {
                                    for (pin in pins) {
                                        val target: Any? =
                                            when (pin.kind) {
                                                QuickplayPinKind.ALBUM ->
                                                    Music.UID.fromString(pin.id)
                                                        ?.let(library::findAlbum)
                                                QuickplayPinKind.ARTIST ->
                                                    Music.UID.fromString(pin.id)
                                                        ?.let(musicRepository::findArtist)
                                                QuickplayPinKind.PLAYLIST ->
                                                    Music.UID.fromString(pin.id)
                                                        ?.let(library::findPlaylist)
                                                QuickplayPinKind.SONG ->
                                                    Music.UID.fromString(pin.id)
                                                        ?.let(library::findSong)
                                                QuickplayPinKind.WORK ->
                                                    musicRepository.classicalWorks
                                                        .firstOrNull { it.key == pin.id }
                                                        ?.recordings
                                                        ?.firstOrNull()
                                                        ?.album
                                            }
                                        val coverId = primaryCoverIdForTarget(target)
                                        if (coverId.isNotEmpty()) {
                                            put(pin.kind to pin.id, coverId)
                                        }
                                    }
                                }
                            } else {
                                emptyMap()
                            }
                        sections to pinCovers
                    }
                hasComputedFromLibrary = true
                lastComputedLibraryRevision = libRev
                lastComputedRevision = key
                quickplaySections.value = computed
                quickplayPinStore.backfillCoverIds(resolvedPinCovers)
                withContext(Dispatchers.IO) {
                    runCatching {
                        val json = QuickplaySectionsCodec.serializeSections(computed)
                        TonewiseSyncDatabase.getInstance(appContext)
                            .syncKvDao()
                            .putValue(SyncKvEntity(KEY_QUICKPLAY_SECTIONS_SNAPSHOT, json))
                    }
                }
            }
    }

    fun refreshQuickplaySections(libraryRevision: Any?, playRevision: Long) {
        val libRev = musicRepository.libraryRevision.value
        if (libRev > 0L) {
            scheduleQuickplayCompute(libRev, playRevision)
        }
    }
    /** A list of [Song]s, sorted by the preferred [Sort], to be shown in the home view. */
    val songList: StateFlow<List<Song>>
        field = MutableStateFlow(listOf<Song>())

    /** Instructions for how to update [songList] in the UI. */
    val songInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [songList]. */
    val songSort: Sort
        get() = listSettings.songSort

    /** The [PlaySong] instructions to use when playing a [Song]. */
    val playWith
        get() = playbackSettings.playInListWith

    /** A list of [Album]s, sorted by the preferred [Sort], to be shown in the home view. */
    val albumList: StateFlow<List<Album>>
        field = MutableStateFlow(listOf<Album>())

    /** Instructions for how to update [albumList] in the UI. */
    val albumInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [albumList]. */
    val albumSort: Sort
        get() = listSettings.albumSort

    private val _artistList = MutableStateFlow(listOf<Artist>())
    /**
     * A list of [Artist]s, sorted by the preferred [Sort], to be shown in the home view. Note that
     * if "Hide collaborators" is on, this list will not include collaborator [Artist]s.
     */
    val artistList: MutableStateFlow<List<Artist>>
        get() = _artistList

    /** Instructions for how to update [artistList] in the UI. */
    val artistInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [artistList]. */
    val artistSort: Sort
        get() = listSettings.artistSort

    private val _composerList = MutableStateFlow(listOf<Artist>())
    /** A list of Composer [Artist]s to be shown in the Composers home tab. */
    val composerList: StateFlow<List<Artist>>
        get() = _composerList

    /** Instructions for how to update [composerList] in the UI. */
    val composerInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [composerList]. */
    val composerSort: Sort
        get() = listSettings.composerSort

    /** Whether a manual drag order is currently overriding [composerSort]. */
    val hasCustomComposerOrder: Boolean
        get() = homeGenerator.hasCustomComposerOrder()

    private val _conductorList = MutableStateFlow(listOf<Artist>())
    /** A list of Conductor [Artist]s to be shown in the Conductors home tab. */
    val conductorList: StateFlow<List<Artist>>
        get() = _conductorList

    /** Instructions for how to update [conductorList] in the UI. */
    val conductorInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [conductorList]. */
    val conductorSort: Sort
        get() = listSettings.conductorSort

    /** Whether a manual drag order is currently overriding [conductorSort]. */
    val hasCustomConductorOrder: Boolean
        get() = homeGenerator.hasCustomConductorOrder()

    /** A list of [Genre]s, sorted by the preferred [Sort], to be shown in the home view. */
    val genreList: StateFlow<List<Genre>>
        field = MutableStateFlow(listOf<Genre>())

    /** Instructions for how to update [genreList] in the UI. */
    val genreInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [genreList]. */
    val genreSort: Sort
        get() = listSettings.genreSort

    /** A list of [Playlist]s, sorted by the preferred [Sort], to be shown in the home view. */
    val playlistList: StateFlow<List<Playlist>>
        field = MutableStateFlow(listOf<Playlist>())

    val empty: StateFlow<Boolean>
        field = MutableStateFlow(false)

    /** Instructions for how to update [genreList] in the UI. */
    val playlistInstructions: Event<UpdateInstructions>
        field = MutableEvent<UpdateInstructions>()

    /** The current [Sort] used for [genreList]. */
    val playlistSort: Sort
        get() = listSettings.playlistSort

    private val homeGenerator = homeGeneratorFactory.create(this)

    private val _visibleCollectionPivots = MutableStateFlow(CollectionPivot.DEFAULT_PIVOTS)
    /** Ordered list of visible level-2 Zune pivots inside `collection`. */
    val visibleCollectionPivots: StateFlow<List<CollectionPivot>> = _visibleCollectionPivots

    private val _collectionPivot = MutableStateFlow(CollectionPivot.ALBUMS)
    /** Active Zune Level-2 sub-pivot inside `collection`. */
    val collectionPivot: StateFlow<CollectionPivot> = _collectionPivot

    private val _currentTabType = MutableStateFlow(MusicType.ALBUMS)
    /** The [MusicType] of the currently shown [Tab]. */
    val currentTabType: StateFlow<MusicType> = _currentTabType

    private val _isFastScrolling = MutableStateFlow(false)
    /** A marker for whether the user is fast-scrolling in the home view or not. */
    val isFastScrolling: StateFlow<Boolean> = _isFastScrolling

    val showOuter: Event<Outer>
        field = MutableEvent<Outer>()

    val chooseMusicLocations: Event<Unit>
        field = MutableEvent<Unit>()

    private val _panoramaPage = MutableStateFlow(0)
    /** Active Zune Level-1 Panorama hub index (0=quickplay, 1=collection, 2=autodj). */
    val panoramaPage: StateFlow<Int> = _panoramaPage

    val requestSortDialog: Event<MusicType>
        field = MutableEvent<MusicType>()

    init {
        homeGenerator.attach()
        startQuickplayObserver()
    }

    override fun onCleared() {
        homeGenerator.release()
    }

    override fun invalidateEmpty() {
        empty.value = homeGenerator.empty()
    }

    override fun invalidateMusic(type: MusicType, instructions: UpdateInstructions) {
        when (type) {
            MusicType.SONGS -> {
                songInstructions.put(instructions)
                songList.value = homeGenerator.songs()
            }
            MusicType.ALBUMS -> {
                albumInstructions.put(instructions)
                albumList.value = homeGenerator.albums()
            }
            MusicType.ARTISTS -> {
                artistInstructions.put(instructions)
                _artistList.value = homeGenerator.artists()
            }
            MusicType.COMPOSERS -> {
                composerInstructions.put(instructions)
                _composerList.value = homeGenerator.composers()
            }
            MusicType.CONDUCTORS -> {
                conductorInstructions.put(instructions)
                _conductorList.value = homeGenerator.conductors()
            }
            MusicType.GENRES -> {
                genreInstructions.put(instructions)
                genreList.value = homeGenerator.genres()
            }
            MusicType.PLAYLISTS -> {
                playlistInstructions.put(instructions)
                playlistList.value = homeGenerator.playlists()
            }
            MusicType.SCENES -> {}
        }
    }

    /**
     * Apply a new [Sort] to [songList].
     *
     * @param sort The [Sort] to apply.
     */
    fun applySongSort(sort: Sort) {
        listSettings.songSort = sort
    }

    /**
     * Apply a new [Sort] to [albumList].
     *
     * @param sort The [Sort] to apply.
     */
    fun applyAlbumSort(sort: Sort) {
        listSettings.albumSort = sort
    }

    /**
     * Apply a new [Sort] to [artistList].
     *
     * @param sort The [Sort] to apply.
     */
    fun applyArtistSort(sort: Sort) {
        listSettings.artistSort = sort
    }

    /**
     * Apply a new [Sort] to [composerList], clearing any manual drag order.
     *
     * @param sort The [Sort] to apply.
     */
    fun applyComposerSort(sort: Sort) {
        val unchanged = listSettings.composerSort == sort
        homeGenerator.clearComposerOrder()
        listSettings.composerSort = sort
        if (unchanged) {
            invalidateMusic(MusicType.COMPOSERS, UpdateInstructions.Replace(0))
        }
    }

    /**
     * Apply a new [Sort] to [conductorList], clearing any manual drag order.
     *
     * @param sort The [Sort] to apply.
     */
    fun applyConductorSort(sort: Sort) {
        val unchanged = listSettings.conductorSort == sort
        homeGenerator.clearConductorOrder()
        listSettings.conductorSort = sort
        if (unchanged) {
            invalidateMusic(MusicType.CONDUCTORS, UpdateInstructions.Replace(0))
        }
    }

    /** Persist the manually reordered composer list. */
    fun commitComposerOrder(items: List<Artist>) {
        homeGenerator.saveComposerOrder(items)
        _composerList.value = items
    }

    /** Persist the manually reordered conductor list. */
    fun commitConductorOrder(items: List<Artist>) {
        homeGenerator.saveConductorOrder(items)
        _conductorList.value = items
    }

    /**
     * Apply a new [Sort] to [genreList].
     *
     * @param sort The [Sort] to apply.
     */
    fun applyGenreSort(sort: Sort) {
        listSettings.genreSort = sort
    }

    /**
     * Apply a new [Sort] to [playlistList].
     *
     * @param sort The [Sort] to apply.
     */
    fun applyPlaylistSort(sort: Sort) {
        listSettings.playlistSort = sort
    }

    fun setPanoramaPage(page: Int) {
        _panoramaPage.value = page.coerceIn(0, 2)
    }

    fun selectCollectionPivot(pivot: CollectionPivot) {
        val normalized = if (pivot == CollectionPivot.COMPOSERS) CollectionPivot.CLASSICAL else pivot
        _collectionPivot.value = normalized
        normalized.musicType?.let { _currentTabType.value = it }
    }

    fun openSortForCurrentPivot() {
        val type = _collectionPivot.value.musicType ?: return
        requestSortDialog.put(type)
    }

    /**
     * Update whether the user is fast scrolling or not in the home view.
     *
     * @param isFastScrolling true if the user is currently fast scrolling, false otherwise.
     */
    fun setFastScrolling(isFastScrolling: Boolean) {
        L.d("Updating fast scrolling state: $isFastScrolling")
        _isFastScrolling.value = isFastScrolling
    }

    fun startChooseMusicLocations() {
        chooseMusicLocations.put(Unit)
    }

    fun showSettings() {
        showOuter.put(Outer.Settings)
    }

    fun showAbout() {
        showOuter.put(Outer.About)
    }

    private companion object {
        const val KEY_QUICKPLAY_SECTIONS_SNAPSHOT = "quickplay_sections_snapshot_v1"
    }
}

sealed interface Outer {
    data object Settings : Outer

    data object About : Outer
}
