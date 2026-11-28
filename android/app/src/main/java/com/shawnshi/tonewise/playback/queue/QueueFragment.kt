/*
 * Copyright (c) 2021 Auxio Project
 * QueueFragment.kt is part of Auxio.
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

import android.os.Bundle
import android.view.LayoutInflater
import android.widget.Toast
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.AssistChip
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.view.isInvisible
import androidx.fragment.app.activityViewModels
import androidx.fragment.app.viewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.ItemTouchHelper
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.snackbar.Snackbar
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlin.math.min
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentQueueBinding
import com.shawnshi.tonewise.list.EditClickListListener
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.SceneBanner
import com.shawnshi.tonewise.sync.SessionSlotItem
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.ui.ViewBindingFragment
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.showToast
import org.oxycblt.musikr.Song
import timber.log.Timber as L

/**
 * A [ViewBindingFragment] that displays an editable queue.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@AndroidEntryPoint
class QueueFragment : ViewBindingFragment<FragmentQueueBinding>(), EditClickListListener<Song> {
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController
    private val queueModel: QueueViewModel by viewModels()
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val queueAdapter = QueueAdapter(this)
    private var touchHelper: ItemTouchHelper? = null

    override fun onCreateBinding(inflater: LayoutInflater) = FragmentQueueBinding.inflate(inflater)

    override fun onBindingCreated(binding: FragmentQueueBinding, savedInstanceState: Bundle?) {
        super.onBindingCreated(binding, savedInstanceState)

        // --- UI SETUP ---
        binding.queueSlotBar.apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val slots by sessionSlotStore.slots.collectAsStateWithLifecycle()
                val scenes by lanSyncRepository.scenes.collectAsStateWithLifecycle()
                val activeSlotIndex by sessionSlotStore.activeSlotIndex.collectAsStateWithLifecycle()
                val activeSceneLabel by
                    sessionSlotStore.activeSceneLabel.collectAsStateWithLifecycle()
                val activeAccent by
                    dynamicAccentController.activeAccentArgb.collectAsStateWithLifecycle()
                val currentQueue by queueModel.queue.collectAsStateWithLifecycle()
                val currentQueueIdx by queueModel.index.collectAsStateWithLifecycle()
                val hasUpcoming = currentQueueIdx in 0 until currentQueue.lastIndex
                val presetScenes =
                    remember(scenes) {
                        scenes.filterNot { it.id == "44444444-4444-4444-8444-444444444444" }.take(5)
                    }

                TonewiseComposeTheme(
                    roundMode = uiSettings.roundMode,
                    dynamicAccentArgb = if (uiSettings.enableDynamicCoverAccent) activeAccent else null,
                ) {
                    QueuePersistentSlotBar(
                        slots = slots,
                        presetScenes = presetScenes,
                        activeSlotIndex = activeSlotIndex,
                        hasUpcoming = hasUpcoming,
                        onSelectSlot = { slotIdx, existing, presetScene ->
                            handleSlotChipClick(
                                targetSlotIndex = slotIdx,
                                activeSlotIndex = activeSlotIndex,
                                activeSceneLabel = activeSceneLabel,
                                slots = slots,
                                existing = existing,
                                presetScene = presetScene,
                            )
                        },
                        onSaveCurrent = {
                            saveCurrentQueueToSlot(activeSlotIndex, activeSceneLabel)
                        },
                        onClearUpcoming = {
                            val cleared = queueModel.clearUpcoming()
                            if (cleared.isNotEmpty()) {
                                Snackbar.make(
                                        requireBinding().root,
                                        getString(R.string.lbl_queue_cleared_upcoming, cleared.size),
                                        Snackbar.LENGTH_LONG,
                                    )
                                    .setAction(R.string.lbl_undo) {
                                        queueModel.restoreUpcoming(cleared)
                                    }
                                    .show()
                            }
                        },
                    )
                }
            }
        }

        binding.queueRecycler.apply {
            adapter = queueAdapter
            touchHelper =
                ItemTouchHelper(QueueDragCallback(queueModel)).also {
                    it.attachToRecyclerView(this)
                }
        }

        // Sometimes the scroll can change without the listener being updated, so we also
        // check for relayout events.
        binding.queueRecycler.apply {
            addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> updateDivider() }
            addOnScrollListener(
                object : RecyclerView.OnScrollListener() {
                    override fun onScrolled(recyclerView: RecyclerView, dx: Int, dy: Int) {
                        updateDivider()
                    }
                }
            )
        }

        // --- VIEWMODEL SETUP ----
        collectImmediately(
            queueModel.queue,
            queueModel.index,
            playbackModel.isPlaying,
            ::updateQueue,
        )
    }

    override fun onDestroyBinding(binding: FragmentQueueBinding) {
        super.onDestroyBinding(binding)
        touchHelper = null
        binding.queueRecycler.adapter = null
        // Avoid possible race conditions that could cause a bad instruction to be consumed
        // during list initialization and crash the app. Could happen if the user is fast enough.
        queueModel.queueInstructions.consume()
    }

    override fun onClick(item: Song, viewHolder: RecyclerView.ViewHolder) {
        queueModel.goto(viewHolder.bindingAdapterPosition)
    }

    override fun onPickUp(viewHolder: RecyclerView.ViewHolder) {
        requireNotNull(touchHelper) { "ItemTouchHelper was not available" }.startDrag(viewHolder)
    }

    private fun updateDivider() {
        val binding = requireBinding()
        binding.queueDivider.isInvisible =
            (binding.queueRecycler.layoutManager as LinearLayoutManager)
                .findFirstCompletelyVisibleItemPosition() < 1
    }

    private fun updateQueue(queue: List<Song>, index: Int, isPlaying: Boolean) {
        val binding = requireBinding()

        val instructions = queueModel.queueInstructions.consume()
        queueAdapter.update(queue, instructions)
        queueAdapter.setPosition(index, isPlaying, instructions == null)

        // If requested, scroll to a new item (occurs when the index moves)
        val scrollTo = queueModel.scrollTo.consume()
        if (scrollTo != null) {
            val lmm = binding.queueRecycler.layoutManager as LinearLayoutManager
            val start = lmm.findFirstCompletelyVisibleItemPosition()
            val end = lmm.findLastCompletelyVisibleItemPosition()
            val notInitialized =
                start == RecyclerView.NO_POSITION || end == RecyclerView.NO_POSITION
            // When we scroll, we want to scroll to the almost-top so the user can see
            // future songs instead of past songs. The way we have to do this however is
            // dependent on where we have to scroll to get to the currently playing song.
            if (notInitialized || scrollTo < start) {
                // We need to scroll upwards, or initialize the scroll, no need to offset
                L.d("Not scrolling downwards, no offset needed")
                binding.queueRecycler.scrollToPosition(scrollTo)
            } else if (scrollTo > end) {
                // We need to scroll downwards, we need to offset by a screen of songs.
                // This does have some error due to how many completely visible items on-screen
                // can vary. This is considered okay.
                val offset = scrollTo + (end - start)
                L.d("Scrolling downwards, offsetting by $offset")
                binding.queueRecycler.scrollToPosition(min(queue.lastIndex, offset))
            }
        }
    }

    private fun saveCurrentQueueToSlot(
        slotIndex: Int,
        sceneLabel: String,
        showFeedback: Boolean = true,
        activate: Boolean = true,
    ) {
        val pagerQueue = playbackModel.pagerQueue.value
        val snapshotSongs = pagerQueue.queue.toList()
        if (snapshotSongs.isEmpty()) {
            if (showFeedback) {
                requireContext().showToast(R.string.lbl_slot_empty_queue)
            }
            return
        }
        val title =
            sceneLabel
                .takeUnless { it.equals("Default", ignoreCase = true) || it.isBlank() }
                ?: playbackModel.song.value?.name?.raw
                ?: getString(R.string.fmt_queue_slot, slotIndex)
        val curIdx = pagerQueue.index
        val posMs = playbackModel.positionDs.value * 100L
        val nowMs = System.currentTimeMillis()
        val appContext = requireContext().applicationContext
        val feedbackText = getString(R.string.fmt_scene_switched_slot, slotIndex, title)
        viewLifecycleOwner.lifecycleScope.launch {
            val canonicalUids = lanSyncRepository.resolveCanonicalTrackUids(snapshotSongs)
            sessionSlotStore.saveSlot(
                SessionSlotItem(
                    slotIndex = slotIndex,
                    name = title,
                    sceneId = null,
                    updatedAtMs = nowMs,
                    queueTrackUids = canonicalUids,
                    currentIndex = curIdx,
                    positionMs = posMs,
                ),
                activate = activate,
            )
            lanSyncRepository.scheduleM3u8WriteBack(title, snapshotSongs)
            if (showFeedback) {
                Toast.makeText(appContext, feedbackText, Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun handleSlotChipClick(
        targetSlotIndex: Int,
        activeSlotIndex: Int,
        activeSceneLabel: String,
        slots: List<SessionSlotItem>,
        existing: SessionSlotItem?,
        presetScene: SceneBanner?,
    ) {
        val outgoingSlot = slots.firstOrNull { it.slotIndex == activeSlotIndex }
        val shouldSnapshotOutgoing =
            targetSlotIndex != activeSlotIndex &&
                outgoingSlot != null &&
                SessionSlotStore.isSlotPopulated(outgoingSlot) &&
                activeSceneLabel.equals(outgoingSlot.name, ignoreCase = true)
        if (shouldSnapshotOutgoing) {
            // Snapshot only an already-populated outgoing slot whose active scene matches it.
            saveCurrentQueueToSlot(
                activeSlotIndex,
                activeSceneLabel,
                showFeedback = false,
                activate = false,
            )
        }
        if (existing == null || existing.queueTrackUids.isEmpty()) {
            if (presetScene != null) {
                val appContext = requireContext().applicationContext
                viewLifecycleOwner.lifecycleScope.launch {
                    val (_, ordered) =
                        withContext(Dispatchers.Default) {
                            lanSyncRepository.generateAutoDjPlaylist(
                                scene = presetScene,
                                targetCount = AutoDjEngine.DEFAULT_TARGET_COUNT,
                            )
                        }
                    if (!isAdded) return@launch
                    if (ordered.isEmpty()) {
                        requireContext().showToast(R.string.lbl_scene_no_matches)
                        return@launch
                    }
                    val canonicalUids = lanSyncRepository.resolveCanonicalTrackUids(ordered)
                    sessionSlotStore.saveSlot(
                        SessionSlotItem(
                            slotIndex = targetSlotIndex,
                            name = presetScene.title,
                            sceneId = presetScene.id,
                            updatedAtMs = System.currentTimeMillis(),
                            queueTrackUids = canonicalUids,
                            currentIndex = 0,
                            positionMs = 0L,
                        ),
                        activate = true,
                    )
                    playbackModel.play(ordered)
                    Toast.makeText(
                            appContext,
                            getString(R.string.fmt_scene_switched_slot, targetSlotIndex, presetScene.title),
                            Toast.LENGTH_SHORT,
                        )
                        .show()
                }
            } else {
                saveCurrentQueueToSlot(targetSlotIndex, "Default", showFeedback = true)
            }
            return
        }
        viewLifecycleOwner.lifecycleScope.launch {
            val resolved = lanSyncRepository.resolveSlotSongs(existing)
            if (!isAdded) return@launch
            if (resolved.isEmpty()) {
                requireContext().showToast(R.string.lbl_scene_no_matches)
                return@launch
            }
            sessionSlotStore.setActiveScene(existing.name, targetSlotIndex)
            playbackModel.restoreSessionQueue(
                songs = resolved,
                index = existing.currentIndex.coerceIn(0, resolved.lastIndex),
                positionMs = existing.positionMs,
            )
            Toast.makeText(
                    requireContext().applicationContext,
                    getString(R.string.fmt_scene_switched_slot, targetSlotIndex, existing.name),
                    Toast.LENGTH_SHORT,
                )
                .show()
        }
    }
}

@Composable
private fun QueuePersistentSlotBar(
    slots: List<SessionSlotItem>,
    presetScenes: List<SceneBanner>,
    activeSlotIndex: Int,
    hasUpcoming: Boolean,
    onSelectSlot: (Int, SessionSlotItem?, SceneBanner?) -> Unit,
    onSaveCurrent: () -> Unit,
    onClearUpcoming: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        color = androidx.compose.ui.graphics.Color.Transparent,
    ) {
        androidx.compose.foundation.layout.Column(modifier = Modifier.fillMaxWidth()) {
            Row(
                modifier =
                    Modifier.fillMaxWidth()
                        .horizontalScroll(rememberScrollState())
                        .padding(horizontal = 12.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (idx in 0 until 5) {
                    val slotIdx = idx + 1
                    val existing = slots.firstOrNull { it.slotIndex == slotIdx }
                    val presetScene =
                        existing?.sceneId?.let { sid ->
                            presetScenes.firstOrNull {
                                it.id.equals(sid, ignoreCase = true) ||
                                    it.title.startsWith(sid, ignoreCase = true)
                            }
                        } ?: presetScenes.getOrNull(idx)
                    val selected = activeSlotIndex == slotIdx
                    val hasTracks = existing != null && existing.queueTrackUids.isNotEmpty()
                    val label =
                        if (hasTracks) {
                            val shortName = existing.name.substringBefore('·').trim()
                            "#$slotIdx $shortName (${existing.queueTrackUids.size})"
                        } else if (presetScene != null) {
                            "#$slotIdx ${presetScene.title.substringBefore('·').trim()}"
                        } else {
                            "+ #$slotIdx"
                        }
                    FilterChip(
                        selected = selected,
                        onClick = { onSelectSlot(slotIdx, existing, presetScene) },
                        label = { Text(label) },
                    )
                }
                AssistChip(
                    onClick = onSaveCurrent,
                    label = { Text(stringResource(R.string.lbl_save_current_slot)) },
                )
                if (hasUpcoming) {
                    AssistChip(
                        onClick = onClearUpcoming,
                        label = { Text(stringResource(R.string.lbl_queue_clear_upcoming)) },
                    )
                }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
        }
    }
}
