/*
 * Copyright (c) 2026 Tonewise Project
 * PersonListFragment.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.list

import android.os.Bundle
import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.core.view.isInvisible
import androidx.core.view.isVisible
import androidx.fragment.app.activityViewModels
import androidx.recyclerview.widget.ItemTouchHelper
import androidx.recyclerview.widget.RecyclerView
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.flow.StateFlow
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.databinding.FragmentHomeListBinding
import com.shawnshi.tonewise.detail.DetailViewModel
import com.shawnshi.tonewise.home.HomeViewModel
import com.shawnshi.tonewise.list.ListFragment
import com.shawnshi.tonewise.list.ListViewModel
import com.shawnshi.tonewise.list.SelectableListListener
import com.shawnshi.tonewise.list.adapter.SelectionIndicatorAdapter
import com.shawnshi.tonewise.list.adapter.UpdateInstructions
import com.shawnshi.tonewise.list.recycler.ArtistViewHolder
import com.shawnshi.tonewise.list.recycler.FastScrollRecyclerView
import com.shawnshi.tonewise.list.sort.Sort
import com.shawnshi.tonewise.music.IndexingState
import com.shawnshi.tonewise.music.MusicViewModel
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.formatDurationMsPopup
import com.shawnshi.tonewise.util.Event
import com.shawnshi.tonewise.util.collectImmediately
import com.shawnshi.tonewise.util.positiveOrNull
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Music
import org.oxycblt.musikr.MusicParent
import org.oxycblt.musikr.Song

/**
 * Shared [ListFragment] base for Composer and Conductor tabs supporting sort modes and long-press
 * drag-and-drop manual reordering persisted across sessions.
 */
