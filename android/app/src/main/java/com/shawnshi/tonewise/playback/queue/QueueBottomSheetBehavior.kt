/*
 * Copyright (c) 2022 Auxio Project
 * QueueBottomSheetBehavior.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.playback.queue

import android.content.Context
import android.util.AttributeSet
import android.view.View
import android.view.WindowInsets
import androidx.coordinatorlayout.widget.CoordinatorLayout
import com.google.android.material.R as MR
import com.google.android.material.shape.MaterialShapeDrawable
import com.google.android.material.shape.ShapeAppearanceModel
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.ui.BaseBottomSheetBehavior
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.getAttrColorCompat
import com.shawnshi.tonewise.util.getDimenPixels
import com.shawnshi.tonewise.util.replaceSystemBarInsetsCompat
import com.shawnshi.tonewise.util.systemBarInsetsCompat

/**
 * The [BaseBottomSheetBehavior] for the queue bottom sheet. This is placed within the playback
 * sheet and automatically arranges itself to show the playback bar at the top.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
class QueueBottomSheetBehavior<V : View>(context: Context, attributeSet: AttributeSet?) :
    BaseBottomSheetBehavior<V>(context, attributeSet) {
    private var barHeight = 0
    private var barSpacing = context.getDimenPixels(R.dimen.spacing_small)

    init {
        // Hide completely until the user explicitly opens Queue from the Now Playing pill.
        isHideable = true
        skipCollapsed = true
    }

    override fun getIdealBarHeight(context: Context) = 0

    override fun onLayoutChild(parent: CoordinatorLayout, child: V, layoutDirection: Int): Boolean {
        val effectiveBarHeight =
            if (barHeight > 0) barHeight else child.context.getDimenPixels(R.dimen.size_touchable_large)
        expandedOffset = effectiveBarHeight + barSpacing
        val layout = super.onLayoutChild(parent, child, layoutDirection)
        peekHeight = 0
        return layout
    }

    override fun layoutDependsOn(parent: CoordinatorLayout, child: V, dependency: View) =
        dependency.id == R.id.playback_bar_fragment

    override fun onDependentViewChanged(
        parent: CoordinatorLayout,
        child: V,
        dependency: View,
    ): Boolean {
        val oldHeight = barHeight
        barHeight = dependency.height
        // If bar height changed, force insets to be recalculated with the new value
        // and signal that the child needs to be re-laid out. This handles cases where
        // the sheet was positioned before the bar was measured or during configuration
        // changes (e.g., split-screen resize) where layout order causes stale values.
        if (oldHeight != barHeight && barHeight > 0) {
            child.requestApplyInsets()
            return true
        }
        return false
    }

    override fun createBackground(context: Context, uiSettings: UISettings) =
        MaterialShapeDrawable.createWithElevationOverlay(context).apply {
            // The queue sheet's background is a static elevated background.
            fillColor = context.getAttrColorCompat(MR.attr.colorSurfaceContainerHigh)
            if (uiSettings.roundMode) {
                shapeAppearanceModel =
                    ShapeAppearanceModel.builder(
                            context,
                            R.style.ShapeAppearance_Auxio_BottomSheet,
                            MR.style.ShapeAppearanceOverlay_Material3_Corner_Top,
                        )
                        .build()
            }
        }

    override fun applyWindowInsets(child: View, insets: WindowInsets): WindowInsets {
        super.applyWindowInsets(child, insets)
        peekHeight = 0
        // Offset our expanded panel by the size of the playback bar, as that is shown when
        // we slide up the panel. Use ideal bar height as fallback when the bar hasn't been
        // measured yet (can occur when window insets are applied before onDependentViewChanged).
        val bars = insets.systemBarInsetsCompat
        val effectiveBarHeight =
            if (barHeight > 0) barHeight else child.context.getDimenPixels(R.dimen.size_touchable_large)
        expandedOffset = effectiveBarHeight + barSpacing
        return insets.replaceSystemBarInsetsCompat(
            bars.left,
            bars.top,
            bars.right,
            expandedOffset + bars.bottom,
        )
    }
}
