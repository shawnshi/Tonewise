/*
 * Copyright (c) 2026 Tonewise Project
 * DirectoryIndexingAndToolbarRegressionTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.playback

import com.shawnshi.tonewise.ServiceLifecyclePolicy
import com.shawnshi.tonewise.music.PendingIndexCoordinator
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectoryIndexingAndToolbarRegressionTest {
    private fun findRepoRoot(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").canonicalFile
        while (dir != null) {
            if (File(dir, "protocol/schemas/manifest.sql").isFile && File(dir, "android").isDirectory) {
                return dir
            }
            dir = dir.parentFile
        }
        error("Could not locate Tonewise repo root from ${System.getProperty("user.dir")}")
    }

    @Test
    fun pendingIndexCoordinator_queuesAndMergesUntilConsumed() {
        val coordinator = PendingIndexCoordinator()
        assertNull(coordinator.consume())

        assertTrue(coordinator.queueOrMerge(withCache = true))
        // uncached request overrides cached request when both arrive before worker attach
        assertFalse(coordinator.queueOrMerge(withCache = false))
        assertFalse(coordinator.queueOrMerge(withCache = true))

        val consumed = coordinator.consume()
        assertEquals(false, consumed)
        assertNull(coordinator.consume())
    }

    @Test
    fun toolbarHomeXml_removesTopBarSortAction_andLanSyncRemovesPlaylistUpload() {
        val repoRoot = findRepoRoot()
        val toolbarXml =
            File(repoRoot, "android/app/src/main/res/menu/toolbar_home.xml")
                .readText(Charsets.UTF_8)
        assertFalse(
            "toolbar_home.xml must not contain action_sort",
            toolbarXml.contains("action_sort"),
        )

        val lanSyncSource =
            File(
                    repoRoot,
                    "android/app/src/main/java/com/shawnshi/tonewise/sync/LanSyncRepository.kt",
                )
                .readText(Charsets.UTF_8)
        assertFalse(
            "Phone -> PC /api/v1/playlists/merge upload must be disabled in LanSyncRepository.kt",
            lanSyncSource.contains("/api/v1/playlists/merge"),
        )
        assertFalse(
            "syncPlaylistsWithHost should be removed from LanSyncRepository.kt",
            lanSyncSource.contains("syncPlaylistsWithHost"),
        )
    }

    @Test
    fun auxioService_backgroundPlaybackLifecycleGuards_keepServiceStartedAndPromoted() {
        // Only stop when no local UI clients remain AND neither playback session nor playback is active
        assertTrue(ServiceLifecyclePolicy.shouldStopWhenIdle(0, isSessionOngoing = false, isPlaying = false))
        assertFalse(ServiceLifecyclePolicy.shouldStopWhenIdle(1, isSessionOngoing = false, isPlaying = false))
        assertFalse(ServiceLifecyclePolicy.shouldStopWhenIdle(0, isSessionOngoing = true, isPlaying = false))
        assertFalse(ServiceLifecyclePolicy.shouldStopWhenIdle(0, isSessionOngoing = false, isPlaying = true))
        assertFalse(ServiceLifecyclePolicy.shouldStopWhenIdle(0, isSessionOngoing = true, isPlaying = true))

        // Playback foreground promotion must ensure service transitions from bound-only to started
        assertTrue(ServiceLifecyclePolicy.shouldEnsureStartedForPlayback(isServiceStarted = false, hasMediaNotification = true))
        assertFalse(ServiceLifecyclePolicy.shouldEnsureStartedForPlayback(isServiceStarted = true, hasMediaNotification = true))
        assertFalse(ServiceLifecyclePolicy.shouldEnsureStartedForPlayback(isServiceStarted = false, hasMediaNotification = false))

        val repoRoot = findRepoRoot()
        val auxioServiceSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/AuxioService.kt")
                .readText(Charsets.UTF_8)
        assertTrue(auxioServiceSrc.contains("override fun onRebind"))
        assertTrue(auxioServiceSrc.contains("return true"))

        val mediaSessionSrc =
            File(
                    repoRoot,
                    "android/app/src/main/java/com/shawnshi/tonewise/playback/service/MediaSessionHolder.kt",
                )
                .readText(Charsets.UTF_8)
        assertFalse(
            "Foreground update must not be gated behind bitmap loading",
            mediaSessionSrc.contains("if (!bitmapProvider.isBusy)"),
        )
        assertTrue(mediaSessionSrc.contains("FOREGROUND_SERVICE_IMMEDIATE"))

        val mainActivitySrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/MainActivity.kt")
                .readText(Charsets.UTF_8)
        assertTrue(mainActivitySrc.contains("Manifest.permission.POST_NOTIFICATIONS"))
    }
}
