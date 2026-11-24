/*
 * Copyright (c) 2026 Tonewise Project
 * HanziPinyinConverter.kt is part of Tonewise.
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

package com.shawnshi.tonewise.music

/**
 * Lightweight converter from CJK Unified Ideographs (U+4E00..U+9FFF)
 * to compact tone-stripped Pinyin strings, backed by the bundled ICU-generated table
 * and curated song-title polyphone variants.
 */
internal object HanziPinyinConverter {
    private const val CJK_START = '\u4E00'
    private const val CJK_END = '\u9FFF'
    private const val CJK_COUNT = CJK_END.code - CJK_START.code + 1
    private const val RESOURCE_PATH = "/tonewise/hanzi_pinyin_table.txt"

    private val POLYPHONE_OVERRIDES: Map<Char, Array<String>> =
        hashMapOf(
            '长' to arrayOf("chang", "zhang"),
            '乐' to arrayOf("le", "yue"),
            '还' to arrayOf("hai", "huan"),
            '行' to arrayOf("xing", "hang"),
            '重' to arrayOf("zhong", "chong"),
            '觉' to arrayOf("jue", "jiao"),
            '调' to arrayOf("diao", "tiao"),
            '着' to arrayOf("zhe", "zhao"),
            '了' to arrayOf("le", "liao"),
            '都' to arrayOf("dou", "du"),
            '的' to arrayOf("de", "di"),
            '地' to arrayOf("di", "de"),
            '得' to arrayOf("de", "dei"),
            '朝' to arrayOf("chao", "zhao"),
            '传' to arrayOf("chuan", "zhuan"),
            '弹' to arrayOf("tan", "dan"),
            '藏' to arrayOf("cang", "zang"),
            '曾' to arrayOf("ceng", "zeng"),
            '单' to arrayOf("dan", "shan"),
            '落' to arrayOf("luo", "la"),
            '血' to arrayOf("xue", "xie"),
            '露' to arrayOf("lu", "lou"),
        )

    private class PinyinTable(
        val syllables: Array<String>,
        val charToSyllable: IntArray,
    )

    private val table: PinyinTable by lazy { loadTable() }

    private fun loadTable(): PinyinTable {
        val stream =
            HanziPinyinConverter::class.java.getResourceAsStream(RESOURCE_PATH)
                ?: throw IllegalStateException("Missing bundled resource: $RESOURCE_PATH")
        val lines =
            stream.bufferedReader(Charsets.UTF_8).use { reader ->
                val header = reader.readLine().orEmpty()
                val data = reader.readLine().orEmpty()
                header to data
            }
        val syllables = lines.first.split(',').toTypedArray()
        val rawBytes = decodeBase64(lines.second)
        val charToSyllable = IntArray(CJK_COUNT)
        val count = minOf(CJK_COUNT, rawBytes.size / 2)
        for (i in 0 until count) {
            val lo = rawBytes[i * 2].toInt() and 0xFF
            val hi = rawBytes[i * 2 + 1].toInt() and 0xFF
            charToSyllable[i] = lo or (hi shl 8)
        }
        return PinyinTable(syllables, charToSyllable)
    }

    private fun decodeBase64(input: String): ByteArray {
        val clean = input.filter { !it.isWhitespace() }
        if (clean.isEmpty()) return ByteArray(0)
        val padding =
            when {
                clean.endsWith("==") -> 2
                clean.endsWith("=") -> 1
                else -> 0
            }
        val out = ByteArray((clean.length * 3) / 4 - padding)
        var outIdx = 0
        var i = 0
        while (i + 3 < clean.length) {
            val c0 = base64Value(clean[i])
            val c1 = base64Value(clean[i + 1])
            val c2 = base64Value(clean[i + 2])
            val c3 = base64Value(clean[i + 3])
            val block = (c0 shl 18) or (c1 shl 12) or (c2 shl 6) or c3
            if (outIdx < out.size) out[outIdx++] = ((block ushr 16) and 0xFF).toByte()
            if (clean[i + 2] != '=' && outIdx < out.size) out[outIdx++] = ((block ushr 8) and 0xFF).toByte()
            if (clean[i + 3] != '=' && outIdx < out.size) out[outIdx++] = (block and 0xFF).toByte()
            i += 4
        }
        return out
    }

    private fun base64Value(c: Char): Int =
        when (c) {
            in 'A'..'Z' -> c.code - 'A'.code
            in 'a'..'z' -> c.code - 'a'.code + 26
            in '0'..'9' -> c.code - '0'.code + 52
            '+' -> 62
            '/' -> 63
            '=' -> 0
            else -> throw IllegalArgumentException("Invalid Base64 character: $c")
        }

    fun containsHanzi(s: String): Boolean {
        for (c in s) {
            if (c in CJK_START..CJK_END) return true
        }
        return false
    }

    fun isPureAsciiAlphaNum(s: String): Boolean {
        if (s.isEmpty()) return false
        for (c in s) {
            val ok = (c in 'a'..'z') || (c in '0'..'9')
            if (!ok) return false
        }
        return true
    }

    fun toCompactPinyinVariants(
        normalized: String,
        maxVariants: Int = 4,
    ): List<String> {
        if (normalized.isEmpty() || !containsHanzi(normalized) || maxVariants <= 0) {
            return emptyList()
        }
        val loaded = table
        val builders = ArrayList<StringBuilder>(maxVariants)
        builders.add(StringBuilder(normalized.length * 4))

        for (c in normalized) {
            if (c in CJK_START..CJK_END) {
                val readings = POLYPHONE_OVERRIDES[c]
                if (readings != null && readings.size > 1 && builders.size < maxVariants) {
                    val currentCount = builders.size
                    for (ri in 1 until readings.size) {
                        if (builders.size >= maxVariants) break
                        val alt = readings[ri]
                        for (bi in 0 until currentCount) {
                            if (builders.size >= maxVariants) break
                            val copy = StringBuilder(builders[bi].toString())
                            copy.append(alt)
                            builders.add(copy)
                        }
                    }
                    val primary = readings[0]
                    for (bi in 0 until currentCount) {
                        builders[bi].append(primary)
                    }
                } else {
                    val id = loaded.charToSyllable[c.code - CJK_START.code]
                    if (id > 0 && id < loaded.syllables.size) {
                        val syll = loaded.syllables[id]
                        for (sb in builders) {
                            sb.append(syll)
                        }
                    }
                }
            } else if ((c in 'a'..'z') || (c in '0'..'9')) {
                for (sb in builders) {
                    sb.append(c)
                }
            }
        }

        val result = ArrayList<String>(builders.size)
        val seen = HashSet<String>(builders.size)
        for (sb in builders) {
            if (sb.isEmpty()) continue
            val value = sb.toString()
            if (seen.add(value)) {
                result.add(value)
            }
        }
        return result
    }
}
