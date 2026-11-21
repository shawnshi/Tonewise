/*
 * Copyright (c) 2026 Tonewise Project
 * LanHttpClient.kt is part of Tonewise.
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

import java.io.EOFException
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.Proxy
import java.net.SocketTimeoutException
import java.net.URI
import java.net.URL
import java.net.URLEncoder
import java.util.Collections
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import org.oxycblt.musikr.core.TonewiseCore

/**
 * Shared HTTP client & cooperative cancellation registry used across LAN Sync and Last.fm sync.
 */
object LanHttpClient {
    class CancelToken {
        private val cancelled = AtomicBoolean(false)
        private val activeConnections =
            Collections.newSetFromMap(ConcurrentHashMap<HttpURLConnection, Boolean>())

        val isCancelled: Boolean
            get() = cancelled.get()

        fun cancel() {
            if (cancelled.compareAndSet(false, true)) {
                val snapshot = activeConnections.toList()
                if (snapshot.isEmpty()) return
                Thread {
                    for (conn in snapshot) {
                        runCatching { conn.disconnect() }
                    }
                }.apply {
                    isDaemon = true
                    name = "tonewise-http-cancel"
                    start()
                }
            }
        }

        fun throwIfCancelled() {
            if (cancelled.get()) {
                throw CancellationException("Sync cancelled")
            }
        }

        fun <T> track(conn: HttpURLConnection, block: (HttpURLConnection) -> T): T {
            throwIfCancelled()
            activeConnections.add(conn)
            return try {
                throwIfCancelled()
                block(conn)
            } catch (t: Throwable) {
                if (cancelled.get() && t !is CancellationException) {
                    throw CancellationException("Sync cancelled").apply { initCause(t) }
                }
                throw t
            } finally {
                activeConnections.remove(conn)
                runCatching { conn.disconnect() }
            }
        }

        companion object {
            suspend fun <T> withCoroutineCancelToken(block: suspend (CancelToken) -> T): T {
                val token = CancelToken()
                val job = currentCoroutineContext()[Job]
                val handle =
                    job?.invokeOnCompletion { cause ->
                        if (cause != null) {
                            token.cancel()
                        }
                    }
                return try {
                    block(token)
                } finally {
                    handle?.dispose()
                }
            }
        }
    }

    fun isSafeLanOrHttpsUrl(urlString: String): Boolean {
        val raw = urlString.trim()
        if (raw.isEmpty()) return false
        val uri = try {
            URI(raw)
        } catch (_: Exception) {
            return false
        }
        val scheme = uri.scheme?.lowercase() ?: return false
        val host = uri.host?.trim('[', ']')?.lowercase()?.takeIf { it.isNotEmpty() } ?: return false
        if (scheme == "https") return true
        if (scheme != "http") return false
        if (host == "localhost" || host.endsWith(".local")) return true

        val ipv4 = parseIpv4Bytes(host)
        if (ipv4 != null) {
            val b0 = ipv4[0].toInt() and 0xFF
            val b1 = ipv4[1].toInt() and 0xFF
            return when {
                b0 == 127 -> true // 127.0.0.0/8 loopback
                b0 == 10 -> true // 10.0.0.0/8 RFC1918
                b0 == 172 && b1 in 16..31 -> true // 172.16.0.0/12 RFC1918
                b0 == 192 && b1 == 168 -> true // 192.168.0.0/16 RFC1918
                b0 == 169 && b1 == 254 -> true // 169.254.0.0/16 link-local
                else -> false
            }
        }

        if (host.contains(':')) {
            val addr = try {
                InetAddress.getByName(host)
            } catch (_: Exception) {
                return false
            }
            val rawBytes = addr.address
            val isUla = rawBytes.size == 16 && (rawBytes[0].toInt() and 0xFE) == 0xFC
            return addr.isLoopbackAddress || addr.isSiteLocalAddress || addr.isLinkLocalAddress || isUla
        }

        return false
    }

