/*
 * Copyright (c) 2023 Auxio Project
 * MusicRepository.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.music

import android.content.Context
import android.content.Intent
import dagger.hilt.android.qualifiers.ApplicationContext
import java.util.Locale
import java.util.UUID
import javax.inject.Inject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.yield
import com.shawnshi.tonewise.AuxioService
import com.shawnshi.tonewise.IntegerTable
import com.shawnshi.tonewise.image.covers.SettingCovers
import com.shawnshi.tonewise.music.MusicRepository.IndexingWorker
import com.shawnshi.tonewise.music.locations.LocationMode
import com.shawnshi.tonewise.music.shim.WriteOnlyMutableCache
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Config
import org.oxycblt.musikr.IndexingProgress
import org.oxycblt.musikr.Interpretation
import org.oxycblt.musikr.Library
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Musikr
import org.oxycblt.musikr.MutableLibrary
import org.oxycblt.musikr.PersonArtist
import org.oxycblt.musikr.PersonRole
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.Storage
import org.oxycblt.musikr.cache.MutableCache
import org.oxycblt.musikr.covers.CoverCollection
import org.oxycblt.musikr.fs.mediastore.MediaStore
import org.oxycblt.musikr.fs.saf.SAF
import org.oxycblt.musikr.playlist.db.StoredPlaylists
import org.oxycblt.musikr.tag.Name
import org.oxycblt.musikr.tag.interpret.Naming
import org.oxycblt.musikr.tag.interpret.Separators
import timber.log.Timber as L

/**
 * Primary manager of music information and loading.
 *
 * Music information is loaded in-memory by this repository using an [IndexingWorker]. Changes in
 * music (loading) can be reacted to with [UpdateListener] and [IndexingListener].
 *
 * @author Alexander Capehart (OxygenCobalt)
 *
 * TODO: Switch listeners to set when you can confirm there are no order-dependent listener
 *   configurations
 */
data class WorkRecording(
    val album: Album,
    val conductorLabel: String,
    val ensembleLabel: String,
    val year: Int?,
    val movements: List<Song>,
    val scopeKey: String = album.uid.toString(),
) {
    val totalDurationMs: Long
        get() = movements.sumOf { it.durationMs }
}

data class ClassicalWork(
    val key: String,
    val workTitle: String,
    val composerName: String,
    val recordings: List<WorkRecording>,
    val form: String = "other",
    val era: String? = null,
)

interface MusicRepository {
    /** The current library */
    val library: Library?

    /** Monotonically incremented whenever [library] or its derived indices are updated. */
    val libraryRevision: StateFlow<Long>

    /** All extracted classical composers indexed from the current library. */
    val composers: Collection<PersonArtist>

    /** All extracted classical conductors indexed from the current library. */
    val conductors: Collection<PersonArtist>

    /** All aggregated classical works (grouped with all local recordings). */
    val classicalWorks: List<ClassicalWork>

    /** The current state of music loading. Null if no load has occurred yet. */
    val indexingState: IndexingState?

    /**
     * Add an [UpdateListener] to receive updates from this instance.
     *
     * @param listener The [UpdateListener] to add.
     */
    fun addUpdateListener(listener: UpdateListener)

    /**
     * Remove an [UpdateListener] such that it does not receive any further updates from this
     * instance.
     *
     * @param listener The [UpdateListener] to remove.
     */
    fun removeUpdateListener(listener: UpdateListener)

    /**
     * Add an [IndexingListener] to receive updates from this instance.
     *
     * @param listener The [UpdateListener] to add.
     */
    fun addIndexingListener(listener: IndexingListener)

    /**
     * Remove an [IndexingListener] such that it does not receive any further updates from this
     * instance.
     *
     * @param listener The [IndexingListener] to remove.
     */
    fun removeIndexingListener(listener: IndexingListener)

    /**
     * Register an [IndexingWorker] to handle loading operations. Will do nothing if one is already
     * registered.
     *
     * @param worker The [IndexingWorker] to register.
     */
    fun registerWorker(worker: IndexingWorker)

    /**
     * Unregister an [IndexingWorker] and drop any work currently being done by it. Does nothing if
     * given [IndexingWorker] is not the currently registered instance.
     *
     * @param worker The [IndexingWorker] to unregister.
     */
    fun unregisterWorker(worker: IndexingWorker)

