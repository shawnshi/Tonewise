/*
 * Copyright (c) 2026 Tonewise Project
 * ZunePanoramaHeaderView.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home

import android.content.Context
import android.graphics.Typeface
import android.util.AttributeSet
import android.util.TypedValue
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import com.google.android.material.R as MR
import com.shawnshi.tonewise.util.getAttrColorCompat
import kotlin.math.abs

/**
 * Zune Panorama Level-1 header displaying `quickplay · collection · autodj` with 0.6x
 * scroll parallax and right-edge peek clipping synchronized to the ViewPager2 scroll state.
 */
class ZunePanoramaHeaderView
@JvmOverloads
constructor(context: Context, attrs: AttributeSet? = null, defStyleAttr: Int = 0) :
    FrameLayout(context, attrs, defStyleAttr) {

    private val hubTitles = listOf("quickplay", "collection", "autodj")
    private val strip: LinearLayout
    private val itemViews = ArrayList<TextView>(hubTitles.size)
    private var currentPosition = 0
    private var currentOffset = 0f

    var onSelectHub: ((Int) -> Unit)? = null

    init {
        clipChildren = true
        clipToPadding = true
        val density = resources.displayMetrics.density
        val startPad = (16f * density).toInt()
        val endPad = (120f * density).toInt()
        val topPad = (2f * density).toInt()
        val bottomPad = (6f * density).toInt()
        val gap = (24f * density).toInt()

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
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 40f)
                    letterSpacing = -0.03f
                    includeFontPadding = false
                    setTextColor(activeColor)
                    alpha = if (index == 0) 1f else 0.34f
                    setPaddingRelative(0, 0, gap, 0)
                    isClickable = true
                    isFocusable = true
                    setOnClickListener { onSelectHub?.invoke(index) }
                }
            itemViews.add(tv)
            strip.addView(tv)
        }
        addView(strip)
    }

    override fun onLayout(changed: Boolean, left: Int, top: Int, right: Int, bottom: Int) {
        super.onLayout(changed, left, top, right, bottom)
        applyParallaxAndAlpha()
    }

    fun updateScroll(position: Int, positionOffset: Float) {
        currentPosition = position.coerceIn(0, itemViews.lastIndex)
        currentOffset = positionOffset.coerceIn(0f, 1f)
        applyParallaxAndAlpha()
    }

    private fun applyParallaxAndAlpha() {
        if (itemViews.isEmpty()) return
        val density = resources.displayMetrics.density
        val baseStart = 16f * density
        val idx0 = currentPosition.coerceIn(0, itemViews.lastIndex)
        val idx1 = (idx0 + 1).coerceAtMost(itemViews.lastIndex)
        val anchor0 = (itemViews[idx0].left - baseStart).coerceAtLeast(0f)
        val anchor1 = (itemViews[idx1].left - baseStart).coerceAtLeast(0f)
        // Parallax translation keeps next hub title visible near the right screen edge.
        val rawTranslation = -(anchor0 + (anchor1 - anchor0) * currentOffset) * 0.88f
        strip.translationX = rawTranslation

        val continuousPos = idx0 + currentOffset
        for ((index, tv) in itemViews.withIndex()) {
            val dist = abs(index - continuousPos).coerceIn(0f, 1f)
            tv.alpha = 1f - 0.66f * dist
        }
    }
}
