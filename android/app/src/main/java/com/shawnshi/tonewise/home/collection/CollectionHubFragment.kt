/*
 * Copyright (c) 2026 Tonewise Project
 * CollectionHubFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.collection

import android.graphics.Typeface
import android.os.Bundle
import android.util.TypedValue
import android.view.Gravity
import android.view.LayoutInflater
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.isVisible
import androidx.fragment.app.Fragment
import androidx.fragment.app.activityViewModels
import com.google.android.material.R as MR
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentCollectionHubBinding
import com.shawnshi.tonewise.home.HomeViewModel
import com.shawnshi.tonewise.home.classical.ClassicalHierarchyFragment
import com.shawnshi.tonewise.home.list.AlbumListFragment
import com.shawnshi.tonewise.home.list.ArtistListFragment
import com.shawnshi.tonewise.home.list.ComposerListFragment
import com.shawnshi.tonewise.home.list.ConductorListFragment
import com.shawnshi.tonewise.home.list.GenreListFragment
import com.shawnshi.tonewise.home.list.PlaylistListFragment
import com.shawnshi.tonewise.home.list.SceneListFragment
import com.shawnshi.tonewise.home.list.SongListFragment
import com.shawnshi.tonewise.music.MusicType
import com.shawnshi.tonewise.ui.ViewBindingFragment
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.getAttrColorCompat
import dagger.hilt.android.AndroidEntryPoint

/**
 * Zune Level-1 `collection` hub containing the tap-switched Level-2 lowercase sub-pivot strip
 * (`albums · tracks · artists · classical · composers · conductors · genres · playlists · scenes`)
 * and child fragment host.
 */
@AndroidEntryPoint
class CollectionHubFragment : ViewBindingFragment<FragmentCollectionHubBinding>() {
    private val homeModel: HomeViewModel by activityViewModels()
    private val pivotViews = LinkedHashMap<CollectionPivot, TextView>()
    private var renderedPivot: CollectionPivot? = null

    override fun onCreateBinding(inflater: LayoutInflater) =
        FragmentCollectionHubBinding.inflate(inflater)

    override fun onBindingCreated(
        binding: FragmentCollectionHubBinding,
        savedInstanceState: Bundle?,
    ) {
        super.onBindingCreated(binding, savedInstanceState)

        binding.collectionSortButton.setOnClickListener { homeModel.openSortForCurrentPivot() }

        collectImmediately(homeModel.visibleCollectionPivots) { pivots ->
            buildPivotStrip(binding, pivots)
            applyActivePivot(homeModel.collectionPivot.value)
        }
        collectImmediately(homeModel.collectionPivot, ::applyActivePivot)
    }

    override fun onDestroyBinding(binding: FragmentCollectionHubBinding) {
        super.onDestroyBinding(binding)
        pivotViews.clear()
        renderedPivot = null
    }

    private fun buildPivotStrip(
        binding: FragmentCollectionHubBinding,
        pivots: List<CollectionPivot>,
    ) {
        val context = requireContext()
        val density = resources.displayMetrics.density
        val padH = (10f * density).toInt()
        val mediumFace = Typeface.create("sans-serif-medium", Typeface.NORMAL)

        binding.collectionPivotStrip.removeAllViews()
        pivotViews.clear()

        for (pivot in pivots) {
            val tv =
                TextView(context).apply {
                    text = pivot.label
                    typeface = mediumFace
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(padH, 0, padH, 0)
                    layoutParams =
                        LinearLayout.LayoutParams(
                            LinearLayout.LayoutParams.WRAP_CONTENT,
                            LinearLayout.LayoutParams.MATCH_PARENT,
                        )
                    isClickable = true
                    isFocusable = true
                    setOnClickListener { homeModel.selectCollectionPivot(pivot) }
                }
            pivotViews[pivot] = tv
            binding.collectionPivotStrip.addView(tv)
        }
    }

    private fun applyActivePivot(pivot: CollectionPivot) {
        val binding = requireBinding()
        val context = requireContext()
        val activeColor = context.getAttrColorCompat(androidx.appcompat.R.attr.colorPrimary).defaultColor
        val mutedColor = context.getAttrColorCompat(MR.attr.colorOnSurfaceVariant).defaultColor

        for ((item, tv) in pivotViews) {
            val selected = item == pivot
            tv.setTextColor(if (selected) activeColor else mutedColor)
            tv.alpha = if (selected) 1f else 0.58f
        }

        val sortable = pivot.musicType != null && pivot.musicType != MusicType.SCENES
        binding.collectionSortButton.isVisible = sortable

        val selectedView = pivotViews[pivot]
        if (selectedView != null) {
            binding.collectionPivotScroll.post {
                val scrollX =
                    (selectedView.left - binding.collectionPivotScroll.width / 3).coerceAtLeast(0)
                binding.collectionPivotScroll.smoothScrollTo(scrollX, 0)
            }
        }

        if (renderedPivot == pivot && childFragmentManager.findFragmentById(R.id.collection_fragment_container) != null) {
            return
        }
        renderedPivot = pivot
        val fragment: Fragment =
            when (pivot) {
                CollectionPivot.ALBUMS -> AlbumListFragment()
                CollectionPivot.TRACKS -> SongListFragment()
                CollectionPivot.ARTISTS -> ArtistListFragment()
                CollectionPivot.CLASSICAL -> ClassicalHierarchyFragment()
                CollectionPivot.COMPOSERS -> ComposerListFragment()
                CollectionPivot.CONDUCTORS -> ConductorListFragment()
                CollectionPivot.GENRES -> GenreListFragment()
                CollectionPivot.PLAYLISTS -> PlaylistListFragment()
                CollectionPivot.SCENES -> SceneListFragment()
            }
        childFragmentManager
            .beginTransaction()
            .replace(R.id.collection_fragment_container, fragment, "pivot_${pivot.name}")
            .commit()
    }
}
