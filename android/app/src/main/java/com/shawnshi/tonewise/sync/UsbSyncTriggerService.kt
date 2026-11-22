/*
 * Copyright (c) 2026 Tonewise Project
 * UsbSyncTriggerService.kt is part of Tonewise.
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
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
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
class UsbSyncTriggerService : Service() {
    @Inject lateinit var lanSyncRepository: LanSyncRepository

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val jobLock = Any()
    private var activeSyncJob: Job? = null
    private var latestStartId: Int = 0
    private var cancelRequested: Boolean = false
    @Volatile private var lastTriggerElapsedMs: Long = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        ensureNotificationChannel()
        when (intent?.action) {
            ACTION_CANCEL_USB_SYNC -> {
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
            ACTION_RESCAN_LIBRARY -> {
                startForegroundCompat(
                    buildNotification(
                        SyncProgressState(isRunning = true, phase = "正在刷新本地音乐曲库索引…")
                    )
                )
                val nonce = intent.getStringExtra(EXTRA_USB_NONCE)?.trim().orEmpty()
                if (nonce.isNotEmpty()) {
                    lanSyncRepository.requestLibraryRescan()
                }
                val stopId =
                    synchronized(jobLock) {
                        latestStartId = startId
                        if (activeSyncJob?.isActive != true) latestStartId else null
                    }
                if (stopId != null) {
                    ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                    stopSelfResult(stopId)
                }
            }
            ACTION_START_USB_SYNC -> {
                // Always enter foreground before any conditional early exit
                startForegroundCompat(buildNotification(lanSyncRepository.syncProgress.value))

                val rawPort = intent.getIntExtra(EXTRA_USB_PORT, DEFAULT_USB_PORT)
                val port = if (rawPort in 1..65535) rawPort else DEFAULT_USB_PORT
                val nonce = intent.getStringExtra(EXTRA_USB_NONCE)?.trim().orEmpty()
                val hostId = intent.getStringExtra(EXTRA_HOST_ID)?.trim()?.takeIf { it.isNotEmpty() }
                val allowAutoPair = intent.getBooleanExtra(EXTRA_ALLOW_AUTO_PAIR, true)

                if (nonce.isEmpty()) {
                    stopIfIdle(startId)
                    return START_NOT_STICKY
                }

                // Hardcode loopback target so exported ADB trigger can never target arbitrary hosts
                val loopbackBaseUrl = "http://127.0.0.1:$port"
                val nowElapsed = SystemClock.elapsedRealtime()

                synchronized(jobLock) {
                    latestStartId = startId
                    if (activeSyncJob?.isActive == true) {
                        return START_NOT_STICKY
                    }
                    if (nowElapsed - lastTriggerElapsedMs in 0 until TRIGGER_COOLDOWN_MS) {
                        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
                        stopSelfResult(startId)
                        return START_NOT_STICKY
                    }
                    lastTriggerElapsedMs = nowElapsed
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
                                        val nowMs = SystemClock.elapsedRealtime()
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
                                    lanSyncRepository.syncWithHost(
                                        baseUrl = loopbackBaseUrl,
                                        transport = SyncTransportMode.USB,
                                        usbNonce = nonce,
                                        expectedHostId = hostId,
                                        allowUsbAutoPair = allowAutoPair,
                                    )
                                }
                            } catch (t: Throwable) {
                                L.w(t, "UsbSyncTriggerService execution error")
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
                                        this@UsbSyncTriggerService,
                                        ServiceCompat.STOP_FOREGROUND_REMOVE,
                                    )
                                    stopSelfResult(stopId)
                                }
                            }
                        }
                    activeSyncJob = launched
                }
            }
            else -> stopIfIdle(startId)
        }
        return START_NOT_STICKY
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        L.w("UsbSyncTriggerService timed out (startId=$startId, fgsType=$fgsType); cancelling sync")
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

    private fun stopIfIdle(startId: Int) {
        val stopId =
            synchronized(jobLock) {
                latestStartId = startId
                if (activeSyncJob?.isActive != true) latestStartId else null
            }
        if (stopId != null) {
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelfResult(stopId)
        }
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
                        "USB 曲库同步",
                        NotificationManager.IMPORTANCE_LOW,
                    ).apply {
                        description = "显示 Tonewise USB 数据线双向同步与镜像清理进度"
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
                2,
                Intent(this, UsbSyncTriggerService::class.java).apply {
                    action = ACTION_CANCEL_USB_SYNC
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )

        val phase = progress.phase.ifBlank { "正在通过 USB 同步电脑端曲库…" }
        val text =
            if (progress.currentTrackTitle.isNotBlank()) {
                "$phase · ${progress.currentTrackTitle}"
            } else {
                phase
            }

        val builder =
            NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_auxio_24)
                .setContentTitle("Tonewise USB 自动同步")
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
        const val ACTION_START_USB_SYNC = "com.shawnshi.tonewise.sync.action.START_USB_SYNC"
        const val ACTION_RESCAN_LIBRARY = "com.shawnshi.tonewise.sync.action.RESCAN_LIBRARY"
        const val ACTION_CANCEL_USB_SYNC = "com.shawnshi.tonewise.sync.action.CANCEL_USB_SYNC"
        const val EXTRA_USB_PORT = "extra_usb_port"
        const val EXTRA_USB_NONCE = "extra_usb_nonce"
        const val EXTRA_HOST_ID = "extra_host_id"
        const val EXTRA_ALLOW_AUTO_PAIR = "extra_allow_auto_pair"
        private const val DEFAULT_USB_PORT = 45820
        private const val TRIGGER_COOLDOWN_MS = 3_000L
        private const val CHANNEL_ID = "tonewise_usb_sync"
        private const val NOTIFICATION_ID = 45822
    }
}
