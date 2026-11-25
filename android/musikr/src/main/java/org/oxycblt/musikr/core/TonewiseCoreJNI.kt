/*
 * Copyright (c) 2026 Tonewise Project
 * TonewiseCoreJNI.kt is part of Tonewise.
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

internal object TonewiseCoreJNI {
    @Volatile
    var isLoaded: Boolean = false
        private set

    fun ensureLoaded() {
        if (isLoaded) return
        synchronized(this) {
            if (!isLoaded) {
                System.loadLibrary("tonewise_core")
                try {
                    verifyAbiVersion()
                    isLoaded = true
                } catch (t: Throwable) {
                    isLoaded = false
                    throw t
                }
            }
        }
    }

    fun loadFromPath(absolutePath: String) {
        synchronized(this) {
            if (!isLoaded) {
                System.load(absolutePath)
                try {
                    verifyAbiVersion()
                    isLoaded = true
                } catch (t: Throwable) {
                    isLoaded = false
                    throw t
                }
            }
        }
    }

    private fun verifyAbiVersion() {
        val actual = nativeAbiVersion()
        TonewiseCore.checkAbiVersionMatch(actual)
    }

    external fun nativeAbiVersion(): Int

    external fun nativeSplitArtistsJson(raw: String): String

    external fun nativeEvaluateSceneJson(sceneJson: String, tracksJson: String): String

    external fun nativeMergeM3u8Json(requestJson: String): String

    external fun nativeAudioFingerprintFd(fd: Int): String

    external fun nativeAudioFingerprintPath(path: String): String

    external fun nativeLookupWaveformFd(fd: Int, blake3Hex: String): String

    external fun nativeLookupWaveformPath(archivePath: String, blake3Hex: String): String

    external fun nativeApplyLwwMutationsJson(requestJson: String): String

    external fun nativeMergeSessionsJson(requestJson: String): String

    external fun nativeApplyHeaderPatchFd(
        fd: Int,
        expectedOldHex: String,
        newHeader: ByteArray,
    ): String

    external fun nativeApplyHeaderPatchPath(
        path: String,
        expectedOldHex: String,
        newHeader: ByteArray,
    ): String

    external fun nativeResolveAutoEqJson(requestJson: String): String

    external fun nativeComposeDspJson(requestJson: String): String

    external fun nativeDefaultAutoEqCatalogJson(): String
}
