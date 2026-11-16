/*
 * Copyright (c) 2026 Tonewise Project
 * PlayCountRepository.kt is part of Tonewise.
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
 
package com.shawnshi.tonewise.playback.persist

import android.content.Context
import android.content.SharedPreferences
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.oxycblt.musikr.Song

interface PlayCountRepository {
    val updates: StateFlow<Long>

    fun getPlayCount(song: Song): Int

    fun incrementPlayCount(song: Song): Int
}

@Singleton
class PlayCountRepositoryImpl
@Inject
constructor(@ApplicationContext private val context: Context) : PlayCountRepository {
    private val prefs: SharedPreferences by lazy {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }

    private val _updates = MutableStateFlow(0L)
    override val updates: StateFlow<Long> = _updates

    @Synchronized
    override fun getPlayCount(song: Song): Int = prefs.getInt(song.uid.toString(), 0)

    @Synchronized
    override fun incrementPlayCount(song: Song): Int {
        val key = song.uid.toString()
        val next = prefs.getInt(key, 0) + 1
        prefs.edit { putInt(key, next) }
        _updates.value += 1
        return next
    }

    companion object {
        private const val PREFS_NAME = "tonewise_play_counts"

        fun getPlayCount(context: Context, song: Song): Int =
            context
                .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .getInt(song.uid.toString(), 0)
    }
}
