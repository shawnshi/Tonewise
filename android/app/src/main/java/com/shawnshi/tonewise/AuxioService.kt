/*
 * Copyright (c) 2024 Auxio Project
 * AuxioService.kt is part of Auxio.
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
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.support.v4.media.MediaBrowserCompat.MediaItem
import androidx.annotation.StringRes
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.media.MediaBrowserServiceCompat
import androidx.media.utils.MediaConstants
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import com.shawnshi.tonewise.music.service.MusicServiceFragment
import com.shawnshi.tonewise.playback.service.PlaybackServiceFragment
import timber.log.Timber

@AndroidEntryPoint
class AuxioService :
    MediaBrowserServiceCompat(), ForegroundListener, MusicServiceFragment.Invalidator {
    @Inject lateinit var playbackFragmentFactory: PlaybackServiceFragment.Factory
    private lateinit var playbackFragment: PlaybackServiceFragment

    @Inject lateinit var musicFragmentFactory: MusicServiceFragment.Factory
    private lateinit var musicFragment: MusicServiceFragment
    private val mainHandler = Handler(Looper.getMainLooper())

    @SuppressLint("WrongConstant")
    override fun onCreate() {
        super.onCreate()
        playbackFragment = playbackFragmentFactory.create(this, this)
        musicFragment = musicFragmentFactory.create(this, this, this)
        sessionToken = playbackFragment.attach()
        musicFragment.attach()
        Timber.d("Service Created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        onHandleForeground(intent)
        val startExtra = intent?.getIntExtra(INTENT_KEY_START_ID, -1)
        if ((startExtra == IntegerTable.START_ID_MEDIA_BUTTON ||
                startExtra == IntegerTable.START_ID_TASKER) && !isForeground) {
            // Guarantees startForeground() completes synchronously within Android's 5s deadline.
            startForegroundCompat(playbackFragment.fallbackNotification)
            isForeground = true
            mainHandler.postDelayed(
                {
                    if (!playbackFragment.isSessionOngoing) {
                        updateForeground(ForegroundListener.Change.MEDIA_SESSION)
                    }
                },
                2500L,
            )
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent): IBinder? {
        val binder = super.onBind(intent)
        onHandleForeground(intent)
        return binder
    }

    private fun onHandleForeground(intent: Intent?) {
        musicFragment.start()
        playbackFragment.start(intent)
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        super.onTaskRemoved(rootIntent)
        playbackFragment.handleTaskRemoved()
    }

    override fun onDestroy() {
        mainHandler.removeCallbacksAndMessages(null)
        isForeground = false
        super.onDestroy()
        musicFragment.release()
        playbackFragment.release()
    }

    override fun onGetRoot(
        clientPackageName: String,
        clientUid: Int,
        rootHints: Bundle?,
    ): BrowserRoot {
        return musicFragment.getRoot()
    }

    override fun onLoadItem(itemId: String, result: Result<MediaItem>) {
        musicFragment.getItem(itemId, result)
    }

    override fun onLoadChildren(parentId: String, result: Result<MutableList<MediaItem>>) {
        val maximumRootChildLimit = getRootChildrenLimit()
        musicFragment.getChildren(parentId, maximumRootChildLimit, result)
    }

    override fun onLoadChildren(
        parentId: String,
        result: Result<MutableList<MediaItem>>,
        options: Bundle,
    ) {
        val maximumRootChildLimit = getRootChildrenLimit()
        musicFragment.getChildren(parentId, maximumRootChildLimit, result)
    }

    override fun onSearch(query: String, extras: Bundle?, result: Result<MutableList<MediaItem>>) {
        musicFragment.search(query, result)
    }

    private fun getRootChildrenLimit(): Int {
        return browserRootHints?.getInt(
            MediaConstants.BROWSER_ROOT_HINTS_KEY_ROOT_CHILDREN_LIMIT,
            4,
        ) ?: 4
    }

    private fun startForegroundCompat(notification: ForegroundServiceNotification) {
        val type =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            } else {
                0
            }
        ServiceCompat.startForeground(this, notification.code, notification.build(), type)
    }

    override fun updateForeground(change: ForegroundListener.Change) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post { updateForeground(change) }
            return
        }
        val mediaNotification = playbackFragment.notification
        if (mediaNotification != null) {
            if (change == ForegroundListener.Change.MEDIA_SESSION || !isForeground) {
                startForegroundCompat(mediaNotification)
            }
            // Nothing changed, but don't show anything music related since we can always
            // index during playback.
            isForeground = true
        } else {
            musicFragment.createNotification {
                if (it != null) {
                    try {
                        startForegroundCompat(it)
                        isForeground = true
                    } catch (e: Exception) {
                        Timber.w(e, "Unable to promote indexer foreground notification")
                        val canNotify =
                            Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                                ContextCompat.checkSelfPermission(
                                    this,
                                    Manifest.permission.POST_NOTIFICATIONS,
                                ) == PackageManager.PERMISSION_GRANTED
                        if (canNotify) {
                            try {
                                NotificationManagerCompat.from(this).notify(it.code, it.build())
                            } catch (_: SecurityException) {}
                        }
                    }
                } else {
                    ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    isForeground = false
                    stopSelf()
                }
            }
        }
    }

    override fun invalidateMusic(mediaId: String) {
        notifyChildrenChanged(mediaId)
    }

    companion object {
        const val ACTION_START = BuildConfig.APPLICATION_ID + ".service.START"

        @Volatile
        var isForeground = false
            private set

        // This is only meant for Auxio to internally ensure that it's state management will work.
        const val INTENT_KEY_START_ID = BuildConfig.APPLICATION_ID + ".service.START_ID"
    }
}

interface ForegroundListener {
    fun updateForeground(change: Change)

    enum class Change {
        MEDIA_SESSION,
        INDEXER,
    }
}

/**
 * Wrapper around [NotificationCompat.Builder] intended for use for [NotificationCompat]s that
 * signal a Service's ongoing foreground state.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
abstract class ForegroundServiceNotification(context: Context, info: ChannelInfo) :
    NotificationCompat.Builder(context, info.id) {
    private val notificationManager = NotificationManagerCompat.from(context)

    init {
        // Set up the notification channel. Foreground notifications are non-substantial, and
        // thus make no sense to have lights, vibration, or lead to a notification badge.
        val channel =
            NotificationChannelCompat.Builder(info.id, NotificationManagerCompat.IMPORTANCE_LOW)
                .setName(context.getString(info.nameRes))
                .setLightsEnabled(false)
                .setVibrationEnabled(false)
                .setShowBadge(false)
                .build()
        notificationManager.createNotificationChannel(channel)
    }

    /**
     * The code used to identify this notification.
     *
     * @see NotificationManagerCompat.notify
     */
    abstract val code: Int

    /**
     * Reduced representation of a [NotificationChannelCompat].
     *
     * @param id The ID of the channel.
     * @param nameRes A string resource ID corresponding to the human-readable name of this channel.
     */
    data class ChannelInfo(val id: String, @StringRes val nameRes: Int)
}
