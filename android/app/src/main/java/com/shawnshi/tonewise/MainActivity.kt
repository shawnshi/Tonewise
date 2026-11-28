/*
 * Copyright (c) 2021 Auxio Project
 * MainActivity.kt is part of Auxio.
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

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.view.View
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.updatePadding
import androidx.lifecycle.lifecycleScope
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.launch
import com.shawnshi.tonewise.databinding.ActivityMainBinding
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.playback.state.DeferredPlayback
import com.shawnshi.tonewise.sync.LastFmAuthCallbackParser
import com.shawnshi.tonewise.sync.LastFmRepository
import com.shawnshi.tonewise.ui.UISettings
import com.shawnshi.tonewise.util.isNight
import com.shawnshi.tonewise.util.showToast
import com.shawnshi.tonewise.util.systemBarInsetsCompat
import timber.log.Timber as L

/**
 * Auxio's single [AppCompatActivity].
 *
 * @author Alexander Capehart (OxygenCobalt)
 *
 * TODO: Add error screens
 * TODO: Custom language support
 * TODO: Use proper material attributes (Not the weird dimen attributes I currently have)
 * TODO: Migrate to material animation system
 * TODO: Unit testing
 * TODO: Fix UID naming
 * TODO: Leverage FlexibleListAdapter more in dialogs (Disable item anims)
 * TODO: Improve multi-threading support in shared objects
 */
