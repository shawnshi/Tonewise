/*
 * Copyright (c) 2022 Auxio Project
 * PlaybackBarFragment.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.playback

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.widget.Toast
import androidx.core.view.doOnLayout
import androidx.core.view.isVisible
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.lifecycleScope
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.launch
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentPlaybackBarBinding
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.state.RepeatMode
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.ui.ViewBindingFragment
import com.shawnshi.tonewise.ui.ZuneAppBarDrawerSheet
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.showToast
import org.oxycblt.musikr.Song
import timber.log.Timber as L

/**
 * A [ViewBindingFragment] that shows the current playback state in a compact manner.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@AndroidEntryPoint
class PlaybackBarFragment : ViewBindingFragment<FragmentPlaybackBarBinding>() {
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var uiSettings: UISettings
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    private var latestSong: Song? = null
    private var latestScenePrefix: String? = null

    override fun onCreateBinding(inflater: LayoutInflater) =
        FragmentPlaybackBarBinding.inflate(inflater)

    override fun onBindingCreated(
        binding: FragmentPlaybackBarBinding,
        savedInstanceState: Bundle?,
    ) {
        super.onBindingCreated(binding, savedInstanceState)

        // --- UI SETUP ---
        binding.root.apply {
            setOnClickListener { playbackModel.openPlayback() }
            setOnLongClickListener {
                playbackModel.song.value?.let(detailModel::showAlbum)
                true
            }
        }

        // Set up marquee on song information
        binding.playbackSong.isSelected = true
        binding.playbackInfo.isSelected = true
        binding.playbackScenePill.isVisible = false
        binding.root.doOnLayout { (binding.root.parent as? View)?.requestApplyInsets() }

        // Set up actions
        binding.playbackPlayPause.setOnClickListener { playbackModel.togglePlaying() }
        binding.playbackAppbarMore.setOnClickListener {
            if (parentFragmentManager.findFragmentByTag(ZuneAppBarDrawerSheet.TAG) == null) {
                ZuneAppBarDrawerSheet().show(parentFragmentManager, ZuneAppBarDrawerSheet.TAG)
            }
        }
        if (!uiSettings.roundMode) {
            binding.playbackProgressBar.trackCornerRadius = 0
        }

        // -- VIEWMODEL SETUP ---
        collectImmediately(playbackModel.song, ::updateSong)
        collectImmediately(playbackModel.isPlaying, ::updatePlaying)
        collectImmediately(playbackModel.positionDs, ::updatePosition)
        collectImmediately(
            sessionSlotStore.activeSceneLabel,
            sessionSlotStore.activeSlotIndex,
            playbackModel.pagerQueue,
            ::updateScenePill,
        )
        collectImmediately(
            playbackModel.currentBarAction,
            playbackModel.repeatMode,
            playbackModel.isShuffled,
            ::updateBarAction,
        )
    }

    override fun onDestroyBinding(binding: FragmentPlaybackBarBinding) {
        super.onDestroyBinding(binding)
        binding.playbackSecondaryAction.clearPendingIcon()
        // Marquee elements leak if they are not disabled when the views are destroyed.
        binding.playbackSong.isSelected = false
        binding.playbackInfo.isSelected = false
    }

    private fun updateScenePill(sceneLabel: String, slotIndex: Int, queue: PagerQueue) {
        binding?.playbackScenePill?.isVisible = false
        val isDefault = sceneLabel.equals("Default", ignoreCase = true)
        latestScenePrefix =
            if ((isDefault && slotIndex == 1) || queue.queue.isEmpty()) {
                null
            } else if (isDefault) {
                getString(R.string.lbl_scene_pill_default) + " #$slotIndex"
            } else {
                sceneLabel
            }
        refreshInfoLine()
    }

    private fun refreshInfoLine() {
        val b = binding ?: return
        val song = latestSong ?: return
        val baseArtist = song.artists.resolveNames(requireContext())
        val prefix = latestScenePrefix
        b.playbackInfo.text =
            if (!prefix.isNullOrBlank()) "$prefix · $baseArtist" else baseArtist
    }

    private fun updateSong(song: Song?) {
        if (song == null) {
            // Nothing to do.
            return
        }

        val context = requireContext()
        val binding = requireBinding()
        latestSong = song
        binding.playbackCover.bind(song)
        binding.playbackSong.text = song.name.resolve(context)
        refreshInfoLine()
        binding.playbackProgressBar.max = song.durationMs.msToDs().toInt()
    }

    private fun updatePlaying(isPlaying: Boolean) {
        val button = requireBinding().playbackPlayPause
        button.isChecked = isPlaying
        button.contentDescription = getString(if (isPlaying) R.string.desc_pause else R.string.desc_play)
    }

    private fun updatePosition(positionDs: Long) {
        requireBinding().playbackProgressBar.progress = positionDs.toInt()
    }

    private fun updateBarAction(
        actionMode: ActionMode,
        repeatMode: RepeatMode,
        isShuffled: Boolean,
    ) {
        val binding = requireBinding()
        when (actionMode) {
            ActionMode.NEXT -> {
                L.d("Using skip next action")
                binding.playbackSecondaryAction.apply {
                    if (tag != actionMode) {
                        setIconResource(R.drawable.ic_skip_next_24)
                        contentDescription = getString(R.string.desc_skip_next)
                        setOnClickListener { playbackModel.next() }
                        isChecked = false
                        tag = actionMode
                    }
                }
            }
            ActionMode.REPEAT -> {
                L.d("Using repeat mode action")
                binding.playbackSecondaryAction.apply {
                    if (tag != actionMode) {
                        contentDescription = getString(R.string.desc_change_repeat)
                        setOnClickListener { playbackModel.toggleRepeatMode() }
                        tag = actionMode
                    }
                    setIconResource(repeatMode.icon)
                    isChecked = repeatMode != RepeatMode.NONE
                }
            }
            ActionMode.SHUFFLE -> {
                L.d("Using shuffle action")
                binding.playbackSecondaryAction.apply {
                    if (tag != actionMode) {
                        setIconResource(R.drawable.sel_shuffle_state_24)
                        contentDescription = getString(R.string.desc_shuffle)
                        setOnClickListener { playbackModel.toggleShuffled() }
                        tag = actionMode
                    }
                    isChecked = isShuffled
                }
            }
        }
    }
}
