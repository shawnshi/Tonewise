/*
 * Copyright (c) 2022 Auxio Project
 * StyledSeekBar.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.playback.ui

import android.content.Context
import android.content.res.ColorStateList
import android.util.AttributeSet
import androidx.core.view.ViewCompat
import com.google.android.material.slider.Slider
import kotlin.math.max
import com.shawnshi.tonewise.databinding.ViewSeekBarBinding
import com.shawnshi.tonewise.playback.dsToSecs
import com.shawnshi.tonewise.playback.formatDurationDs
import com.shawnshi.tonewise.playback.formatDurationSecs
import com.shawnshi.tonewise.util.inflater
import timber.log.Timber as L

/**
 * A wrapper around [Slider] that shows position and duration values and sanitizes input to reduce
 * crashes from invalid values.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
class StyledSeekBar
@JvmOverloads
constructor(context: Context, attrs: AttributeSet? = null, defStyleAttr: Int = 0) :
    ForcedLTRFrameLayout(context, attrs, defStyleAttr),
    Slider.OnSliderTouchListener,
    Slider.OnChangeListener {
    private val binding = ViewSeekBarBinding.inflate(context.inflater, this, true)
    private var displayedPositionSecs: Long? = null
    private var showRemainingTime: Boolean = false
    private val defaultTrackActiveTint: ColorStateList = binding.seekBarSlider.trackActiveTintList
    private val defaultThumbTint: ColorStateList = binding.seekBarSlider.thumbTintList

    init {
        binding.seekBarSlider.addOnSliderTouchListener(this)
        binding.seekBarSlider.addOnChangeListener(this)
        binding.seekBarSlider.setLabelFormatter { value ->
            value.toLong().dsToSecs().formatDurationSecs(true)
        }
        binding.seekBarSlider.dragTooltipFormatter = { value ->
            value.toLong().dsToSecs().formatDurationSecs(true)
        }
        binding.seekBarDuration.setOnClickListener {
            showRemainingTime = !showRemainingTime
            updateDurationLabel(positionDs)
        }
    }

    /** Enables/disables wavy active-track rendering to match playback state. */
    fun setWaveEnabled(enabled: Boolean) {
        binding.seekBarSlider.setWaveEnabled(enabled)
    }

    /** Configures a 150-point precomputed RMS waveform envelope, or null to restore placeholder/wavy mode. */
    fun setWaveformEnvelope(envelope: FloatArray?) {
        binding.seekBarSlider.setWaveformEnvelope(envelope)
    }

    /** Enables or disables PC-style 150-bar waveform progress bar rendering. */
    fun setWaveformStyleEnabled(enabled: Boolean) {
        binding.seekBarSlider.setWaveformStyleEnabled(enabled)
    }

    /** Switches waveform bars and track to 0dp rectilinear Zune geometry. */
    fun setRectilinearMode(enabled: Boolean) {
        binding.seekBarSlider.setRectilinearMode(enabled)
    }

    /** Optionally overrides active waveform/thumb tint for Dynamic Cover Accent, or restores default on null. */
    fun setDynamicAccentColor(argb: Int?) {
        if (argb != null) {
            val csl = ColorStateList.valueOf(argb)
            binding.seekBarSlider.trackActiveTintList = csl
            binding.seekBarSlider.thumbTintList = csl
        } else {
            binding.seekBarSlider.trackActiveTintList = defaultTrackActiveTint
            binding.seekBarSlider.thumbTintList = defaultThumbTint
        }
    }

    /** The current [Listener] attached to this instance. */
    var listener: Listener? = null

    /**
     * The current position, in deci-seconds(1/10th of a second). This is the current value of the
     * SeekBar and is indicated by the start TextView in the layout.
     */
    var positionDs: Long
        get() = binding.seekBarSlider.value.toLong()
        set(value) {
            // Sanity check 1: Ensure that no negative values are sneaking their way into
            // this component.
            val from = max(value, 0)
            // Sanity check 2: Ensure that this value is within the duration and will not crash
            // the app, and that the user is not currently seeking (which would cause the SeekBar
            // to jump around).
            if (from <= durationDs && !isActivated) {
                binding.seekBarSlider.value = from.toFloat()
                // We would want to keep this in the listener, but the listener only fires when
                // a value changes completely, and sometimes that does not happen with this view.
                updatePositionLabel(from)
            }
        }

    /**
     * The current duration, in deci-seconds (1/10th of a second). This is the end value of the
     * SeekBar and is indicated by the end TextView in the layout.
     */
    var durationDs: Long
        get() = binding.seekBarSlider.valueTo.toLong()
        set(value) {
            // Sanity check 1: If this is a value so low that it effectively rounds down to
            // zero, use 1 instead and disable the SeekBar.
            val to = max(value, 1)
            isEnabled = value > 0
            L.d("Value sanitization finished [to=$to, enabled=$isEnabled]")
            // Sanity check 2: If the current value exceeds the new duration value, clamp it
            // down so that we don't crash and instead have an annoying visual flicker.
            if (positionDs > to) {
                L.d("Clamping invalid position [current: $positionDs new max: $to]")
                binding.seekBarSlider.value = to.toFloat()
            }
            binding.seekBarSlider.valueTo = to.toFloat()
            updateDurationLabel(positionDs)
        }

    override fun onStartTrackingTouch(slider: Slider) {
        L.d("Starting seek mode")
        // User has begun seeking, place the SeekBar into a "Suspended" mode in which no
        // position updates are sent and is indicated by the position value turning accented.
        isActivated = true
    }

    override fun onStopTrackingTouch(slider: Slider) {
        L.d("Confirming seek")
        // End of seek event, send off new value to listener.
        isActivated = false
        listener?.onSeekConfirmed(slider.value.toLong())
    }

    override fun onValueChange(slider: Slider, value: Float, fromUser: Boolean) {
        updatePositionLabel(value.toLong())
        if (fromUser && !isActivated) {
            listener?.onSeekConfirmed(value.toLong())
        }
    }

    private fun updatePositionLabel(positionDs: Long) {
        val seconds = positionDs.dsToSecs()
        if (displayedPositionSecs == seconds) {
            return
        }
        // The slider changes every decisecond, but its label only displays whole seconds.
        // Reassigning even identical text requests layout because the label wraps its content.
        val formatted = seconds.formatDurationSecs(true)
        binding.seekBarPosition.text = formatted
        ViewCompat.setStateDescription(binding.seekBarSlider, formatted)
        displayedPositionSecs = seconds
        if (showRemainingTime) {
            updateDurationLabel(positionDs)
        }
    }

    private fun updateDurationLabel(curPositionDs: Long) {
        val totalDs = durationDs
        binding.seekBarDuration.text =
            if (showRemainingTime) {
                val remDs = (totalDs - curPositionDs).coerceAtLeast(0L)
                "-${remDs.formatDurationDs(false)}"
            } else {
                totalDs.formatDurationDs(false)
            }
    }

    /** A listener for SeekBar interactions. */
    interface Listener {
        /**
         * Called when the internal [Slider] was scrubbed to a new position, requesting that a seek
         * be performed.
         *
         * @param positionDs The position to seek to, in deci-seconds (1/10th of a second).
         */
        fun onSeekConfirmed(positionDs: Long)
    }
}
