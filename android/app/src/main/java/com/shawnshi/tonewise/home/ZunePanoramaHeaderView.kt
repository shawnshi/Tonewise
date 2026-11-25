/*
 * Copyright (c) 2026 Tonewise Project
 * ZunePanoramaHeaderView.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Typeface
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import com.google.android.material.R as MR
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.util.getAttrColorCompat
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Zune Panorama Level-1 header displaying `quickplay · collection · autodj` on a single horizontal
 * line with active underline indicator, unconstrained horizontal measurement, and accessibility-safe
 * scroll clamping when system font scale exceeds screen width.
 */
class ZunePanoramaHeaderView
@JvmOverloads
constructor(context: Context, attrs: AttributeSet? = null, defStyleAttr: Int = 0) :
    FrameLayout(context, attrs, defStyleAttr) {

    private val hubTitles =
        listOf(
            context.getString(R.string.lbl_hub_quickplay),
            context.getString(R.string.lbl_hub_collection),
            context.getString(R.string.lbl_hub_autodj),
        )
    private val strip: LinearLayout
    private val itemViews = ArrayList<TextView>(hubTitles.size)
    private val indicatorPaint =
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.FILL
            color = context.getAttrColorCompat(androidx.appcompat.R.attr.colorPrimary).defaultColor
        }
    private val indicatorHeightPx = 2f * resources.displayMetrics.density
    private var currentPosition = 0
    private var currentOffset = 0f

    var onSelectHub: ((Int) -> Unit)? = null

    init {
        clipChildren = true
        clipToPadding = true
        val density = resources.displayMetrics.density
        val startPad = (16f * density).toInt()
        val endPad = (16f * density).toInt()
        val topPad = (2f * density).toInt()
        val bottomPad = (6f * density).toInt()
        val gap = (18f * density).toInt()
        val minTouchHeight = (48f * density).toInt()

        strip =
            LinearLayout(context).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPaddingRelative(startPad, topPad, endPad, bottomPad)
                layoutParams =
                    LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT)
            }

        val activeColor = context.getAttrColorCompat(MR.attr.colorOnSurface).defaultColor
        val lightTypeface = Typeface.create("sans-serif-light", Typeface.NORMAL)

        for ((index, label) in hubTitles.withIndex()) {
            val tv =
                TextView(context).apply {
                    text = label
                    typeface = lightTypeface
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
                    letterSpacing = -0.02f
                    includeFontPadding = false
                    maxLines = 1
                    isSingleLine = true
                    ellipsize = null
                    gravity = Gravity.CENTER_VERTICAL
                    minHeight = minTouchHeight
                    setTextColor(activeColor)
                    alpha = if (index == 0) 1f else 0.58f
                    isSelected = index == 0
                    setPaddingRelative(0, 0, if (index < hubTitles.lastIndex) gap else 0, 0)
                    isClickable = true
                    isFocusable = true
                    setOnClickListener { onSelectHub?.invoke(index) }
                }
            itemViews.add(tv)
            strip.addView(tv)
        }
        addView(strip)
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val unconstrainedW = MeasureSpec.makeMeasureSpec(0, MeasureSpec.UNSPECIFIED)
        strip.measure(unconstrainedW, heightMeasureSpec)
        val measuredW = resolveSize(strip.measuredWidth, widthMeasureSpec)
        val measuredH = resolveSize(strip.measuredHeight, heightMeasureSpec)
        setMeasuredDimension(measuredW, measuredH)
    }

    override fun onLayout(changed: Boolean, left: Int, top: Int, right: Int, bottom: Int) {
        strip.layout(0, 0, strip.measuredWidth, strip.measuredHeight)
        applyParallaxAndAlpha()
    }

    fun updateScroll(position: Int, positionOffset: Float) {
        currentPosition = position.coerceIn(0, itemViews.lastIndex)
        currentOffset = positionOffset.coerceIn(0f, 1f)
        applyParallaxAndAlpha()
    }

    private fun applyParallaxAndAlpha() {
        if (itemViews.isEmpty()) return
        val idx0 = currentPosition.coerceIn(0, itemViews.lastIndex)
        val continuousPos = (idx0 + currentOffset).coerceIn(0f, itemViews.lastIndex.toFloat())
        val maxScroll = (strip.measuredWidth - width).coerceAtLeast(0)
        if (maxScroll <= 0) {
            strip.translationX = 0f
        } else {
            val denom = itemViews.lastIndex.coerceAtLeast(1).toFloat()
            strip.translationX = -maxScroll * (continuousPos / denom)
        }

        val roundedPos = continuousPos.roundToInt().coerceIn(0, itemViews.lastIndex)
        for ((index, tv) in itemViews.withIndex()) {
            val dist = abs(index - continuousPos).coerceIn(0f, 1f)
            tv.alpha = 1f - 0.42f * dist
            tv.isSelected = index == roundedPos
        }
        invalidate()
    }

    override fun dispatchDraw(canvas: Canvas) {
        super.dispatchDraw(canvas)
        if (itemViews.isEmpty()) return
        val idx0 = currentPosition.coerceIn(0, itemViews.lastIndex)
        val idx1 = (idx0 + 1).coerceAtMost(itemViews.lastIndex)
        val v0 = itemViews[idx0]
        val v1 = itemViews[idx1]
        val w0 = (v0.width - v0.paddingRight - v0.paddingLeft).coerceAtLeast(0).toFloat()
        val w1 = (v1.width - v1.paddingRight - v1.paddingLeft).coerceAtLeast(0).toFloat()
        val x0 = v0.left + v0.paddingLeft + strip.translationX
        val x1 = v1.left + v1.paddingLeft + strip.translationX
        val left = x0 + (x1 - x0) * currentOffset
        val right = left + w0 + (w1 - w0) * currentOffset
        val bottom = height.toFloat()
        val top = (bottom - indicatorHeightPx).coerceAtLeast(0f)
        if (right > left) {
            canvas.drawRect(left, top, right, bottom, indicatorPaint)
        }
    }
}
