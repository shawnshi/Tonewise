/*
 * Copyright (c) 2024 Auxio Project
 * ExoPlaybackStateHolder.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.playback.service

import android.content.Context
import android.content.Intent
import android.media.audiofx.AudioEffect
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.OpenableColumns
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.decoder.ffmpeg.FfmpegAudioRenderer
import androidx.media3.exoplayer.BaseRenderer
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.RenderersFactory
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.audio.DefaultAudioSink
import androidx.media3.exoplayer.audio.MediaCodecAudioRenderer
import androidx.media3.exoplayer.mediacodec.MediaCodecSelector
import androidx.media3.exoplayer.source.MediaSource
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Provider
import kotlin.math.abs
import kotlin.time.Duration.Companion.milliseconds
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import com.shawnshi.tonewise.image.ImageSettings
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.audio.AudioOutputManager
import com.shawnshi.tonewise.playback.dsp.DspRepository
import com.shawnshi.tonewise.playback.persist.PersistenceRepository
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.replaygain.ReplayGainAudioProcessor
import com.shawnshi.tonewise.playback.sleep.SleepTimerController
import com.shawnshi.tonewise.playback.state.DeferredPlayback
import com.shawnshi.tonewise.playback.state.PlaybackCommand
import com.shawnshi.tonewise.playback.state.PlaybackStateHolder
import com.shawnshi.tonewise.playback.state.PlaybackStateManager
import com.shawnshi.tonewise.playback.state.Progression
import com.shawnshi.tonewise.playback.state.RawQueue
import com.shawnshi.tonewise.playback.state.RepeatMode
import com.shawnshi.tonewise.playback.state.ShuffleMode
import com.shawnshi.tonewise.playback.state.StateAck
import com.shawnshi.tonewise.sync.LastFmRepository
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.MusicParent
import org.oxycblt.musikr.Song
import timber.log.Timber as L

@OptIn(UnstableApi::class)
class ExoPlaybackStateHolder(
    private val context: Context,
    private val player: ExoPlayer,
    private val playbackManager: PlaybackStateManager,
    private val persistenceRepository: PersistenceRepository,
    private val playCountRepository: PlayCountRepository,
    private val playbackSettings: PlaybackSettings,
    private val commandFactory: PlaybackCommand.Factory,
    private val ffmpegReplayGainProcessor: ReplayGainAudioProcessor,
    private val mediaCodecReplayGainProcessor: ReplayGainAudioProcessor,
    private val audioOutputManager: AudioOutputManager,
    private val dspRepository: DspRepository,
    private val musicRepository: MusicRepository,
    private val imageSettings: ImageSettings,
    private val lastFmRepository: LastFmRepository,
    private val sleepTimerController: SleepTimerController,
) :
    PlaybackStateHolder,
    Player.Listener,
    MusicRepository.UpdateListener,
    PlaybackSettings.Listener,
    ImageSettings.Listener {
    private val saveJob = SupervisorJob()
    private val saveScope = CoroutineScope(Dispatchers.IO + saveJob)
    private val restoreScope = CoroutineScope(Dispatchers.IO + saveJob)
    private val mainScope = CoroutineScope(Dispatchers.Main.immediate + saveJob)
    private var currentSaveJob: Job? = null
    private var openAudioEffectSession = false
    private var activePlaySong: Song? = null
    private var accumulatedPlayMs = 0L
    private var playStartElapsedMs: Long? = null
    private var activePlayStartEpochSec: Long? = null
    private var lastNowPlayingUid: Music.UID? = null
    private var countedCurrentPlay = false
    private var currentDecoderName: String? = null
    private var consecutiveErrorCount = 0
    private val analyticsListener =
        object : AnalyticsListener {
            override fun onAudioDecoderInitialized(
                eventTime: AnalyticsListener.EventTime,
                decoderName: String,
                initializedTimestampMs: Long,
                initializationDurationMs: Long,
            ) {
                currentDecoderName = decoderName
                syncSignalPath(player.currentMediaItem?.song)
            }

            override fun onAudioInputFormatChanged(
                eventTime: AnalyticsListener.EventTime,
                format: androidx.media3.common.Format,
                decoderReuseEvaluation: androidx.media3.exoplayer.DecoderReuseEvaluation?,
            ) {
                syncSignalPath(player.currentMediaItem?.song)
            }
        }

    var sessionOngoing = false
        private set

    val wantsPlayback: Boolean
        get() = player.playWhenReady

    fun attach() {
        playbackManager.registerStateHolder(this)
        musicRepository.addUpdateListener(this)
        player.addListener(this)
        player.addAnalyticsListener(analyticsListener)
        ffmpegReplayGainProcessor.attach()
        mediaCodecReplayGainProcessor.attach()
        audioOutputManager.start { bitPerfect ->
            ffmpegReplayGainProcessor.setBypassed(bitPerfect)
            mediaCodecReplayGainProcessor.setBypassed(bitPerfect)
            reconcileAudioEffectSession(bitPerfect)
        }
        mainScope.launch {
            dspRepository.config
                .map { it.enabled to it.externalBridgeMode }
                .distinctUntilChanged()
                .drop(1)
                .collect { audioOutputManager.refreshRoute() }
        }
        mainScope.launch {
            var lastHandledSleepExpiryMs = sleepTimerController.state.value.expiredEpochMs
            sleepTimerController.state.collect { st ->
                player.volume = st.fadeGain.coerceIn(0f, 1f)
                if (st.expiredEpochMs > lastHandledSleepExpiryMs) {
                    lastHandledSleepExpiryMs = st.expiredEpochMs
                    player.playWhenReady = false
                    player.volume = 1f
                    deferSave()
                }
            }
        }
        playbackSettings.registerListener(this)
        imageSettings.registerListener(this)
    }

    fun release() {
        flushActivePlayTime()
        commitPlayCountIfQualified(completedNaturally = false)
        if (sessionOngoing) {
            val state = playbackManager.toSavedState()
            runCatching {
                runBlocking(Dispatchers.IO) {
                    withTimeoutOrNull(500L) {
                        persistenceRepository.saveState(state)
                    }
                }
            }
        }
        saveJob.cancel()
        playbackManager.unregisterStateHolder(this)
        musicRepository.removeUpdateListener(this)
        player.removeListener(this)
        player.removeAnalyticsListener(analyticsListener)
        audioOutputManager.stop()
        ffmpegReplayGainProcessor.release()
        mediaCodecReplayGainProcessor.release()
        imageSettings.unregisterListener(this)
        playbackSettings.unregisterListener(this)
        player.release()
    }

    override var parent: MusicParent? = null
        private set

    override val progression: Progression
        get() {
            val mediaItem = player.currentMediaItem ?: return Progression.nil()
            val duration = mediaItem.mediaMetadata.extras?.getLong("durationMs") ?: Long.MAX_VALUE
            val clampedPosition = player.currentPosition.coerceAtLeast(0).coerceAtMost(duration)
            val effectivePlayWhenReady =
                player.playWhenReady &&
                    player.playbackSuppressionReason != Player.PLAYBACK_SUPPRESSION_REASON_TRANSIENT_AUDIO_FOCUS_LOSS
            return Progression.from(effectivePlayWhenReady, player.isPlaying, clampedPosition)
        }

    override val repeatMode
        get() =
            when (val repeatMode = player.repeatMode) {
                Player.REPEAT_MODE_OFF -> RepeatMode.NONE
                Player.REPEAT_MODE_ONE -> RepeatMode.TRACK
                Player.REPEAT_MODE_ALL -> RepeatMode.ALL
                else -> throw IllegalStateException("Unknown repeat mode: $repeatMode")
            }

    override val audioSessionId: Int
        get() = player.audioSessionId

    override fun resolveQueue(): RawQueue {
        if (musicRepository.library == null) {
            // No library, cannot do anything.
            return RawQueue(emptyList(), emptyList(), 0)
        }
        val heap = (0 until player.mediaItemCount).map { player.getMediaItemAt(it) }
        val shuffledMapping =
            if (player.shuffleModeEnabled) {
                player.unscrambleQueueIndices()
            } else {
                emptyList()
            }
        return RawQueue(heap.mapNotNull { it.song }, shuffledMapping, player.currentMediaItemIndex)
    }

    override fun handleDeferred(action: DeferredPlayback): Boolean {
        val library =
            musicRepository.library?.takeIf { !it.empty() }
                // No library, cannot do anything.
                ?: return false

        when (action) {
            // Restore state -> Start a new restoreState job
            is DeferredPlayback.RestoreState -> {
                L.d("Restoring playback state")
                restoreScope.launch {
                    val state = persistenceRepository.readState()
                    withContext(Dispatchers.Main) {
                        if (state != null) {
                            // Apply the saved state on the main thread to prevent code expecting
                            // state updates on the main thread from crashing.
                            playbackManager.applySavedState(state, false)
                            if (action.play) {
                                playbackManager.playing(true)
                            }
                        } else if (action.fallback != null) {
                            playbackManager.playDeferred(action.fallback)
                        }
                    }
                }
            }
            // Shuffle all -> Start new playback from all songs
            is DeferredPlayback.ShuffleAll -> {
                L.d("Shuffling all tracks")
                playbackManager.play(
                    requireNotNull(commandFactory.all(ShuffleMode.ON)) {
                        "Invalid playback parameters"
                    }
                )
            }
            // Open -> Try to find the Song for the given file and then play it from all songs
            is DeferredPlayback.Open -> {
                L.d("Opening specified file")
                context.applicationContext.contentResolver
                    .query(
                        action.uri,
                        arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
                        null,
                        null,
                        null,
                    )
                    ?.use { cursor ->
                        val displayNameIndex =
                            cursor.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME)
                        val sizeIndex = cursor.getColumnIndexOrThrow(OpenableColumns.SIZE)
                        if (cursor.moveToFirst()) {
                            val displayName = cursor.getString(displayNameIndex)
                            val size = cursor.getLong(sizeIndex)
                            val song =
                                library.songs.find {
                                    it.path.name == displayName && it.size == size
                                }
                            if (song != null) {
                                val command =
                                    requireNotNull(
                                        commandFactory.songFromAll(song, ShuffleMode.IMPLICIT)
                                    ) {
                                        "Invalid playback command"
                                    }
                                playbackManager.play(command)
                            }
                        }
                    }
            }
        }

        return true
    }

    override fun playing(playing: Boolean) {
        if (playing) {
            consecutiveErrorCount = 0
            sessionOngoing = true
        }
        player.playWhenReady = playing
        if (!playing) {
            deferSave()
        }
    }

    override fun seekTo(positionMs: Long) {
        consecutiveErrorCount = 0
        player.seekTo(positionMs)
        deferSave()
        // Ack handled w/ExoPlayer events
    }

    override fun repeatMode(repeatMode: RepeatMode) {
        player.repeatMode =
            when (repeatMode) {
                RepeatMode.NONE -> Player.REPEAT_MODE_OFF
                RepeatMode.ALL -> Player.REPEAT_MODE_ALL
                RepeatMode.TRACK -> Player.REPEAT_MODE_ONE
            }
        updatePauseOnRepeat()
        playbackManager.ack(this, StateAck.RepeatModeChanged)
        deferSave()
    }

    override fun newPlayback(command: PlaybackCommand) {
        consecutiveErrorCount = 0
        parent = command.parent
        player.shuffleModeEnabled = command.shuffled
        player.setMediaItems(command.queue.map { it.buildMediaItem() })
        val startIndex =
            command.song
                ?.let { command.queue.indexOf(it) }
                .also { check(it != -1) { "Start song not in queue" } }
        if (command.shuffled) {
            player.setShuffleOrder(
                WorkShuffleOrder.fromSongs(command.queue, startIndex ?: -1)
            )
        }
        val target = startIndex ?: player.currentTimeline.getFirstWindowIndex(command.shuffled)
        sessionOngoing = true
        player.seekTo(target, C.TIME_UNSET)
        player.prepare()
        player.play()
        playbackManager.ack(this, StateAck.NewPlayback)
        deferSave()
    }

    override fun shuffled(shuffled: Boolean) {
        player.setShuffleModeEnabled(shuffled)
        if (player.shuffleModeEnabled) {
            // Have to manually refresh the shuffle seed and anchor it to the new current songs
            val queueSongs = (0 until player.mediaItemCount).mapNotNull { player.getMediaItemAt(it).song }
            val order =
                if (queueSongs.size == player.mediaItemCount) {
                    WorkShuffleOrder.fromSongs(queueSongs, player.currentMediaItemIndex)
                } else {
                    BetterShuffleOrder(player.mediaItemCount, player.currentMediaItemIndex)
                }
            player.setShuffleOrder(order)
        }
        playbackManager.ack(this, StateAck.QueueReordered)
        deferSave()
    }

    override fun next() {
        // Replicate the old pseudo-circular queue behavior when no repeat option is implemented.
        // Basically, you can't skip back and wrap around the queue, but you can skip forward and
        // wrap around the queue, albeit playback will be paused.
        if (player.repeatMode == Player.REPEAT_MODE_ALL || player.hasNextMediaItem()) {
            player.seekToNext()
            if (!playbackSettings.rememberPause) {
                player.play()
            }
        } else {
            player.seekTo(
                player.currentTimeline.getFirstWindowIndex(player.shuffleModeEnabled),
                C.TIME_UNSET,
            )
            // TODO: Dislike the UX implications of this, I feel should I bite the bullet
            //  and switch to dynamic skip enable/disable?
            if (!playbackSettings.rememberPause) {
                player.pause()
            }
        }
        playbackManager.ack(this, StateAck.IndexMoved)
        deferSave()
    }

    override fun prev() {
        if (playbackSettings.rewindWithPrev) {
            player.seekToPrevious()
        } else if (player.hasPreviousMediaItem()) {
            player.seekToPreviousMediaItem()
        } else {
            player.seekTo(0)
        }
        if (!playbackSettings.rememberPause) {
            player.play()
        }
        playbackManager.ack(this, StateAck.IndexMoved)
        deferSave()
    }

    override fun goto(index: Int) {
        val indices = player.unscrambleQueueIndices()
        if (index !in indices.indices) {
            return
        }

        consecutiveErrorCount = 0
        val trueIndex = indices[index]
        player.seekTo(trueIndex, C.TIME_UNSET) // Handles remaining custom logic
        if (!playbackSettings.rememberPause) {
            player.play()
        }
        playbackManager.ack(this, StateAck.IndexMoved)
        deferSave()
    }

    override fun playNext(songs: List<Song>, ack: StateAck.PlayNext) {
        val currTimeline = player.currentTimeline
        val nextIndex =
            if (currTimeline.isEmpty) {
                C.INDEX_UNSET
            } else {
                currTimeline.getNextWindowIndex(
                    player.currentMediaItemIndex,
                    Player.REPEAT_MODE_OFF,
                    player.shuffleModeEnabled,
                )
            }

        if (nextIndex == C.INDEX_UNSET) {
            player.addMediaItems(songs.map { it.buildMediaItem() })
        } else {
            player.addMediaItems(nextIndex, songs.map { it.buildMediaItem() })
        }
        playbackManager.ack(this, ack)
        deferSave()
    }

    override fun addToQueue(songs: List<Song>, ack: StateAck.AddToQueue) {
        player.addMediaItems(songs.map { it.buildMediaItem() })
        playbackManager.ack(this, ack)
        deferSave()
    }

    override fun move(from: Int, to: Int, ack: StateAck.Move) {
        val indices = player.unscrambleQueueIndices()
        if (indices.isEmpty()) {
            return
        }

        val trueFrom = indices[from]
        val trueTo = indices[to]
        // ExoPlayer does not actually update it's ShuffleOrder when moving items. Retain a
        // semblance of "normalcy" by doing a weird no-op swap that actually moves the item.
        when {
            trueFrom > trueTo -> {
                player.moveMediaItem(trueFrom, trueTo)
                player.moveMediaItem(trueTo + 1, trueFrom)
            }
            trueTo > trueFrom -> {
                player.moveMediaItem(trueFrom, trueTo)
                player.moveMediaItem(trueTo - 1, trueFrom)
            }
        }
        playbackManager.ack(this, ack)
        deferSave()
    }

    override fun remove(at: Int, ack: StateAck.Remove) {
        val indices = player.unscrambleQueueIndices()
        if (at !in indices.indices) {
            return
        }

        val trueIndex = indices[at]
        val songWillChange = player.currentMediaItemIndex == trueIndex
        player.removeMediaItem(trueIndex)
        if (songWillChange && !playbackSettings.rememberPause) {
            player.play()
        }
        playbackManager.ack(this, ack)
        deferSave()
    }

    override fun applySavedState(
        parent: MusicParent?,
        rawQueue: RawQueue,
        positionMs: Long,
        repeatMode: RepeatMode,
        ack: StateAck.NewPlayback?,
    ) {
        var sendNewPlaybackEvent = false
        var shouldSeek = false
        if (this.parent != parent) {
            this.parent = parent
            sendNewPlaybackEvent = true
        }
        if (rawQueue != resolveQueue()) {
            val wasPlaying =
                player.playWhenReady &&
                    player.playbackState != Player.STATE_IDLE &&
                    player.playbackState != Player.STATE_ENDED
            player.setMediaItems(rawQueue.heap.map { it.buildMediaItem() })
            if (rawQueue.isShuffled) {
                player.shuffleModeEnabled = true
                player.setShuffleOrder(BetterShuffleOrder(rawQueue.shuffledMapping.toIntArray()))
            } else {
                player.shuffleModeEnabled = false
            }
            player.seekTo(rawQueue.heapIndex, C.TIME_UNSET)
            player.prepare()
            if (wasPlaying) {
                sessionOngoing = true
                player.play()
            } else {
                player.pause()
            }
            sendNewPlaybackEvent = true
            shouldSeek = true
        }

        repeatMode(repeatMode)
        // See if we differ by more than a second. This allows us to avoid a meaningless seek
        // in the case of a "tight restore" (i.e music was reloaded).
        // In the case that this is a false positive, it's not very percievable (at least compared
        // to skipping when updating the library).
        // TODO: Introduce a better state management system rather than do something finicky like
        // this.
        if (shouldSeek || abs(player.currentPosition - positionMs) > 1000L) {
            player.seekTo(positionMs)
        }

        if (sendNewPlaybackEvent) {
            ack?.let { playbackManager.ack(this, it) }
        }
    }

    override fun endSession() {
        // This session has ended, so we need to reset this flag for when the next
        // session starts.
        playbackManager.playing(false)
        save {
            // User could feasibly start playing again if they were fast enough, so
            // we need to avoid stopping the foreground state if that's the case.
            if (!playbackManager.progression.isPlaying) {
                sessionOngoing = false
                playbackManager.ack(this, StateAck.SessionEnded)
            }
        }
    }

    override fun reset(ack: StateAck.NewPlayback) {
        player.setMediaItems(listOf())
        playbackManager.ack(this, ack)
        deferSave()
    }

    // --- PLAYER OVERRIDES ---

    override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
        super.onPlayWhenReadyChanged(playWhenReady, reason)

        if (player.playWhenReady) {
            // Mark that we have started playing so that the notification can now be posted.
            L.d("Player has started playing")
            sessionOngoing = true
            reconcileAudioEffectSession(audioOutputManager.signalPath.value.dspBypassed)
        } else if (openAudioEffectSession) {
            // Make sure to close the audio session when we stop playback.
            L.d("Closing audio effect session")
            broadcastAudioEffectAction(AudioEffect.ACTION_CLOSE_AUDIO_EFFECT_CONTROL_SESSION)
            openAudioEffectSession = false
        }
    }

    override fun onIsPlayingChanged(isPlaying: Boolean) {
        super.onIsPlayingChanged(isPlaying)
        if (isPlaying) {
            consecutiveErrorCount = 0
            val song = player.currentMediaItem?.song
            if (song != null && activePlaySong != song) {
                activePlaySong = song
                accumulatedPlayMs = 0L
                countedCurrentPlay = false
                activePlayStartEpochSec = System.currentTimeMillis() / 1000L
            }
            if (activePlayStartEpochSec == null) {
                activePlayStartEpochSec = System.currentTimeMillis() / 1000L
            }
            if (playStartElapsedMs == null) {
                playStartElapsedMs = SystemClock.elapsedRealtime()
            }
            if (song != null && lastNowPlayingUid != song.uid) {
                lastNowPlayingUid = song.uid
                lastFmRepository.onNowPlaying(song)
            }
            lastFmRepository.triggerBackgroundFlushIfNeeded()
        } else {
            flushActivePlayTime()
            commitPlayCountIfQualified(completedNaturally = false)
        }
    }

    override fun onPlaybackStateChanged(playbackState: Int) {
        super.onPlaybackStateChanged(playbackState)

        if (playbackState == Player.STATE_ENDED) {
            flushActivePlayTime()
            commitPlayCountIfQualified(completedNaturally = true)
            if (player.repeatMode == Player.REPEAT_MODE_OFF) {
                val firstIndex = player.currentTimeline.getFirstWindowIndex(player.shuffleModeEnabled)
                if (firstIndex != C.INDEX_UNSET) {
                    player.seekTo(firstIndex, C.TIME_UNSET)
                }
                player.pause()
                playbackManager.ack(this, StateAck.IndexMoved)
                deferSave()
            }
        }
    }

    override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
        flushActivePlayTime()
        val completedNaturally =
            reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO ||
                reason == Player.MEDIA_ITEM_TRANSITION_REASON_REPEAT
        commitPlayCountIfQualified(completedNaturally = completedNaturally)

        val previousSong = activePlaySong
        val nextSong = mediaItem?.song
        activePlaySong = nextSong
        accumulatedPlayMs = 0L
        countedCurrentPlay = false
        playStartElapsedMs = if (player.isPlaying) SystemClock.elapsedRealtime() else null
        activePlayStartEpochSec = if (player.isPlaying) System.currentTimeMillis() / 1000L else null
        lastNowPlayingUid = null

        super.onMediaItemTransition(mediaItem, reason)

        syncSignalPath(nextSong)
        if (nextSong != null && (player.isPlaying || player.playWhenReady)) {
            lastNowPlayingUid = nextSong.uid
            lastFmRepository.onNowPlaying(nextSong)
        }

        if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO) {
            if (sleepTimerController.onAutoTrackTransition(previousSong, nextSong)) {
                player.playWhenReady = false
                player.seekTo(0L)
            }
            playbackManager.ack(this, StateAck.IndexMoved)
            deferSave()
        }
    }

    private fun activeReplayGainProcessor(): ReplayGainAudioProcessor {
        val decoder = currentDecoderName
        return when {
            decoder != null && decoder.contains("ffmpeg", ignoreCase = true) ->
                ffmpegReplayGainProcessor
            mediaCodecReplayGainProcessor.configuredEncoding != C.ENCODING_INVALID ->
                mediaCodecReplayGainProcessor
            else -> ffmpegReplayGainProcessor
        }
    }

    private fun syncSignalPath(song: Song?) {
        val activeProcessor = activeReplayGainProcessor()
        val bitPerfect =
            audioOutputManager.updatePlaybackMetadata(
                song = song,
                decoderName = currentDecoderName,
                pcmEncoding = activeProcessor.configuredEncoding,
                replayGainDb = activeProcessor.currentAdjustmentDb,
            )
        ffmpegReplayGainProcessor.setBypassed(bitPerfect)
        mediaCodecReplayGainProcessor.setBypassed(bitPerfect)
        reconcileAudioEffectSession(bitPerfect)
    }

    private fun reconcileAudioEffectSession(bitPerfect: Boolean) {
        if (Looper.myLooper() != player.applicationLooper) {
            Handler(player.applicationLooper).post { reconcileAudioEffectSession(bitPerfect) }
            return
        }
        val shouldOpen =
            player.playWhenReady && !bitPerfect && dspRepository.config.value.externalBridgeMode
        if (shouldOpen && !openAudioEffectSession) {
            L.d("Opening external audio effect session")
            broadcastAudioEffectAction(AudioEffect.ACTION_OPEN_AUDIO_EFFECT_CONTROL_SESSION)
            openAudioEffectSession = true
        } else if (!shouldOpen && openAudioEffectSession) {
            L.d("Closing external audio effect session")
            broadcastAudioEffectAction(AudioEffect.ACTION_CLOSE_AUDIO_EFFECT_CONTROL_SESSION)
            openAudioEffectSession = false
        }
    }

    private fun flushActivePlayTime() {
        val startedAt = playStartElapsedMs ?: return
        val delta = (SystemClock.elapsedRealtime() - startedAt).coerceAtLeast(0L)
        accumulatedPlayMs += delta
        playStartElapsedMs = if (player.isPlaying) SystemClock.elapsedRealtime() else null
    }

    private fun commitPlayCountIfQualified(completedNaturally: Boolean) {
        val song = activePlaySong ?: return
        if (countedCurrentPlay) return
        val effectiveDurationMs = if (song.durationMs > 0L) song.durationMs else player.duration.coerceAtLeast(0L)
        if (isPlayQualified(effectiveDurationMs, accumulatedPlayMs)) {
            playCountRepository.incrementPlayCount(song)
            lastFmRepository.onSongPlayed(
                song = song,
                timestampSec = activePlayStartEpochSec ?: (System.currentTimeMillis() / 1000L),
            )
            countedCurrentPlay = true
        }
    }

    override fun onEvents(player: Player, events: Player.Events) {
        super.onEvents(player, events)

        // So many actions trigger progression changes that it becomes easier just to handle it
        // in an ExoPlayer callback anyway. This doesn't really cause issues anywhere.
        if (
            events.containsAny(
                Player.EVENT_PLAY_WHEN_READY_CHANGED,
                Player.EVENT_IS_PLAYING_CHANGED,
                Player.EVENT_PLAYBACK_SUPPRESSION_REASON_CHANGED,
                Player.EVENT_POSITION_DISCONTINUITY,
            )
        ) {
            L.d("Player state changed, must synchronize state")
            playbackManager.ack(this, StateAck.ProgressionChanged)
        }
    }

    override fun onPlayerError(error: PlaybackException) {
        L.e("Player error occurred")
        L.e(error.stackTraceToString())
        consecutiveErrorCount++
        if (consecutiveErrorCount >= MAX_CONSECUTIVE_ERRORS || player.mediaItemCount <= 1) {
            L.w("Halting playback after $consecutiveErrorCount consecutive errors")
            player.pause()
            playbackManager.ack(this, StateAck.ProgressionChanged)
            return
        }
        player.prepare()
        playbackManager.next()
    }

    private fun broadcastAudioEffectAction(event: String) {
        L.d("Broadcasting AudioEffect event: $event")
        context.sendBroadcast(
            Intent(event)
                .putExtra(AudioEffect.EXTRA_PACKAGE_NAME, context.packageName)
                .putExtra(AudioEffect.EXTRA_AUDIO_SESSION, audioSessionId)
                .putExtra(AudioEffect.EXTRA_CONTENT_TYPE, AudioEffect.CONTENT_TYPE_MUSIC)
        )
    }

    // --- MUSICREPOSITORY METHODS ---

    override fun onMusicChanges(changes: MusicRepository.Changes) {
        if (changes.deviceLibrary && musicRepository.library?.takeIf { !it.empty() } != null) {
            // We now have a library, see if we have anything we need to do.
            L.d("Library obtained, requesting action")
            playbackManager.requestAction(this)
        }
    }

    // --- PLAYBACKSETTINGS OVERRIDES ---

    override fun onPauseOnRepeatChanged() {
        super.onPauseOnRepeatChanged()
        updatePauseOnRepeat()
    }

    override fun onReplayGainSettingsChanged() {
        syncSignalPath(player.currentMediaItem?.song)
    }

    override fun onAudioOutputSettingsChanged() {
        syncSignalPath(player.currentMediaItem?.song)
    }

    private fun updatePauseOnRepeat() {
        player.pauseAtEndOfMediaItems =
            player.repeatMode == Player.REPEAT_MODE_ONE && playbackSettings.pauseOnRepeat
    }

    private fun save(cb: () -> Unit) {
        saveJob {
            if (sessionOngoing) {
                persistenceRepository.saveState(playbackManager.toSavedState())
            }
            withContext(Dispatchers.Main) { cb() }
        }
    }

    private fun deferSave() {
        saveJob {
            L.d("Waiting for save buffer")
            delay(SAVE_BUFFER.milliseconds)
            yield()
            L.d("Committing saved state")
            if (sessionOngoing) {
                persistenceRepository.saveState(playbackManager.toSavedState())
            }
        }
    }

    private fun saveJob(block: suspend () -> Unit) {
        currentSaveJob?.let {
            L.d("Discarding prior save job")
            it.cancel()
        }
        currentSaveJob = saveScope.launch { block() }
    }

    private fun Song.buildMediaItem() = MediaItem.Builder().setUri(uri).setTag(this).build()

    private val MediaItem.song: Song?
        get() = this.localConfiguration?.tag as? Song?

    private fun Player.unscrambleQueueIndices(): List<Int> {
        val timeline = currentTimeline
        if (timeline.isEmpty) {
            return emptyList()
        }
        val queue = mutableListOf<Int>()

        // Add the active queue item.
        val currentMediaItemIndex = currentMediaItemIndex
        queue.add(currentMediaItemIndex)

        // Fill queue alternating with next and/or previous queue items.
        var firstMediaItemIndex = currentMediaItemIndex
        var lastMediaItemIndex = currentMediaItemIndex
        val shuffleModeEnabled = shuffleModeEnabled
        while ((firstMediaItemIndex != C.INDEX_UNSET || lastMediaItemIndex != C.INDEX_UNSET)) {
            // Begin with next to have a longer tail than head if an even sized queue needs to be
            // trimmed.
            if (lastMediaItemIndex != C.INDEX_UNSET) {
                lastMediaItemIndex =
                    timeline.getNextWindowIndex(
                        lastMediaItemIndex,
                        Player.REPEAT_MODE_OFF,
                        shuffleModeEnabled,
                    )
                if (lastMediaItemIndex != C.INDEX_UNSET) {
                    queue.add(lastMediaItemIndex)
                }
            }
            if (firstMediaItemIndex != C.INDEX_UNSET) {
                firstMediaItemIndex =
                    timeline.getPreviousWindowIndex(
                        firstMediaItemIndex,
                        Player.REPEAT_MODE_OFF,
                        shuffleModeEnabled,
                    )
                if (firstMediaItemIndex != C.INDEX_UNSET) {
                    queue.add(0, firstMediaItemIndex)
                }
            }
        }

        return queue
    }

    class Factory
    @Inject
    constructor(
        @ApplicationContext private val context: Context,
        private val playbackManager: PlaybackStateManager,
        private val persistenceRepository: PersistenceRepository,
        private val playCountRepository: PlayCountRepository,
        private val playbackSettings: PlaybackSettings,
        private val commandFactory: PlaybackCommand.Factory,
        private val mediaSourceFactory: MediaSource.Factory,
        private val replayGainProcessorProvider: Provider<ReplayGainAudioProcessor>,
        private val audioOutputManager: AudioOutputManager,
        private val dspRepository: DspRepository,
        private val musicRepository: MusicRepository,
        private val imageSettings: ImageSettings,
        private val lastFmRepository: LastFmRepository,
        private val sleepTimerController: SleepTimerController,
    ) {
        fun create(): ExoPlaybackStateHolder {
            val enableFloat = audioOutputManager.shouldUseFloatPipeline()
            audioOutputManager.setActiveFloatPipeline(enableFloat)
            val ffmpegProcessor = replayGainProcessorProvider.get()
            val mediaCodecProcessor = replayGainProcessorProvider.get()
            val ffmpegSink =
                DefaultAudioSink.Builder(context)
                    .setEnableFloatOutput(enableFloat)
                    .setAudioProcessors(arrayOf(ffmpegProcessor))
                    .build()
            val mediaCodecSink =
                DefaultAudioSink.Builder(context)
                    .setEnableFloatOutput(enableFloat)
                    .setAudioProcessors(arrayOf(mediaCodecProcessor))
                    .build()
            // Since Auxio is a music player, only specify an audio renderer to save
            // battery/apk size/cache size]
            val audioRenderer = RenderersFactory { handler, _, audioListener, _, _ ->
                arrayOf<BaseRenderer>(
                    FfmpegAudioRenderer(handler, audioListener, ffmpegSink),
                    MediaCodecAudioRenderer(
                        context,
                        MediaCodecSelector.DEFAULT,
                        handler,
                        audioListener,
                        mediaCodecSink,
                    ),
                )
            }

            val exoPlayer =
                ExoPlayer.Builder(context, audioRenderer)
                    .setMediaSourceFactory(mediaSourceFactory)
                    // Enable automatic WakeLock support
                    .setWakeMode(C.WAKE_MODE_LOCAL)
                    .setAudioAttributes(
                        // Signal that we are a music player.
                        AudioAttributes.Builder()
                            .setUsage(C.USAGE_MEDIA)
                            .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                            .build(),
                        true,
                    )
                    .build()

            return ExoPlaybackStateHolder(
                context,
                exoPlayer,
                playbackManager,
                persistenceRepository,
                playCountRepository,
                playbackSettings,
                commandFactory,
                ffmpegProcessor,
                mediaCodecProcessor,
                audioOutputManager,
                dspRepository,
                musicRepository,
                imageSettings,
                lastFmRepository,
                sleepTimerController,
            )
        }
    }

    internal companion object {
        const val SAVE_BUFFER = 5000L
        const val MAX_CONSECUTIVE_ERRORS = 5

        internal fun isPlayQualified(durationMs: Long, accumulatedPlayMs: Long): Boolean {
            val minThresholdMs =
                if (durationMs < 30_000L) {
                    maxOf(1_000L, durationMs / 2L)
                } else {
                    minOf(240_000L, maxOf(30_000L, durationMs / 2L))
                }
            return accumulatedPlayMs >= minThresholdMs
        }
    }
}
