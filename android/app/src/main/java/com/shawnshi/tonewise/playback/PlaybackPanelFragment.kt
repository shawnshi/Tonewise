/*
 * Copyright (c) 2021 Auxio Project
 * PlaybackPanelFragment.kt is part of Auxio.
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

import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.audiofx.AudioEffect
import android.os.Build
import android.os.Bundle
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.TextUtils
import android.text.style.ForegroundColorSpan
import android.util.TypedValue
import android.view.LayoutInflater
import android.view.MenuItem
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.widget.Toolbar
import androidx.core.view.isVisible
import androidx.core.view.updatePadding
import androidx.dynamicanimation.animation.SpringForce
import androidx.fragment.app.activityViewModels
import androidx.fragment.app.viewModels
import androidx.lifecycle.lifecycleScope
import coil3.ImageLoader
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.request.allowHardware
import coil3.toBitmap
import com.google.android.material.R as MaterialR
import com.google.android.material.color.MaterialColors
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.shawnshi.tonewise.playback.replaygain.ReplayGainAudioProcessor
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlin.math.abs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentPlaybackPanelBinding
import com.shawnshi.tonewise.detail.BookletPdfDialog
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.detail.WorkRecordingsDialog
import com.shawnshi.tonewise.list.ListViewModel
import com.shawnshi.tonewise.list.recycler.SongViewHolder
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.audio.SignalPathDialog
import com.shawnshi.tonewise.playback.audio.SignalPathState
import com.shawnshi.tonewise.playback.audio.SignalQualityTier
import com.shawnshi.tonewise.playback.dsp.DspControlDialog
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.queue.QueueViewModel
import com.shawnshi.tonewise.playback.state.RepeatMode
import com.shawnshi.tonewise.playback.ui.StyledSeekBar
import com.shawnshi.tonewise.playback.ui.clock.DeskClockDialog
import com.shawnshi.tonewise.playback.ui.stepper.Direction
import com.shawnshi.tonewise.playback.ui.stepper.StepperOverlay
import com.shawnshi.tonewise.playback.ui.swiper.CarouselTransformer
import com.shawnshi.tonewise.playback.ui.swiper.CoverPagerAdapter
import com.shawnshi.tonewise.playback.ui.swiper.UserAwarePagerCallback
import com.shawnshi.tonewise.sync.FocusFilterSpec
import com.shawnshi.tonewise.sync.FocusPillState
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.sync.QuickTagDialog
import com.shawnshi.tonewise.sync.SessionSlotStore
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.ui.ViewBindingFragment
import com.shawnshi.tonewise.ui.ZuneColorMath
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.dampen
import com.shawnshi.tonewise.util.isNight
import com.shawnshi.tonewise.util.recycler
import com.shawnshi.tonewise.util.showToast
import com.shawnshi.tonewise.util.smoothScrollByPageTo
import com.shawnshi.tonewise.util.systemBarInsetsCompat
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.MusicParent
import org.oxycblt.musikr.Song
import timber.log.Timber as L

/**
 * A [ViewBindingFragment] more information about the currently playing song, alongside all
 * available controls.
 *
 * @author Alexander Capehart (OxygenCobalt)
 *
 * TODO: Improve flickering situation on play button
 */
