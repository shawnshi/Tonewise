/*
 * Copyright (c) 2026 Tonewise Project
 * SyncAndBackgroundRegressionTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.sync

import java.io.File
import java.net.ConnectException
import java.net.InetAddress
import java.net.SocketTimeoutException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SyncAndBackgroundRegressionTest {
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
    fun normalizeBaseUrlStatic_defaultsPort45820_andNormalizesFullWidthColon() {
        assertEquals("http://192.168.8.242:45820", LanSyncRepository.normalizeBaseUrlStatic("192.168.8.242"))
        assertEquals("http://192.168.8.242:45820", LanSyncRepository.normalizeBaseUrlStatic("http://192.168.8.242/"))
        assertEquals("http://192.168.8.242:45820", LanSyncRepository.normalizeBaseUrlStatic("192.168.8.242：45820"))
        assertEquals("http://192.168.1.42:45820", LanSyncRepository.normalizeBaseUrlStatic("http://192.168.1.42:45820/"))
        assertEquals("http://[::1]:45820", LanSyncRepository.normalizeBaseUrlStatic("[::1]"))
        assertEquals("https://example.com", LanSyncRepository.normalizeBaseUrlStatic("https://example.com/"))
        assertEquals("", LanSyncRepository.normalizeBaseUrlStatic("   "))
    }

    @Test
    fun lanHttpClient_bypassesProxyOnlyForPrivateLanHttpUrls() {
        assertTrue(LanHttpClient.isPrivateLanHttpUrl("http://192.168.8.242:45820/api/v2/ping"))
        assertTrue(LanHttpClient.isPrivateLanHttpUrl("http://10.0.2.2:45820/api/v2/outbox"))
        assertTrue(LanHttpClient.isPrivateLanHttpUrl("http://172.16.10.20:45820/api/v2/pair"))
        assertTrue(LanHttpClient.isPrivateLanHttpUrl("http://127.0.0.1:45820/api/v2/report"))

        assertFalse(LanHttpClient.isPrivateLanHttpUrl("https://ws.audioscrobbler.com/2.0/"))
        assertFalse(LanHttpClient.isPrivateLanHttpUrl("https://192.168.8.242:45820/api/v2/ping"))
        assertFalse(LanHttpClient.isPrivateLanHttpUrl("http://example.com/api/v2/ping"))

        val lanHttpSrc =
            File(
                    findRepoRoot(),
                    "android/app/src/main/java/com/shawnshi/tonewise/sync/LanHttpClient.kt",
                )
                .readText(Charsets.UTF_8)
        assertTrue(lanHttpSrc.contains("openConnection(Proxy.NO_PROXY)"))
    }

    @Test
    fun selectPreferredResolvedHostFromAddresses_excludesVirtual198_18_andPrefersLanIpv4() {
        val mixed =
            listOf(
                InetAddress.getByName("127.0.0.1"),
                InetAddress.getByName("169.254.10.20"),
                InetAddress.getByName("198.18.0.1"),
                InetAddress.getByName("fe80::1"),
                InetAddress.getByName("2001:db8::1"),
                InetAddress.getByName("192.168.8.242"),
            )
        assertEquals("192.168.8.242", LanSyncRepository.selectPreferredResolvedHostFromAddresses(mixed))

        val ipv6Fallback =
            listOf(
                InetAddress.getByName("198.18.0.1"),
                InetAddress.getByName("2001:db8::1"),
            )
        assertEquals("[2001:db8:0:0:0:0:0:1]", LanSyncRepository.selectPreferredResolvedHostFromAddresses(ipv6Fallback))
    }

    @Test
    fun formatConnectionError_returnsActionableChineseGuidance() {
        val refused =
            ConnectException(
                "failed to connect to /192.168.8.242 (port 45820) from /192.168.8.197 (port 43904): connect failed: ECONNREFUSED (Connection refused)"
            )
        val refusedMsg = LanSyncRepository.formatConnectionError("http://192.168.8.242:45820", refused)
        assertTrue(refusedMsg.contains("45820 端口未监听"))
        assertTrue(refusedMsg.contains("局域网同步服务已启动"))

        val timedOut = SocketTimeoutException("failed to connect to /192.168.8.242 (port 45820) after 5000ms")
        val timeoutMsg = LanSyncRepository.formatConnectionError("http://192.168.8.242:45820", timedOut)
        assertTrue(timeoutMsg.contains("超时或不可达"))
        assertTrue(timeoutMsg.contains("代理/VPN"))

        val readTimedOut = SocketTimeoutException("Read timed out")
        val readTimeoutMsg = LanSyncRepository.formatConnectionError("http://192.168.8.242:45820", readTimedOut)
        assertTrue(readTimeoutMsg.contains("电脑端响应超时"))
        assertTrue(readTimeoutMsg.contains("Read timed out"))
    }

    @Test
    fun lanSyncV2Client_andLanHttpClient_useExpandedOutboxTimeout_AndSlicedDownloadRetries() {
        val root = findRepoRoot()

        val v2ClientSrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/sync/LanSyncV2Client.kt")
                .readText(Charsets.UTF_8)
        assertTrue(v2ClientSrc.contains("readTimeoutMs = 60_000"))
        assertTrue(v2ClientSrc.contains("readTimeoutMs = 30_000"))

        val httpClientSrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/sync/LanHttpClient.kt")
                .readText(Charsets.UTF_8)
        assertTrue(httpClientSrc.contains("if (cancelToken != null) 800 else 30_000"))
        assertTrue(httpClientSrc.contains("consecutiveTimeouts < 38"))
    }

    @Test
    fun backgroundSwitchingAndFgsGuards_coverSyncSessionOngoing_UserLeaveHint_AndBackCallbackCleanup() {
        val root = findRepoRoot()

        val exoSrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/playback/service/ExoPlaybackStateHolder.kt")
                .readText(Charsets.UTF_8)
        assertTrue(exoSrc.contains("val wantsPlayback: Boolean"))
        assertTrue(exoSrc.contains("sessionOngoing = true"))

        val auxioSrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/AuxioService.kt")
                .readText(Charsets.UTF_8)
        assertTrue(auxioSrc.contains("EXTRA_FORCE_FOREGROUND"))
        assertTrue(auxioSrc.contains("if (playbackFragment.isSessionOngoing || playbackFragment.isPlaying)"))

        val mainActivitySrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/MainActivity.kt")
                .readText(Charsets.UTF_8)
        assertTrue(mainActivitySrc.contains("override fun onUserLeaveHint()"))
        assertTrue(mainActivitySrc.contains("override fun onPause()"))
        assertTrue(mainActivitySrc.contains("ensurePlaybackForegroundIfActive()"))

        val mainFragmentSrc =
            File(root, "android/app/src/main/java/com/shawnshi/tonewise/MainFragment.kt")
                .readText(Charsets.UTF_8)
        assertTrue(mainFragmentSrc.contains("override fun onPause()"))
        assertTrue(mainFragmentSrc.contains("speedDialBackCallback?.remove()"))
        assertTrue(mainFragmentSrc.contains("playbackSheetBehavior.targetState == BackportBottomSheetBehavior.STATE_EXPANDED"))
    }

    @Test
    fun formatConnectionError_withVpnActiveAndUnexpectedEof_surfacesSplitTunnelGuidance() {
        val eof = java.io.EOFException("unexpected end of stream on com.android.okhttp.Address@deadbeef")
        val msg =
            LanSyncRepository.formatConnectionError(
                baseUrl = "http://192.168.8.242:45820",
                error = eof,
                vpnActive = true,
            )
        assertTrue(msg.contains("VPN"))
        assertTrue(msg.contains("白名单"))
        assertFalse(msg.contains("com.android.okhttp.Address"))
    }

    @Test
    fun lanHttpClient_getBytes_retriesOnceAfterUnexpectedEof() {
        val server = java.net.ServerSocket(0)
        server.soTimeout = 5_000
        val workerError = java.util.concurrent.atomic.AtomicReference<Throwable?>(null)
        val serverThread =
            Thread {
                try {
                    // First connection: accept and close abruptly before response (EOF)
                    server.accept().use { first ->
                        first.soTimeout = 2_000
                        runCatching { first.getInputStream().read() }
                    }
                    // Second connection: read HTTP request headers then send 200 OK
                    server.accept().use { second ->
                        second.soTimeout = 2_000
                        val reader = second.getInputStream().bufferedReader(Charsets.UTF_8)
                        while (true) {
                            val line = reader.readLine() ?: break
                            if (line.isEmpty()) break
                        }
                        val payload = "ok-after-retry".toByteArray(Charsets.UTF_8)
                        val response =
                            "HTTP/1.1 200 OK\r\nContent-Length: ${payload.size}\r\nConnection: close\r\n\r\nok-after-retry"
                        second.getOutputStream().write(response.toByteArray(Charsets.UTF_8))
                        second.getOutputStream().flush()
                    }
                } catch (t: Throwable) {
                    workerError.set(t)
                }
            }
        serverThread.isDaemon = true
        serverThread.start()

        try {
            val bytes = LanHttpClient.getBytes(url = "http://127.0.0.1:${server.localPort}/api/v2/ping")
            serverThread.join(5_000)
            assertEquals(null, workerError.get())
            assertEquals("ok-after-retry", String(bytes, Charsets.UTF_8))
        } finally {
            runCatching { server.close() }
        }
    }
}
