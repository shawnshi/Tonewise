/*
 * Copyright (c) 2026 Tonewise Project
 * ConductorSortDialog.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.sort

import androidx.fragment.app.activityViewModels
import dagger.hilt.android.AndroidEntryPoint
import com.shawnshi.tonewise.home.HomeViewModel
import com.shawnshi.tonewise.list.sort.Sort
import com.shawnshi.tonewise.list.sort.SortDialog

/**
 * A [SortDialog] that controls the [Sort] of [HomeViewModel.conductorList].
 */
@AndroidEntryPoint
class ConductorSortDialog : SortDialog() {
    private val homeModel: HomeViewModel by activityViewModels()

    override fun getInitialSort() = homeModel.conductorSort

    override fun allowSaveUnchangedSort() = homeModel.hasCustomConductorOrder

    override fun applyChosenSort(sort: Sort) {
        homeModel.applyConductorSort(sort)
    }

    override fun getModeChoices() =
        listOf(Sort.Mode.ByName, Sort.Mode.ByDuration, Sort.Mode.ByCount)
}