    /**
     * Generically search for the [Music] associated with the given [Music.UID]. Note that this
     * method is much slower that type-specific find implementations, so this should only be used if
     * the type of music being searched for is entirely unknown.
     *
     * @param uid The [Music.UID] to search for.
     * @return The expected [Music] information, or null if it could not be found.
     */
    fun find(uid: Music.UID): Music?

    /** Look up an [Artist] by [uid], including synthesized [PersonArtist] composer/conductor entries. */
    fun findArtist(uid: Music.UID): Artist?

    /** Look up a [PersonArtist] aggregated for the given composer name. */
    fun findComposer(name: String): PersonArtist?

    /** Look up a [PersonArtist] aggregated for the given conductor name. */
    fun findConductor(name: String): PersonArtist?

    /** Look up an aggregated [ClassicalWork] (and all local recordings) for the given [song]. */
    fun findWorkForSong(song: Song): ClassicalWork?

    /**
     * Create a new [Playlist] of the given [Song]s.
     *
     * @param name The name of the new [Playlist].
     * @param songs The songs to populate the new [Playlist] with.
     */
    suspend fun createPlaylist(name: String, songs: List<Song>)

    /**
     * Rename a [Playlist].
     *
     * @param playlist The [Playlist] to rename.
     * @param name The name of the new [Playlist].
     */
    suspend fun renamePlaylist(playlist: Playlist, name: String)

    /**
     * Delete a [Playlist].
     *
     * @param playlist The playlist to delete.
     */
    suspend fun deletePlaylist(playlist: Playlist)

    /**
     * Add the given [Song]s to a [Playlist].
     *
     * @param songs The [Song]s to add to the [Playlist].
     * @param playlist The [Playlist] to add to.
     */
    suspend fun addToPlaylist(songs: List<Song>, playlist: Playlist)

    /**
     * Update the [Song]s of a [Playlist].
     *
     * @param playlist The [Playlist] to update.
     * @param songs The new [Song]s to be contained in the [Playlist].
     */
    suspend fun rewritePlaylist(playlist: Playlist, songs: List<Song>)

    /**
     * Request that a music loading operation is started by the current [IndexingWorker]. Does
     * nothing if one is not available.
     *
     * @param withCache Whether to load with the music cache or not.
     */
    fun requestIndex(withCache: Boolean)

    /**
     * Load the music library. Any prior loads will be canceled.
     *
     * @param worker The [IndexingWorker] to perform the work with.
     * @param withCache Whether to load with the music cache or not.
     * @return The top-level music loading [Job] started.
     */
    suspend fun index(worker: IndexingWorker, withCache: Boolean)

    /** A listener for changes to the stored music information. */
    interface UpdateListener {
        /**
         * Called when a change to the stored music information occurs.
         *
         * @param changes The [Changes] that have occurred.
         */
        fun onMusicChanges(changes: Changes)
    }

    /**
     * Flags indicating which kinds of music information changed.
     *
     * @param deviceLibrary Whether the current songs/albums/artists/genres has changed.
     * @param userLibrary Whether the current playlists have changed.
     */
    data class Changes(val deviceLibrary: Boolean, val userLibrary: Boolean)

    /** A listener for events in the music loading process. */
    interface IndexingListener {
        /** Called when the music loading state changed. */
        fun onIndexingStateChanged()
    }

    /** A persistent worker that can load music in the background. */
    interface IndexingWorker {
        /**
         * Request that the music loading process ([index]) should be started. Any prior loads
         * should be canceled.
         *
         * @param withCache Whether to use the music cache when loading.
         */
        fun requestIndex(withCache: Boolean)
    }
}

/**
 * Represents the current state of the music loader.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
sealed interface IndexingState {
    /**
     * Music loading is on-going.
     *
     * @param progress The current progress of the music loading.
     * @param isBackground Whether cached data has already been hydrated and this refresh is running
     *   silently in the background.
     */
    data class Indexing(val progress: IndexingProgress, val isBackground: Boolean = false) :
        IndexingState

    /**
     * Music loading has completed.
     *
     * @param error If music loading has failed, the error that occurred will be here. Otherwise, it
     *   will be null.
     */
    data class Completed(val error: Exception?) : IndexingState
}

internal class PendingIndexCoordinator {
    private var pendingWithCache: Boolean? = null

