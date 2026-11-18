/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseCore.kt is part of Tonewise.
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
 
package org.oxycblt.musikr.core

import android.os.ParcelFileDescriptor

data class M3u8MergeConflict(val index: Int, val ours: List<String>, val theirs: List<String>)

data class M3u8MergeResult(val merged: List<String>, val conflicts: List<M3u8MergeConflict>)

data class AudioFrameFingerprint(
    val audioFrameBlake3Hex: String,
    val headerBlake3Hex: String,
    val headerSize: Long,
    val fileSize: Long,
)

/**
 * Kotlin facade over the shared Rust `tonewise-core` native library (`libtonewise_core.so` on
 * Android, `tonewise_core.dll` in Windows unit tests).
 */
object TonewiseCore {
    fun parseJson(input: String): Any? = MiniJson.parse(input)

    fun abiVersion(): Int {
        TonewiseCoreJNI.ensureLoaded()
        return TonewiseCoreJNI.nativeAbiVersion()
    }

    fun splitArtists(raw: String): List<String> {
        TonewiseCoreJNI.ensureLoaded()
        val json = TonewiseCoreJNI.nativeSplitArtistsJson(raw)
        val parsed = MiniJson.parse(json)
        checkNoError(parsed)
        @Suppress("UNCHECKED_CAST")
        return (parsed as List<Any?>).map { it.toString() }
    }

    fun evaluateScene(sceneJson: String, tracksJson: String): List<String> {
        TonewiseCoreJNI.ensureLoaded()
        val json = TonewiseCoreJNI.nativeEvaluateSceneJson(sceneJson, tracksJson)
        val parsed = MiniJson.parse(json)
        checkNoError(parsed)
        @Suppress("UNCHECKED_CAST")
        return (parsed as List<Any?>).map { it.toString() }
    }

    fun mergeM3u8(
        base: List<String>,
        ours: List<String>,
        theirs: List<String>,
    ): M3u8MergeResult {
        TonewiseCoreJNI.ensureLoaded()
        val requestJson =
            buildString {
                append('{')
                append("\"base\":")
                appendJsonStringArray(base)
                append(",\"ours\":")
                appendJsonStringArray(ours)
                append(",\"theirs\":")
                appendJsonStringArray(theirs)
                append('}')
            }
        val responseJson = TonewiseCoreJNI.nativeMergeM3u8Json(requestJson)
        @Suppress("UNCHECKED_CAST")
        val root = MiniJson.parse(responseJson) as Map<String, Any?>
        checkNoError(root)
        @Suppress("UNCHECKED_CAST")
        val merged = (root["merged"] as List<Any?>).map { it.toString() }
        @Suppress("UNCHECKED_CAST")
        val conflicts =
            (root["conflicts"] as List<Any?>).map { raw ->
                val c = raw as Map<String, Any?>
                M3u8MergeConflict(
                    index = (c["index"] as Number).toInt(),
                    ours = (c["ours"] as List<Any?>).map { it.toString() },
                    theirs = (c["theirs"] as List<Any?>).map { it.toString() },
                )
            }
        return M3u8MergeResult(merged, conflicts)
    }

    fun audioFingerprint(pfd: ParcelFileDescriptor): AudioFrameFingerprint {
        TonewiseCoreJNI.ensureLoaded()
        return parseFingerprint(TonewiseCoreJNI.nativeAudioFingerprintFd(pfd.fd))
    }

    fun audioFingerprintPath(path: String): AudioFrameFingerprint {
        TonewiseCoreJNI.ensureLoaded()
        return parseFingerprint(TonewiseCoreJNI.nativeAudioFingerprintPath(path))
    }

    fun lookupWaveform(pfd: ParcelFileDescriptor, blake3Hex: String): FloatArray? {
        TonewiseCoreJNI.ensureLoaded()
        return parseWaveformJson(TonewiseCoreJNI.nativeLookupWaveformFd(pfd.fd, blake3Hex))
    }

