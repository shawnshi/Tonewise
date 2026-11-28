/*
 * Copyright (c) 2026 Tonewise Project
 * ZuneAppBarDrawerSheet.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.home.HomeViewModel
import com.shawnshi.tonewise.home.focus.FocusFilterDialog
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.dsp.DspControlDialog
import com.shawnshi.tonewise.playback.ui.clock.DeskClockDialog
import com.shawnshi.tonewise.sync.ScenesAndSyncDialog
import com.shawnshi.tonewise.util.showToast
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

/**
 * Zune Metro `···` AppBar Drawer housing global utilities, LAN Sync/Last.fm,
 * 5D Focus filter, Rescan, Settings, and About without cluttering the Panorama top chrome.
 */
@AndroidEntryPoint
class ZuneAppBarDrawerSheet : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val homeModel: HomeViewModel by activityViewModels()
    private val musicModel: MusicViewModel by activityViewModels()

    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View =
        ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val activeAccent by
                    dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val currentSong by playbackModel.song.collectAsStateWithLifecycle()

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    ZuneDrawerContent(
                        hasActiveSong = currentSong != null,
                        onOpenNowPlaying = {
                            playbackModel.openPlayback()
                            dismissAllowingStateLoss()
                        },
                        onOpenFocusFilter = {
                            val fm = parentFragmentManager
                            dismissAllowingStateLoss()
                            if (fm.findFragmentByTag(FocusFilterDialog.TAG) == null) {
                                FocusFilterDialog.newInstance().show(fm, FocusFilterDialog.TAG)
                            }
                        },
                        onOpenSyncAndLastFm = {
                            val fm = parentFragmentManager
                            dismissAllowingStateLoss()
                            if (fm.findFragmentByTag(ScenesAndSyncDialog.TAG) == null) {
                                ScenesAndSyncDialog.newInstance().show(fm, ScenesAndSyncDialog.TAG)
                            }
                        },
                        onOpenDspConsole = {
                            val fm = parentFragmentManager
                            dismissAllowingStateLoss()
                            if (fm.findFragmentByTag(DspControlDialog.TAG) == null) {
                                DspControlDialog.newInstance().show(fm, DspControlDialog.TAG)
                            }
                        },
                        onOpenDeskClock = {
                            val fm = parentFragmentManager
                            dismissAllowingStateLoss()
                            if (fm.findFragmentByTag(DeskClockDialog.TAG) == null) {
                                DeskClockDialog.newInstance().show(fm, DeskClockDialog.TAG)
                            }
                        },
                        onRescanLibrary = {
                            musicModel.rescan()
                            requireContext().showToast(R.string.lbl_rescan_started)
                            dismissAllowingStateLoss()
                        },
                        onOpenSettings = {
                            playbackModel.openMain()
                            homeModel.showSettings()
                            dismissAllowingStateLoss()
                        },
                        onOpenAbout = {
                            playbackModel.openMain()
                            homeModel.showAbout()
                            dismissAllowingStateLoss()
                        },
                    )
                }
            }
        }

    companion object {
        const val TAG = "ZuneAppBarDrawerSheet"
    }
}

@Composable
private fun ZuneDrawerContent(
    hasActiveSong: Boolean,
    onOpenNowPlaying: () -> Unit,
    onOpenFocusFilter: () -> Unit,
    onOpenSyncAndLastFm: () -> Unit,
    onOpenDspConsole: () -> Unit,
    onOpenDeskClock: () -> Unit,
    onRescanLibrary: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenAbout: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.surface,
        contentColor = MaterialTheme.colorScheme.onSurface,
    ) {
        Column(
            modifier =
                Modifier.fillMaxWidth()
                    .verticalScroll(rememberScrollState())
                    .navigationBarsPadding()
                    .padding(bottom = 16.dp)
        ) {
            Box(
                modifier =
                    Modifier.fillMaxWidth()
                        .height(2.dp)
                        .background(MaterialTheme.colorScheme.primary)
            )
            Spacer(modifier = Modifier.height(12.dp))

            ZuneDrawerItem(
                label = "now playing",
                subtitle = stringResource(R.string.lbl_playback),
                enabled = hasActiveSong,
                accented = hasActiveSong,
                onClick = onOpenNowPlaying,
            )
            ZuneDrawerItem(
                label = "5d filter",
                subtitle = stringResource(R.string.lbl_focus_filter_drawer),
                onClick = onOpenFocusFilter,
            )
            ZuneDrawerItem(
                label = "sync & last.fm",
                subtitle = stringResource(R.string.lbl_sync_lastfm_drawer),
                onClick = onOpenSyncAndLastFm,
            )
            ZuneDrawerItem(
                label = "64-bit dsp",
                subtitle = stringResource(R.string.lbl_now_playing_dsp),
                onClick = onOpenDspConsole,
            )
            ZuneDrawerItem(
                label = "oled clock",
                subtitle = stringResource(R.string.lbl_desk_clock),
                onClick = onOpenDeskClock,
            )

            HorizontalDivider(
                modifier = Modifier.padding(vertical = 6.dp),
                color = MaterialTheme.colorScheme.outlineVariant,
            )

            ZuneDrawerItem(
                label = "rescan library",
                subtitle = stringResource(R.string.lbl_rescan_library),
                onClick = onRescanLibrary,
            )
            ZuneDrawerItem(
                label = "settings",
                subtitle = stringResource(R.string.set_root_title),
                onClick = onOpenSettings,
            )
            ZuneDrawerItem(
                label = "about",
                subtitle = stringResource(R.string.lbl_about),
                onClick = onOpenAbout,
            )
        }
    }
}

@Composable
private fun ZuneDrawerItem(
    label: String,
    subtitle: String,
    enabled: Boolean = true,
    accented: Boolean = false,
    onClick: () -> Unit,
) {
    val primaryColor =
        when {
            !enabled -> MaterialTheme.colorScheme.onSurface.copy(alpha = 0.35f)
            accented -> MaterialTheme.colorScheme.primary
            else -> MaterialTheme.colorScheme.onSurface
        }
    Row(
        modifier =
            Modifier.fillMaxWidth()
                .clickable(enabled = enabled, onClick = onClick)
                .padding(horizontal = 24.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = label,
            fontSize = 22.sp,
            fontWeight = FontWeight.Light,
            letterSpacing = (-0.5).sp,
            color = primaryColor,
        )
        Text(
            text = subtitle,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}
