/*
 * Copyright (c) 2026 Tonewise Project
 * LanSyncForegroundService.kt is part of Tonewise.
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

package com.shawnshi.tonewise.sync

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.shawnshi.tonewise.MainActivity
import com.shawnshi.tonewise.R
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import timber.log.Timber as L

@AndroidEntryPoint
class LanSyncForegroundService : Service() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val jobLock = Any()
    private var activeSyncJob: Job? = null
    private var latestStartId: Int = 0
    private var cancelRequested: Boolean = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        ensureNotificationChannel()
        when (intent?.action) {
            ACTION_CANCEL_SYNC -> {
                val shouldStopNow =
                    synchronized(jobLock) {
                        latestStartId = startId
                        cancelRequested = true
                        activeSyncJob?.isActive != true
                    }
                lanSyncRepository.cancelSync()
                if (shouldStopNow) {
                    val stopId = synchronized(jobLock) { latestStartId }
                    ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    stopSelfResult(stopId)
                }
            }
            ACTION_START_SYNC -> {
                // Must call startForeground before any conditional early exit when started via startForegroundService.
                startForegroundCompat(buildNotification(lanSyncRepository.syncProgress.value))

                val baseUrl = intent.getStringExtra(EXTRA_BASE_URL)?.trim().orEmpty()
                if (baseUrl.isEmpty()) {
                    val stopId =
                        synchronized(jobLock) {
                            latestStartId = startId
                            if (activeSyncJob?.isActive != true) latestStartId else null
                        }
                    if (stopId != null) {
                        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                        stopSelfResult(stopId)
                    }
                    return START_NOT_STICKY
                }

                synchronized(jobLock) {
                    latestStartId = startId
                    if (activeSyncJob?.isActive == true) {
                        return START_NOT_STICKY
                    }
                    cancelRequested = false
                    lateinit var launched: Job
                    launched =
                        serviceScope.launch {
                            val notifManager = getSystemService(NotificationManager::class.java)
                            var lastNotifyMs = 0L
                            var lastPhase = ""
                            val progressJob =
                                launch {
                                    lanSyncRepository.syncProgress.collect { progress ->
                                        val nowMs = android.os.SystemClock.elapsedRealtime()
                                        if (progress.isRunning &&
                                            (progress.phase != lastPhase || nowMs - lastNotifyMs >= 400L)
                                        ) {
                                            lastNotifyMs = nowMs
                                            lastPhase = progress.phase
                                            runCatching {
                                                notifManager?.notify(
                                                    NOTIFICATION_ID,
                                                    buildNotification(progress),
                                                )
                                            }
                                        }
                                    }
                                }
                            try {
                                val abortedEarly = synchronized(jobLock) { cancelRequested }
                                if (!abortedEarly) {
                                    lanSyncRepository.syncWithHost(baseUrl)
                                }
                            } catch (t: Throwable) {
                                L.w(t, "LanSyncForegroundService execution error")
                            } finally {
                                progressJob.cancel()
                                val stopId =
                                    synchronized(jobLock) {
                                        if (activeSyncJob === launched) {
                                            activeSyncJob = null
                                        }
                                        if (activeSyncJob?.isActive != true) latestStartId else null
                                    }
                                if (stopId != null) {
                                    ServiceCompat.stopForeground(
                                        this@LanSyncForegroundService,
                                        ServiceCompat.STOP_FOREGROUND_REMOVE,
                                    )
                                    stopSelfResult(stopId)
                                }
                            }
                        }
                    activeSyncJob = launched
                }
            }
            else -> {
                val stopId =
                    synchronized(jobLock) {
                        latestStartId = startId
                        if (activeSyncJob?.isActive != true) latestStartId else null
                    }
                if (stopId != null) {
                    stopSelfResult(stopId)
                }
            }
        }
        return START_NOT_STICKY
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        L.w("LanSyncForegroundService timed out (startId=$startId, fgsType=$fgsType); cancelling sync")
        val stopId =
            synchronized(jobLock) {
                cancelRequested = true
                if (startId > latestStartId) latestStartId = startId
                latestStartId
            }
        lanSyncRepository.cancelSync()
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelfResult(stopId)
    }

    override fun onDestroy() {
        synchronized(jobLock) {
            cancelRequested = true
            activeSyncJob?.cancel()
            activeSyncJob = null
        }
        serviceScope.cancel()
        super.onDestroy()
    }

    private fun startForegroundCompat(notification: Notification) {
        val type =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
            } else {
                0
            }
        ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, type)
    }

    private fun ensureNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java) ?: return
            if (manager.getNotificationChannel(CHANNEL_ID) == null) {
                val channel =
                    NotificationChannel(
                        CHANNEL_ID,
                        "局域网曲库同步",
                        NotificationManager.IMPORTANCE_LOW,
                    ).apply {
                        description = "显示 Tonewise 电脑端局域网歌曲与标签同步进度"
                    }
                manager.createNotificationChannel(channel)
            }
        }
    }

    private fun buildNotification(progress: SyncProgressState): Notification {
        val contentIntent =
            PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java).apply {
                    flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )

        val cancelIntent =
            PendingIntent.getService(
                this,
                1,
                Intent(this, LanSyncForegroundService::class.java).apply {
                    action = ACTION_CANCEL_SYNC
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )

        val phase = progress.phase.ifBlank { "正在同步电脑端曲库…" }
        val text =
            if (progress.currentTrackTitle.isNotBlank()) {
                "$phase · ${progress.currentTrackTitle}"
            } else {
                phase
            }

        val builder =
            NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_auxio_24)
                .setContentTitle("Tonewise 局域网同步")
                .setContentText(text)
                .setContentIntent(contentIntent)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .addAction(0, "取消同步", cancelIntent)

        if (progress.totalBytes > 0L) {
            val pct =
                ((progress.downloadedBytes * 100L) / progress.totalBytes)
                    .toInt()
                    .coerceIn(0, 100)
            builder.setProgress(100, pct, false)
        } else {
            builder.setProgress(0, 0, true)
        }
        return builder.build()
    }

    companion object {
        const val ACTION_START_SYNC = "com.shawnshi.tonewise.sync.action.START_SYNC"
        const val ACTION_CANCEL_SYNC = "com.shawnshi.tonewise.sync.action.CANCEL_SYNC"
        const val EXTRA_BASE_URL = "extra_base_url"
        private const val CHANNEL_ID = "tonewise_lan_sync"
        private const val NOTIFICATION_ID = 45821

        fun startSync(context: Context, baseUrl: String) {
            val intent =
                Intent(context, LanSyncForegroundService::class.java).apply {
                    action = ACTION_START_SYNC
                    putExtra(EXTRA_BASE_URL, baseUrl)
                }
            ContextCompat.startForegroundService(context, intent)
        }

        fun cancelSync(context: Context) {
            val intent =
                Intent(context, LanSyncForegroundService::class.java).apply {
                    action = ACTION_CANCEL_SYNC
                }
            context.startService(intent)
        }
    }
}