abstract class PersonListFragment :
    ListFragment<Artist, FragmentHomeListBinding>(),
    FastScrollRecyclerView.PopupProvider,
    FastScrollRecyclerView.Listener {
    protected val homeModel: HomeViewModel by activityViewModels()
    private val detailModel: DetailViewModel by activityViewModels()
    override val listModel: ListViewModel by activityViewModels()
    override val musicModel: MusicViewModel by activityViewModels()
    override val playbackModel: PlaybackViewModel by activityViewModels()
    private val personAdapter = PersonAdapter(this)
    private var touchHelper: ItemTouchHelper? = null

    protected abstract val recyclerViewId: Int
    protected abstract val emptyDescriptionRes: Int
    protected abstract val personFlow: StateFlow<List<Artist>>
    protected abstract val instructionsEvent: Event<UpdateInstructions>
    protected abstract val activeSort: Sort
    protected abstract val hasCustomOrder: Boolean

    protected abstract fun commitPersonOrder(items: List<Artist>)

    override fun onCreateBinding(inflater: LayoutInflater) =
        FragmentHomeListBinding.inflate(inflater)

    override fun onBindingCreated(binding: FragmentHomeListBinding, savedInstanceState: Bundle?) {
        super.onBindingCreated(binding, savedInstanceState)

        binding.homeRecycler.apply {
            id = recyclerViewId
            adapter = personAdapter
            popupProvider = this@PersonListFragment
            listener = this@PersonListFragment
            touchHelper =
                ItemTouchHelper(PersonDragCallback(personAdapter, ::commitPersonOrder)).also {
                    it.attachToRecyclerView(this)
                }
        }

        binding.homeNoMusicPlaceholder.apply {
            setImageResource(R.drawable.ic_artist_48)
            contentDescription = getString(emptyDescriptionRes)
        }
        binding.homeNoMusicMsg.text = getString(R.string.lng_empty_artists)
        binding.homeNoMusicAction.setOnClickListener { homeModel.startChooseMusicLocations() }

        collectImmediately(personFlow, ::updatePeople)
        collectImmediately(
            personFlow,
            homeModel.empty,
            musicModel.indexingState,
            ::updateNoMusicIndicator,
        )
        collectImmediately(listModel.selected, ::updateSelection)
        collectImmediately(
            playbackModel.song,
            playbackModel.parent,
            playbackModel.isPlaying,
            ::updatePlayback,
        )
    }

    override fun onDestroyBinding(binding: FragmentHomeListBinding) {
        super.onDestroyBinding(binding)
        touchHelper?.attachToRecyclerView(null)
        touchHelper = null
        binding.homeRecycler.apply {
            adapter = null
            popupProvider = null
            listener = null
        }
    }

    override fun getPopupData(pos: Int): FastScrollRecyclerView.PopupProvider.PopupData? {
        val person = personFlow.value.getOrNull(pos) ?: return null
        if (hasCustomOrder) {
            return FastScrollRecyclerView.PopupProvider.PopupData("#${pos + 1}")
        }
        return when (activeSort.mode) {
            is Sort.Mode.ByName ->
                FastScrollRecyclerView.PopupProvider.PopupData(person.name.thumb() ?: "?")
            is Sort.Mode.ByDuration ->
                person.durationMs?.formatDurationMsPopup()?.let {
                    FastScrollRecyclerView.PopupProvider.PopupData(it)
                }
            is Sort.Mode.ByCount ->
                person.songs.size.positiveOrNull()?.toString()?.let {
                    FastScrollRecyclerView.PopupProvider.PopupData(it)
                }
            else -> null
        }
    }

    override fun onFastScrollingChanged(isFastScrolling: Boolean) {
        homeModel.setFastScrolling(isFastScrolling)
    }

    override fun onRealClick(item: Artist) {
        detailModel.showArtist(item)
    }

    override fun onOpenMenu(item: Artist) {
        listModel.openMenu(R.menu.parent, item)
    }

    private fun updatePeople(people: List<Artist>) {
        personAdapter.update(people, instructionsEvent.consume())
    }

    private fun updateNoMusicIndicator(
        people: List<Artist>,
        libraryEmpty: Boolean,
        indexingState: IndexingState?,
    ) {
        val binding = requireBinding()
        val showEmpty = people.isEmpty()
        binding.homeRecycler.isInvisible = showEmpty
        binding.homeNoMusic.isInvisible = !showEmpty
        binding.homeNoMusicAction.isVisible =
            indexingState == null || (libraryEmpty && indexingState is IndexingState.Completed)
    }

    private fun updateSelection(selection: List<Music>) {
        personAdapter.setSelected(selection.filterIsInstanceTo(mutableSetOf()))
    }

    private fun updatePlayback(song: Song?, parent: MusicParent?, isPlaying: Boolean) {
        val activeParent =
            (parent as? Artist)?.takeIf { p ->
                personAdapter.currentList.any { it.uid == p.uid } ||
                    (song?.artists?.contains(p) == true)
            }
        personAdapter.setPlaying(activeParent, isPlaying)
    }

    private class PersonAdapter(private val listener: SelectableListListener<Artist>) :
        SelectionIndicatorAdapter<Artist, ArtistViewHolder>(ArtistViewHolder.DIFF_CALLBACK) {

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int) =
            ArtistViewHolder.from(parent)

        override fun onBindViewHolder(holder: ArtistViewHolder, position: Int) {
            holder.bind(getItem(position), listener)
            // Long-press initiates drag-and-drop manual reordering instead of multi-select.
            holder.itemView.setOnLongClickListener(null)
            holder.itemView.isLongClickable = false
        }

        fun moveLocalItem(from: Int, to: Int) {
            val updated = currentList.toMutableList()
            val moved = updated.removeAt(from)
            updated.add(to, moved)
            update(updated, UpdateInstructions.Move(from, to))
        }
    }

    private class PersonDragCallback(
        private val adapter: PersonAdapter,
        private val onCommit: (List<Artist>) -> Unit,
    ) : ItemTouchHelper.Callback() {
        private var movedDuringDrag = false

        override fun isLongPressDragEnabled(): Boolean = true

        override fun isItemViewSwipeEnabled(): Boolean = false

        override fun getMovementFlags(
            recyclerView: RecyclerView,
            viewHolder: RecyclerView.ViewHolder,
        ) = makeFlag(ItemTouchHelper.ACTION_STATE_DRAG, ItemTouchHelper.UP or ItemTouchHelper.DOWN)

        override fun onSelectedChanged(viewHolder: RecyclerView.ViewHolder?, actionState: Int) {
            super.onSelectedChanged(viewHolder, actionState)
            if (actionState == ItemTouchHelper.ACTION_STATE_DRAG && viewHolder != null) {
                movedDuringDrag = false
                viewHolder.itemView.animate()
                    .scaleX(1.02f)
                    .scaleY(1.02f)
                    .alpha(0.92f)
                    .setDuration(120)
                    .start()
            }
        }

        override fun onMove(
            recyclerView: RecyclerView,
            viewHolder: RecyclerView.ViewHolder,
            target: RecyclerView.ViewHolder,
        ): Boolean {
            val from = viewHolder.bindingAdapterPosition
            val to = target.bindingAdapterPosition
            if (from == RecyclerView.NO_POSITION || to == RecyclerView.NO_POSITION || from == to) {
                return false
            }
            adapter.moveLocalItem(from, to)
            movedDuringDrag = true
            return true
        }

        override fun clearView(recyclerView: RecyclerView, viewHolder: RecyclerView.ViewHolder) {
            super.clearView(recyclerView, viewHolder)
            viewHolder.itemView.animate().scaleX(1f).scaleY(1f).alpha(1f).setDuration(120).start()
            if (movedDuringDrag) {
                movedDuringDrag = false
                onCommit(adapter.currentList)
            }
        }

        override fun onSwiped(viewHolder: RecyclerView.ViewHolder, direction: Int) = Unit
    }
}

@AndroidEntryPoint
class ComposerListFragment : PersonListFragment() {
    override val recyclerViewId = R.id.home_composer_recycler
    override val emptyDescriptionRes = R.string.lbl_composers
    override val personFlow
        get() = homeModel.composerList
    override val instructionsEvent
        get() = homeModel.composerInstructions
    override val activeSort
        get() = homeModel.composerSort
    override val hasCustomOrder
        get() = homeModel.hasCustomComposerOrder

    override fun commitPersonOrder(items: List<Artist>) {
        homeModel.commitComposerOrder(items)
    }
}

@AndroidEntryPoint
class ConductorListFragment : PersonListFragment() {
    override val recyclerViewId = R.id.home_conductor_recycler
    override val emptyDescriptionRes = R.string.lbl_conductors
    override val personFlow
        get() = homeModel.conductorList
    override val instructionsEvent
        get() = homeModel.conductorInstructions
    override val activeSort
        get() = homeModel.conductorSort
    override val hasCustomOrder
        get() = homeModel.hasCustomConductorOrder

    override fun commitPersonOrder(items: List<Artist>) {
        homeModel.commitConductorOrder(items)
    }
}