    @Synchronized
    fun queueOrMerge(withCache: Boolean): Boolean {
        val merged = pendingWithCache?.let { it && withCache } ?: withCache
        pendingWithCache = merged
        return merged
    }

    @Synchronized
    fun consume(): Boolean? {
        val next = pendingWithCache
        pendingWithCache = null
        return next
    }
}

class MusicRepositoryImpl
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val cache: MutableCache,
    private val storedPlaylists: StoredPlaylists,
    private val settingCovers: SettingCovers,
    private val musicSettings: MusicSettings,
    private val autoPlaylistImporter: AutoPlaylistImporter,
) : MusicRepository {
    private val updateListeners = mutableListOf<MusicRepository.UpdateListener>()
    private val indexingListeners = mutableListOf<MusicRepository.IndexingListener>()
    @Volatile private var indexingWorker: IndexingWorker? = null
    private val pendingIndex = PendingIndexCoordinator()

    @Volatile override var library: MutableLibrary? = null
    private val _libraryRevision = MutableStateFlow(0L)
    override val libraryRevision: StateFlow<Long> = _libraryRevision.asStateFlow()
    @Volatile private var composerMap: Map<String, PersonArtist> = emptyMap()
    @Volatile private var conductorMap: Map<String, PersonArtist> = emptyMap()
    @Volatile private var personUidMap: Map<Music.UID, PersonArtist> = emptyMap()
    @Volatile private var workBySongUid: Map<Music.UID, ClassicalWork> = emptyMap()
    @Volatile private var allClassicalWorks: List<ClassicalWork> = emptyList()
    @Volatile private var previousCompletedState: IndexingState.Completed? = null
    @Volatile private var currentIndexingState: IndexingState? = null
    override val composers: Collection<PersonArtist>
        get() = composerMap.values.distinctBy { it.uid }

    override val conductors: Collection<PersonArtist>
        get() = conductorMap.values.distinctBy { it.uid }

    override val classicalWorks: List<ClassicalWork>
        get() = allClassicalWorks

    override val indexingState: IndexingState?
        get() = currentIndexingState ?: previousCompletedState

    @Synchronized
    override fun addUpdateListener(listener: MusicRepository.UpdateListener) {
        L.d("Adding $listener to update listeners")
        updateListeners.add(listener)
        listener.onMusicChanges(MusicRepository.Changes(deviceLibrary = true, userLibrary = true))
    }

    @Synchronized
    override fun removeUpdateListener(listener: MusicRepository.UpdateListener) {
        L.d("Removing $listener to update listeners")
        if (!updateListeners.remove(listener)) {
            L.w("Update listener $listener was not added prior, cannot remove")
        }
    }

    @Synchronized
    override fun addIndexingListener(listener: MusicRepository.IndexingListener) {
        L.d("Adding $listener to indexing listeners")
        indexingListeners.add(listener)
        listener.onIndexingStateChanged()
    }

    @Synchronized
    override fun removeIndexingListener(listener: MusicRepository.IndexingListener) {
        L.d("Removing $listener from indexing listeners")
        if (!indexingListeners.remove(listener)) {
            L.w("Indexing listener $listener was not added prior, cannot remove")
        }
    }

    @Synchronized
    override fun registerWorker(worker: IndexingWorker) {
        if (indexingWorker != null) {
            L.w("Worker is already registered")
            return
        }
        L.d("Registering worker $worker")
        indexingWorker = worker
        val pending = pendingIndex.consume()
        if (pending != null) {
            L.d("Flushing queued pending index [cache=$pending]")
            worker.requestIndex(pending)
        }
    }

    @Synchronized
    override fun unregisterWorker(worker: IndexingWorker) {
        if (indexingWorker !== worker) {
            L.w("Given worker did not match current worker")
            return
        }
        L.d("Unregistering worker $worker")
        indexingWorker = null
        currentIndexingState = null
    }

    @Synchronized
    override fun find(uid: Music.UID) =
        (library?.run {
            findSong(uid)
                ?: findAlbum(uid)
                ?: findArtist(uid)
                ?: findGenre(uid)
                ?: findPlaylist(uid)
        }) ?: personUidMap[uid]

    @Synchronized
    override fun findArtist(uid: Music.UID): Artist? =
        library?.findArtist(uid) ?: personUidMap[uid]

    @Synchronized
    override fun findComposer(name: String): PersonArtist? =
        composerMap[name.trim().lowercase()]

    @Synchronized
    override fun findConductor(name: String): PersonArtist? =
        conductorMap[name.trim().lowercase()]

    @Synchronized
    override fun findWorkForSong(song: Song): ClassicalWork? = workBySongUid[song.uid]

    override suspend fun createPlaylist(name: String, songs: List<Song>) {
        val library = synchronized(this) { library ?: return }
        L.d("Creating playlist $name with ${songs.size} songs")
        val newLibrary = library.createPlaylist(name, songs)
        synchronized(this) {
            this.library = newLibrary
            _libraryRevision.value += 1L
        }
        withContext(Dispatchers.Main) { dispatchLibraryChange(device = false, user = true) }
    }

    override suspend fun renamePlaylist(playlist: Playlist, name: String) {
        val library = synchronized(this) { library ?: return }
        L.d("Renaming $playlist to $name")
        val newLibrary = library.renamePlaylist(playlist, name)
        synchronized(this) {
            this.library = newLibrary
            _libraryRevision.value += 1L
        }
        withContext(Dispatchers.Main) { dispatchLibraryChange(device = false, user = true) }
    }

    override suspend fun deletePlaylist(playlist: Playlist) {
        val library = synchronized(this) { library ?: return }
        L.d("Deleting $playlist")
        val newLibrary = library.deletePlaylist(playlist)
        synchronized(this) {
            this.library = newLibrary
            _libraryRevision.value += 1L
        }
        withContext(Dispatchers.Main) { dispatchLibraryChange(device = false, user = true) }
    }

    override suspend fun addToPlaylist(songs: List<Song>, playlist: Playlist) {
        val library = synchronized(this) { library ?: return }
        L.d("Adding ${songs.size} songs to $playlist")
        val newLibrary = library.addToPlaylist(playlist, songs)
        synchronized(this) {
            this.library = newLibrary
            _libraryRevision.value += 1L
        }
        withContext(Dispatchers.Main) { dispatchLibraryChange(device = false, user = true) }
    }

    override suspend fun rewritePlaylist(playlist: Playlist, songs: List<Song>) {
        val library = synchronized(this) { library ?: return }
        L.d("Rewriting $playlist with ${songs.size} songs")
        val newLibrary = library.rewritePlaylist(playlist, songs)
        synchronized(this) {
            this.library = newLibrary
            _libraryRevision.value += 1L
        }
        withContext(Dispatchers.Main) { dispatchLibraryChange(device = false, user = true) }
    }

    @Synchronized
    override fun requestIndex(withCache: Boolean) {
        L.d("Requesting index operation [cache=$withCache]")
        val worker = indexingWorker
        if (worker != null) {
            pendingIndex.consume()
            worker.requestIndex(withCache)
        } else {
            val queued = pendingIndex.queueOrMerge(withCache)
            L.d("No indexing worker attached; queued pending index [cache=$queued] and starting AuxioService")
            runCatching {
                context.startService(
                    Intent(context, AuxioService::class.java)
                        .setAction(AuxioService.ACTION_START)
                        .putExtra(AuxioService.INTENT_KEY_START_ID, IntegerTable.START_ID_ACTIVITY)
                )
            }.onFailure { L.w(it, "Unable to start AuxioService for pending index") }
        }
    }

    override suspend fun index(worker: IndexingWorker, withCache: Boolean) {
        L.d("Begin index [cache=$withCache]")
        try {
            indexImpl(withCache)
        } catch (e: CancellationException) {
            // Got cancelled, propagate upwards to top-level co-routine.
            L.d("Loading routine was cancelled")
            throw e
        } catch (e: Exception) {
            // Music loading process failed due to something we have not handled.
            L.e("Music indexing failed")
            L.e(e.stackTraceToString())
            emitIndexingCompletion(e)
        }
    }

    private suspend fun indexImpl(withCache: Boolean) {
        L.d("Index requested, initializing")
        // Obtain configuration information
        val separators = Separators.from(musicSettings.separators)
        L.d("Separators: $separators")
        val nameFactory =
            if (musicSettings.intelligentSorting) {
                Naming.intelligent()
            } else {
                Naming.simple()
            }
        L.d("Names: $nameFactory")
        val currentRevision = musicSettings.revision
        val newRevision = currentRevision?.takeIf { withCache } ?: UUID.randomUUID()
        L.d("Revisions: cur=$currentRevision, new=$newRevision")
        val cache = if (withCache) cache else WriteOnlyMutableCache(cache)
        L.d("Cache: $cache")
        val covers = settingCovers.mutate(context, newRevision)
        L.d("Covers: $covers")
        val fs =
            when (musicSettings.locationMode) {
                LocationMode.SAF -> SAF.from(context, musicSettings.safQuery)
                LocationMode.MEDIA_STORE -> MediaStore.from(context, musicSettings.mediaStoreQuery)
            }
        L.d("FS: $fs")
        val storage = Storage(cache, covers, storedPlaylists)
        val interpretation = Interpretation(nameFactory, separators)
        val config = Config(fs, storage, interpretation)
        val musikr = Musikr.new(context, config)
        val canHydrateCache =
            withCache &&
                this.library == null &&
                currentRevision != null &&
                !(musicSettings.locationMode == LocationMode.SAF &&
                    musicSettings.safQuery.source.isEmpty())
        if (canHydrateCache) {
            val hydrateStart = System.currentTimeMillis()
            val cachedLibrary =
                runCatching { musikr.hydrateFromCache() }
                    .onFailure {
                        L.w(it, "Cached library hydration failed, falling back to full index")
                    }
                    .getOrNull()
            if (cachedLibrary != null) {
                L.d(
                    "Hydrated cached library (${cachedLibrary.songs.size} songs, ${cachedLibrary.playlists.size} playlists) in ${System.currentTimeMillis() - hydrateStart}ms"
                )
                emitLibrary(cachedLibrary)
            }
        }
        val isBackgroundRefresh = withCache && (this.library?.songs?.isNotEmpty() == true)
        L.d("Running index [background=$isBackgroundRefresh]...")
        val start = System.currentTimeMillis()
        val result =
            musikr.run { progress -> emitIndexingProgress(progress, isBackgroundRefresh) }
        L.d("Index finished in ${System.currentTimeMillis() - start}ms")
        val finalLibrary = autoPlaylistImporter.sync(result.library, result.discoveredPlaylists)
        // Music loading completed, update the revision right now so we re-use this work
        // later.
        L.d("Revisioning from $currentRevision -> $newRevision")
        musicSettings.revision = newRevision
        // Deliver the library to the rest of the app
        // This will more or less block until all required item translation and
        // cleanup finishes.
        L.d("Emitting new library")
        emitLibrary(finalLibrary)
        // Clean up old data that is now impossible for the app to be using.
        L.d("Cleanup")
        result.cleanup()
        // Finish up loading.
        L.d("Indexing complete")
        emitIndexingCompletion(null)
    }

    private suspend fun emitIndexingProgress(
        progress: IndexingProgress,
        isBackground: Boolean = false,
    ) {
        yield()
        synchronized(this) {
            currentIndexingState = IndexingState.Indexing(progress, isBackground)
            for (listener in indexingListeners) {
                listener.onIndexingStateChanged()
            }
        }
    }

    private suspend fun emitLibrary(newLibrary: MutableLibrary) {
        val deviceLibraryChanged: Boolean
        val userLibraryChanged: Boolean
        // We want to make sure that all reads and writes are synchronized due to the sheer
        // amount of consumers of MusicRepository.
        synchronized(this) {
            // It's possible that this reload might have changed nothing, so make sure that
            // hasn't happened before dispatching a change to all consumers.

            // This is an old compat shim back when device library and user library were different
            // thinks. For the sake of avoiding drastic changes, it sticks around.
            // TODO: Remove this once you start work on kindred.
            deviceLibraryChanged =
                this.library?.songs != newLibrary.songs ||
                    this.library?.albums != newLibrary.albums ||
                    this.library?.artists != newLibrary.artists ||
                    this.library?.genres != newLibrary.genres
            userLibraryChanged = this.library?.playlists != newLibrary.playlists
            if (!deviceLibraryChanged && !userLibraryChanged) {
                L.d("Library has not changed, skipping update")
                return
            }

            this.library = newLibrary
            if (deviceLibraryChanged) {
                rebuildPersonIndices(newLibrary)
            }
            _libraryRevision.value += 1L
        }

        // Consumers expect their updates to be on the main thread (notably PlaybackService),
        // so switch to it.
        withContext(Dispatchers.Main) {
            dispatchLibraryChange(deviceLibraryChanged, userLibraryChanged)
        }
    }

    private suspend fun emitIndexingCompletion(error: Exception?) {
        yield()
        synchronized(this) {
            previousCompletedState = IndexingState.Completed(error)
            currentIndexingState = null
            L.d("Dispatching completion state [error=$error]")
            for (listener in indexingListeners) {
                listener.onIndexingStateChanged()
            }
        }
    }

    @Synchronized
    private fun dispatchLibraryChange(device: Boolean, user: Boolean) {
        val changes = MusicRepository.Changes(device, user)
        L.d("Dispatching library change [changes=$changes]")
        for (listener in updateListeners) {
            listener.onMusicChanges(changes)
        }
    }

    private fun rebuildPersonIndices(lib: Library) {
        val composerSongs = linkedMapOf<String, MutableSet<Song>>()
        val composerDisplayNames = mutableMapOf<String, String>()
        val conductorSongs = linkedMapOf<String, MutableSet<Song>>()
        val conductorDisplayNames = mutableMapOf<String, String>()

        for (song in lib.songs) {
            for (raw in song.composers) {
                val trimmed = raw.trim()
                if (trimmed.isEmpty()) continue
                val key = trimmed.lowercase()
                composerDisplayNames.putIfAbsent(key, trimmed)
                composerSongs.getOrPut(key) { linkedSetOf() }.add(song)
            }
            for (raw in song.conductors) {
                val trimmed = raw.trim()
                if (trimmed.isEmpty()) continue
                val key = trimmed.lowercase()
                conductorDisplayNames.putIfAbsent(key, trimmed)
                conductorSongs.getOrPut(key) { linkedSetOf() }.add(song)
            }
        }

        val composers = mutableMapOf<String, PersonArtist>()
        val conductors = mutableMapOf<String, PersonArtist>()
        val byUid = mutableMapOf<Music.UID, PersonArtist>()

        for ((key, songs) in composerSongs) {
            val person =
                buildPersonArtist(PersonRole.COMPOSER, composerDisplayNames.getValue(key), songs)
            composers[key] = person
            byUid[person.uid] = person
        }
        for ((key, songs) in conductorSongs) {
            val person =
                buildPersonArtist(PersonRole.CONDUCTOR, conductorDisplayNames.getValue(key), songs)
            conductors[key] = person
            byUid[person.uid] = person
        }

        composerMap = composers
        conductorMap = conductors
        personUidMap = byUid
        val builtWorks = buildClassicalWorkIndex(lib)
        workBySongUid = builtWorks.first
        allClassicalWorks = builtWorks.second
    }

    private fun buildClassicalWorkIndex(
        lib: Library
    ): Pair<Map<Music.UID, ClassicalWork>, List<ClassicalWork>> =
        buildClassicalWorkIndex(lib.songs)

    private fun buildPersonArtist(
        role: PersonRole,
        rawName: String,
        songs: Set<Song>,
    ): PersonArtist =
        PersonArtist(
            role = role,
            rawName = rawName,
            songs = songs,
            explicitAlbums = songs.mapTo(linkedSetOf()) { it.album },
            implicitAlbums = emptySet(),
            genres = songs.flatMap { it.genres }.distinct(),
            covers = CoverCollection.from(songs.mapNotNull { it.cover }),
        )
}

