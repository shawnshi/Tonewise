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
import android.os.Build
import android.os.Bundle
import android.text.TextUtils
import android.view.LayoutInflater
import android.view.MenuItem
import android.view.View
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
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlin.math.abs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentPlaybackPanelBinding
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.list.ListViewModel
import com.shawnshi.tonewise.music.ClassicalMetadataEngine
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.dsp.DspControlDialog
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.queue.QueueViewModel
import com.shawnshi.tonewise.playback.sleep.SleepTimerMode
import com.shawnshi.tonewise.playback.sleep.SleepTimerState
import com.shawnshi.tonewise.playback.state.RepeatMode
import com.shawnshi.tonewise.playback.ui.StyledSeekBar
import com.shawnshi.tonewise.playback.ui.clock.DeskClockDialog
import com.shawnshi.tonewise.playback.ui.stepper.Direction
import com.shawnshi.tonewise.playback.ui.stepper.StepperOverlay
import com.shawnshi.tonewise.playback.ui.swiper.CarouselTransformer
import com.shawnshi.tonewise.playback.ui.swiper.CoverPagerAdapter
import com.shawnshi.tonewise.playback.ui.swiper.UserAwarePagerCallback
import com.shawnshi.tonewise.sync.LanSyncRepository
import com.shawnshi.tonewise.ui.DynamicAccentController
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.ui.ViewBindingFragment
import com.shawnshi.tonewise.ui.ZuneColorMath
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.dampen
import com.shawnshi.tonewise.util.isNight
import com.shawnshi.tonewise.util.recycler
import com.shawnshi.tonewise.util.smoothScrollByPageTo
import com.shawnshi.tonewise.util.systemBarInsetsCompat
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
    StepperOverlay.Listener,
    UISettings.Listener {
    @Inject lateinit var musicRepository: MusicRepository
    @Inject lateinit var lanSyncRepository: LanSyncRepository
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
    private var accentSampleJob: Job? = null
    private var latestCoverSeedArgb: Int? = null
    private var defaultArtistTextColors: ColorStateList? = null
    private var pendingCreditTiers: List<String> = emptyList()
    private var creditLayoutListener: View.OnLayoutChangeListener? = null
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
        defaultArtistTextColors = binding.playbackArtist.textColors
        binding.playbackSong.apply {
            isSelected = false
            setOnClickListener { navigateToCurrentSong() }
        }
        binding.playbackArtist.apply {
            isSelected = false
            setOnClickListener { navigateToCurrentArtist() }
        }
        binding.playbackAlbum.apply {
            isSelected = false
            setOnClickListener { navigateToCurrentAlbum() }
        }
        binding.playbackMeta.apply {
            isSelected = false
            setOnClickListener { playbackModel.song.value?.let(detailModel::showSong) }
            val listener =
                View.OnLayoutChangeListener { _, left, _, right, _, _, _, _, _ ->
                    if (pendingCreditTiers.isNotEmpty() && right - left > 0) {
                        text = selectFittingCreditTier(this, pendingCreditTiers, right - left)
                    }
                }
            addOnLayoutChangeListener(listener)
            creditLayoutListener = listener
        }

        binding.playbackSeekBar?.apply {
            listener = this@PlaybackPanelFragment
            setRectilinearMode(!uiSettings.roundMode)
        }
        defaultPlayPauseBgTint = binding.playbackPlayPause.backgroundTintList
        defaultPlayPauseIconTint = binding.playbackPlayPause.iconTint
        uiSettings.registerListener(this)
        refreshMosaicBackdrop()
        binding.playbackVinylView.apply {
            onSwipeNext = { playbackModel.next() }
            onSwipePrev = { playbackModel.prev() }
            onDoubleTapSeek = ::seek
        }
        binding.playbackStageToggle.setOnClickListener {
            uiSettings.playbackVinylMode = !uiSettings.playbackVinylMode
            applyStageVisualMode()
        }
        applyStageVisualMode()

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
                    if (nextLoved) "5" else "0",
                )
                updateLoveButton(current)
            }
        }

        // --- VIEWMODEL SETUP --
        collectImmediately(playbackModel.song, playbackModel.currentPlayCount, ::updateSong)
        collectImmediately(lanSyncRepository.pendingMutationCount) { _ ->
            updateLoveButton(playbackModel.song.value)
        }
        collectImmediately(playbackModel.sleepTimerState, ::updateSleepClockBadge)
        collectImmediately(playbackModel.waveformEnvelope, ::updateWaveform)
        collectImmediately(playbackModel.parent, ::updateParent)
        collectImmediately(playbackModel.positionDs, ::updatePosition)
        collectImmediately(playbackModel.repeatMode, ::updateRepeat)
        collectImmediately(playbackModel.isPlaying, ::updatePlaying)
        collectImmediately(playbackModel.isShuffled, ::updateShuffled)
        collectImmediately(playbackModel.pagerQueue, ::updatePager)
        collectImmediately(playbackModel.isPlaybackSheetExpanded, ::updateSheetExpanded)
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
        uiSettings.unregisterListener(this)
        accentSampleJob?.cancel()
        accentSampleJob = null
        equalizerLauncher = null
        binding.playbackRepeat.clearPendingIcon()
        creditLayoutListener?.let { binding.playbackMeta.removeOnLayoutChangeListener(it) }
        creditLayoutListener = null
        pendingCreditTiers = emptyList()
        binding.playbackSong.isSelected = false
        binding.playbackArtist.isSelected = false
        binding.playbackAlbum.isSelected = false
        binding.playbackMeta.isSelected = false
        binding.playbackToolbar.setOnMenuItemClickListener(null)
        userAwarePagerCallback?.release()
        binding.playbackPager.adapter = null
    }

    override fun onRoundModeChanged() {}

    override fun onPlaybackBackdropChanged() {
        applyBackdropStyle()
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
        if (item.itemId == R.id.action_song_more) {
            playbackModel.song.value?.let {
                listModel.openMenu(R.menu.playback_song, it, PlaySong.ByItself)
            }
            return true
        }

        return false
    }

    override fun onSeekConfirmed(positionDs: Long) {
        playbackModel.seekTo(positionDs)
    }

    private fun selectFittingCreditTier(
        textView: android.widget.TextView,
        tiers: List<String>,
        measuredWidthPx: Int,
    ): String {
        if (tiers.isEmpty()) return ""
        val available = (measuredWidthPx - textView.paddingLeft - textView.paddingRight).toFloat()
        if (available <= 0f) return tiers.first()
        for (tier in tiers) {
            if (textView.paint.measureText(tier) <= available) {
                return tier
            }
        }
        return tiers.last()
    }

    private fun updateSong(song: Song?, playCount: Int) {
        if (song == null) {
            // Nothing to do.
            return
        }

        val binding = requireBinding()
        val context = requireContext()
        L.d("Updating song display: $song")
        val rawTitle = song.name.resolve(context)
        val genreNames = song.genres.map { it.name.resolve(context) }
        val isSongClassical =
            ClassicalPlaybackActions.isClassicalTrack(
                workName = song.workName,
                genreNames = genreNames,
                conductors = song.conductors,
                composers = song.composers,
                title = rawTitle,
            )

        binding.playbackComposer.isVisible = false
        binding.playbackAlbum.isVisible = false

        if (isSongClassical) {
            val artistNames = song.artists.map { it.name.resolve(context) }.filter { it.isNotBlank() }
            val formatted =
                ClassicalMetadataEngine.formatClassicalNowPlaying(
                    rawTitle = rawTitle,
                    explicitWork = song.workName,
                    explicitMovement = song.movementName,
                    explicitMovementNumber = song.movementNumber,
                    album = song.album.name.resolve(context),
                    composers = song.composers,
                    artists = artistNames,
                    conductors = song.conductors,
                    year = song.date?.year,
                )
            val tiers = formatted.line3Tiers
            pendingCreditTiers = tiers

            binding.playbackSong.apply {
                typeface = Typeface.DEFAULT
                textSize = 17.5f
                isSingleLine = false
                maxLines = 2
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = formatted.line1
            }
            binding.playbackArtist.apply {
                typeface = Typeface.DEFAULT
                textSize = 14f
                setTextColor(binding.playbackSong.textColors)
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = formatted.line2
            }
            binding.playbackMeta.apply {
                typeface = Typeface.DEFAULT
                textSize = 12.5f
                visibility = View.VISIBLE
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                val fallbackMeta =
                    if (tiers.isNotEmpty()) {
                        selectFittingCreditTier(this, tiers, width)
                    } else {
                        song.album.name.resolve(context)
                    }
                text = fallbackMeta
            }
        } else {
            pendingCreditTiers = emptyList()
            val artistPart = song.artists.resolveNames(context).trim()
            val albumPart = song.album.name.resolve(context).takeIf { it.isNotBlank() }
            val yearPart = song.date?.year?.takeIf { it > 0 }?.toString()
            val albumYearLine = listOfNotNull(albumPart, yearPart).joinToString(" · ")

            binding.playbackSong.apply {
                typeface = Typeface.DEFAULT
                textSize = 19f
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = rawTitle
            }
            binding.playbackArtist.apply {
                typeface = Typeface.DEFAULT
                textSize = 14f
                defaultArtistTextColors?.let(::setTextColor)
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = artistPart
            }
            binding.playbackMeta.apply {
                typeface = Typeface.DEFAULT
                textSize = 12.5f
                isSingleLine = true
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
                isSelected = false
                text = albumYearLine
                visibility = if (albumYearLine.isNotEmpty()) View.VISIBLE else View.INVISIBLE
            }
        }
        updateLoveButton(song)
        binding.playbackVinylView.submitCover(imageLoader, song.cover, viewLifecycleOwner.lifecycleScope)
        binding.playbackSeekBar?.durationDs = song.durationMs.msToDs()
        refreshMosaicBackdrop()
        updateDynamicCoverAccent(song)
    }

    private fun applyStageVisualMode() {
        val b = binding ?: return
        val vinylMode = uiSettings.playbackVinylMode
        b.playbackVinylView.isVisible = vinylMode
        b.playbackPager.visibility = if (vinylMode) View.INVISIBLE else View.VISIBLE
        b.playbackPager.isUserInputEnabled = !vinylMode
        val activeColor =
            if (uiSettings.enableDynamicCoverAccent) {
                dynamicAccentController.activeAccentArgb.value
            } else {
                null
            } ?: MaterialColors.getColor(
                b.playbackStageToggle,
                androidx.appcompat.R.attr.colorPrimary,
                if (requireContext().isNight) Color.WHITE else Color.BLACK,
            )
        val inactiveColor =
            MaterialColors.getColor(
                b.playbackStageToggle,
                MaterialR.attr.colorOnSurfaceVariant,
                if (requireContext().isNight) Color.LTGRAY else Color.DKGRAY,
            )
        b.playbackStageToggle.iconTint =
            ColorStateList.valueOf(if (vinylMode) activeColor else inactiveColor)
        b.playbackStageToggle.alpha = if (vinylMode) 1.0f else 0.78f
    }

    private fun applyBackdropStyle() {
        val b = binding ?: return
        b.playbackMosaicBackdrop.setAmbientBackdrop(
            enabled = uiSettings.useAmbientPlaybackBackdrop,
            seedArgb = latestCoverSeedArgb,
        )
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
        applyBackdropStyle()
    }

    private fun updateDynamicCoverAccent(song: Song) {
        accentSampleJob?.cancel()
        if (!uiSettings.enableDynamicCoverAccent) {
            dynamicAccentController.clear()
        }
        val cover = song.cover
        if (cover == null) {
            latestCoverSeedArgb = null
            applyBackdropStyle()
            if (!uiSettings.enableDynamicCoverAccent) {
                dynamicAccentController.clear()
            }
            return
        }
        if (!uiSettings.enableDynamicCoverAccent && !uiSettings.useAmbientPlaybackBackdrop) {
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
                    latestCoverSeedArgb = ZuneColorMath.selectVibrantSeed(pixels)
                    applyBackdropStyle()
                    if (uiSettings.enableDynamicCoverAccent) {
                        dynamicAccentController.updateFromPixels(pixels, dark)
                    }
                } else {
                    latestCoverSeedArgb = null
                    applyBackdropStyle()
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
        applyStageVisualMode()
    }

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
                ColorStateList.valueOf(Color.parseColor("#D96B52"))
            } else {
                ColorStateList.valueOf(
                    MaterialColors.getColor(
                        btn,
                        MaterialR.attr.colorOnSurfaceVariant,
                        if (requireContext().isNight) Color.LTGRAY else Color.DKGRAY,
                    )
                )
            }
    }

    private fun updateSleepClockBadge(state: SleepTimerState) {
        val b = binding ?: return
        val clockItem = b.playbackToolbar.menu.findItem(R.id.action_desk_clock) ?: return
        val icon = clockItem.icon?.mutate() ?: return
        val tintColor =
            if (state.mode != SleepTimerMode.OFF) {
                Color.parseColor("#D9B372")
            } else {
                MaterialColors.getColor(
                    b.playbackToolbar,
                    MaterialR.attr.colorOnSurfaceVariant,
                    if (requireContext().isNight) Color.WHITE else Color.BLACK,
                )
            }
        icon.setTint(tintColor)
        clockItem.icon = icon
    }

    private fun updateWaveform(envelope: FloatArray?) {
        requireBinding().playbackSeekBar?.setWaveformEnvelope(envelope)
    }

    private fun updateRepeat(repeatMode: RepeatMode) {
        val repeatButton = requireBinding().playbackRepeat
        repeatButton.isChecked = repeatMode != RepeatMode.NONE
        repeatButton.setIconResource(repeatMode.icon)
    }

    private fun updateSheetExpanded(expanded: Boolean) {
        val b = requireBinding()
        b.playbackMosaicBackdrop.setPlaybackSheetExpanded(expanded)
        b.playbackVinylView.setPlaybackSheetExpanded(expanded)
        b.playbackSeekBar?.setWaveEnabled(playbackModel.isPlaying.value && expanded)
    }

    private fun updatePlaying(isPlaying: Boolean) {
        val b = requireBinding()
        val playPause = b.playbackPlayPause
        playPause.isChecked = isPlaying
        playPause.contentDescription =
            getString(if (isPlaying) R.string.desc_pause else R.string.desc_play)
        b.playbackVinylView.setPlaying(isPlaying)
        b.playbackSeekBar?.setWaveEnabled(
            isPlaying && playbackModel.isPlaybackSheetExpanded.value
        )
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
