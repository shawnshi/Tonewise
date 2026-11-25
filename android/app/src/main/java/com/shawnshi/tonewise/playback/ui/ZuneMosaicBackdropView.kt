/*
 * Copyright (c) 2026 Tonewise Project
 * ZuneMosaicBackdropView.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.ui

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.RectF
import android.graphics.Shader
import android.util.AttributeSet
import android.view.View
import android.view.animation.LinearInterpolator
import coil3.ImageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.google.android.material.R as MaterialR
import com.google.android.material.color.MaterialColors
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.oxycblt.musikr.covers.Cover

/**
 * Low-opacity edge-to-edge 0dp square album-cover mosaic backdrop rendered behind Now Playing,
 * overlaid with a directional surface scrim to preserve WCAG contrast for foreground typography.
 */
class ZuneMosaicBackdropView
@JvmOverloads
constructor(context: Context, attrs: AttributeSet? = null, defStyleAttr: Int = 0) :
    View(context, attrs, defStyleAttr) {

    private var tiles: List<Bitmap> = emptyList()
    private var lastSignature: String? = null
    private var loadJob: Job? = null
    private var isDarkSurface: Boolean = true
    private var panOffsetPx: Float = 0f
    private var panAnimator: ValueAnimator? = null

    private val tilePaint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val scrimPaint = Paint()
    private var lastScrimHeight = -1
    private var lastScrimBaseColor = 0
    private val dstRect = RectF()
    private val srcRect = Rect()

    fun submitCovers(
        imageLoader: ImageLoader,
        covers: List<Cover>,
        darkSurface: Boolean,
        scope: CoroutineScope,
    ) {
        val sample = covers.distinctBy { it.id }.take(36)
        val signature = "${darkSurface}:${sample.joinToString(",") { it.id }}"
        isDarkSurface = darkSurface
        tilePaint.alpha = if (darkSurface) 36 else 26 // ~14% dark / ~10% light
        if (signature == lastSignature) {
            invalidate()
            return
        }
        lastSignature = signature
        loadJob?.cancel()
        if (sample.isEmpty()) {
            tiles = emptyList()
            invalidate()
            return
        }

        val appContext = context.applicationContext
        loadJob =
            scope.launch {
                val decoded =
                    withContext(Dispatchers.IO) {
                        val list = ArrayList<Bitmap>(sample.size)
                        for (cover in sample) {
                            val req =
                                ImageRequest.Builder(appContext)
                                    .data(cover)
                                    .size(96, 96)
                                    .allowHardware(false)
                                    .build()
                            val res = runCatching { imageLoader.execute(req) }.getOrNull()
                            if (res is SuccessResult) {
                                list.add(res.image.toBitmap())
                            }
                        }
                        list
                    }
                tiles = decoded
                invalidate()
                maybeStartPanAnimator()
            }
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        maybeStartPanAnimator()
    }

    override fun onDetachedFromWindow() {
        panAnimator?.cancel()
        panAnimator = null
        loadJob?.cancel()
        loadJob = null
        super.onDetachedFromWindow()
    }

    private fun maybeStartPanAnimator() {
        if (!isAttachedToWindow || tiles.isEmpty() || !ValueAnimator.areAnimatorsEnabled()) {
            panAnimator?.cancel()
            panAnimator = null
            panOffsetPx = 0f
            return
        }
        if (panAnimator != null) return
        val maxDrift = resources.displayMetrics.density * 28f
        panAnimator =
            ValueAnimator.ofFloat(0f, maxDrift).apply {
                duration = 26_000L
                repeatCount = ValueAnimator.INFINITE
                repeatMode = ValueAnimator.REVERSE
                interpolator = LinearInterpolator()
                addUpdateListener { anim ->
                    panOffsetPx = anim.animatedValue as Float
                    invalidate()
                }
                start()
            }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val w = width
        val h = height
        if (w <= 0 || h <= 0) return

        val fallbackColor = if (isDarkSurface) Color.BLACK else Color.WHITE
        val baseColor = MaterialColors.getColor(this, MaterialR.attr.colorSurface, fallbackColor)
        canvas.drawColor(baseColor)

        if (tiles.isNotEmpty()) {
            val cols = if (w > h) 8 else 5
            val cell = (w.toFloat() / cols).coerceAtLeast(1f)
            val rows = (h / cell).toInt() + 2
            var idx = 0
            for (r in 0 until rows) {
                for (c in -1 until cols + 1) {
                    val bmp = tiles[idx % tiles.size]
                    idx++
                    if (bmp.isRecycled) continue
                    val left = c * cell - panOffsetPx
                    val top = r * cell
                    dstRect.set(left, top, left + cell, top + cell)
                    srcRect.set(0, 0, bmp.width, bmp.height)
                    canvas.drawBitmap(bmp, srcRect, dstRect, tilePaint)
                }
            }
        }

        if (lastScrimHeight != h || lastScrimBaseColor != baseColor) {
            lastScrimHeight = h
            lastScrimBaseColor = baseColor
            val edgeAlpha = if (isDarkSurface) 235 else 240
            val midAlpha = if (isDarkSurface) 175 else 190
            val r = Color.red(baseColor)
            val g = Color.green(baseColor)
            val b = Color.blue(baseColor)
            scrimPaint.shader =
                LinearGradient(
                    0f,
                    0f,
                    0f,
                    h.toFloat(),
                    intArrayOf(
                        Color.argb(edgeAlpha, r, g, b),
                        Color.argb(midAlpha, r, g, b),
                        Color.argb(edgeAlpha, r, g, b),
                    ),
                    floatArrayOf(0f, 0.42f, 1f),
                    Shader.TileMode.CLAMP,
                )
        }
        canvas.drawRect(0f, 0f, w.toFloat(), h.toFloat(), scrimPaint)
    }
}
