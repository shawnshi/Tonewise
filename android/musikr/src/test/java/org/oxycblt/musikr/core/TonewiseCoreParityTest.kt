/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseCoreParityTest.kt is part of Tonewise.
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

import java.io.ByteArrayOutputStream
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/**
 * Verifies 100% parity between Kotlin (`:musikr`) and Rust (`tonewise-core`) over JNI using the
 * shared `protocol/fixtures/` golden test suite.
 */
class TonewiseCoreParityTest {
    private lateinit var repoRoot: File

    @Before
    fun loadNativeLibrary() {
        repoRoot = findRepoRoot()
        val dll = File(repoRoot, "core/target/release/tonewise_core.dll")
        val so = File(repoRoot, "core/target/release/libtonewise_core.so")
        val nativeLib =
            when {
                dll.isFile -> dll
                so.isFile -> so
                else -> null
            }
        assumeTrue("Desktop tonewise_core native library must be built before parity test", nativeLib != null)
        TonewiseCoreJNI.loadFromPath(nativeLib!!.absolutePath)
    }

    @Test
    fun abiVersion_andArtistWhitelistParity() {
        assertEquals(1, TonewiseCore.abiVersion())
        assertEquals(listOf("Above & Beyond"), TonewiseCore.splitArtists("Above & Beyond"))
        assertEquals(listOf("AC/DC", "Slash"), TonewiseCore.splitArtists("AC/DC / Slash"))
        assertEquals(listOf("卢家宏", "李宗盛"), TonewiseCore.splitArtists("卢家宏 / 李宗盛"))
    }

    @Test
    fun rulesGoldenFixtures_matchThroughJni() {
        val raw = File(repoRoot, "protocol/fixtures/rules_golden.json").readText(Charsets.UTF_8)
        @Suppress("UNCHECKED_CAST")
        val root = MiniJson.parse(raw) as Map<String, Any?>
        val tracksJson = toJson(root["tracks"])
        @Suppress("UNCHECKED_CAST")
        val cases = root["cases"] as List<Map<String, Any?>>

        for (case in cases) {
            val name = case["name"].toString()
            val sceneJson = toJson(case["scene"])
            @Suppress("UNCHECKED_CAST")
            val expected = (case["expected_track_uids"] as List<Any?>).map { it.toString() }

            val actual = TonewiseCore.evaluateScene(sceneJson, tracksJson)
            assertEquals("JNI rules golden mismatch on '$name'", expected, actual)
        }
    }

    @Test
    fun m3u8MergeGoldenFixtures_matchThroughJni() {
        val raw = File(repoRoot, "protocol/fixtures/m3u8_merge_golden.json").readText(Charsets.UTF_8)
        @Suppress("UNCHECKED_CAST")
        val root = MiniJson.parse(raw) as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val cases = root["cases"] as List<Map<String, Any?>>

        for (case in cases) {
            val name = case["name"].toString()
            @Suppress("UNCHECKED_CAST")
            val base = (case["base"] as List<Any?>).map { it.toString() }
            @Suppress("UNCHECKED_CAST")
            val ours = (case["ours"] as List<Any?>).map { it.toString() }
            @Suppress("UNCHECKED_CAST")
            val theirs = (case["theirs"] as List<Any?>).map { it.toString() }
            @Suppress("UNCHECKED_CAST")
            val expectedMerged = (case["expected_merged"] as List<Any?>).map { it.toString() }
            val expectedConflicts = (case["expected_conflict_count"] as Number).toInt()

            val result = TonewiseCore.mergeM3u8(base, ours, theirs)
            assertEquals("JNI m3u8 merge mismatch on '$name'", expectedMerged, result.merged)
            assertEquals(
                "JNI m3u8 conflict count mismatch on '$name'",
                expectedConflicts,
                result.conflicts.size,
            )
        }
    }

    @Test
    fun flacAudioFrameFingerprint_remainsStableAfterTagEdit() {
        val audio = "SYNTHETIC_FLAC_FRAMES_123456".toByteArray(Charsets.UTF_8)
        val file1 = File.createTempFile("tw_fp1_", ".flac")
        val file2 = File.createTempFile("tw_fp2_", ".flac")
        try {
            file1.writeBytes(buildFlac("DJ_SCENE=relax".toByteArray(), 8192, audio))
            file2.writeBytes(
                buildFlac("DJ_SCENE=relax;focus\u0000TEMPO=104".toByteArray(), 8172, audio)
            )

            val fp1 = TonewiseCore.audioFingerprintPath(file1.absolutePath)
            val fp2 = TonewiseCore.audioFingerprintPath(file2.absolutePath)

            assertEquals(64, fp1.audioFrameBlake3Hex.length)
            assertEquals(fp1.audioFrameBlake3Hex, fp2.audioFrameBlake3Hex)
            assertNotEquals(fp1.headerBlake3Hex, fp2.headerBlake3Hex)
            assertTrue(fp1.headerSize > 8192)
        } finally {
            file1.delete()
            file2.delete()
        }
    }