@AndroidEntryPoint
class PlaybackPanelFragment :
    ViewBindingFragment<FragmentPlaybackPanelBinding>(),
    Toolbar.OnMenuItemClickListener,
    StyledSeekBar.Listener,
    StepperOverlay.Listener {
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var lanSyncRepository: LanSyncRepository
    @Inject lateinit var sessionSlotStore: SessionSlotStore
    @Inject lateinit var playCountRepository: PlayCountRepository
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var dynamicAccentController: DynamicAccentController
    @Inject lateinit var imageLoader: ImageLoader
    private val coverPagerAdapter = CoverPagerAdapter(this)
    private val playbackModel: PlaybackViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    private val listModel: ListViewModel by activityViewModels()
    private val queueModel: QueueViewModel by viewModels()
    private var equalizerLauncher: ActivityResultLauncher<Intent>? = null
    private var userAwarePagerCallback: UserAwarePagerCallback? = null
    private var djFreezeBackup: Pair<List<Song>, Int>? = null
    private var activeFreezeToken: String? = null
    private var latestSignalPath: SignalPathState? = null
    private var accentSampleJob: Job? = null
    private var defaultPlayPauseBgTint: ColorStateList? = null
    private var defaultPlayPauseIconTint: ColorStateList? = null

    override fun onCreateBinding(inflater: LayoutInflater) =
        FragmentPlaybackPanelBinding.inflate(inflater)

    override fun onBindingCreated(
        binding: FragmentPlaybackPanelBinding,
        savedInstanceState: Bundle?,
    ) {
        super.onBindingCreated(binding, savedInstanceState)

        // AudioEffect expects you to use startActivityForResult with the panel intent. There is no
        // contract analogue for this intent, so the generic contract is used instead.
        equalizerLauncher =
            registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
                // Nothing to do
            }

        // --- UI SETUP ---
        binding.root.setOnApplyWindowInsetsListener { view, insets ->
            val bars = insets.systemBarInsetsCompat
            view.updatePadding(bottom = bars.bottom)
            insets
        }

        binding.playbackToolbar.apply {
            setNavigationOnClickListener { playbackModel.openMain() }
            setOnMenuItemClickListener(this@PlaybackPanelFragment)
        }

        binding.playbackPager.apply {
            // intentional LTR override since thepager is a chronological element
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            adapter = coverPagerAdapter
            userAwarePagerCallback =
                UserAwarePagerCallback(this) {
                        // Posting the queue goto command prevents the seekbar pos from desyncing
                        // from the song's duration, which creates a visual flicker in the seekbar.
                        post { queueModel.goto(it) }
                    }
                    .also { it.attach() }
            setPageTransformer(CarouselTransformer())
            recycler().apply {
                // Make it possible to collapse the bottom sheet from the ViewPager's touch area.
                isNestedScrollingEnabled = false
                // Visual effect consistency
                // TODO: Custom overscroll?
                overScrollMode = View.OVER_SCROLL_NEVER
            }
            // Make it easier to collapse the bottom sheet
            dampen()
            offscreenPageLimit = 1
        }

        binding.playbackComposer.setOnClickListener {
            playbackModel.song.value?.composers?.firstOrNull()?.let(detailModel::showComposer)
        }
        binding.playbackSong.apply {
            isSelected = true
            setOnClickListener { navigateToCurrentSong() }
        }
        binding.playbackArtist.apply {
            isSelected = true
            setOnClickListener { navigateToCurrentArtist() }
        }
        binding.playbackAlbum.apply {
            isSelected = true
            setOnClickListener { navigateToCurrentAlbum() }
        }
        binding.playbackMeta.apply {
            isSelected = true
            setOnClickListener { playbackModel.song.value?.let(detailModel::showSong) }
        }

        binding.playbackSeekBar?.apply {
            listener = this@PlaybackPanelFragment
            setRectilinearMode(!uiSettings.roundMode)
        }
        defaultPlayPauseBgTint = binding.playbackPlayPause.backgroundTintList
        defaultPlayPauseIconTint = binding.playbackPlayPause.iconTint
        refreshMosaicBackdrop()

        // Set up actions
        // TODO: Add better playback button accessibility
        binding.playbackRepeat.setOnClickListener { playbackModel.toggleRepeatMode() }
        binding.playbackSkipPrev.setOnClickListener { playbackModel.prev() }
        binding.playbackPlayPause.apply {
            @SuppressLint("RestrictedApi")
            setCornerSpringForce(
                SpringForce().apply {
                    stiffness = 700f
                    dampingRatio = 0.9f
                }
            )
            setOnClickListener { playbackModel.togglePlaying() }
        }
        binding.playbackSkipNext.setOnClickListener { playbackModel.next() }
        binding.playbackShuffle.setOnClickListener { playbackModel.toggleShuffled() }
        binding.playbackLove?.setOnClickListener {
            playbackModel.song.value?.let { current ->
                val nextLoved = !lanSyncRepository.isLoved(current)
                lanSyncRepository.enqueueTagMutation(
                    current,
                    "rating",
                    if (nextLoved) "5" else "3",
                )
                updateLoveButton(current)
            }
        }
        binding.playbackMore?.setOnClickListener {
            playbackModel.song.value?.let {
                listModel.openMenu(R.menu.playback_song, it, PlaySong.ByItself)
            }
        }

        // --- VIEWMODEL SETUP --
        collectImmediately(playbackModel.song, playbackModel.currentPlayCount, ::updateSong)
        collectImmediately(lanSyncRepository.pendingMutationCount) { _ ->
            playbackModel.song.value?.let { current ->
                updateMicroCapsules(
                    song = current,
                    isClassical = !current.workName.isNullOrBlank(),
                    classicalWork = musicRepository.findWorkForSong(current),
                )
            }
        }
        collectImmediately(playbackModel.signalPath, ::updateSignalPill)
        collectImmediately(playbackModel.waveformEnvelope, ::updateWaveform)
        collectImmediately(playbackModel.parent, ::updateParent)
        collectImmediately(playbackModel.positionDs, ::updatePosition)
        collectImmediately(playbackModel.repeatMode, ::updateRepeat)
        collectImmediately(playbackModel.isPlaying, ::updatePlaying)
        collectImmediately(playbackModel.isShuffled, ::updateShuffled)
        collectImmediately(playbackModel.pagerQueue, ::updatePager)
        collectImmediately(dynamicAccentController.activeAccentArgb, ::applyDynamicAccent)
    }

    // FIXME: Old code!! Maybe not necessary anymore?
    //    override fun onStart() {
    //        super.onStart()
    //        playbackModel.song.value?.let { requireBinding().playbackCover.bind(it) }
    //        requireBinding().root.viewTreeObserver.addOnGlobalLayoutListener(this)
    //    }

    //    override fun onStop() {
    //        super.onStop()
    //        requireBinding().root.viewTreeObserver.removeOnGlobalLayoutListener(this)
    //    }

    //    override fun onGlobalLayout() {
    //        if (binding == null || lastCoverWidth < 0) {
    //            return
    //        }
    // Hacky workaround for cover radius not being preserved in between sizing changes
    // (i.e split screen or landscape mode)
    // For some reason ConstraintLayout does several passes on 1:1 elements that causes their
    // size to radically change, so we wait until it stabilizes and then force an image
    // reload if needed. Optimistically this is a no-op from coil caching, but when the cover
    // did accidentally load the wrong image (with weird corner radius intended for bigger
    // covers) we can force it to reload.
    // If this breaks, it's fine since we also started a load as we normally did w/state
    // updates, so the cover will not break.
    //        val binding = requireBinding()
    //        val coverWidth = binding.playbackCover.width
    //        if (lastCoverWidth != coverWidth) {
    //            lastCoverWidth = coverWidth
    //        } else {
    //            playbackModel.song.value?.let { binding.playbackCover.bind(it) }
    //            lastCoverWidth = -1
    //        }
    //    }

    override fun onDestroyBinding(binding: FragmentPlaybackPanelBinding) {
        accentSampleJob?.cancel()
        accentSampleJob = null
        equalizerLauncher = null
        binding.playbackRepeat.clearPendingIcon()
        binding.playbackSong.isSelected = false
        binding.playbackArtist.isSelected = false
        binding.playbackAlbum.isSelected = false
        binding.playbackMeta.isSelected = false
        binding.playbackToolbar.setOnMenuItemClickListener(null)
        userAwarePagerCallback?.release()
        binding.playbackPager.adapter = null
    }

    override fun onMenuItemClick(item: MenuItem): Boolean {
        if (item.itemId == R.id.action_desk_clock) {
            if (parentFragmentManager.findFragmentByTag(DeskClockDialog.TAG) == null) {
                DeskClockDialog.newInstance().show(parentFragmentManager, DeskClockDialog.TAG)
            }
            return true
        }
        if (item.itemId == R.id.action_open_equalizer) {
            if (parentFragmentManager.findFragmentByTag(DspControlDialog.TAG) == null) {
                DspControlDialog.newInstance().show(parentFragmentManager, DspControlDialog.TAG)
            }
            return true
        }
        if (item.itemId == R.id.action_sleep_timer) {
            showSleepTimerPicker()
            return true
        }

        return false
    }

    private fun showSleepTimerPicker() {
        val options =
            arrayOf(
                getString(R.string.lbl_sleep_off),
                getString(R.string.lbl_sleep_15m),
                getString(R.string.lbl_sleep_30m),
                getString(R.string.lbl_sleep_45m),
                getString(R.string.lbl_sleep_60m),
                getString(R.string.lbl_sleep_end_track),
                getString(R.string.lbl_sleep_end_work),
            )
        MaterialAlertDialogBuilder(requireContext())
            .setTitle(R.string.lbl_sleep_timer)
            .setItems(options) { _, which ->
                when (which) {
                    0 -> playbackModel.cancelSleepTimer()
                    1 -> playbackModel.setSleepTimerMinutes(15)
                    2 -> playbackModel.setSleepTimerMinutes(30)
                    3 -> playbackModel.setSleepTimerMinutes(45)
                    4 -> playbackModel.setSleepTimerMinutes(60)
                    5 -> playbackModel.setSleepTimerEndOfTrack()
                    6 -> playbackModel.setSleepTimerEndOfWork()
                }
            }
            .show()
    }

    override fun onSeekConfirmed(positionDs: Long) {
        playbackModel.seekTo(positionDs)
    }

    private fun updateSong(song: Song?, playCount: Int) {
        if (song == null) {
            // Nothing to do.
            return
        }

        val binding = requireBinding()
        val context = requireContext()
        L.d("Updating song display: $song")
        val work = song.workName?.trim()?.takeIf { it.isNotEmpty() }
        val classicalWork = if (work != null) musicRepository.findWorkForSong(song) else null
        binding.playbackAlbum.isVisible = false
        binding.playbackMeta.isVisible = false
        binding.playbackSignalPill.isVisible = false

        if (work != null) {
            val mvtTitle =
                song.movementName?.trim()?.takeIf { it.isNotEmpty() } ?: song.name.resolve(context)
            val mvtPrefix = song.movementNumber?.let { "$it. " } ?: ""
            val mvtLabel = "$mvtPrefix$mvtTitle"
            val composerLabel =
                song.composers
                    .filter { it.isNotBlank() }
                    .distinct()
                    .joinToString(" · ")
                    .ifEmpty { song.artists.resolveNames(context) }

            // Classical 3-row editorial typography:
            // 1. Composer · Work eyebrow -> 2. Movement title -> 3. Ensemble · Conductor
            binding.playbackComposer.apply {
                isVisible = true
                typeface = Typeface.SERIF
                text = "$composerLabel · $work"
            }
            binding.playbackSong.apply {
                typeface = Typeface.SERIF
                isSingleLine = false
                maxLines = 2
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = mvtLabel
            }
            val performerPart = song.artists.resolveNames(context)
            val conductorPart = song.conductors.firstOrNull()?.takeIf { it.isNotBlank() }
            val ensembleLine =
                listOfNotNull(performerPart.takeIf { it.isNotBlank() }, conductorPart)
                    .distinct()
                    .joinToString(" · ")
                    .ifEmpty { song.album.name.resolve(context) }
            binding.playbackArtist.apply {
                typeface = Typeface.DEFAULT
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.MARQUEE
                isSelected = true
                text = ensembleLine
            }
        } else {
            binding.playbackComposer.isVisible = false
            binding.playbackSong.apply {
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.MARQUEE
                isSelected = true
                typeface = Typeface.DEFAULT
                text = song.name.resolve(context)
            }
            binding.playbackArtist.apply {
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.MARQUEE
                isSelected = true
                typeface = Typeface.DEFAULT
                text = "${song.artists.resolveNames(context)} · ${song.album.name.resolve(context)}"
            }
        }
        updateLoveButton(song)
        updateMicroCapsules(song = song, isClassical = work != null, classicalWork = classicalWork)
        binding.playbackSeekBar?.durationDs = song.durationMs.msToDs()
        refreshMosaicBackdrop()
        updateDynamicCoverAccent(song)
    }

    private fun refreshMosaicBackdrop() {
        val b = binding ?: return
        val covers =
            musicRepository.library
                ?.albums
                ?.mapNotNull { it.covers.covers.firstOrNull() }
                .orEmpty()
        b.playbackMosaicBackdrop.submitCovers(
            imageLoader = imageLoader,
            covers = covers,
            darkSurface = requireContext().isNight,
            scope = viewLifecycleOwner.lifecycleScope,
        )
    }

    private fun updateDynamicCoverAccent(song: Song) {
        accentSampleJob?.cancel()
        if (!uiSettings.enableDynamicCoverAccent) {
            dynamicAccentController.clear()
            return
        }
        val cover = song.cover
        if (cover == null) {
            dynamicAccentController.clear()
            return
        }
        val appContext = requireContext().applicationContext
        val dark = requireContext().isNight
        accentSampleJob =
            viewLifecycleOwner.lifecycleScope.launch {
                val pixels =
                    withContext(Dispatchers.IO) {
                        val req =
                            ImageRequest.Builder(appContext)
                                .data(cover)
                                .size(48, 48)
                                .allowHardware(false)
                                .build()
                        val res = runCatching { imageLoader.execute(req) }.getOrNull()
                        val bmp = (res as? SuccessResult)?.image?.toBitmap() ?: return@withContext null
                        val w = bmp.width
                        val h = bmp.height
                        if (w <= 0 || h <= 0) return@withContext null
                        IntArray(w * h).also { arr ->
                            bmp.getPixels(arr, 0, w, 0, 0, w, h)
                        }
                    }
                if (!isAdded) return@launch
                if (pixels != null) {
                    dynamicAccentController.updateFromPixels(pixels, dark)
                } else {
                    dynamicAccentController.clear()
                }
            }
    }

    private fun applyDynamicAccent(argb: Int?) {
        val b = binding ?: return
        val effective = if (uiSettings.enableDynamicCoverAccent) argb else null
        b.playbackSeekBar?.setDynamicAccentColor(effective)
        if (effective != null) {
            val surfaceColor =
                MaterialColors.getColor(
                    b.playbackPlayPause,
                    MaterialR.attr.colorSurfaceContainer,
                    if (requireContext().isNight) Color.BLACK else Color.WHITE,
                )
            val pausedContainer =
                androidx.core.graphics.ColorUtils.blendARGB(surfaceColor, effective, 0.24f)
            val states =
                arrayOf(
                    intArrayOf(android.R.attr.state_checked),
                    intArrayOf(-android.R.attr.state_checked),
                )
            b.playbackPlayPause.backgroundTintList =
                ColorStateList(states, intArrayOf(effective, pausedContainer))
            b.playbackPlayPause.iconTint =
                ColorStateList(
                    states,
                    intArrayOf(ZuneColorMath.contrastingOnColor(effective), effective),
                )
        } else {
            b.playbackPlayPause.backgroundTintList = defaultPlayPauseBgTint
            b.playbackPlayPause.iconTint = defaultPlayPauseIconTint
        }
    }

    private fun updateMicroCapsules(
        song: Song,
        isClassical: Boolean,
        classicalWork: com.shawnshi.tonewise.music.ClassicalWork?,
    ) {
        val row = requireBinding().playbackCapsuleRow
        row.removeAllViews()
        val context = requireContext()

        fun addCapsuleView(
            label: CharSequence,
            highlighted: Boolean = false,
            onClick: () -> Unit,
        ) {
            val density = resources.displayMetrics.density
            val hPad = (10f * density).toInt()
            val vPad = (4f * density).toInt()
            val endMargin = (6f * density).toInt()

            val bgColor =
                if (highlighted) {
                    MaterialColors.getColor(row, MaterialR.attr.colorPrimaryContainer)
                } else {
                    MaterialColors.getColor(row, MaterialR.attr.colorSurfaceContainer)
                }
            val fgColor =
                if (highlighted) {
                    MaterialColors.getColor(row, MaterialR.attr.colorOnPrimaryContainer)
                } else {
                    MaterialColors.getColor(row, MaterialR.attr.colorOnSurfaceVariant)
                }
            val strokeColor =
                if (highlighted) {
                    MaterialColors.getColor(row, androidx.appcompat.R.attr.colorPrimary)
                } else {
                    MaterialColors.getColor(row, MaterialR.attr.colorOutlineVariant)
                }

            val shape =
                GradientDrawable().apply {
                    shape = GradientDrawable.RECTANGLE
                    cornerRadius = if (uiSettings.roundMode) 999f * density else 0f
                    setColor(bgColor)
                    setStroke((1f * density).toInt().coerceAtLeast(1), strokeColor)
                }

            val tv =
                TextView(context).apply {
                    text = label
                    setTextColor(fgColor)
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 11.5f)
                    isSingleLine = true
                    background = shape
                    setPadding(hPad, vPad, hPad, vPad)
                    layoutParams =
                        LinearLayout.LayoutParams(
                                LinearLayout.LayoutParams.WRAP_CONTENT,
                                LinearLayout.LayoutParams.WRAP_CONTENT,
                            )
                            .apply { marginEnd = endMargin }
                    isClickable = true
                    isFocusable = true
                    setOnClickListener { onClick() }
                }
            row.addView(tv)
        }

        // First slot in Unified Studio Strip: live audio signal path badge
        latestSignalPath?.let { state ->
            addCapsuleView(label = formatSignalStripLabel(state, row)) {
                SignalPathDialog.newInstance().show(parentFragmentManager, SignalPathDialog.TAG)
            }
        }

        if (isClassical) {
            if (classicalWork != null && classicalWork.recordings.isNotEmpty()) {
                val recCount = classicalWork.recordings.size
                addCapsuleView(
                    label = context.getString(R.string.fmt_classical_recordings_pill, recCount)
                ) {
                    WorkRecordingsDialog.newInstance()
                        .show(parentFragmentManager, WorkRecordingsDialog.TAG)
                }
            }
            addCapsuleView(label = context.getString(R.string.lbl_booklet_pdf)) {
                BookletPdfDialog.newInstance().show(parentFragmentManager, BookletPdfDialog.TAG)
            }
            return
        }

        val entries = mutableListOf<Triple<String, String, FocusFilterSpec>>()
        for (sc in lanSyncRepository.getEffectiveDjScenes(song)) {
            entries +=
                Triple(
                    "scene:$sc",
                    humanizeDjLabel(sc),
                    FocusFilterSpec(scenes = mapOf(sc to FocusPillState.INCLUDE)),
                )
        }
        for (cl in lanSyncRepository.getEffectiveMoodClusters(song)) {
            entries +=
                Triple(
                    "mood:$cl",
                    humanizeDjLabel(cl),
                    FocusFilterSpec(moods = mapOf(cl to FocusPillState.INCLUDE)),
                )
        }
        val vocals = lanSyncRepository.getEffectiveDjVocals(song)
        if (!vocals.equals("unknown", ignoreCase = true) && vocals.isNotBlank()) {
            entries +=
                Triple(
                    "vocals:$vocals",
                    humanizeDjLabel(vocals),
                    FocusFilterSpec(vocals = mapOf(vocals to FocusPillState.INCLUDE)),
                )
        }
        val energy = lanSyncRepository.getEffectiveDjEnergy(song)
        if (!energy.equals("unknown", ignoreCase = true) && energy.isNotBlank()) {
            entries +=
                Triple(
                    "energy:$energy",
                    humanizeDjLabel(energy),
                    FocusFilterSpec(energies = mapOf(energy to FocusPillState.INCLUDE)),
                )
        }

        for ((token, label, spec) in entries) {
            val isActive = activeFreezeToken == token
            val displayLabel = if (isActive) "❄ $label" else label
            addCapsuleView(label = displayLabel, highlighted = isActive) {
                toggleDjFreeze(song = song, token = token, label = label, spec = spec)
            }
        }

        addCapsuleView(label = context.getString(R.string.lbl_quick_tag_tempo_add)) {
            QuickTagDialog.newInstance(song.uid.toString())
                .show(parentFragmentManager, QuickTagDialog.TAG)
        }
    }

    private fun humanizeDjLabel(raw: String): String =
        when (raw.trim().lowercase()) {
            "focus" -> "Focus 专注"
            "coding" -> "Coding 编程"
            "relax" -> "Relax 松弛"
            "energy" -> "Energy 律动"
            "pop" -> "Pop 流行"
            "calm_relaxed" -> "松弛"
            "bright_joyful" -> "明快"
            "deep_introspective" -> "内省"
            "warm_romantic" -> "温润"
            "high_tension" -> "张力"
            "instrumental" -> "纯器乐"
            "vocal" -> "人声"
            "low" -> "低能"
            "medium" -> "中能"
            "high" -> "高能"
            else -> raw
        }

    private fun formatSignalStripLabel(state: SignalPathState, containerView: View): CharSequence {
        val rateHz = state.outputSampleRateHz ?: state.sourceSampleRateHz
        val rateText =
            if (rateHz > 0) {
                if (rateHz % 1000 == 0) "${rateHz / 1000}kHz"
                else String.format("%.1fkHz", rateHz / 1000.0)
            } else {
                null
            }
        val (dotColor, tierLabel) =
            when (state.qualityTier) {
                SignalQualityTier.BIT_PERFECT ->
                    Color.parseColor("#D9B372") to "USB BIT-PERFECT"
                SignalQualityTier.HIGH_RES_FLOAT ->
                    Color.parseColor("#4DA3FF") to
                        if (state.dspSummaryLabel == "Flat" || state.dspSummaryLabel == "64-bit Flat") "32-bit Float"
                        else "64-bit DSP · ${state.dspSummaryLabel}"
                SignalQualityTier.SYSTEM_MIXER ->
                    MaterialColors.getColor(containerView, MaterialR.attr.colorOnSurfaceVariant) to
                        "System Mixer"
            }
        val summary = listOfNotNull(tierLabel, rateText).joinToString(" · ")
        return SpannableStringBuilder("● ").append(summary).apply {
            setSpan(ForegroundColorSpan(dotColor), 0, 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
    }

    private fun toggleDjFreeze(
        song: Song,
        token: String,
        label: String,
        spec: FocusFilterSpec,
    ) {
        val context = requireContext()
        if (activeFreezeToken == token) {
            freezeJob?.cancel()
            val backup = djFreezeBackup
            activeFreezeToken = null
            djFreezeBackup = null
            if (backup != null && backup.first.isNotEmpty()) {
                playbackModel.restoreSessionQueue(
                    songs = backup.first,
                    index = backup.second.coerceIn(0, backup.first.lastIndex),
                    positionMs = playbackModel.positionDs.value * 100L,
                )
            }
            context.showToast(R.string.lbl_dj_freeze_restored)
            updateMicroCapsules(
                song = song,
                isClassical = !song.workName.isNullOrBlank(),
                classicalWork = musicRepository.findWorkForSong(song),
            )
            return
        }

        freezeJob?.cancel()
        freezeJob =
            viewLifecycleOwner.lifecycleScope.launch {
                val currentSong = playbackModel.song.value ?: song
                val pqSnapshot = playbackModel.pagerQueue.value
                val (tailMovements, remainingMatches) =
                    withContext(Dispatchers.Default) {
                        val curWorkKey = ReplayGainAudioProcessor.buildWorkBlockKey(currentSong)
                        val tail =
                            if (curWorkKey == null) {
                                emptyList()
                            } else {
                                val queuedTail =
                                    if (pqSnapshot.index in pqSnapshot.queue.indices) {
                                        pqSnapshot.queue
                                            .drop(pqSnapshot.index + 1)
                                            .takeWhile {
                                                ReplayGainAudioProcessor.buildWorkBlockKey(it) == curWorkKey
                                            }
                                    } else {
                                        emptyList()
                                    }
                                if (queuedTail.isNotEmpty()) {
                                    queuedTail
                                } else {
                                    val recMovements =
                                        musicRepository
                                            .findWorkForSong(currentSong)
                                            ?.recordings
                                            ?.firstOrNull { it.album.uid == currentSong.album.uid }
                                            ?.movements
                                            .orEmpty()
                                    val pos = recMovements.indexOfFirst { it.uid == currentSong.uid }
                                    if (pos >= 0 && pos + 1 < recMovements.size) {
                                        recMovements.subList(pos + 1, recMovements.size)
                                    } else {
                                        emptyList()
                                    }
                                }
                            }
                        val excludedUids = HashSet<Music.UID>(tail.size + 1).apply {
                            add(currentSong.uid)
                            tail.forEach { add(it.uid) }
                        }
                        val matches =
                            lanSyncRepository
                                .matchSongsByFocus(spec)
                                .filter { it.uid !in excludedUids }
                        val shuffledMatches = lanSyncRepository.orderSongsWorkShuffle(matches)
                        tail to shuffledMatches
                    }
                if (binding == null) return@launch
                val freezeQueue = listOf(currentSong) + tailMovements + remainingMatches
                if (freezeQueue.size <= 1) {
                    context.showToast(R.string.lbl_scene_no_matches)
                    return@launch
                }
                if (activeFreezeToken == null) {
                    val pq = playbackModel.pagerQueue.value
                    if (pq.queue.isNotEmpty()) {
                        djFreezeBackup = pq.queue to pq.index
                    }
                }
                activeFreezeToken = token
                sessionSlotStore.setActiveScene("${getString(R.string.lbl_dj_freeze_active)} · $label")
                playbackModel.restoreSessionQueue(
                    songs = freezeQueue,
                    index = 0,
                    positionMs = playbackModel.positionDs.value * 100L,
                )
                android.widget.Toast.makeText(
                        context.applicationContext,
                        getString(R.string.fmt_dj_freeze_started, label, freezeQueue.size),
                        android.widget.Toast.LENGTH_SHORT,
                    )
                    .show()
                updateMicroCapsules(
                    song = currentSong,
                    isClassical = !currentSong.workName.isNullOrBlank(),
                    classicalWork = musicRepository.findWorkForSong(currentSong),
                )
            }
    }

    private var freezeJob: Job? = null

    private fun updateParent(parent: MusicParent?) {
        val binding = requireBinding()
        val context = requireContext()
        binding.playbackToolbar.subtitle =
            parent?.run { name.resolve(context) } ?: context.getString(R.string.lbl_all_songs)
    }

    private fun updatePosition(positionDs: Long) {
        requireBinding().playbackSeekBar?.positionDs = positionDs
    }

    private fun updateLoveButton(song: Song?) {
        val b = requireBinding()
        val btn = b.playbackLove ?: return
        val loved = song != null && lanSyncRepository.isLoved(song)
        btn.setIconResource(if (loved) R.drawable.ic_heart_filled_24 else R.drawable.ic_heart_outline_24)
        btn.iconTint =
            if (loved) {
                android.content.res.ColorStateList.valueOf(Color.parseColor("#D96B52"))
            } else {
                b.playbackMore?.iconTint
            }
    }

    private fun updateSignalPill(state: SignalPathState) {
        latestSignalPath = state
        val current = playbackModel.song.value ?: return
        updateMicroCapsules(
            song = current,
            isClassical = !current.workName.isNullOrBlank(),
            classicalWork = musicRepository.findWorkForSong(current),
        )
    }

    private fun updateWaveform(envelope: FloatArray?) {
        requireBinding().playbackSeekBar?.setWaveformEnvelope(envelope)
    }

    private fun updateRepeat(repeatMode: RepeatMode) {
        val repeatButton = requireBinding().playbackRepeat
        repeatButton.isChecked = repeatMode != RepeatMode.NONE
        repeatButton.setIconResource(repeatMode.icon)
    }

    private fun updatePlaying(isPlaying: Boolean) {
        requireBinding().playbackPlayPause.isChecked = isPlaying
        requireBinding().playbackSeekBar?.setWaveEnabled(isPlaying)
    }

    private fun updateShuffled(isShuffled: Boolean) {
        requireBinding().playbackShuffle.isChecked = isShuffled
    }

    private fun updatePager(queue: PagerQueue) {
        // Right now there's easily 140ms of frame skipping when going next/prev. This is primarily
        // the fault of specifically the nested bottom sheet UI setup, which is intractable to
        // optimize. If I don't do multiple remeasures/relayouts on every slightest state
        // instability
        // I will suddenly encounter insane issues where the sheet fails to measure, appears below
        // the sidebar, flies away, not changing with ui scale, etc, often only on third-party OEM
        // ROMs that randomly mangle  SDK APIs and the SystemUI chrome for no reason.
        //
        // Historically this was not an issue, as I did not animate next/prev. Now I do, and it's
        // highly noticeable. So at least for plain next/prev I have to hack around it, do not
        // execute any transition until the state has fully adjudicated and laid out the UI. It's
        // not effective for swiping but there's nothing I can do there.
        //
        // Eventually one day Claude Fable 6.7 will probably be able to figure out that you need to
        // reflect into System.FoobaCrumbo::beegieConnector(GoolaUtils.PlubBud) and call it
        // specifically with 0x189B31FA alongside disabling the AndroidX Helpo SuperCharge by
        // manually clobbering `BottomSheetM2InternalBoogieCompat::scrimbloManager` to null for it
        // to not actually randomly mangle the sheets and do it in 1 clean layout, but for now I
        // must do this to keep my sanity.
        //
        // Actual snippet here was codex, just cleaned & adapted it / cognitive ownership
        requireBinding().playbackPager.apply {
            if (!isAttachedToWindow) {
                post { updatePagerImpl(queue) }
                return
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && isHardwareAccelerated) {
                // New version using post-Q frame hooks
                viewTreeObserver.registerFrameCommitCallback {
                    post { postOnAnimation { updatePagerImpl(queue) } }
                }
                postInvalidateOnAnimation()
            } else {
                // Let current layout happen, then wait for the next to conclude
                postOnAnimation { postOnAnimation { updatePagerImpl(queue) } }
            }
        }
    }

    private fun updatePagerImpl(queue: PagerQueue) {
        // Android insanity means this may be executed after view destruction
        // but only on some devices.
        val binding = binding ?: return

        val command = playbackModel.pagerCommand.consume()
        if (command == null) {
            // This probably shouldn't happen in practice, as QueueViewModel directly
            // attaches to PlaybackStateManager and will basically always initialize
            // with a command as a result.
            //
            // If it does happen we should just make sure the UI state is aligned. Don't
            // want broken UI.
            coverPagerAdapter.update(queue.queue, null)
            binding.playbackPager.setCurrentItem(queue.index, false)
            return
        }

        if (command.update != null) {
            // queue needs to be updated.
            coverPagerAdapter.update(queue.queue, command.update)
        }

        if (command.scroll != null) {
            // we need to scroll, however the smooth scroll only really looks best
            // when we are only doing next/prev due to various factors. better to
            // just not animate on outright gotos or queue updates
            val delta = binding.playbackPager.currentItem - command.scroll
            if (delta == 0) {
                // user scroll, carry on
                return
            }
            if (command.update == null && abs(delta) == 1) {
                binding.playbackPager.smoothScrollByPageTo(command.scroll)
            } else {
                binding.playbackPager.setCurrentItem(command.scroll, false)
            }
        }
    }

    private fun navigateToCurrentSong() {
        playbackModel.song.value?.let(detailModel::showAlbum)
    }

    private fun navigateToCurrentArtist() {
        playbackModel.song.value?.let(detailModel::showArtist)
    }

    private fun navigateToCurrentAlbum() {
        playbackModel.song.value?.let { detailModel.showAlbum(it.album) }
    }

    override fun seek(direction: Direction) {
        when (direction) {
            Direction.FORWARDS -> playbackModel.stepForward()
            Direction.BACKWARDS -> playbackModel.stepBackwards()
        }
    }
}