    fun enforceSafeBearerTransport(url: String, bearerToken: String?) {
        if (!isSafeLanOrHttpsUrl(url)) {
            val hasBearer = !bearerToken.isNullOrBlank()
            val detail =
                if (hasBearer) "拒绝在非局域网明文连接上发送凭据" else "拒绝通过非局域网明文连接请求同步接口"
            throw IOException("$detail: $url")
        }
    }

    internal fun isPrivateLanHttpUrl(urlString: String): Boolean {
        val raw = urlString.trim()
        if (raw.isEmpty()) return false
        val uri =
            try {
                URI(raw)
            } catch (_: Exception) {
                return false
            }
        return uri.scheme?.equals("http", ignoreCase = true) == true && isSafeLanOrHttpsUrl(raw)
    }

    @Volatile
    internal var lanConnectionOpener: ((URL) -> HttpURLConnection?)? = null

    private fun isRetryableStreamEof(error: Throwable): Boolean =
        generateSequence(error) { it.cause }.any { cause ->
            cause is EOFException ||
                cause.message?.contains("unexpected end of stream", ignoreCase = true) == true
        }

    internal fun openConnectionForUrl(
        url: String,
        allowLanNetworkBypass: Boolean = true,
    ): HttpURLConnection {
        val parsed = URL(url)
        val isLan = isPrivateLanHttpUrl(url)
        val conn =
            if (isLan) {
                val bypassed =
                    if (allowLanNetworkBypass) {
                        runCatching { lanConnectionOpener?.invoke(parsed) }.getOrNull()
                    } else {
                        null
                    }
                bypassed ?: parsed.openConnection(Proxy.NO_PROXY)
            } else {
                parsed.openConnection()
            }
        val httpConn = conn as HttpURLConnection
        if (isLan) {
            httpConn.setRequestProperty("Connection", "close")
        }
        return httpConn
    }

    private fun parseIpv4Bytes(host: String): ByteArray? {
        val parts = host.split('.')
        if (parts.size != 4) return null
        val out = ByteArray(4)
        for (i in 0 until 4) {
            val part = parts[i]
            if (part.isEmpty() || part.length > 3) return null
            val num = part.toIntOrNull() ?: return null
            if (num !in 0..255) return null
            out[i] = num.toByte()
        }
        return out
    }

    private fun throwIfHttpStatusError(conn: HttpURLConnection, url: String, code: Int, authMessage: String) {
        if (code == HttpURLConnection.HTTP_UNAUTHORIZED) {
            throw LanSyncAuthException(code, authMessage)
        }
        if (code == 429) {
            throw parseRateLimitException(conn)
        }
        if (code in 300..399) {
            throw IOException("Blocked HTTP redirect ($code) for $url")
        }
        if (code !in 200..299) {
            throw IOException("HTTP $code for $url")
        }
    }

    private fun parseRateLimitException(conn: HttpURLConnection): LanSyncRateLimitException {
        val headerSeconds = conn.getHeaderField("Retry-After")?.trim()?.toLongOrNull()
        val bodyText =
            runCatching { conn.errorStream?.use { String(it.readBytes(), Charsets.UTF_8) } }.getOrNull()
        val jsonSeconds =
            if (!bodyText.isNullOrBlank()) {
                val parsed = runCatching { TonewiseCore.parseJson(bodyText) }.getOrNull() as? Map<*, *>
                val raw = parsed?.get("retry_after_seconds")
                (raw as? Number)?.toLong() ?: raw?.toString()?.toLongOrNull()
            } else {
                null
            }
        val retryAfter = (jsonSeconds ?: headerSeconds ?: 60L).coerceAtLeast(1L)
        return LanSyncRateLimitException(retryAfter, "配对尝试过于频繁，请在 ${retryAfter} 秒后重试")
    }

