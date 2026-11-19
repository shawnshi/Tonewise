/*
 * Copyright (c) 2026 Shawn Shi
 *
 * TonewiseDreamService.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.ui.clock

import android.graphics.Color
import android.graphics.Typeface
import android.os.Handler
import android.os.Looper
import android.service.dreams.DreamService
import android.text.format.DateFormat
import android.view.Gravity
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextClock
import android.widget.TextView
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Pure black OLED system screensaver (Daydream) providing a low-burn-in ambient clock
 * using the same 16-point pixel orbit as [DeskClockDialog].
 */
class TonewiseDreamService : DreamService() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var rootContainer: LinearLayout? = null
    private var dateView: TextView? = null
    private var minuteTick = 0

    private val orbitRunnable =
        object : Runnable {
            override fun run() {
                updateOrbitAndDate()
                mainHandler.postDelayed(this, ORBIT_INTERVAL_MS)
            }
        }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        isInteractive = false
        isFullscreen = true
        isScreenBright = false

        val density = resources.displayMetrics.density
        val frame =
            FrameLayout(this).apply {
                setBackgroundColor(Color.BLACK)
                layoutParams =
                    ViewGroup.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT,
                    )
            }

        val content =
            LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER
                val padPx = (24f * density).toInt()
                setPadding(padPx, padPx, padPx, padPx)
            }

        val clock =
            TextClock(this).apply {
                format12Hour = "h:mm"
                format24Hour = "HH:mm"
                textSize = 72f
                setTextColor(0xFFF2F0EC.toInt())
                typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
                fontFeatureSettings = "tnum"
                gravity = Gravity.CENTER
            }

        val dateText =
            TextView(this).apply {
                textSize = 14f
                setTextColor(0xFF8C96A6.toInt())
                letterSpacing = 0.08f
                gravity = Gravity.CENTER
                setPadding(0, (8f * density).toInt(), 0, 0)
            }

        val caption =
            TextView(this).apply {
                text = "TONEWISE OLED CLOCK"
                textSize = 10f
                setTextColor(0xFFD9B372.toInt())
                letterSpacing = 0.24f
                gravity = Gravity.CENTER
                setPadding(0, (14f * density).toInt(), 0, 0)
            }

        content.addView(clock)
        content.addView(dateText)
        content.addView(caption)

        frame.addView(
            content,
            FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            ),
        )

        rootContainer = content
        dateView = dateText
        setContentView(frame)
    }

    override fun onDreamingStarted() {
        super.onDreamingStarted()
        mainHandler.removeCallbacks(orbitRunnable)
        updateOrbitAndDate()
        mainHandler.postDelayed(orbitRunnable, ORBIT_INTERVAL_MS)
    }

    override fun onDreamingStopped() {
        mainHandler.removeCallbacks(orbitRunnable)
        super.onDreamingStopped()
    }

    override fun onDetachedFromWindow() {
        mainHandler.removeCallbacksAndMessages(null)
        rootContainer = null
        dateView = null
        super.onDetachedFromWindow()
    }

    private fun updateOrbitAndDate() {
        val locale = Locale.getDefault()
        val pattern = DateFormat.getBestDateTimePattern(locale, "EEEEMMMMd")
        dateView?.text = SimpleDateFormat(pattern, locale).format(Date()).uppercase(locale)

        val (shiftX, shiftY) = computePixelShiftDp(minuteTick)
        val density = resources.displayMetrics.density
        rootContainer?.translationX = shiftX.value * density
        rootContainer?.translationY = shiftY.value * density
        minuteTick = (minuteTick + 1) % 16
    }

    private companion object {
        const val ORBIT_INTERVAL_MS = 60_000L
    }
}
