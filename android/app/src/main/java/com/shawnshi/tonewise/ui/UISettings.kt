/*
 * Copyright (c) 2023 Auxio Project
 * UISettings.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.ui

import android.content.Context
import android.os.Build
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.settings.Settings
import com.shawnshi.tonewise.ui.accent.Accent
import timber.log.Timber as L

/**
 * User configuration for the general app UI.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
interface UISettings : Settings<UISettings.Listener> {
    /** The current theme. Represented by the AppCompatDelegate constants. */
    val theme: Int
    /** Whether to use a black background when a dark theme is currently used. */
    val useBlackTheme: Boolean
    /** The current [Accent] (Color Scheme). */
    var accent: Accent
    /** Whether to round additional UI elements that require album covers to be rounded. */
    val roundMode: Boolean
    /** Whether to dynamically derive a high-contrast accent tint from the playing album cover. */
    val enableDynamicCoverAccent: Boolean
    /** Whether Now Playing displays the vinyl turntable stage instead of the cover pager. */
    var playbackVinylMode: Boolean
    /** Whether Now Playing uses a soft cover-derived radial glow instead of the album mosaic. */
    val useAmbientPlaybackBackdrop: Boolean

    interface Listener {
        /** Called when [roundMode] changes. */
        fun onRoundModeChanged()
        /** Called when [enableDynamicCoverAccent] changes. */
        fun onDynamicCoverAccentChanged() {}
        /** Called when [useAmbientPlaybackBackdrop] changes. */
        fun onPlaybackBackdropChanged() {}
    }
}

class UISettingsImpl @Inject constructor(@ApplicationContext context: Context) :
    Settings.Impl<UISettings.Listener>(context), UISettings {
    override val theme: Int
        get() =
            sharedPreferences.getInt(
                getString(R.string.set_key_theme),
                AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM,
            )

    override val useBlackTheme: Boolean
        get() = sharedPreferences.getBoolean(getString(R.string.set_key_black_theme), true)

    override var accent: Accent
        get() =
            Accent.from(
                sharedPreferences.getInt(getString(R.string.set_key_accent), Accent.DEFAULT)
            )
        set(value) {
            sharedPreferences.edit {
                putInt(getString(R.string.set_key_accent), value.index)
                apply()
            }
        }

    override val roundMode: Boolean
        get() = sharedPreferences.getBoolean(getString(R.string.set_key_round_mode), false)

    override val enableDynamicCoverAccent: Boolean
        get() =
            sharedPreferences.getBoolean(
                getString(R.string.set_key_dynamic_cover_accent),
                false,
            )

    override var playbackVinylMode: Boolean
        get() =
            sharedPreferences.getBoolean(
                getString(R.string.set_key_playback_vinyl_mode),
                true,
            )
        set(value) {
            sharedPreferences.edit {
                putBoolean(getString(R.string.set_key_playback_vinyl_mode), value)
                apply()
            }
        }

    override val useAmbientPlaybackBackdrop: Boolean
        get() =
            sharedPreferences.getBoolean(
                getString(R.string.set_key_playback_ambient_backdrop),
                true,
            )

    override fun migrate() {
        if (sharedPreferences.contains(OLD_KEY_ACCENT3)) {
            L.d("Migrating $OLD_KEY_ACCENT3")

            var accent = sharedPreferences.getInt(OLD_KEY_ACCENT3, 5)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                // Accents were previously frozen as soon as the OS was updated to android
                // twelve, as dynamic colors were enabled by default. This is no longer the
                // case, so we need to re-update the setting to dynamic colors here.
                accent = Accent.INDEX_DYNAMIC
            }

            sharedPreferences.edit {
                putInt(getString(R.string.set_key_accent), accent)
                remove(OLD_KEY_ACCENT3)
                apply()
            }
        }
    }

    override fun onSettingChanged(key: String, listener: UISettings.Listener) {
        if (key == getString(R.string.set_key_round_mode)) {
            L.d("Dispatching round mode setting change")
            listener.onRoundModeChanged()
        } else if (key == getString(R.string.set_key_dynamic_cover_accent)) {
            L.d("Dispatching dynamic cover accent setting change")
            listener.onDynamicCoverAccentChanged()
        } else if (key == getString(R.string.set_key_playback_ambient_backdrop)) {
            L.d("Dispatching playback ambient backdrop setting change")
            listener.onPlaybackBackdropChanged()
        }
    }

    private companion object {
        const val OLD_KEY_ACCENT3 = "auxio_accent"
    }
}