internal fun buildClassicalWorkIndex(
    songs: Collection<Song>
): Pair<Map<Music.UID, ClassicalWork>, List<ClassicalWork>> {
    val songsByWorkKey = linkedMapOf<String, MutableList<Song>>()
    val titleByWorkKey = mutableMapOf<String, String>()
    val composerByWorkKey = mutableMapOf<String, String>()
    val genreByWorkKey = mutableMapOf<String, String?>()
    val fallbackMovementNumByUid = mutableMapOf<Music.UID, Int?>()

    for (song in songs) {
        val rawTitle = (song.name as? Name.Known)?.raw.orEmpty()
        val genreNames = song.genres.mapNotNull { (it.name as? Name.Known)?.raw }
        val primaryGenre =
            genreNames.firstOrNull { ClassicalMetadataEngine.isClassicalGenre(it) }
                ?: genreNames.firstOrNull { ClassicalMetadataEngine.isExplicitNonClassicalGenre(it) }
                ?: genreNames.firstOrNull()
        val explicitComposer = song.composers.firstOrNull()?.trim()?.takeIf { it.isNotEmpty() }
        val explicitConductor = song.conductors.firstOrNull()?.trim()?.takeIf { it.isNotEmpty() }
        if (!ClassicalMetadataEngine.isClassicalTrack(
                genre = primaryGenre,
                work = song.workName,
                composer = explicitComposer,
                conductor = explicitConductor,
                title = rawTitle,
            )
        ) {
            continue
        }

        val parsed =
            ClassicalMetadataEngine.splitWorkAndMovement(
                rawTitle = rawTitle,
                explicitWork = song.workName,
                explicitMovement = song.movementName,
                explicitMovementNumber = song.movementNumber,
            )
        val rawWork =
            parsed.work
                .ifBlank { rawTitle }
                .ifBlank { (song.album.name as? Name.Known)?.raw.orEmpty() }
                .trim()
        if (rawWork.isEmpty()) continue

        val composer =
            explicitComposer
                ?: (song.artists.firstOrNull()?.name as? Name.Known)?.raw
                ?: (song.album.artists.firstOrNull()?.name as? Name.Known)?.raw
                ?: "Unknown"
        val normalizedComposer = ClassicalMetadataEngine.normalizeComposerName(composer).ifBlank { composer }
        val workKey = "${normalizedComposer.lowercase(Locale.ROOT)}::${rawWork.lowercase(Locale.ROOT)}"
        titleByWorkKey.putIfAbsent(workKey, rawWork)
        composerByWorkKey.putIfAbsent(workKey, normalizedComposer)
        genreByWorkKey.putIfAbsent(workKey, primaryGenre)
        fallbackMovementNumByUid[song.uid] = parsed.movementNumber
        songsByWorkKey.getOrPut(workKey) { mutableListOf() }.add(song)
    }

    val index = mutableMapOf<Music.UID, ClassicalWork>()
    val uniqueWorks = ArrayList<ClassicalWork>(songsByWorkKey.size)
    val movementCmp =
        compareBy<Song>(
            { it.disc?.number ?: 0 },
            { it.movementNumber ?: fallbackMovementNumByUid[it.uid] ?: Int.MAX_VALUE },
            { it.track ?: Int.MAX_VALUE },
            { (it.name as? Name.Known)?.raw?.lowercase(Locale.ROOT).orEmpty() },
        )
    for ((workKey, workSongs) in songsByWorkKey) {
        val recordings =
            workSongs
                .groupBy { ClassicalWorkKey.recordingScope(it) }
                .entries
                .map { (scopeKey, scopedSongs) ->
                    val orderedMovements = scopedSongs.sortedWith(movementCmp)
                    val first = orderedMovements.first()
                    val conductors =
                        orderedMovements.flatMap { it.conductors }.distinct().joinToString(", ")
                    val artists =
                        orderedMovements
                            .flatMap { it.artists }
                            .mapNotNull { (it.name as? Name.Known)?.raw }
                            .distinct()
                            .joinToString(", ")
                    val year = orderedMovements.mapNotNull { it.date?.year }.minOrNull()
                    WorkRecording(
                        album = first.album,
                        conductorLabel = conductors,
                        ensembleLabel = artists,
                        year = year,
                        movements = orderedMovements,
                        scopeKey = scopeKey,
                    )
                }
                .sortedWith(compareBy<WorkRecording>({ it.year ?: Int.MAX_VALUE }, { it.album.name }, { it.conductorLabel }))

        val resolvedWorkTitle = titleByWorkKey.getValue(workKey)
        val resolvedComposer = composerByWorkKey.getValue(workKey)
        val resolvedGenre = genreByWorkKey[workKey]
        val classicalWork =
            ClassicalWork(
                key = workKey,
                workTitle = resolvedWorkTitle,
                composerName = resolvedComposer,
                recordings = recordings,
                form = ClassicalMetadataEngine.classifyWorkForm(resolvedWorkTitle, resolvedGenre),
                era = ClassicalMetadataEngine.classifyComposerEra(resolvedComposer, resolvedGenre),
            )
        uniqueWorks.add(classicalWork)
        for (song in workSongs) {
            index[song.uid] = classicalWork
        }
    }
    return index to uniqueWorks
}
