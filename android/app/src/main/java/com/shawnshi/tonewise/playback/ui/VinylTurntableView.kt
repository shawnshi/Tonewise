/*
 * Copyright (c) 2026 Tonewise Project
 * VinylTurntableView.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.ui

import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.Shader
import android.graphics.SweepGradient
import android.util.AttributeSet
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.view.animation.LinearInterpolator
import androidx.core.graphics.withRotation
import androidx.core.graphics.withSave
import coil3.ImageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.shawnshi.tonewise.playback.ui.stepper.Direction
import kotlin.math.abs
import kotlin.math.min
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.oxycblt.musikr.covers.Cover

/**
 * MD-Vinyl-inspired turntable stage for Now Playing: renders a matte grooved vinyl disc with
 * circular center album art label, dynamic sheen, and animated metallic tonearm.
 * Respects system reduced-motion settings ([ValueAnimator.areAnimatorsEnabled]).
 */
class VinylTurntableView
@JvmOverloads
constructor(context: Context, attrs: AttributeSet? = null, defStyleAttr: Int = 0) :
    View(context, attrs, defStyleAttr) {

    var onSwipeNext: (() -> Unit)? = null
    var onSwipePrev: (() -> Unit)? = null
    var onDoubleTapSeek: ((Direction) -> Unit)? = null

    private var coverBitmap: Bitmap? = null
    private var centerShader: BitmapShader? = null
    private val shaderMatrix = Matrix()
    private var lastCoverId: String? = null
    private var loadJob: Job? = null

    private var isPlayingState = false
    private var isSheetExpanded = false
    private var discAngleDeg = 0f
    private var lastSpinFraction = 0f
    private var spinAnimator: ValueAnimator? = null

    private var tonearmAngleDeg = TONEARM_PARKED_DEG
    private var tonearmAnimator: ValueAnimator? = null

    private val discPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val groovePaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(18, 255, 255, 255)
        }
    private val rimPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(38, 255, 255, 255)
        }
    private val sheenPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val centerLabelPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fallbackLabelPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.parseColor("#2A241B")
        }
    private val labelBorderPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(160, 0, 0, 0)
        }
    private val spindlePaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.parseColor("#0B0C10")
        }
    private val spindleRimPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(90, 255, 255, 255)
        }
    private val armShadowPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeCap = Paint.Cap.ROUND
            color = Color.argb(110, 0, 0, 0)
        }
    private val armTubePaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeCap = Paint.Cap.ROUND
        }
    private val armPivotPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.parseColor("#1D2028")
        }
    private val armAccentPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.parseColor("#D9B372")
        }
    private val cartridgePaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = Color.parseColor("#262A34")
        }
    private val armPath = Path()
    private val labelClipPath = Path()

    private var cachedRadius = -1f

    private val gestureDetector =
        GestureDetector(
            context,
            object : GestureDetector.SimpleOnGestureListener() {
                override fun onDown(e: MotionEvent): Boolean = true

                override fun onFling(
                    e1: MotionEvent?,
                    e2: MotionEvent,
                    velocityX: Float,
                    velocityY: Float,
                ): Boolean {
                    if (e1 == null) return false
                    val dx = e2.x - e1.x
                    val dy = e2.y - e1.y
                    if (abs(dx) > abs(dy) && abs(dx) > SWIPE_THRESHOLD_PX && abs(velocityX) > SWIPE_VELOCITY_PX) {
                        if (dx < 0) onSwipeNext?.invoke() else onSwipePrev?.invoke()
                        return true
                    }
                    return false
                }

                override fun onDoubleTap(e: MotionEvent): Boolean {
                    if (width <= 0) return false
                    val dir = if (e.x < width * 0.5f) Direction.BACKWARDS else Direction.FORWARDS
                    onDoubleTapSeek?.invoke(dir)
                    return true
                }
            },
        )

    fun submitCover(imageLoader: ImageLoader, cover: Cover?, scope: CoroutineScope) {
        val coverId = cover?.id
        if (coverId == lastCoverId) return
        lastCoverId = coverId
        loadJob?.cancel()
        if (cover == null) {
            coverBitmap = null
            centerShader = null
            invalidate()
            return
        }
        val appContext = context.applicationContext
        loadJob =
            scope.launch {
                val bmp =
                    withContext(Dispatchers.IO) {
                        val req =
                            ImageRequest.Builder(appContext)
                                .data(cover)
                                .size(320, 320)
                                .allowHardware(false)
                                .build()
                        (runCatching { imageLoader.execute(req) }.getOrNull() as? SuccessResult)
                            ?.image
                            ?.toBitmap()
                    }
                coverBitmap = bmp
                centerShader =
                    bmp?.let { BitmapShader(it, Shader.TileMode.CLAMP, Shader.TileMode.CLAMP) }
                invalidate()
            }
    }

    fun setPlaying(playing: Boolean) {
        if (isPlayingState == playing) return
        isPlayingState = playing
        updateTonearmTarget()
        updateSpinState()
    }

    fun setPlaybackSheetExpanded(expanded: Boolean) {
        if (isSheetExpanded == expanded) return
        isSheetExpanded = expanded
        updateSpinState()
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        updateSpinState()
    }

    override fun onVisibilityChanged(changedView: View, visibility: Int) {
        super.onVisibilityChanged(changedView, visibility)
        updateSpinState()
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        updateSpinState()
    }

    override fun onDetachedFromWindow() {
        spinAnimator?.cancel()
        spinAnimator = null
        tonearmAnimator?.cancel()
        tonearmAnimator = null
        loadJob?.cancel()
        loadJob = null
        super.onDetachedFromWindow()
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val handled = gestureDetector.onTouchEvent(event)
        return handled || super.onTouchEvent(event)
    }

    private fun updateTonearmTarget() {
        val target = if (isPlayingState) TONEARM_PLAYING_DEG else TONEARM_PARKED_DEG
        tonearmAnimator?.cancel()
        tonearmAnimator = null
        if (!ValueAnimator.areAnimatorsEnabled() || !isAttachedToWindow) {
            tonearmAngleDeg = target
            invalidate()
            return
        }
        tonearmAnimator =
            ValueAnimator.ofFloat(tonearmAngleDeg, target).apply {
                duration = 380L
                interpolator = DecelerateInterpolator()
                addUpdateListener { anim ->
                    tonearmAngleDeg = anim.animatedValue as Float
                    invalidate()
                }
                start()
            }
    }

    private fun updateSpinState() {
        val shouldSpin =
            isAttachedToWindow &&
                isShown &&
                windowVisibility == VISIBLE &&
                isPlayingState &&
                isSheetExpanded &&
                ValueAnimator.areAnimatorsEnabled()
        if (!shouldSpin) {
            spinAnimator?.cancel()
            spinAnimator = null
            return
        }
        if (spinAnimator != null) return
        lastSpinFraction = 0f
        spinAnimator =
            ValueAnimator.ofFloat(0f, 1f).apply {
                duration = 18_000L // 33 1/3 RPM = 1.8s/rev visually slowed to graceful 18s/rev
                repeatCount = ValueAnimator.INFINITE
                repeatMode = ValueAnimator.RESTART
                interpolator = LinearInterpolator()
                addUpdateListener { anim ->
                    val frac = anim.animatedValue as Float
                    var delta = frac - lastSpinFraction
                    if (delta < 0f) delta += 1f
                    lastSpinFraction = frac
                    discAngleDeg = (discAngleDeg + delta * 360f) % 360f
                    invalidate()
                }
                start()
            }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return

        val density = resources.displayMetrics.density
        val cx = w * 0.48f
        val cy = h * 0.52f
        val discRadius = (min(w, h) * 0.43f).coerceAtLeast(24f)
        val labelRadius = discRadius * 0.36f
        val spindleRadius = (5.5f * density).coerceAtLeast(4f)

        if (cachedRadius != discRadius) {
            cachedRadius = discRadius
            discPaint.shader =
                RadialGradient(
                    cx,
                    cy,
                    discRadius,
                    intArrayOf(
                        Color.parseColor("#181A20"),
                        Color.parseColor("#0B0C10"),
                        Color.parseColor("#13151B"),
                        Color.parseColor("#07080B"),
                    ),
                    floatArrayOf(0f, 0.45f, 0.78f, 1f),
                    Shader.TileMode.CLAMP,
                )
            sheenPaint.shader =
                SweepGradient(
                    cx,
                    cy,
                    intArrayOf(
                        Color.argb(32, 255, 255, 255),
                        Color.TRANSPARENT,
                        Color.TRANSPARENT,
                        Color.argb(28, 255, 255, 255),
                        Color.TRANSPARENT,
                        Color.argb(32, 255, 255, 255),
                    ),
                    floatArrayOf(0f, 0.16f, 0.44f, 0.54f, 0.78f, 1f),
                )
        }

        // Platter body
        canvas.drawCircle(cx, cy, discRadius, discPaint)

        // Subtle vinyl grooves
        groovePaint.strokeWidth = 1f * density
        for (frac in floatArrayOf(0.42f, 0.50f, 0.58f, 0.66f, 0.74f, 0.82f, 0.90f)) {
            canvas.drawCircle(cx, cy, discRadius * frac, groovePaint)
        }

        // Specular sheen
        canvas.withRotation(32f, cx, cy) {
            drawCircle(cx, cy, discRadius, sheenPaint)
        }

        // Outer disc rim
        rimPaint.strokeWidth = 1.2f * density
        canvas.drawCircle(cx, cy, discRadius - rimPaint.strokeWidth * 0.5f, rimPaint)

        // Rotating center label artwork
        canvas.withRotation(discAngleDeg, cx, cy) {
            val shader = centerShader
            val bmp = coverBitmap
            if (shader != null && bmp != null && !bmp.isRecycled) {
                val scale = (labelRadius * 2f) / min(bmp.width, bmp.height).toFloat()
                shaderMatrix.reset()
                shaderMatrix.setScale(scale, scale)
                shaderMatrix.postTranslate(
                    cx - (bmp.width * scale) / 2f,
                    cy - (bmp.height * scale) / 2f,
                )
                shader.setLocalMatrix(shaderMatrix)
                centerLabelPaint.shader = shader
                labelClipPath.reset()
                labelClipPath.addCircle(cx, cy, labelRadius, Path.Direction.CW)
                withSave {
                    clipPath(labelClipPath)
                    drawCircle(cx, cy, labelRadius, centerLabelPaint)
                }
            } else {
                drawCircle(cx, cy, labelRadius, fallbackLabelPaint)
            }
        }

        labelBorderPaint.strokeWidth = 2f * density
        canvas.drawCircle(cx, cy, labelRadius, labelBorderPaint)

        // Spindle hole
        canvas.drawCircle(cx, cy, spindleRadius, spindlePaint)
        spindleRimPaint.strokeWidth = 1f * density
        canvas.drawCircle(cx, cy, spindleRadius, spindleRimPaint)

        // Metallic tonearm pivoted near top-right
        val pivotX = w * 0.86f
        val pivotY = h * 0.13f
        val elbowX = pivotX - discRadius * 0.16f
        val elbowY = pivotY + discRadius * 0.68f
        val tipX = cx + discRadius * 0.60f
        val tipY = cy + discRadius * 0.14f

        canvas.withRotation(tonearmAngleDeg, pivotX, pivotY) {
            armPath.reset()
            armPath.moveTo(pivotX, pivotY)
            armPath.lineTo(elbowX, elbowY)
            armPath.lineTo(tipX, tipY)

            armShadowPaint.strokeWidth = 5f * density
            withSave {
                translate(2f * density, 4f * density)
                drawPath(armPath, armShadowPaint)
            }

            armTubePaint.strokeWidth = 3.6f * density
            armTubePaint.shader =
                LinearGradient(
                    pivotX,
                    pivotY,
                    tipX,
                    tipY,
                    intArrayOf(
                        Color.parseColor("#D4D8E0"),
                        Color.parseColor("#8D95A3"),
                        Color.parseColor("#E6E9EF"),
                    ),
                    null,
                    Shader.TileMode.CLAMP,
                )
            drawPath(armPath, armTubePaint)

            // Cartridge headshell
            withRotation(-28f, tipX, tipY) {
                val hw = 6f * density
                val hh = 11f * density
                drawRoundRect(
                    tipX - hw,
                    tipY - hh * 0.35f,
                    tipX + hw,
                    tipY + hh * 0.65f,
                    2f * density,
                    2f * density,
                    cartridgePaint,
                )
                drawCircle(tipX, tipY + hh * 0.45f, 1.8f * density, armAccentPaint)
            }

            // Pivot housing & brass cap
            drawCircle(pivotX, pivotY, 13f * density, armPivotPaint)
            rimPaint.strokeWidth = 1.2f * density
            drawCircle(pivotX, pivotY, 13f * density, rimPaint)
            drawCircle(pivotX, pivotY, 5.5f * density, armAccentPaint)
        }
    }

    private companion object {
        const val TONEARM_PLAYING_DEG = 0f
        const val TONEARM_PARKED_DEG = -22f
        const val SWIPE_THRESHOLD_PX = 80
        const val SWIPE_VELOCITY_PX = 240
    }
}