    fun lookupWaveformPath(archivePath: String, blake3Hex: String): FloatArray? {
        TonewiseCoreJNI.ensureLoaded()
        return parseWaveformJson(TonewiseCoreJNI.nativeLookupWaveformPath(archivePath, blake3Hex))
    }

    fun applyLwwMutationsJson(requestJson: String): String {
        TonewiseCoreJNI.ensureLoaded()
        val json = TonewiseCoreJNI.nativeApplyLwwMutationsJson(requestJson)
        checkNoError(MiniJson.parse(json))
        return json
    }

    fun mergeSessionsJson(requestJson: String): String {
        TonewiseCoreJNI.ensureLoaded()
        val json = TonewiseCoreJNI.nativeMergeSessionsJson(requestJson)
        checkNoError(MiniJson.parse(json))
        return json
    }

    fun applyHeaderPatch(
        pfd: ParcelFileDescriptor,
        expectedOldHeaderHex: String,
        newHeaderBytes: ByteArray,
    ): String {
        TonewiseCoreJNI.ensureLoaded()
        val json =
            TonewiseCoreJNI.nativeApplyHeaderPatchFd(pfd.fd, expectedOldHeaderHex, newHeaderBytes)
        checkNoError(MiniJson.parse(json))
        return json
    }

    fun applyHeaderPatchPath(
        path: String,
        expectedOldHeaderHex: String,
        newHeaderBytes: ByteArray,
    ): String {
        TonewiseCoreJNI.ensureLoaded()
        val json =
            TonewiseCoreJNI.nativeApplyHeaderPatchPath(path, expectedOldHeaderHex, newHeaderBytes)
        checkNoError(MiniJson.parse(json))
        return json
    }

    private fun parseWaveformJson(json: String): FloatArray? {
        val parsed = MiniJson.parse(json) ?: return null
        checkNoError(parsed)
        @Suppress("UNCHECKED_CAST")
        val list = parsed as List<Any?>
        return FloatArray(list.size) { i -> ((list[i] as Number).toFloat() / 255f).coerceIn(0f, 1f) }
    }

    private fun parseFingerprint(json: String): AudioFrameFingerprint {
        @Suppress("UNCHECKED_CAST")
        val root = MiniJson.parse(json) as Map<String, Any?>
        checkNoError(root)
        return AudioFrameFingerprint(
            audioFrameBlake3Hex = root["audio_frame_blake3_hex"].toString(),
            headerBlake3Hex = root["header_blake3_hex"].toString(),
            headerSize = (root["header_size"] as Number).toLong(),
            fileSize = (root["file_size"] as Number).toLong(),
        )
    }

    private fun checkNoError(parsed: Any?) {
        if (parsed is Map<*, *> && parsed.containsKey("error")) {
            throw IllegalStateException("tonewise-core error: ${parsed["error"]}")
        }
    }

    private fun StringBuilder.appendJsonStringArray(values: List<String>) {
        append('[')
        values.forEachIndexed { idx, v ->
            if (idx > 0) append(',')
            append('"')
            for (ch in v) {
                when (ch) {
                    '"' -> append("\\\"")
                    '\\' -> append("\\\\")
                    '\b' -> append("\\b")
                    '\u000C' -> append("\\f")
                    '\n' -> append("\\n")
                    '\r' -> append("\\r")
                    '\t' -> append("\\t")
                    else -> {
                        if (ch.code < 0x20) {
                            append("\\u")
                            append(ch.code.toString(16).padStart(4, '0'))
                        } else {
                            append(ch)
                        }
                    }
                }
            }
            append('"')
        }
        append(']')
    }
}

/** Minimal strict RFC 8259 JSON parser for zero-dependency JVM/Android parity across FFI. */
internal object MiniJson {
    private const val MAX_DEPTH = 64

    fun parse(input: String): Any? = Parser(input).parseValue()

    private class Parser(private val src: String) {
        private var pos = 0
        private var depth = 0