    fun pruneStalePartFiles(
        dir: File,
        maxAgeMs: Long = 7L * 24L * 60L * 60L * 1000L,
        nowMs: Long = System.currentTimeMillis(),
    ): Int {
        if (!dir.exists() || !dir.isDirectory) return 0
        var removed = 0
        dir.walkTopDown().forEach { entry ->
            if (!entry.isFile) return@forEach
            val name = entry.name
            val isStaleCandidate =
                name.endsWith(".part", ignoreCase = true) ||
                    name.endsWith(".tmp", ignoreCase = true) ||
                    name.contains(".twtmp", ignoreCase = true) ||
                    name.contains(".twbak", ignoreCase = true)
            if (isStaleCandidate && (nowMs - entry.lastModified()) >= maxAgeMs) {
                if (runCatching { entry.delete() }.getOrDefault(false)) {
                    removed++
                }
            }
        }
        return removed
    }

    fun getBytes(
        url: String,
        bearerToken: String? = null,
        cancelToken: CancelToken? = null,
        connectTimeoutMs: Int = 5_000,
        readTimeoutMs: Int = 15_000,
        userAgent: String? = null,
    ): ByteArray {
        enforceSafeBearerTransport(url, bearerToken)
        var lastError: IOException? = null
        for (attempt in 0 until 2) {
            cancelToken?.throwIfCancelled()
            val conn =
                openConnectionForUrl(url).apply {
                    instanceFollowRedirects = false
                    connectTimeout = connectTimeoutMs
                    readTimeout = readTimeoutMs
                    requestMethod = "GET"
                    if (!bearerToken.isNullOrBlank()) {
                        setRequestProperty("Authorization", "Bearer $bearerToken")
                    }
                    if (!userAgent.isNullOrBlank()) {
                        setRequestProperty("User-Agent", userAgent)
                    }
                }
            val execute: (HttpURLConnection) -> ByteArray = { c ->
                val code = c.responseCode
                throwIfHttpStatusError(c, url, code, "未授权或配对已过期，请重新配对")
                c.inputStream.use { it.readBytes() }
            }
            try {
                return if (cancelToken != null) {
                    cancelToken.track(conn, execute)
                } else {
                    try {
                        execute(conn)
                    } finally {
                        runCatching { conn.disconnect() }
                    }
                }
            } catch (ce: CancellationException) {
                throw ce
            } catch (io: IOException) {
                lastError = io
                if (attempt == 0 && isRetryableStreamEof(io)) {
                    cancelToken?.throwIfCancelled()
                    Thread.sleep(150L)
                    continue
                }
                throw io
            }
        }
        throw (lastError ?: IOException("HTTP GET failed for $url"))
    }

    fun getString(
        url: String,
        bearerToken: String? = null,
        cancelToken: CancelToken? = null,
        connectTimeoutMs: Int = 5_000,
        readTimeoutMs: Int = 15_000,
        userAgent: String? = null,
    ): String = String(getBytes(url, bearerToken, cancelToken, connectTimeoutMs, readTimeoutMs, userAgent), Charsets.UTF_8)

    fun postJson(
        url: String,
        body: String,
        bearerToken: String? = null,
        cancelToken: CancelToken? = null,
        connectTimeoutMs: Int = 5_000,
        readTimeoutMs: Int = 20_000,
    ): String {
        enforceSafeBearerTransport(url, bearerToken)
        val conn =
            openConnectionForUrl(url).apply {
                instanceFollowRedirects = false
                connectTimeout = connectTimeoutMs
                readTimeout = readTimeoutMs
                requestMethod = "POST"
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
                if (!bearerToken.isNullOrBlank()) {
                    setRequestProperty("Authorization", "Bearer $bearerToken")
                }
            }
        val execute: (HttpURLConnection) -> String = { c ->
            c.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            val code = c.responseCode
            throwIfHttpStatusError(c, url, code, "配对码错误或未授权")
            c.inputStream.use { String(it.readBytes(), Charsets.UTF_8) }
        }
        return if (cancelToken != null) {
            cancelToken.track(conn, execute)
        } else {
            try {
                execute(conn)
            } finally {
                runCatching { conn.disconnect() }
            }
        }
    }

