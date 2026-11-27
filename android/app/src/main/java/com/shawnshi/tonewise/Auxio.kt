/*
 * Copyright (c) 2021 Auxio Project
 * Auxio.kt is part of Auxio.
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
 
package com.shawnshi.tonewise

import android.app.Application
import android.content.Intent
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import dagger.hilt.android.HiltAndroidApp
import javax.inject.Inject
import com.shawnshi.tonewise.home.HomeSettings
import com.shawnshi.tonewise.home.quickplay.QuickplayPinStore
import com.shawnshi.tonewise.image.ImageSettings
import com.shawnshi.tonewise.playback.PlaybackSettings
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.CopyleftNoticeTree
import timber.log.Timber

/**
 * A simple, rational music player for android.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
@HiltAndroidApp
class Auxio : Application() {
    @Inject lateinit var imageSettings: ImageSettings
    @Inject lateinit var playbackSettings: PlaybackSettings
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var homeSettings: HomeSettings
    @Inject lateinit var quickplayPinStore: QuickplayPinStore
    @Inject lateinit var playCountRepository: PlayCountRepository

    override fun onCreate() {
        super.onCreate()
        // Pre-warm Room-backed Quickplay stores off the main thread before UI inflates.
        quickplayPinStore.ensureLoaded()
        playCountRepository.ensureLoaded()
        @Suppress("KotlinConstantConditions")
        if (
            BuildConfig.APPLICATION_ID != "com.shawnshi.tonewise" &&
                BuildConfig.APPLICATION_ID != "com.shawnshi.tonewise.debug"
        ) {
            Timber.plant(CopyleftNoticeTree())
        } else if (BuildConfig.DEBUG) {
            Timber.plant(Timber.DebugTree())
        }

        // Migrate any settings that may have changed in an app update.
        imageSettings.migrate()
        playbackSettings.migrate()
        uiSettings.migrate()
        homeSettings.migrate()
        // Adding static shortcuts in a dynamic manner is better than declaring them
        // manually, as it will properly handle the difference between debug and release
        // Auxio instances.
        // TODO: Switch to static shortcuts
        ShortcutManagerCompat.addDynamicShortcuts(
            this,
            listOf(
                ShortcutInfoCompat.Builder(this, SHORTCUT_SHUFFLE_ID)
                    .setShortLabel(getString(R.string.lbl_shuffle_shortcut_short))
                    .setLongLabel(getString(R.string.lbl_shuffle_shortcut_long))
                    .setIcon(IconCompat.createWithResource(this, R.drawable.ic_shortcut_shuffle_24))
                    .setIntent(
                        Intent(this, MainActivity::class.java)
                            .setAction(INTENT_KEY_SHORTCUT_SHUFFLE)
                    )
                    .build()
            ),
        )
    }

    companion object {
        /** The [Intent] name for the "Shuffle All" shortcut. */
        const val INTENT_KEY_SHORTCUT_SHUFFLE = BuildConfig.APPLICATION_ID + ".action.SHUFFLE_ALL"
        /** The ID of the "Shuffle All" shortcut. */
        private const val SHORTCUT_SHUFFLE_ID = "shortcut_shuffle"
    }
}
