/*
 * Copyright (c) 2026 Tonewise Project
 * VinylTurntableView.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback.ui

import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.content.res.Configuration
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
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.view.animation.LinearInterpolator
import androidx.annotation.ColorInt
import androidx.core.graphics.withRotation
import androidx.core.graphics.withSave
import coil3.ImageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.shawnshi.tonewise.playback.ui.stepper.Direction
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin
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
    var onLongPress: (() -> Unit)? = null

    private var vinylStyle: VinylStyle = VinylStyle.DEFAULT
    @ColorInt private var coverSeedArgb: Int? = null

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

    private val shadowPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val discPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val nacreSwirlPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val diffractionPrimaryPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val diffractionSecondaryPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val groovePaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(18, 255, 255, 255)
        }
    private val grooveLightPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(54, 255, 255, 255)
        }
    private val rimPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(38, 255, 255, 255)
        }
    private val pivotRimPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            color = Color.argb(56, 255, 255, 255)
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

    private var cachedWidth = -1f
    private var cachedHeight = -1f
    private var cachedRadius = -1f
    private var cachedStyle: VinylStyle? = null
    private var cachedCoverSeedArgb: Int? = null
    private var cachedDarkMode: Boolean? = null

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

                override fun onLongPress(e: MotionEvent) {
                    val callback = onLongPress ?: return
                    performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                    callback.invoke()
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

    /** Switches the turntable platter surface finish without disturbing spin or tonearm state. */
    fun setVinylStyle(style: VinylStyle) {
        if (vinylStyle == style) return
        vinylStyle = style
        cachedRadius = -1f
        invalidate()
    }

    /** Updates the cover-derived tint seed used by [VinylStyle.COVER_PEARL]. */
    fun setCoverSeedColor(@ColorInt seedArgb: Int?) {
        if (coverSeedArgb == seedArgb) return
        coverSeedArgb = seedArgb
        if (vinylStyle == VinylStyle.COVER_PEARL) {
            cachedRadius = -1f
            invalidate()
        }
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
        val pivotX = w * 0.86f
        val pivotY = h * 0.13f
        val elbowX = pivotX - discRadius * 0.16f
        val elbowY = pivotY + discRadius * 0.68f
        val tipX = cx + discRadius * 0.60f
        val tipY = cy + discRadius * 0.14f
        val isDarkMode =
            (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
                Configuration.UI_MODE_NIGHT_YES

        if (
            cachedWidth != w ||
                cachedHeight != h ||
                cachedRadius != discRadius ||
                cachedStyle != vinylStyle ||
                cachedCoverSeedArgb != coverSeedArgb ||
                cachedDarkMode != isDarkMode
        ) {
            cachedWidth = w
            cachedHeight = h
            cachedRadius = discRadius
            cachedStyle = vinylStyle
            cachedCoverSeedArgb = coverSeedArgb
            cachedDarkMode = isDarkMode
            rebuildMaterialShaders(cx, cy, discRadius, pivotX, pivotY, tipX, tipY, isDarkMode)
        }

        when (vinylStyle) {
            VinylStyle.CLASSIC_BLACK -> {
                canvas.drawCircle(cx, cy, discRadius, discPaint)
                groovePaint.strokeWidth = 1f * density
                for (frac in CLASSIC_GROOVE_FRACS) {
                    canvas.drawCircle(cx, cy, discRadius * frac, groovePaint)
                }
                canvas.withRotation(32f, cx, cy) {
                    drawCircle(cx, cy, discRadius, sheenPaint)
                }
                rimPaint.strokeWidth = 1.2f * density
                canvas.drawCircle(cx, cy, discRadius - rimPaint.strokeWidth * 0.5f, rimPaint)
            }
            VinylStyle.COVER_PEARL -> {
                canvas.drawCircle(cx, cy, discRadius * 1.045f, shadowPaint)
                canvas.drawCircle(cx, cy, discRadius, discPaint)
                canvas.withRotation(discAngleDeg, cx, cy) {
                    drawCircle(cx, cy, discRadius, nacreSwirlPaint)
                }
                groovePaint.strokeWidth = 1.0f * density
                grooveLightPaint.strokeWidth = 0.85f * density
                for (frac in PEARL_GROOVE_FRACS) {
                    val r = discRadius * frac
                    canvas.drawCircle(cx, cy, r, groovePaint)
                    canvas.drawCircle(
                        cx,
                        cy,
                        (r - 0.9f * density).coerceAtLeast(labelRadius + 2f),
                        grooveLightPaint,
                    )
                }
                val fresnelDeg = 32f + sin(Math.toRadians(discAngleDeg.toDouble())).toFloat() * 3f
                canvas.withRotation(fresnelDeg, cx, cy) {
                    drawCircle(cx, cy, discRadius, sheenPaint)
                }
                rimPaint.strokeWidth = 1.3f * density
                canvas.drawCircle(cx, cy, discRadius - rimPaint.strokeWidth * 0.5f, rimPaint)
            }
            VinylStyle.GROOVE_DIFFRACTION -> {
                canvas.drawCircle(cx, cy, discRadius, discPaint)
                groovePaint.strokeWidth = 0.9f * density
                for (frac in DIFFRACTION_GROOVE_FRACS) {
                    canvas.drawCircle(cx, cy, discRadius * frac, groovePaint)
                }
                val wobble1 = sin(Math.toRadians((discAngleDeg * 2f).toDouble())).toFloat() * 12f
                val wobble2 = cos(Math.toRadians((discAngleDeg * 2f).toDouble())).toFloat() * 16f
                canvas.withRotation(32f + wobble1, cx, cy) {
                    drawCircle(cx, cy, discRadius, diffractionPrimaryPaint)
                }
                canvas.withRotation(36f + wobble2, cx, cy) {
                    drawCircle(cx, cy, discRadius, diffractionSecondaryPaint)
                }
                canvas.withRotation(32f, cx, cy) {
                    drawCircle(cx, cy, discRadius, sheenPaint)
                }
                rimPaint.strokeWidth = 1.2f * density
                canvas.drawCircle(cx, cy, discRadius - rimPaint.strokeWidth * 0.5f, rimPaint)
            }
        }

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
            pivotRimPaint.strokeWidth = 1.2f * density
            drawCircle(pivotX, pivotY, 13f * density, pivotRimPaint)
            drawCircle(pivotX, pivotY, 5.5f * density, armAccentPaint)
        }
    }

    private fun rebuildMaterialShaders(
        cx: Float,
        cy: Float,
        discRadius: Float,
        pivotX: Float,
        pivotY: Float,
        tipX: Float,
        tipY: Float,
        isDarkMode: Boolean,
    ) {
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

        when (vinylStyle) {
            VinylStyle.CLASSIC_BLACK -> {
                shadowPaint.shader = null
                nacreSwirlPaint.shader = null
                diffractionPrimaryPaint.shader = null
                diffractionSecondaryPaint.shader = null
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
                groovePaint.color = Color.argb(18, 255, 255, 255)
                rimPaint.color = Color.argb(38, 255, 255, 255)
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
            VinylStyle.COVER_PEARL -> {
                diffractionPrimaryPaint.shader = null
                diffractionSecondaryPaint.shader = null
                val palette = VinylMaterialMath.derivePearlPalette(coverSeedArgb, isDarkMode)
                shadowPaint.shader =
                    RadialGradient(
                        cx,
                        cy,
                        discRadius * 1.045f,
                        intArrayOf(palette.dropShadowArgb, palette.dropShadowArgb, Color.TRANSPARENT),
                        floatArrayOf(0f, 0.90f, 1f),
                        Shader.TileMode.CLAMP,
                    )
                discPaint.shader =
                    RadialGradient(
                        cx,
                        cy,
                        discRadius,
                        palette.bodyStops,
                        floatArrayOf(0f, 0.44f, 0.78f, 1f),
                        Shader.TileMode.CLAMP,
                    )
                nacreSwirlPaint.shader =
                    SweepGradient(
                        cx,
                        cy,
                        intArrayOf(
                            palette.swirlWarmArgb,
                            Color.TRANSPARENT,
                            palette.swirlCoolArgb,
                            Color.TRANSPARENT,
                            palette.swirlWarmArgb,
                        ),
                        floatArrayOf(0f, 0.25f, 0.52f, 0.78f, 1f),
                    )
                groovePaint.color = palette.grooveDarkArgb
                grooveLightPaint.color = palette.grooveLightArgb
                rimPaint.color = palette.outerRimArgb
                sheenPaint.shader =
                    SweepGradient(
                        cx,
                        cy,
                        intArrayOf(
                            Color.argb(112, 255, 255, 255),
                            Color.TRANSPARENT,
                            Color.TRANSPARENT,
                            Color.argb(86, 255, 255, 255),
                            Color.TRANSPARENT,
                            Color.argb(112, 255, 255, 255),
                        ),
                        floatArrayOf(0f, 0.14f, 0.44f, 0.56f, 0.80f, 1f),
                    )
            }
            VinylStyle.GROOVE_DIFFRACTION -> {
                shadowPaint.shader = null
                nacreSwirlPaint.shader = null
                discPaint.shader =
                    RadialGradient(
                        cx,
                        cy,
                        discRadius,
                        intArrayOf(
                            Color.parseColor("#232630"),
                            Color.parseColor("#0E1016"),
                            Color.parseColor("#1C1F29"),
                            Color.parseColor("#08090E"),
                        ),
                        floatArrayOf(0f, 0.42f, 0.78f, 1f),
                        Shader.TileMode.CLAMP,
                    )
                groovePaint.color = Color.argb(26, 224, 232, 248)
                rimPaint.color = Color.argb(56, 210, 220, 240)
                diffractionPrimaryPaint.shader =
                    SweepGradient(
                        cx,
                        cy,
                        VinylMaterialMath.diffractionSpectrumStops(),
                        VinylMaterialMath.diffractionSpectrumPositions(),
                    )
                diffractionSecondaryPaint.shader =
                    SweepGradient(
                        cx,
                        cy,
                        intArrayOf(
                            Color.argb(38, 175, 220, 255),
                            Color.TRANSPARENT,
                            Color.TRANSPARENT,
                            Color.argb(36, 240, 188, 224),
                            Color.TRANSPARENT,
                            Color.argb(38, 175, 220, 255),
                        ),
                        floatArrayOf(0f, 0.08f, 0.47f, 0.53f, 0.62f, 1f),
                    )
                sheenPaint.shader =
                    SweepGradient(
                        cx,
                        cy,
                        intArrayOf(
                            Color.argb(52, 255, 255, 255),
                            Color.TRANSPARENT,
                            Color.TRANSPARENT,
                            Color.argb(44, 255, 255, 255),
                            Color.TRANSPARENT,
                            Color.argb(52, 255, 255, 255),
                        ),
                        floatArrayOf(0f, 0.10f, 0.46f, 0.55f, 0.76f, 1f),
                    )
            }
        }
    }

    private companion object {
        const val TONEARM_PLAYING_DEG = 0f
        const val TONEARM_PARKED_DEG = -22f
        const val SWIPE_THRESHOLD_PX = 80
        const val SWIPE_VELOCITY_PX = 240
        val CLASSIC_GROOVE_FRACS = floatArrayOf(0.42f, 0.50f, 0.58f, 0.66f, 0.74f, 0.82f, 0.90f)
        val PEARL_GROOVE_FRACS =
            floatArrayOf(0.42f, 0.48f, 0.54f, 0.60f, 0.66f, 0.72f, 0.78f, 0.85f, 0.91f)
        val DIFFRACTION_GROOVE_FRACS =
            floatArrayOf(
                0.41f,
                0.46f,
                0.50f,
                0.55f,
                0.60f,
                0.64f,
                0.69f,
                0.74f,
                0.79f,
                0.84f,
                0.89f,
                0.93f,
            )
    }
}