    fun downloadFileResumable(
        url: String,
        bearerToken: String,
        partFile: File,
        expectedSize: Long,
        onProgress: (downloadedBytes: Long, totalBytes: Long) -> Unit = { _, _ -> },
        cancelToken: CancelToken? = null,
        expectedEtag: String? = null,
    ): Long {
        enforceSafeBearerTransport(url, bearerToken)
        val maxConsecutiveStalls = 36 // ~28.8s consecutive no-progress read timeout if not cancelled
        var remainingConsecutiveStalls = maxConsecutiveStalls
        var lastObservedSize = if (partFile.exists()) partFile.length() else 0L
        var activeEtag = expectedEtag?.trim()?.takeIf { it.isNotEmpty() }
        while (true) {
            val sizeBeforeAttempt = if (partFile.exists()) partFile.length() else 0L
            lastObservedSize = maxOf(lastObservedSize, sizeBeforeAttempt)
            try {
                return downloadFileResumableInternal(
                    url = url,
                    bearerToken = bearerToken,
                    partFile = partFile,
                    expectedSize = expectedSize,
                    onProgress = { downloaded, total ->
                        if (downloaded > lastObservedSize) {
                            lastObservedSize = downloaded
                            remainingConsecutiveStalls = maxConsecutiveStalls
                        }
                        onProgress(downloaded, total)
                    },
                    cancelToken = cancelToken,
                    allowRetry416 = true,
                    expectedEtag = activeEtag,
                    onEtagObserved = { observed ->
                        if (observed.isNotBlank()) {
                            activeEtag = observed
                        }
                    },
                )
            } catch (timeout: SocketTimeoutException) {
                cancelToken?.throwIfCancelled()
                val sizeAfterAttempt = if (partFile.exists()) partFile.length() else 0L
                if (sizeAfterAttempt > lastObservedSize) {
                    lastObservedSize = sizeAfterAttempt
                    remainingConsecutiveStalls = maxConsecutiveStalls
                    continue
                }
                if (remainingConsecutiveStalls <= 0) throw timeout
                remainingConsecutiveStalls--
            }
        }
    }

