/*
 * Copyright (c) 2026 Tonewise Project
 * SleepTimerController.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.sleep

import com.shawnshi.tonewise.playback.replaygain.ReplayGainAudioProcessor
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.oxycblt.musikr.Song

enum class SleepTimerMode {
    OFF,
    COUNTDOWN,
    END_OF_TRACK,
    END_OF_WORK,
}

data class SleepTimerState(
    val mode: SleepTimerMode = SleepTimerMode.OFF,
    val remainingMs: Long = 0L,
    val fadeGain: Float = 1f,
    val expiredEpochMs: Long = 0L,
)

/**
 * Shared sleep timer across main playback and OLED Wall Clock supporting timed countdown with
 * 10-second fade-out, end-of-track stop, and classical end-of-work-block stop.
 */
@Singleton
class SleepTimerController internal constructor(
    private val nowMs: () -> Long,
) {
    @Inject constructor() : this({ System.currentTimeMillis() })

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val stateLock = Any()
    private val _state = MutableStateFlow(SleepTimerState())
    val state: StateFlow<SleepTimerState> = _state.asStateFlow()

    private var targetEndMs: Long = 0L
    private var armedWorkKey: String? = null
    private var tickerJob: Job? = null

    fun setCountdown(minutes: Int) {
        val clamped = minutes.coerceIn(1, 720)
        synchronized(stateLock) {
            tickerJob?.cancel()
            val totalMs = clamped * 60_000L
            targetEndMs = nowMs() + totalMs
            armedWorkKey = null
            _state.value =
                _state.value.copy(
                    mode = SleepTimerMode.COUNTDOWN,
                    remainingMs = totalMs,
                    fadeGain = 1f,
                )
            tickerJob =
                scope.launch {
                    while (isActive) {
                        delay(TICK_INTERVAL_MS)
                        if (!tickCountdown()) {
                            break
                        }
                    }
                }
        }
    }

    fun setEndOfTrack() {
        synchronized(stateLock) {
            tickerJob?.cancel()
            tickerJob = null
            targetEndMs = 0L
            armedWorkKey = null
            _state.value =
                _state.value.copy(
                    mode = SleepTimerMode.END_OF_TRACK,
                    remainingMs = 0L,
                    fadeGain = 1f,
                )
        }
    }

    fun setEndOfWork(activeSong: Song? = null) {
        synchronized(stateLock) {
            tickerJob?.cancel()
            tickerJob = null
            targetEndMs = 0L
            armedWorkKey = activeSong?.let { ReplayGainAudioProcessor.buildWorkBlockKey(it) }
            _state.value =
                _state.value.copy(
                    mode = SleepTimerMode.END_OF_WORK,
                    remainingMs = 0L,
                    fadeGain = 1f,
                )
        }
    }

    fun cancel() {
        synchronized(stateLock) {
            tickerJob?.cancel()
            tickerJob = null
            targetEndMs = 0L
            armedWorkKey = null
            _state.value =
                _state.value.copy(
                    mode = SleepTimerMode.OFF,
                    remainingMs = 0L,
                    fadeGain = 1f,
                )
        }
    }

    fun onAutoTrackTransition(previousSong: Song?, nextSong: Song?): Boolean {
        synchronized(stateLock) {
            val curMode = _state.value.mode
            if (curMode != SleepTimerMode.END_OF_TRACK && curMode != SleepTimerMode.END_OF_WORK) {
                return false
            }
            val prevKey =
                previousSong?.let { ReplayGainAudioProcessor.buildWorkBlockKey(it) }
                    ?: armedWorkKey
            val nextKey = nextSong?.let { ReplayGainAudioProcessor.buildWorkBlockKey(it) }
            if (!shouldStopAtTransition(curMode, prevKey, nextKey)) {
                return false
            }
            targetEndMs = 0L
            armedWorkKey = null
            _state.value =
                SleepTimerState(
                    mode = SleepTimerMode.OFF,
                    remainingMs = 0L,
                    fadeGain = 1f,
                    expiredEpochMs = nowMs(),
                )
            return true
        }
    }

    private fun tickCountdown(): Boolean {
        synchronized(stateLock) {
            if (_state.value.mode != SleepTimerMode.COUNTDOWN) {
                return false
            }
            val remaining = targetEndMs - nowMs()
            if (remaining <= 0L) {
                targetEndMs = 0L
                armedWorkKey = null
                tickerJob = null
                _state.value =
                    SleepTimerState(
                        mode = SleepTimerMode.OFF,
                        remainingMs = 0L,
                        fadeGain = 0f,
                        expiredEpochMs = nowMs(),
                    )
                return false
            }
            _state.value =
                _state.value.copy(
                    mode = SleepTimerMode.COUNTDOWN,
                    remainingMs = remaining,
                    fadeGain = computeFadeGain(remaining),
                )
            return true
        }
    }

    companion object {
        private const val TICK_INTERVAL_MS = 250L
        private const val FADE_WINDOW_MS = 10_000L

        fun computeFadeGain(remainingMs: Long, fadeWindowMs: Long = FADE_WINDOW_MS): Float {
            if (remainingMs <= 0L) return 0f
            if (fadeWindowMs <= 0L || remainingMs >= fadeWindowMs) return 1f
            return (remainingMs.toFloat() / fadeWindowMs.toFloat()).coerceIn(0f, 1f)
        }

        fun shouldStopAtTransition(
            mode: SleepTimerMode,
            previousWorkKey: String?,
            nextWorkKey: String?,
        ): Boolean =
            when (mode) {
                SleepTimerMode.END_OF_TRACK -> true
                SleepTimerMode.END_OF_WORK -> {
                    val prev = previousWorkKey?.trim()
                    prev.isNullOrEmpty() || !prev.equals(nextWorkKey?.trim(), ignoreCase = true)
                }
                else -> false
            }
    }
}
