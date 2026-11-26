/*
 * Copyright (c) 2023 Auxio Project
 * HomeSettings.kt is part of Auxio.
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

import android.content.Context
import androidx.core.content.edit
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.settings.Settings
import timber.log.Timber as L

/**
 * User configuration specific to the home UI.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
interface HomeSettings : Settings<HomeSettings.Listener> {
    /** Whether to hide artists considered "collaborators" from the home UI. */
    val shouldHideCollaborators: Boolean

    interface Listener {
        /** Called when the [shouldHideCollaborators] configuration changes. */
        fun onHideCollaboratorsChanged() {}
    }
}

class HomeSettingsImpl @Inject constructor(@ApplicationContext context: Context) :
    Settings.Impl<HomeSettings.Listener>(context), HomeSettings {
    override val shouldHideCollaborators: Boolean
        get() = sharedPreferences.getBoolean(getString(R.string.set_key_hide_collaborators), false)

    override fun migrate() {
        val currentTabsKey = getString(R.string.set_key_home_tabs)
        if (
            sharedPreferences.contains(OLD_KEY_LIB_TABS) ||
                sharedPreferences.contains(currentTabsKey)
        ) {
            L.d("Removing obsolete library tab preferences")
            sharedPreferences.edit {
                remove(OLD_KEY_LIB_TABS)
                remove(currentTabsKey)
            }
        }
    }

    override fun onSettingChanged(key: String, listener: HomeSettings.Listener) {
        if (key == getString(R.string.set_key_hide_collaborators)) {
            L.d("Dispatching collaborator setting change")
            listener.onHideCollaboratorsChanged()
        }
    }

    companion object {
        const val OLD_KEY_LIB_TABS = "auxio_lib_tabs"
    }
}
