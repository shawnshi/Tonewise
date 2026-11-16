/*
 * Copyright (c) 2026 Tonewise Project
 * BioViewHolder.kt is part of Tonewise.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

package com.shawnshi.tonewise.detail.list

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.annotation.StringRes
import androidx.recyclerview.widget.RecyclerView
import com.shawnshi.tonewise.IntegerTable
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.ItemDetailBioBinding
import com.shawnshi.tonewise.list.adapter.SimpleDiffCallback

/**
 * Detail list [com.shawnshi.tonewise.list.Item] displaying a biography card for an album, artist,
 * composer, or conductor.
 */
data class BioItem(@StringRes val titleRes: Int, val text: String)

/**
 * ViewHolder for [BioItem] that supports collapsing and expanding longer biographies.
 */
class BioViewHolder private constructor(private val binding: ItemDetailBioBinding) :
    RecyclerView.ViewHolder(binding.root) {

    private var expanded = false
    private var canExpand = false
    private var boundText: String = ""

    init {
        binding.bioCard.setOnClickListener {
            if (canExpand || expanded) {
                expanded = !expanded
                applyExpandState()
            }
        }
    }

    fun bind(item: BioItem) {
        boundText = item.text
        expanded = false
        canExpand = item.text.length > FALLBACK_EXPAND_THRESHOLD
        binding.bioTitle.setText(item.titleRes)
        binding.bioBody.text = item.text
        applyExpandState()
        binding.bioBody.post {
            if (boundText != item.text || expanded) return@post
            val layout = binding.bioBody.layout ?: return@post
            val truncated =
                layout.lineCount > COLLAPSED_MAX_LINES ||
                    (layout.lineCount > 0 && layout.getEllipsisCount(layout.lineCount - 1) > 0)
            if (canExpand != truncated) {
                canExpand = truncated
                applyExpandState()
            }
        }
    }

    private fun applyExpandState() {
        binding.bioBody.maxLines = if (expanded) Int.MAX_VALUE else COLLAPSED_MAX_LINES
        binding.bioToggle.setText(
            if (expanded) R.string.lbl_bio_collapse else R.string.lbl_bio_expand
        )
        binding.bioToggle.visibility = if (expanded || canExpand) View.VISIBLE else View.GONE
    }

    companion object {
        const val VIEW_TYPE = IntegerTable.VIEW_TYPE_DETAIL_BIO
        private const val COLLAPSED_MAX_LINES = 6
        private const val FALLBACK_EXPAND_THRESHOLD = 220

        fun from(parent: ViewGroup) =
            BioViewHolder(
                ItemDetailBioBinding.inflate(LayoutInflater.from(parent.context), parent, false)
            )

        val DIFF_CALLBACK =
            object : SimpleDiffCallback<BioItem>() {
                override fun areContentsTheSame(oldItem: BioItem, newItem: BioItem) =
                    oldItem.titleRes == newItem.titleRes && oldItem.text == newItem.text
            }
    }
}
