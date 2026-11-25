/*
 * Copyright (c) 2026 Tonewise Project
 * SecretBox.kt is part of Tonewise.
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

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import java.security.KeyStore
import java.security.KeyStoreException
import java.security.Security
import java.security.UnrecoverableKeyException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import timber.log.Timber as L

/**
 * Hardware-backed secret box using AndroidKeyStore AES-256-GCM (`enc:v1:<hex>`).
 *
 * Legacy plaintext values (without the `enc:v1:` prefix) are returned as-is on [decrypt] for
 * transparent migration, while callers write back encrypted payloads on next mutation.
 *
 * On headless JVM unit test environments where the `AndroidKeyStore` provider does not exist,
 * [encrypt] / [decrypt] transparently fall back to an in-memory ephemeral AES-256 key so callers can
 * be exercised without Robolectric. Direct [encryptWithKey] / [decryptWithKey] accept any [SecretKey].
 */
object SecretBox {
    private const val ANDROID_KEYSTORE = "AndroidKeyStore"
    private const val KEY_ALIAS = "tonewise_sync_secret_v1"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"
    private const val GCM_IV_BYTES = 12
    private const val GCM_TAG_BITS = 128
    internal const val PREFIX = "enc:v1:"

    private val keyLock = Any()
    private val HEX_CHARS = "0123456789abcdef".toCharArray()

    @Volatile private var jvmFallbackKey: SecretKey? = null

    fun isEncrypted(value: String?): Boolean = !value.isNullOrEmpty() && value.startsWith(PREFIX)

    private fun hasAndroidKeystoreProvider(): Boolean = Security.getProvider(ANDROID_KEYSTORE) != null

    fun encrypt(plaintext: String): String {
        if (plaintext.isEmpty()) return ""
        val androidKeystore = hasAndroidKeystoreProvider()
        val key =
            try {
                getOrCreateKey()
            } catch (t: Throwable) {
                if (androidKeystore) {
                    throw IllegalStateException("Android Keystore 加密失败，已阻止明文写入", t)
                }
                null
            }
        if (key == null) {
            if (androidKeystore) {
                throw IllegalStateException("Android Keystore 密钥不可用，已阻止明文写入")
            }
            return encryptWithKey(plaintext, getOrCreateJvmFallbackKey())
        }
        return try {
            encryptWithKey(plaintext, key)
        } catch (t: Throwable) {
            if (androidKeystore) {
                throw IllegalStateException("Android Keystore 加密失败，已阻止明文写入", t)
            }
            throw t
        }
    }

    fun decrypt(stored: String): String {
        if (stored.isEmpty()) return ""
        if (!stored.startsWith(PREFIX)) return stored
        val key =
            getOrCreateKey()
                ?: run {
                    L.w("SecretBox decrypt aborted: Keystore key temporarily unavailable")
                    return ""
                }
        return try {
            decryptWithKey(stored, key)
        } catch (t: Throwable) {
            L.w(t, "SecretBox decrypt failed")
            ""
        }
    }

    fun encryptWithKey(plaintext: String, key: SecretKey): String {
        if (plaintext.isEmpty()) return ""
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key)
        val iv = cipher.iv
        require(iv != null && iv.size == GCM_IV_BYTES) {
            "Unexpected GCM IV length: ${iv?.size ?: -1}"
        }
        val ciphertext = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
        val payload = ByteArray(GCM_IV_BYTES + ciphertext.size)
        System.arraycopy(iv, 0, payload, 0, GCM_IV_BYTES)
        System.arraycopy(ciphertext, 0, payload, GCM_IV_BYTES, ciphertext.size)
        return PREFIX + toHex(payload)
    }

    fun decryptWithKey(stored: String, key: SecretKey): String {
        if (stored.isEmpty()) return ""
        if (!stored.startsWith(PREFIX)) return stored
        val payload = fromHex(stored.substring(PREFIX.length))
        require(payload.size > GCM_IV_BYTES) { "Encrypted payload too short" }
        val iv = payload.copyOfRange(0, GCM_IV_BYTES)
        val ciphertext = payload.copyOfRange(GCM_IV_BYTES, payload.size)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(GCM_TAG_BITS, iv))
        val plaintextBytes = cipher.doFinal(ciphertext)
        return String(plaintextBytes, Charsets.UTF_8)
    }

    private fun getOrCreateKey(): SecretKey? {
        if (Security.getProvider(ANDROID_KEYSTORE) == null) {
            return getOrCreateJvmFallbackKey()
        }
        return synchronized(keyLock) {
            try {
                val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
                val existing = ks.getKey(KEY_ALIAS, null) as? SecretKey
                if (existing != null) {
                    return@synchronized existing
                }
                generateAndroidKey()
            } catch (e: KeyStoreException) {
                L.w(e, "SecretBox KeyStoreException; preserving existing alias without deletion")
                null
            } catch (e: KeyPermanentlyInvalidatedException) {
                L.w(e, "SecretBox key permanently invalidated; regenerating alias")
                deleteAndRegenerateAndroidKey()
            } catch (e: UnrecoverableKeyException) {
                L.w(e, "SecretBox key unrecoverable; regenerating alias")
                deleteAndRegenerateAndroidKey()
            } catch (t: Throwable) {
                // Transient KeyStore / TEE failure: NEVER destroy the existing alias.
                L.w(t, "SecretBox transient error; preserving existing key alias")
                null
            }
        }
    }

    private fun deleteAndRegenerateAndroidKey(): SecretKey? =
        runCatching {
            val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
            if (ks.containsAlias(KEY_ALIAS)) {
                ks.deleteEntry(KEY_ALIAS)
            }
            generateAndroidKey()
        }.getOrElse { err ->
            L.w(err, "SecretBox failed to regenerate invalidated key")
            null
        }

    private fun generateAndroidKey(): SecretKey {
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        val spec =
            KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .build()
        generator.init(spec)
        return generator.generateKey()
    }

    private fun getOrCreateJvmFallbackKey(): SecretKey {
        jvmFallbackKey?.let { return it }
        return synchronized(keyLock) {
            jvmFallbackKey
                ?: KeyGenerator.getInstance("AES")
                    .apply { init(256) }
                    .generateKey()
                    .also { jvmFallbackKey = it }
        }
    }

    internal fun toHex(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2] = HEX_CHARS[v ushr 4]
            out[i * 2 + 1] = HEX_CHARS[v and 0x0F]
        }
        return String(out)
    }

    internal fun fromHex(hex: String): ByteArray {
        val len = hex.length
        require(len % 2 == 0) { "Odd hex length: $len" }
        val out = ByteArray(len / 2)
        var i = 0
        while (i < len) {
            val hi = hexDigit(hex[i])
            val lo = hexDigit(hex[i + 1])
            require(hi >= 0 && lo >= 0) { "Invalid hex character at index $i" }
            out[i / 2] = ((hi shl 4) or lo).toByte()
            i += 2
        }
        return out
    }

    private fun hexDigit(c: Char): Int =
        when (c) {
            in '0'..'9' -> c - '0'
            in 'a'..'f' -> c - 'a' + 10
            in 'A'..'F' -> c - 'A' + 10
            else -> -1
        }
}