        fun parseValue(): Any? {
            skipWhitespace()
            if (pos >= src.length) throw IllegalArgumentException("Unexpected end of JSON")
            return when (val ch = src[pos]) {
                '{' -> parseObject()
                '[' -> parseArray()
                '"' -> parseString()
                't' -> expectLiteral("true", true)
                'f' -> expectLiteral("false", false)
                'n' -> expectLiteral("null", null)
                '-',
                in '0'..'9' -> parseNumber()
                else -> throw IllegalArgumentException("Unexpected char '$ch' at $pos")
            }
        }

        private fun parseObject(): Map<String, Any?> {
            if (++depth > MAX_DEPTH) {
                throw IllegalArgumentException("JSON nesting depth exceeded $MAX_DEPTH")
            }
            try {
                pos++ // '{'
                skipWhitespace()
                val map = LinkedHashMap<String, Any?>()
                if (peek('}')) {
                    pos++
                    return map
                }
                while (true) {
                    skipWhitespace()
                    val key = parseString()
                    skipWhitespace()
                    expect(':')
                    val value = parseValue()
                    map[key] = value
                    skipWhitespace()
                    if (peek(',')) {
                        pos++
                    } else {
                        expect('}')
                        break
                    }
                }
                return map
            } finally {
                depth--
            }
        }

        private fun parseArray(): List<Any?> {
            if (++depth > MAX_DEPTH) {
                throw IllegalArgumentException("JSON nesting depth exceeded $MAX_DEPTH")
            }
            try {
                pos++ // '['
                skipWhitespace()
                val list = ArrayList<Any?>()
                if (peek(']')) {
                    pos++
                    return list
                }
                while (true) {
                    list.add(parseValue())
                    skipWhitespace()
                    if (peek(',')) {
                        pos++
                    } else {
                        expect(']')
                        break
                    }
                }
                return list
            } finally {
                depth--
            }
        }

        private fun parseString(): String {
            expect('"')
            val sb = StringBuilder()
            while (pos < src.length) {
                val ch = src[pos++]
                when (ch) {
                    '"' -> return sb.toString()
                    '\\' -> {
                        if (pos >= src.length) throw IllegalArgumentException("Bad escape")
                        when (val esc = src[pos++]) {
                            '"',
                            '\\',
                            '/' -> sb.append(esc)
                            'b' -> sb.append('\b')
                            'f' -> sb.append('\u000C')
                            'n' -> sb.append('\n')
                            'r' -> sb.append('\r')
                            't' -> sb.append('\t')
                            'u' -> {
                                val hex = src.substring(pos, pos + 4)
                                sb.append(hex.toInt(16).toChar())
                                pos += 4
                            }
                            else -> throw IllegalArgumentException("Unknown escape \\$esc")
                        }
                    }
                    else -> sb.append(ch)
                }
            }
            throw IllegalArgumentException("Unterminated string")
        }

        private fun parseNumber(): Number {
            val start = pos
            if (peek('-')) pos++
            while (pos < src.length && src[pos].isDigit()) pos++
            var isFloat = false
            if (peek('.')) {
                isFloat = true
                pos++
                while (pos < src.length && src[pos].isDigit()) pos++
            }
            if (peek('e') || peek('E')) {
                isFloat = true
                pos++
                if (peek('+') || peek('-')) pos++
                while (pos < src.length && src[pos].isDigit()) pos++
            }
            val raw = src.substring(start, pos)
            return if (isFloat) raw.toDouble() else raw.toLong()
        }

        private fun <T> expectLiteral(lit: String, value: T): T {
            if (!src.startsWith(lit, pos)) {
                throw IllegalArgumentException("Expected $lit at $pos")
            }
            pos += lit.length
            return value
        }

        private fun skipWhitespace() {
            while (pos < src.length && src[pos].isWhitespace()) pos++
        }

        private fun peek(ch: Char) = pos < src.length && src[pos] == ch

        private fun expect(ch: Char) {
            if (pos >= src.length || src[pos] != ch) {
                throw IllegalArgumentException("Expected '$ch' at $pos")
            }
            pos++
        }
    }
}
