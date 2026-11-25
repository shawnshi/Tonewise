/*
 * Copyright (c) 2021 Auxio Project
 * HomeFragment.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.home

import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.os.Bundle
import android.view.LayoutInflater
import android.view.MenuItem
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.view.MenuCompat
import androidx.core.view.isInvisible
import androidx.core.view.isVisible
import androidx.fragment.app.Fragment
import androidx.fragment.app.FragmentManager
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.LifecycleOwner
import androidx.navigation.fragment.findNavController
import androidx.viewpager2.adapter.FragmentStateAdapter
import androidx.viewpager2.widget.ViewPager2
import com.google.android.material.appbar.AppBarLayout
import com.google.android.material.transition.MaterialSharedAxis
import dagger.hilt.android.AndroidEntryPoint
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentHomeBinding
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.detail.Show
import com.shawnshi.tonewise.home.autodj.AutoDjHubFragment
import com.shawnshi.tonewise.home.collection.CollectionHubFragment
import com.shawnshi.tonewise.home.quickplay.QuickplayFragment
import com.shawnshi.tonewise.list.ListViewModel
import com.shawnshi.tonewise.list.SelectionFragment
import com.shawnshi.tonewise.list.menu.Menu
import com.shawnshi.tonewise.music.IndexingState
import com.shawnshi.tonewise.music.MusicType
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.music.PlaylistDecision
import com.shawnshi.tonewise.music.PlaylistMessage
import com.shawnshi.tonewise.playback.PlaybackDecision
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.FadingToolbarOffsetListener
import com.shawnshi.tonewise.ui.ZuneAppBarDrawerSheet
import com.shawnshi.tonewise.util.collect
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.dampen
import com.shawnshi.tonewise.util.navigateSafe
import com.shawnshi.tonewise.util.showToast
import org.oxycblt.musikr.IndexingProgress
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.playlist.m3u.M3U
import timber.log.Timber as L

/**
 * The starting [SelectionFragment] of Auxio. Shows the user's music library and enables navigation
 * to other views.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@AndroidEntryPoint
class HomeFragment : SelectionFragment<FragmentHomeBinding>() {
    override val listModel: ListViewModel by activityViewModels()
    override val musicModel: MusicViewModel by activityViewModels()
    override val playbackModel: PlaybackViewModel by activityViewModels()
    private val homeModel: HomeViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    private var storagePermissionLauncher: ActivityResultLauncher<String>? = null
    private var getContentLauncher: ActivityResultLauncher<String>? = null
    private var pendingImportTarget: Playlist? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (ValueAnimator.areAnimatorsEnabled()) {
            enterTransition = MaterialSharedAxis(MaterialSharedAxis.Z, true)
            returnTransition = MaterialSharedAxis(MaterialSharedAxis.Z, false)
            exitTransition = MaterialSharedAxis(MaterialSharedAxis.Z, true)
            reenterTransition = MaterialSharedAxis(MaterialSharedAxis.Z, false)
        } else {
            enterTransition = null
            returnTransition = null
            exitTransition = null
            reenterTransition = null
        }
    }

    override fun onCreateBinding(inflater: LayoutInflater) = FragmentHomeBinding.inflate(inflater)

    override fun getSelectionToolbar(binding: FragmentHomeBinding) = binding.homeSelectionToolbar

    @SuppressLint("ClickableViewAccessibility")
    override fun onBindingCreated(binding: FragmentHomeBinding, savedInstanceState: Bundle?) {
        super.onBindingCreated(binding, savedInstanceState)

        // Have to set up the permission launcher before the view is shown
        storagePermissionLauncher =
            registerForActivityResult(ActivityResultContracts.RequestPermission()) {
                musicModel.refresh()
            }

        getContentLauncher =
            registerForActivityResult(ActivityResultContracts.GetContent()) { uri ->
                if (uri == null) {
                    L.w("No URI returned from file picker")
                    return@registerForActivityResult
                }

                L.d("Received playlist URI $uri")
                musicModel.importPlaylist(uri, pendingImportTarget)
            }

        // --- UI SETUP ---

        binding.homeAppbar.addOnOffsetChangedListener(
            FadingToolbarOffsetListener(binding.homeToolbar, binding.homeContent)
        )
        binding.homeNormalToolbar.apply {
            setOnMenuItemClickListener(this@HomeFragment)
            MenuCompat.setGroupDividerEnabled(menu, true)
        }

        binding.homePager.apply {
            registerOnPageChangeCallback(
                object : ViewPager2.OnPageChangeCallback() {
                    override fun onPageScrolled(
                        position: Int,
                        positionOffset: Float,
                        positionOffsetPixels: Int,
                    ) {
                        binding.homePanoramaHeader.updateScroll(position, positionOffset)
                    }

                    override fun onPageSelected(position: Int) {
                        homeModel.setPanoramaPage(position)
                        binding.homePanoramaHeader.updateScroll(position, 0f)
                        updateLiftTarget(binding)
                    }
                }
            )

            // ViewPager2 will nominally consume window insets, which will then break the window
            // insets applied to the indexing view before API 30. Fix this by overriding the
            // listener with a non-consuming listener.
            setOnApplyWindowInsetsListener { _, insets -> insets }

            offscreenPageLimit = 1

            dampen()
        }

        binding.homePanoramaHeader.onSelectHub = { index ->
            binding.homePager.setCurrentItem(index, true)
        }

        // Further initialization must be done in the function that also handles
        // re-creating the ViewPager.
        setupPager(binding)

        // --- VIEWMODEL SETUP ---
        collect(homeModel.recreateTabs.flow, ::handleRecreate)
        collect(homeModel.chooseMusicLocations.flow, ::handleChooseFolders)
        collect(homeModel.requestSortDialog.flow, ::handleSortDialog)
        collectImmediately(homeModel.currentTabType, ::updateCurrentTab)
        collect(detailModel.toShow.flow, ::handleShow)
        collect(listModel.menu.flow, ::handleMenu)
        collectImmediately(listModel.selected, ::updateSelection)
        collectImmediately(musicModel.indexingState, ::updateIndexerState)
        collect(musicModel.playlistDecision.flow, ::handlePlaylistDecision)
        collectImmediately(musicModel.playlistMessage.flow, ::handlePlaylistMessage)
        collect(playbackModel.playbackDecision.flow, ::handlePlaybackDecision)
    }

    override fun onDestroyBinding(binding: FragmentHomeBinding) {
        super.onDestroyBinding(binding)
        storagePermissionLauncher = null
        binding.homeNormalToolbar.setOnMenuItemClickListener(null)
    }

    override fun onMenuItemClick(item: MenuItem): Boolean {
        if (super.onMenuItemClick(item)) {
            return true
        }

        return when (item.itemId) {
            // Handle main actions (Search, Settings, About)
            R.id.action_search -> {
                L.d("Navigating to search")
                findNavController().navigateSafe(HomeFragmentDirections.search())
                true
            }
            R.id.action_zune_drawer -> {
                if (parentFragmentManager.findFragmentByTag(ZuneAppBarDrawerSheet.TAG) == null) {
                    ZuneAppBarDrawerSheet().show(parentFragmentManager, ZuneAppBarDrawerSheet.TAG)
                }
                true
            }
            else -> {
                L.w("Unexpected menu item selected")
                false
            }
        }
    }

    private fun setupPager(binding: FragmentHomeBinding) {
        binding.homePager.adapter =
            HomePagerAdapter(childFragmentManager, viewLifecycleOwner)

        val toolbarParams = binding.homeToolbar.layoutParams as AppBarLayout.LayoutParams
        toolbarParams.scrollFlags =
            AppBarLayout.LayoutParams.SCROLL_FLAG_SCROLL or
                AppBarLayout.LayoutParams.SCROLL_FLAG_ENTER_ALWAYS

        val targetPage = homeModel.panoramaPage.value.coerceIn(0, 2)
        binding.homePager.setCurrentItem(targetPage, false)
        binding.homePanoramaHeader.updateScroll(targetPage, 0f)
        updateLiftTarget(binding)
    }

    private fun updateCurrentTab(tabType: MusicType) {
        updateLiftTarget(requireBinding(), tabType)
    }

    private fun updateLiftTarget(
        binding: FragmentHomeBinding,
        tabType: MusicType = homeModel.currentTabType.value,
    ) {
        binding.homeAppbar.liftOnScrollTargetViewId =
            if (homeModel.panoramaPage.value != 1) {
                android.view.View.NO_ID
            } else {
                when (tabType) {
                    MusicType.SONGS -> R.id.home_song_recycler
                    MusicType.ALBUMS -> R.id.home_album_recycler
                    MusicType.ARTISTS -> R.id.home_artist_recycler
                    MusicType.COMPOSERS -> R.id.home_composer_recycler
                    MusicType.CONDUCTORS -> R.id.home_conductor_recycler
                    MusicType.GENRES -> R.id.home_genre_recycler
                    MusicType.PLAYLISTS -> R.id.home_playlist_recycler
                    MusicType.SCENES -> android.view.View.NO_ID
                }
            }
    }

    private fun handleSortDialog(type: MusicType?) {
        if (type == null) return
        val directions =
            when (type) {
                MusicType.SONGS -> HomeFragmentDirections.sortSongs()
                MusicType.ALBUMS -> HomeFragmentDirections.sortAlbums()
                MusicType.ARTISTS -> HomeFragmentDirections.sortArtists()
                MusicType.COMPOSERS -> HomeFragmentDirections.sortComposers()
                MusicType.CONDUCTORS -> HomeFragmentDirections.sortConductors()
                MusicType.GENRES -> HomeFragmentDirections.sortGenres()
                MusicType.PLAYLISTS -> HomeFragmentDirections.sortPlaylists()
                MusicType.SCENES -> null
            }
        if (directions != null) {
            findNavController().navigateSafe(directions)
        }
        homeModel.requestSortDialog.consume()
    }

    private fun handleRecreate(recreate: Unit?) {
        if (recreate == null) return
        L.d("Tabs configuration updated; CollectionHubFragment refreshes pivots in place")
        homeModel.recreateTabs.consume()
    }

    private fun handleChooseFolders(unit: Unit?) {
        if (unit == null) {
            return
        }
        findNavController().navigateSafe(HomeFragmentDirections.chooseLocations())
        homeModel.chooseMusicLocations.consume()
    }

    private fun updateIndexerState(state: IndexingState?) {
        val binding = requireBinding()
        when (state) {
            is IndexingState.Completed -> {
                binding.homeIndexingContainer.isInvisible = state.error == null
                binding.homeIndexingProgress.isInvisible = state.error != null
                binding.homeIndexingError.isInvisible = state.error == null
                if (state.error != null) {
                    binding.homeIndexingContainer.setOnClickListener {
                        findNavController()
                            .navigateSafe(HomeFragmentDirections.reportError(state.error))
                    }
                } else {
                    binding.homeIndexingContainer.setOnClickListener(null)
                }
            }
            is IndexingState.Indexing -> {
                if (state.isBackground) {
                    binding.homeIndexingContainer.isInvisible = true
                    binding.homeIndexingProgress.isInvisible = true
                    binding.homeIndexingError.isInvisible = true
                    return
                }
                binding.homeIndexingContainer.isInvisible = false
                binding.homeIndexingProgress.apply {
                    isInvisible = false
                    when (state.progress) {
                        is IndexingProgress.Songs -> {
                            isIndeterminate = false
                            progress = state.progress.loaded
                            max = state.progress.explored
                        }
                        is IndexingProgress.Indeterminate -> {
                            isIndeterminate = true
                        }
                    }
                }
                binding.homeIndexingError.isInvisible = true
            }
            null -> {
                binding.homeIndexingContainer.isInvisible = true
            }
        }
    }

    private fun handlePlaylistDecision(decision: PlaylistDecision?) {
        if (decision == null) return
        val directions =
            when (decision) {
                is PlaylistDecision.New -> {
                    L.d("Creating new playlist")
                    HomeFragmentDirections.newPlaylist(
                        decision.songs.map { it.uid }.toTypedArray(),
                        decision.template,
                        decision.reason,
                    )
                }
                is PlaylistDecision.Import -> {
                    L.d("Importing playlist")
                    pendingImportTarget = decision.target
                    requireNotNull(getContentLauncher) {
                            "Content picker launcher was not available"
                        }
                        .launch(M3U.MIME_TYPE)
                    musicModel.playlistDecision.consume()
                    return
                }
                is PlaylistDecision.Rename -> {
                    L.d("Renaming ${decision.playlist}")
                    HomeFragmentDirections.renamePlaylist(
                        decision.playlist.uid,
                        decision.template,
                        decision.applySongs.map { it.uid }.toTypedArray(),
                        decision.reason,
                    )
                }
                is PlaylistDecision.Export -> {
                    L.d("Exporting ${decision.playlist}")
                    HomeFragmentDirections.exportPlaylist(decision.playlist.uid)
                }
                is PlaylistDecision.Delete -> {
                    L.d("Deleting ${decision.playlist}")
                    HomeFragmentDirections.deletePlaylist(decision.playlist.uid)
                }
                is PlaylistDecision.Add -> {
                    L.d("Adding ${decision.songs.size} to a playlist")
                    HomeFragmentDirections.addToPlaylist(
                        decision.songs.map { it.uid }.toTypedArray()
                    )
                }
            }
        findNavController().navigateSafe(directions)
    }

    private fun handlePlaylistMessage(message: PlaylistMessage?) {
        if (message == null) return
        requireContext().showToast(message.stringRes)
        musicModel.playlistMessage.consume()
    }

    private fun handlePlaybackDecision(decision: PlaybackDecision?) {
        when (decision) {
            is PlaybackDecision.PlayFromArtist -> {
                findNavController()
                    .navigateSafe(HomeFragmentDirections.playFromArtist(decision.song.uid))
            }
            is PlaybackDecision.PlayFromGenre -> {
                findNavController()
                    .navigateSafe(HomeFragmentDirections.playFromGenre(decision.song.uid))
            }
            null -> {}
        }
    }

    private fun handleShow(show: Show?) {
        when (show) {
            is Show.SongDetails -> {
                L.d("Navigating to ${show.song}")
                findNavController().navigateSafe(HomeFragmentDirections.showSong(show.song.uid))
            }
            is Show.SongAlbumDetails -> {
                L.d("Navigating to the album of ${show.song}")
                findNavController()
                    .navigateSafe(HomeFragmentDirections.showAlbum(show.song.album.uid))
            }
            is Show.AlbumDetails -> {
                L.d("Navigating to ${show.album}")
                findNavController().navigateSafe(HomeFragmentDirections.showAlbum(show.album.uid))
            }
            is Show.ArtistDetails -> {
                L.d("Navigating to ${show.artist}")
                findNavController().navigateSafe(HomeFragmentDirections.showArtist(show.artist.uid))
            }
            is Show.SongArtistDecision -> {
                L.d("Navigating to artist choices for ${show.song}")
                findNavController()
                    .navigateSafe(HomeFragmentDirections.showArtistChoices(show.song.uid))
            }
            is Show.AlbumArtistDecision -> {
                L.d("Navigating to artist choices for ${show.album}")
                findNavController()
                    .navigateSafe(HomeFragmentDirections.showArtistChoices(show.album.uid))
            }
            is Show.GenreDetails -> {
                L.d("Navigating to ${show.genre}")
                findNavController().navigateSafe(HomeFragmentDirections.showGenre(show.genre.uid))
            }
            is Show.PlaylistDetails -> {
                L.d("Navigating to ${show.playlist}")
                findNavController()
                    .navigateSafe(HomeFragmentDirections.showPlaylist(show.playlist.uid))
            }
            is Show.SearchQuery -> {
                L.d("Navigating to search for ${show.query}")
                findNavController().navigateSafe(HomeFragmentDirections.search(show.query))
            }
            null -> {}
        }
    }

    private fun handleMenu(menu: Menu?) {
        if (menu == null) return
        val directions =
            when (menu) {
                is Menu.ForSong -> HomeFragmentDirections.openSongMenu(menu.parcel)
                is Menu.ForAlbum -> HomeFragmentDirections.openAlbumMenu(menu.parcel)
                is Menu.ForArtist -> HomeFragmentDirections.openArtistMenu(menu.parcel)
                is Menu.ForGenre -> HomeFragmentDirections.openGenreMenu(menu.parcel)
                is Menu.ForPlaylist -> HomeFragmentDirections.openPlaylistMenu(menu.parcel)
                is Menu.ForSelection -> HomeFragmentDirections.openSelectionMenu(menu.parcel)
            }
        findNavController().navigateSafe(directions)
    }

    private fun updateSelection(selected: List<Music>) {
        val binding = requireBinding()
        if (selected.isNotEmpty()) {
            binding.homeSelectionToolbar.title = getString(R.string.fmt_selected, selected.size)
            if (binding.homeToolbar.setVisible(R.id.home_selection_toolbar)) {
                // New selection started, show the AppBarLayout to indicate the new state.
                L.d("Significant selection occurred, expanding AppBar")
                binding.homeAppbar.expandWithScrollingRecycler()
            }
        } else {
            binding.homeToolbar.setVisible(R.id.home_normal_toolbar)
        }
    }

    /**
     * [FragmentStateAdapter] for Zune Panorama Level-1 hubs (`quickplay · collection · autodj`).
     */
    private class HomePagerAdapter(
        fragmentManager: FragmentManager,
        lifecycleOwner: LifecycleOwner,
    ) : FragmentStateAdapter(fragmentManager, lifecycleOwner.lifecycle) {
        override fun getItemCount() = 3

        override fun createFragment(position: Int): Fragment =
            when (position) {
                0 -> QuickplayFragment()
                1 -> CollectionHubFragment()
                else -> AutoDjHubFragment()
            }
    }
}