@AndroidEntryPoint
class MainActivity : AppCompatActivity() {
    private val playbackModel: PlaybackViewModel by viewModels()
    @Inject lateinit var uiSettings: UISettings
    @Inject lateinit var lastFmRepository: LastFmRepository
    private var isServiceBound = false
    private var notificationPermissionLauncher: ActivityResultLauncher<String>? = null
    private val serviceConnection =
        object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName?, service: IBinder?) {}

            override fun onServiceDisconnected(name: ComponentName?) {}
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        notificationPermissionLauncher =
            registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
                if (granted) {
                    ensurePlaybackForegroundIfActive()
                }
            }
        setupTheme()
        // Inflate the views after setting up the theme so that the theme attributes are applied.
        val binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        setupEdgeToEdge(binding.root)
        maybeRequestPostNotificationsPermission()
        L.d("Activity created")
    }

    private fun maybeRequestPostNotificationsPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        val permission = Manifest.permission.POST_NOTIFICATIONS
        if (
            ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        val prefs = getSharedPreferences("tonewise_runtime_perms", Context.MODE_PRIVATE)
        if (prefs.getBoolean(KEY_ASKED_POST_NOTIFICATIONS, false)) {
            return
        }
        prefs.edit().putBoolean(KEY_ASKED_POST_NOTIFICATIONS, true).apply()
        runCatching { notificationPermissionLauncher?.launch(permission) }
    }

    override fun onStart() {
        super.onStart()
        val bindIntent =
            Intent(this, AuxioService::class.java)
                .setAction(AuxioService.ACTION_BIND_LOCAL)
                .putExtra(AuxioService.INTENT_KEY_START_ID, IntegerTable.START_ID_ACTIVITY)
        runCatching {
            if (bindService(bindIntent, serviceConnection, Context.BIND_AUTO_CREATE)) {
                isServiceBound = true
            }
        }
    }

    override fun onStop() {
        if (isServiceBound) {
            isServiceBound = false
            runCatching { unbindService(serviceConnection) }
        }
        super.onStop()
    }

    override fun onResume() {
        super.onResume()

        if (!startIntentAction(intent)) {
            // No intent action to do, just restore the previously saved state.
            playbackModel.playDeferred(DeferredPlayback.RestoreState(false))
        }
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        ensurePlaybackForegroundIfActive()
    }

    override fun onPause() {
        ensurePlaybackForegroundIfActive()
        super.onPause()
    }

    private fun ensurePlaybackForegroundIfActive() {
        if (
            playbackModel.song.value != null &&
                playbackModel.isPlaying.value &&
                !AuxioService.isForeground
        ) {
            val startIntent =
                Intent(this, AuxioService::class.java)
                    .setAction(AuxioService.ACTION_START)
                    .putExtra(AuxioService.INTENT_KEY_START_ID, IntegerTable.START_ID_ACTIVITY)
                    .putExtra(AuxioService.EXTRA_FORCE_FOREGROUND, true)
            runCatching { ContextCompat.startForegroundService(this, startIntent) }
                .onFailure { runCatching { startService(startIntent) } }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        startIntentAction(intent)
    }

    private fun setupTheme() {
        // Apply the theme configuration.
        AppCompatDelegate.setDefaultNightMode(uiSettings.theme)
        // Apply the color scheme. The black theme requires it's own set of themes since
        // it's not possible to modify the themes at run-time.
        if (isNight && uiSettings.useBlackTheme) {
            L.d("Applying black theme [accent ${uiSettings.accent}]")
            setTheme(uiSettings.accent.blackTheme)
        } else {
            L.d("Applying normal theme [accent ${uiSettings.accent}]")
            setTheme(uiSettings.accent.theme)
        }
        if (!uiSettings.roundMode) {
            theme.applyStyle(R.style.ThemeOverlay_Tonewise_Rectilinear, true)
        }
    }

    private fun setupEdgeToEdge(contentView: View) {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        contentView.setOnApplyWindowInsetsListener { view, insets ->
            // Automatically inset the view to the left/right, as component support for
            // these insets are highly lacking.
            val bars = insets.systemBarInsetsCompat
            view.updatePadding(left = bars.left, right = bars.right)
            insets
        }
    }

    /**
     * Transform an [Intent] given to [MainActivity] into a [DeferredPlayback] that can be used in
     * the playback system.
     *
     * @param intent The (new) [Intent] given to this [MainActivity], or null if there is no intent.
     * @return true If the analogous [DeferredPlayback] to the given [Intent] was started, false
     *   otherwise.
     */
    private fun startIntentAction(intent: Intent?): Boolean {
        if (intent == null) {
            // Nothing to do.
            L.d("No intent to handle")
            return false
        }

        if (intent.getBooleanExtra(KEY_INTENT_USED, false)) {
            // Don't commit the action, but also return that the intent was applied.
            // This is because onStart can run multiple times, and thus we really don't
            // want to return false and override the original delayed action with a
            // RestoreState action.
            L.d("Already used this intent")
            return true
        }
        intent.putExtra(KEY_INTENT_USED, true)

        val uri = intent.data
        if (
            intent.action == Intent.ACTION_VIEW &&
                LastFmAuthCallbackParser.isLastFmCallback(
                    scheme = uri?.scheme,
                    host = uri?.host,
                    expectedScheme = BuildConfig.APPLICATION_ID,
                )
        ) {
            val token =
                uri?.getQueryParameter("token")
                    ?: LastFmAuthCallbackParser.extractCallbackToken(uri?.encodedQuery)
            intent.data = null
            intent.action = null
            lifecycleScope.launch {
                val ok = lastFmRepository.handleBrowserAuthCallback(token)
                if (ok) {
                    showToast(R.string.lbl_lastfm_connected_toast)
                } else {
                    val msg = lastFmRepository.state.value.statusMessage
                    if (msg.isNotBlank()) {
                        android.widget.Toast.makeText(
                                this@MainActivity,
                                msg,
                                android.widget.Toast.LENGTH_LONG,
                            )
                            .show()
                    }
                }
            }
            return true
        }

        val action =
            when (intent.action) {
                Intent.ACTION_VIEW -> DeferredPlayback.Open(intent.data ?: return false)
                Auxio.INTENT_KEY_SHORTCUT_SHUFFLE -> DeferredPlayback.ShuffleAll
                else -> {
                    L.w("Unexpected intent ${intent.action}")
                    return false
                }
            }
        L.d("Translated intent to $action")
        playbackModel.playDeferred(action)
        return true
    }

    private companion object {
        const val KEY_INTENT_USED = BuildConfig.APPLICATION_ID + ".key.FILE_INTENT_USED"
        const val KEY_ASKED_POST_NOTIFICATIONS = "asked_post_notifications"
    }
}