    @Test
    fun waveformArchiveLookup_throughJniMatchesRustBinaryFormat() {
        val fakeHash = ByteArray(32) { i -> (i + 1).toByte() }
        val points = ByteArray(150) { i -> ((i * 255) / 149).toByte() }
        val prefix = fakeHash.copyOfRange(0, 16)
        val archive = ByteArray(12 + 20 + 150)
        // "TWWF", version=1, points=150, count=1
        archive[0] = 'T'.code.toByte()
        archive[1] = 'W'.code.toByte()
        archive[2] = 'W'.code.toByte()
        archive[3] = 'F'.code.toByte()
        archive[4] = 1; archive[5] = 0
        archive[6] = 150.toByte(); archive[7] = 0
        archive[8] = 1; archive[9] = 0; archive[10] = 0; archive[11] = 0
        System.arraycopy(prefix, 0, archive, 12, 16)
        // index 0 -> archive[28..32] = 0
        System.arraycopy(points, 0, archive, 32, 150)

        val binFile = File.createTempFile("tw_wf_", ".bin")
        try {
            binFile.writeBytes(archive)
            val hex = fakeHash.joinToString("") { "%02x".format(it) }
            val result = TonewiseCore.lookupWaveformPath(binFile.absolutePath, hex)
            assertNotNull(result)
            assertEquals(150, result!!.size)
            assertEquals(0f, result[0], 0.01f)
            assertEquals(1f, result[149], 0.01f)

            val missingHex = "ff".repeat(32)
            assertNull(TonewiseCore.lookupWaveformPath(binFile.absolutePath, missingHex))
        } finally {
            binFile.delete()
        }
    }

    @Test
    fun mergeSessionsAndInPlaceHeaderPatch_workThroughJni() {
        val req =
            """{"local":[{"slot_index":1,"name":"Focus","scene_id":"focus","updated_at_ms":1000,"queue_track_uids":["u1"],"current_index":0,"position_ms":1000}],"remote":[{"slot_index":1,"name":"Focus","scene_id":"focus","updated_at_ms":2500,"queue_track_uids":["u1","u2"],"current_index":1,"position_ms":55000}]}"""
        val mergedJson = TonewiseCore.mergeSessionsJson(req)
        @Suppress("UNCHECKED_CAST")
        val merged = MiniJson.parse(mergedJson) as List<Map<String, Any?>>
        assertEquals(1, merged.size)
        assertEquals(55000L, (merged[0]["position_ms"] as Number).toLong())

        val flacFile = File.createTempFile("tw_patch_", ".flac")
        try {
            val audio = "PATCH_TEST_AUDIO_FRAMES".toByteArray(Charsets.UTF_8)
            flacFile.writeBytes(buildFlac("DJ_ENERGY=low".toByteArray(), 256, audio))
            val before = TonewiseCore.audioFingerprintPath(flacFile.absolutePath)

            val newHeader = buildFlac("DJ_ENERGY=medium".toByteArray(), 253, ByteArray(0))
            val patchResJson =
                TonewiseCore.applyHeaderPatchPath(
                    flacFile.absolutePath,
                    before.headerBlake3Hex,
                    newHeader,
                )
            @Suppress("UNCHECKED_CAST")
            val patchMap = MiniJson.parse(patchResJson) as Map<String, Any?>
            assertEquals("applied", patchMap["status"])

            val after = TonewiseCore.audioFingerprintPath(flacFile.absolutePath)
            assertEquals(before.audioFrameBlake3Hex, after.audioFrameBlake3Hex)
            assertNotEquals(before.headerBlake3Hex, after.headerBlake3Hex)
        } finally {
            flacFile.delete()
        }
    }

    private fun buildFlac(comment: ByteArray, paddingLen: Int, audio: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        out.write("fLaC".toByteArray(Charsets.US_ASCII))
        // STREAMINFO (34 bytes), not last
        out.write(0x00)
        out.write(byteArrayOf(0, 0, 34))
        out.write(ByteArray(34))
        // VORBIS_COMMENT (type 4), not last
        out.write(0x04)
        out.write(
            byteArrayOf(
                ((comment.size shr 16) and 0xFF).toByte(),
                ((comment.size shr 8) and 0xFF).toByte(),
                (comment.size and 0xFF).toByte(),
            )
        )
        out.write(comment)
        // PADDING (type 1), last
        out.write(0x81)
        out.write(
            byteArrayOf(
                ((paddingLen shr 16) and 0xFF).toByte(),
                ((paddingLen shr 8) and 0xFF).toByte(),
                (paddingLen and 0xFF).toByte(),
            )
        )
        out.write(ByteArray(paddingLen))
        out.write(audio)
        return out.toByteArray()
    }

    private fun findRepoRoot(): File {
        var cur = File(System.getProperty("user.dir") ?: ".").canonicalFile
        while (cur.parentFile != null) {
            if (File(cur, "protocol/fixtures/rules_golden.json").isFile) {
                return cur
            }
            cur = cur.parentFile
        }
        error("Could not locate Tonewise repo root from ${System.getProperty("user.dir")}")
    }

    private fun toJson(value: Any?): String =
        when (value) {
            null -> "null"
            is Boolean,
            is Number -> value.toString()
            is String ->
                buildString {
                    append('"')
                    for (ch in value) {
                        when (ch) {
                            '"' -> append("\\\"")
                            '\\' -> append("\\\\")
                            '\n' -> append("\\n")
                            '\r' -> append("\\r")
                            '\t' -> append("\\t")
                            else -> append(ch)
                        }
                    }
                    append('"')
                }
            is List<*> -> value.joinToString(prefix = "[", postfix = "]", separator = ",") { toJson(it) }
            is Map<*, *> ->
                value.entries.joinToString(prefix = "{", postfix = "}", separator = ",") { (k, v) ->
                    "${toJson(k.toString())}:${toJson(v)}"
                }
            else -> toJson(value.toString())
        }
}