    private fun downloadFileResumableInternal(
        url: String,
        bearerToken: String,
        partFile: File,
        expectedSize: Long,
        onProgress: (Long, Long) -> Unit,
        cancelToken: CancelToken?,
        allowRetry416: Boolean,
        expectedEtag: String? = null,
        onEtagObserved: (String) -> Unit = {},
    ): Long {
        cancelToken?.throwIfCancelled()
        partFile.parentFile?.mkdirs()
        var existingBytes = if (partFile.isFile) partFile.length() else 0L
        if (expectedSize > 0L && existingBytes == expectedSize) {
            onProgress(expectedSize, expectedSize)
            return expectedSize
        }
        if (expectedSize > 0L && existingBytes > expectedSize) {
            partFile.delete()
            existingBytes = 0L
        }

        val conn =
            openConnectionForUrl(url).apply {
                instanceFollowRedirects = false
                connectTimeout = 8_000
                readTimeout = if (cancelToken != null) 800 else 30_000
                requestMethod = "GET"
                setRequestProperty("Authorization", "Bearer $bearerToken")
                setRequestProperty("Connection", "close")
                if (existingBytes > 0L) {
                    setRequestProperty("Range", "bytes=$existingBytes-")
                    if (!expectedEtag.isNullOrBlank()) {
                        setRequestProperty("If-Range", expectedEtag)
                    }
                }
            }

        var retry416 = false
        val result =
            try {
                val execute: (HttpURLConnection) -> Long = { c ->
                    val code = c.responseCode
                    if (code == 416) {
                        if (expectedSize > 0L && existingBytes == expectedSize) {
                            onProgress(expectedSize, expectedSize)
                            expectedSize
                        } else if (allowRetry416) {
                            retry416 = true
                            -1L
                        } else {
                            throw IOException("HTTP 416 Range mismatch for $url")
                        }
                    } else {
                        throwIfHttpStatusError(c, url, code, "未授权或配对已过期，请重新配对")
                        c.getHeaderField("ETag")?.trim()?.takeIf { it.isNotEmpty() }?.let(onEtagObserved)

                        val append = (code == HttpURLConnection.HTTP_PARTIAL && existingBytes > 0L)
                        if (!append && existingBytes > 0L) {
                            existingBytes = 0L
                        }
                        var written = existingBytes
                        val contentLen = c.contentLengthLong
                        val totalTarget =
                            when {
                                expectedSize > 0L -> expectedSize
                                contentLen > 0L -> existingBytes + contentLen
                                else -> -1L
                            }

                        val buffer = ByteArray(32 * 1024)
                        var consecutiveTimeouts = 0
                        c.inputStream.use { input ->
                            FileOutputStream(partFile, append).use { output ->
                                while (true) {
                                    cancelToken?.throwIfCancelled()
                                    val n =
                                        try {
                                            input.read(buffer).also { consecutiveTimeouts = 0 }
                                        } catch (ste: SocketTimeoutException) {
                                            cancelToken?.throwIfCancelled()
                                            if (cancelToken != null && consecutiveTimeouts < 38) {
                                                consecutiveTimeouts++
                                                continue
                                            }
                                            throw ste
                                        }
                                    if (n <= 0) break
                                    cancelToken?.throwIfCancelled()
                                    output.write(buffer, 0, n)
                                    written += n
                                    onProgress(written, totalTarget)
                                }
                                output.flush()
                            }
                        }

                        val finalSize = partFile.length()
                        if (expectedSize > 0L && finalSize != expectedSize) {
                            throw IOException("文件传输未完成（已接收 $finalSize / $expectedSize 字节），下次同步将继续断点续传")
                        }
                        finalSize
                    }
                }
                if (cancelToken != null) {
                    cancelToken.track(conn, execute)
                } else {
                    try {
                        execute(conn)
                    } finally {
                        runCatching { conn.disconnect() }
                    }
                }
            } catch (ce: CancellationException) {
                throw ce
            }

        if (retry416) {
            partFile.delete()
            return downloadFileResumableInternal(
                url = url,
                bearerToken = bearerToken,
                partFile = partFile,
                expectedSize = expectedSize,
                onProgress = onProgress,
                cancelToken = cancelToken,
                allowRetry416 = false,
            )
        }
        return result
    }

    suspend fun getStringForLastFm(url: String, userAgent: String): String =
        CancelToken.withCoroutineCancelToken { cancelToken ->
            enforceSafeBearerTransport(url, bearerToken = null)
            var lastError: IOException? = null
            for (attempt in 0 until 2) {
                cancelToken.throwIfCancelled()
                val conn =
                    openConnectionForUrl(url, allowLanNetworkBypass = false).apply {
                        instanceFollowRedirects = false
                        connectTimeout = 8_000
                        readTimeout = 15_000
                        requestMethod = "GET"
                        setRequestProperty("User-Agent", userAgent)
                    }
                try {
                    return@withCoroutineCancelToken cancelToken.track(conn) { c ->
                        val code = c.responseCode
                        if (code in 300..399) {
                            throw IOException("Blocked HTTP redirect ($code) for $url")
                        }
                        if (code == 429 || code >= 500) {
                            throw TransientHttpException(code)
                        }
                        val stream =
                            if (code in 200..299) c.inputStream else (c.errorStream ?: c.inputStream)
                        stream?.use { String(it.readBytes(), Charsets.UTF_8) } ?: ""
                    }
                } catch (ce: CancellationException) {
                    throw ce
                } catch (e: IOException) {
                    lastError = e
                    if (attempt == 0) {
                        delay(400L)
                    }
                }
            }
            throw lastError ?: IOException("Last.fm GET failed for $url")
        }

    suspend fun postFormForLastFm(
        url: String,
        params: Map<String, String>,
        userAgent: String,
    ): String =
        CancelToken.withCoroutineCancelToken { cancelToken ->
            enforceSafeBearerTransport(url, bearerToken = null)
            val encoded =
                params.entries.joinToString("&") { (k, v) ->
                    "${URLEncoder.encode(k, "UTF-8")}=${URLEncoder.encode(v, "UTF-8")}"
                }
            var lastError: IOException? = null
            for (attempt in 0 until 2) {
                cancelToken.throwIfCancelled()
                val conn =
                    openConnectionForUrl(url, allowLanNetworkBypass = false).apply {
                        instanceFollowRedirects = false
                        connectTimeout = 8_000
                        readTimeout = 15_000
                        requestMethod = "POST"
                        doOutput = true
                        setRequestProperty("Content-Type", "application/x-www-form-urlencoded; charset=utf-8")
                        setRequestProperty("User-Agent", userAgent)
                    }
                try {
                    return@withCoroutineCancelToken cancelToken.track(conn) { c ->
                        c.outputStream.use { it.write(encoded.toByteArray(Charsets.UTF_8)) }
                        val code = c.responseCode
                        if (code in 300..399) {
                            throw IOException("Blocked HTTP redirect ($code) for $url")
                        }
                        if (code == 429 || code >= 500) {
                            throw TransientHttpException(code)
                        }
                        val stream =
                            if (code in 200..299) c.inputStream else (c.errorStream ?: c.inputStream)
                        stream?.use { String(it.readBytes(), Charsets.UTF_8) } ?: ""
                    }
                } catch (ce: CancellationException) {
                    throw ce
                } catch (e: IOException) {
                    lastError = e
                    if (attempt == 0) {
                        delay(400L)
                    }
                }
            }
            throw lastError ?: IOException("Last.fm POST failed for $url")
        }

    fun downloadFileToTempAndReplace(
        url: String,
        bearerToken: String? = null,
        targetFile: File,
        cancelToken: CancelToken? = null,
        connectTimeoutMs: Int = 5_000,
        readTimeoutMs: Int = 30_000,
    ): Long {
        enforceSafeBearerTransport(url, bearerToken)
        targetFile.parentFile?.mkdirs()
        val tmpFile = File(targetFile.parentFile, "${targetFile.name}.tmp")
        if (tmpFile.exists()) tmpFile.delete()
        val conn =
            openConnectionForUrl(url).apply {
                instanceFollowRedirects = false
                connectTimeout = connectTimeoutMs
                readTimeout = readTimeoutMs
                requestMethod = "GET"
                if (!bearerToken.isNullOrBlank()) {
                    setRequestProperty("Authorization", "Bearer $bearerToken")
                }
            }
        val execute: (HttpURLConnection) -> Long = { c ->
            val code = c.responseCode
            throwIfHttpStatusError(c, url, code, "未授权或配对已过期，请重新配对")
            var total = 0L
            try {
                c.inputStream.use { input ->
                    FileOutputStream(tmpFile).use { fos ->
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            cancelToken?.throwIfCancelled()
                            val n = input.read(buf)
                            if (n <= 0) break
                            fos.write(buf, 0, n)
                            total += n
                        }
                        fos.fd.sync()
                    }
                }
                if (!tmpFile.renameTo(targetFile)) {
                    if (targetFile.exists()) {
                        targetFile.delete()
                    }
                    if (!tmpFile.renameTo(targetFile)) {
                        tmpFile.copyTo(targetFile, overwrite = true)
                        tmpFile.delete()
                    }
                }
                total
            } catch (t: Throwable) {
                runCatching { tmpFile.delete() }
                throw t
            }
        }
        return if (cancelToken != null) {
            cancelToken.track(conn, execute)
        } else {
            try {
                execute(conn)
            } finally {
                runCatching { conn.disconnect() }
            }
        }
    }

    private class TransientHttpException(val code: Int) : IOException("Transient HTTP $code")
}
