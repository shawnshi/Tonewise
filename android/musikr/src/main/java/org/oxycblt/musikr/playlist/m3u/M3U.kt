/*
 * Copyright (c) 2023 Auxio Project
 * M3U.kt is part of Auxio.
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
 
package org.oxycblt.musikr.playlist.m3u

import android.content.Context
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStream
import java.io.OutputStream
import java.io.StringReader
import java.net.URLDecoder
import java.nio.ByteBuffer
import java.nio.charset.Charset
import java.nio.charset.CodingErrorAction
import java.text.Normalizer
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.fs.Path
import org.oxycblt.musikr.fs.Volume
import org.oxycblt.musikr.fs.path.VolumeManager
import org.oxycblt.musikr.playlist.ExportConfig
import org.oxycblt.musikr.playlist.ImportedPlaylist
import org.oxycblt.musikr.playlist.ImportedPlaylistEntry
import org.oxycblt.musikr.playlist.PossiblePaths
import org.oxycblt.musikr.tag.Name
import org.oxycblt.musikr.util.correctWhitespace
import org.oxycblt.musikr.util.unlikelyToBeNull

/**
 * Minimal M3U file format implementation.
 *
 * @author Alexander Capehart (OxygenCobalt)
 */
abstract class M3U {
    /**
     * Reads an M3U file from the given [stream] and returns a [ImportedPlaylist] containing the
     * paths to the files listed in the M3U file.
     *
     * @param stream The stream to read the M3U file from.
     * @param workingDirectory The directory that the M3U file is contained in. This is used to
     *   resolve relative paths.
     * @return An [ImportedPlaylist] containing the paths to the files listed in the M3U file,
     */
    internal abstract fun read(stream: InputStream, workingDirectory: Path): ImportedPlaylist?

    /**
     * Writes the given [playlist] to the given [outputStream] in the M3U format,.
     *
     * @param playlist The playlist to write.
     * @param outputStream The stream to write the M3U file to.
     * @param workingDirectory The directory that the M3U file is contained in. This is used to
     *   create relative paths to where the M3U file is assumed to be stored.
     * @param config The configuration to use when exporting the playlist.
     */
    internal abstract fun write(
        playlist: Playlist,
        outputStream: OutputStream,
        workingDirectory: Path,
        config: ExportConfig,
    )

    companion object {
        /** The mime type used for M3U files by the android system. */
        const val MIME_TYPE = "audio/x-mpegurl"

        private val M3U_MIME_TYPES =
            setOf(
                MIME_TYPE,
                "audio/mpegurl",
                "application/vnd.apple.mpegurl",
                "application/x-mpegurl",
                "application/mpegurl",
            )

        fun isM3uFile(fileName: String?, mimeType: String?): Boolean {
            val normalizedName = fileName?.lowercase()
            if (normalizedName != null) {
                if (normalizedName.startsWith(".")) {
                    return false
                }
                if (normalizedName.endsWith(".m3u") || normalizedName.endsWith(".m3u8")) {
                    return true
                }
            }
            val normalizedMime = mimeType?.lowercase()
            return normalizedMime != null && normalizedMime in M3U_MIME_TYPES
        }

        fun isInPlaylistDirectory(path: Path): Boolean =
            path.directory.components.components.any {
                it.equals("playlist", ignoreCase = true) ||
                    it.equals("playlists", ignoreCase = true)
            }

        internal fun from(context: Context): M3U = M3UImpl(VolumeManager.from(context))

        internal fun from(volumeManager: VolumeManager): M3U = M3UImpl(volumeManager)
    }
}

private class M3UImpl(private val volumeManager: VolumeManager) : M3U() {
    override fun read(stream: InputStream, workingDirectory: Path): ImportedPlaylist? {
        val volumes = volumeManager.getVolumes()
        val decodedText = decodePlaylistBytes(stream.readBytes())
        val reader = BufferedReader(StringReader(decodedText))
        val entries = mutableListOf<ImportedPlaylistEntry>()
        var name: String? = null
        var isFirstLine = true
        var pendingExtInfTitle: String? = null
        var pendingExtInfArtist: String? = null
        var pendingDurationSec = 0
        var pendingAlbum: String? = null

        fun resetPendingMeta() {
            pendingExtInfTitle = null
            pendingExtInfArtist = null
            pendingDurationSec = 0
            pendingAlbum = null
        }

        consumeFile@ while (true) {
            var path: String? = null
            collectMetadata@ while (true) {
                var rawLine = reader.readLine() ?: break@consumeFile
                if (isFirstLine) {
                    isFirstLine = false
                    rawLine = rawLine.removePrefix("\uFEFF")
                }
                val currentLine = rawLine.correctWhitespace() ?: continue@collectMetadata
                if (currentLine.startsWith("#")) {
                    val split = currentLine.split(":", limit = 2)
                    val directive = split[0].uppercase()
                    val payload = split.getOrNull(1)?.correctWhitespace()
                    when (directive) {
                        "#PLAYLIST" -> name = payload
                        "#EXTALB" -> pendingAlbum = payload
                        "#EXTINF" -> {
                            if (payload != null) {
                                val commaIdx = payload.indexOf(',')
                                if (commaIdx >= 0) {
                                    pendingDurationSec =
                                        payload.substring(0, commaIdx).trim().toIntOrNull()?.coerceAtLeast(0)
                                            ?: 0
                                    val label = payload.substring(commaIdx + 1).trim()
                                    val (artist, title) = splitExtInfLabel(label)
                                    pendingExtInfArtist = artist
                                    pendingExtInfTitle = title
                                } else {
                                    pendingExtInfTitle = payload
                                    pendingExtInfArtist = null
                                }
                            }
                        }
                        "#EXTTW-UNMATCHED" -> {
                            val rawPayload = rawLine.trim().substringAfter(':', "")
                            val parts = rawPayload.split('\t')
                            val artist = parts.getOrNull(0)?.trim()?.takeIf { it.isNotEmpty() } ?: pendingExtInfArtist
                            val title = parts.getOrNull(1)?.trim()?.takeIf { it.isNotEmpty() } ?: pendingExtInfTitle
                            val album = parts.getOrNull(2)?.trim()?.takeIf { it.isNotEmpty() } ?: pendingAlbum
                            if (!title.isNullOrEmpty() || !artist.isNullOrEmpty()) {
                                entries.add(
                                    ImportedPlaylistEntry(
                                        paths = emptyList(),
                                        title = title,
                                        artist = artist,
                                        album = album,
                                        durationSec = pendingDurationSec,
                                    )
                                )
                            }
                            resetPendingMeta()
                        }
                        else -> {}
                    }
                } else {
                    path = currentLine
                    break@collectMetadata
                }
            }

            val interpretations = interpretPath(unlikelyToBeNull(path))
            val possibilities = interpretations.flatMap {
                expandInterpretation(it, workingDirectory, volumes)
            }

            entries.add(
                ImportedPlaylistEntry(
                    paths = possibilities,
                    title = pendingExtInfTitle,
                    artist = pendingExtInfArtist,
                    album = pendingAlbum,
                    durationSec = pendingDurationSec,
                )
            )
            resetPendingMeta()
        }

        return if (entries.isNotEmpty()) {
            ImportedPlaylist(name, entries.map { it.paths }, entries)
        } else {
            null
        }
    }

    private fun decodePlaylistBytes(bytes: ByteArray): String {
        if (bytes.size >= 2 && bytes[0] == 0xFF.toByte() && bytes[1] == 0xFE.toByte()) {
            return String(bytes, Charsets.UTF_16LE)
        }
        if (bytes.size >= 2 && bytes[0] == 0xFE.toByte() && bytes[1] == 0xFF.toByte()) {
            return String(bytes, Charsets.UTF_16BE)
        }
        val utf8Decoder =
            Charsets.UTF_8
                .newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
        return try {
            utf8Decoder.decode(ByteBuffer.wrap(bytes)).toString()
        } catch (_: Exception) {
            val gbk = runCatching { Charset.forName("GB18030") }.getOrNull() ?: Charsets.UTF_8
            String(bytes, gbk)
        }
    }

    private fun splitExtInfLabel(label: String): Pair<String?, String?> {
        for (sep in listOf(" - ", " – ", " — ", " —— ", " － ")) {
            val idx = label.indexOf(sep)
            if (idx > 0 && idx + sep.length < label.length) {
                val left = label.substring(0, idx).trim()
                val right = label.substring(idx + sep.length).trim()
                if (left.isNotEmpty() && right.isNotEmpty()) {
                    return left to right
                }
            }
        }
        return null to label.trim().takeIf { it.isNotEmpty() }
    }

    private fun normalizeRawPlaylistPath(raw: String): String {
        var s = raw.trim()
        if (s.startsWith("file://", ignoreCase = true)) {
            s = s.substring("file://".length)
            if (s.startsWith("/") && s.length >= 3 && s[2] == ':') {
                s = s.substring(1)
            }
        }
        if (PERCENT_HEX_REGEX.containsMatchIn(s)) {
            s =
                runCatching { URLDecoder.decode(s.replace("+", "%2B"), "UTF-8") }
                    .getOrDefault(s)
        }
        return Normalizer.normalize(s, Normalizer.Form.NFC)
    }

    private data class InterpretedPath(val components: Components, val likelyAbsolute: Boolean)

    private fun interpretPath(rawPath: String): List<InterpretedPath> {
        val path = normalizeRawPlaylistPath(rawPath)
        return when {
            path.startsWith('/') -> listOf(InterpretedPath(Components.parseUnix(path), true))
            path.startsWith("./") -> listOf(InterpretedPath(Components.parseUnix(path), false))
            path.matches(WINDOWS_VOLUME_PREFIX_REGEX) ->
                listOf(InterpretedPath(Components.parseWindows(path.substring(2)), true))
            path.startsWith("\\") -> listOf(InterpretedPath(Components.parseWindows(path), true))
            path.startsWith(".\\") -> listOf(InterpretedPath(Components.parseWindows(path), false))
            else ->
                listOf(
                    InterpretedPath(Components.parseUnix(path), false),
                    InterpretedPath(Components.parseWindows(path), true),
                )
        }
    }

    private fun expandInterpretation(
        path: InterpretedPath,
        workingDirectory: Path,
        volumes: List<Volume>,
    ): List<Path> {
        val absoluteInterpretation = Path(workingDirectory.volume, path.components)
        val relativeInterpretation =
            Path(workingDirectory.volume, path.components.absoluteTo(workingDirectory.components))
        val volumeExactMatch = volumes.find { it.components?.contains(path.components) == true }
        val volumeInterpretation = volumeExactMatch?.let {
            val components =
                unlikelyToBeNull(volumeExactMatch.components).containing(path.components)
            Path(volumeExactMatch, components)
        }
        return if (path.likelyAbsolute) {
            listOfNotNull(volumeInterpretation, absoluteInterpretation, relativeInterpretation)
        } else {
            listOfNotNull(relativeInterpretation, volumeInterpretation, absoluteInterpretation)
        }
    }

    override fun write(
        playlist: Playlist,
        outputStream: OutputStream,
        workingDirectory: Path,
        config: ExportConfig,
    ) {
        val writer = outputStream.bufferedWriter()
        // Try to be as compliant to the spec as possible while also cramming it full of extensions
        // I imagine other players will use.
        writer.writeLine("#EXTM3U")
        writer.writeLine("#EXTENC:UTF-8")
        writer.writeLine("#PLAYLIST:${playlist.name.raw}")
        for (song in playlist.songs) {
            val durationSec = (song.durationMs / 1000L).coerceAtLeast(0L)
            val artistNames =
                song.artists.mapNotNull { (it.name as? Name.Known)?.raw }.filter { it.isNotBlank() }
            val extinfTitle =
                if (artistNames.isNotEmpty()) {
                    "${artistNames.joinToString(", ")} - ${song.name.raw}"
                } else {
                    song.name.raw
                }
            writer.writeLine("#EXTINF:$durationSec,$extinfTitle")
            val albumName = song.album.name
            if (albumName is Name.Known) {
                writer.writeLine("#EXTALB:${albumName.raw}")
            }
            // TODO: See if repeating #EXTART and #EXTGEN is legal
            for (artist in song.artists) {
                val name = artist.name
                if (name is Name.Known) {
                    writer.writeLine("#EXTART:${name.raw}")
                }
            }
            for (genre in song.genres) {
                val name = genre.name
                if (name is Name.Known) {
                    writer.writeLine("#EXTGEN:${name.raw}")
                }
            }

            val formattedPath =
                if (config.absolute) {
                    // The path is already absolute in this case, but we need to prefix and separate
                    // it differently depending on the setting.
                    if (config.windowsPaths) {
                        // Assume the plain windows C volume, since that's probably where most music
                        // libraries are on a windows PC.
                        "C:\\\\${song.path.volume.components?.let { it.windowsString + "\\" } ?: ""}${song.path.components.windowsString}"
                    } else {
                        "/${song.path.volume.components?.let { it.unixString + "/" } ?: ""}${song.path.components.unixString}"
                    }
                } else {
                    // First need to make this path relative to the working directory of the M3U
                    // file, and then format it with the correct separators.
                    val relativePath = song.path.components.relativeTo(workingDirectory.components)
                    if (config.windowsPaths) {
                        relativePath.windowsString
                    } else {
                        relativePath.unixString
                    }
                }
            writer.writeLine(formattedPath)
        }
        writer.flush()
    }

    private fun BufferedWriter.writeLine(line: String) {
        write(line)
        newLine()
    }

    private fun Components.absoluteTo(workingDirectory: Components): Components {
        var absoluteComponents = workingDirectory
        for (component in components) {
            when (component) {
                // Parent specifier, go "back" one directory (in practice cleave off the last
                // component)
                ".." -> absoluteComponents = absoluteComponents.parent()
                // Current directory, the components are already there.
                "." -> {}
                // New directory, add it
                else -> absoluteComponents = absoluteComponents.child(component)
            }
        }
        return absoluteComponents
    }

    private fun Components.relativeTo(workingDirectory: Components): Components {
        // We want to find the common prefix of the working directory and path, and then
        // and them combine them with the correct relative elements to make sure they
        // resolve the same.
        var commonIndex = 0
        while (
            commonIndex < components.size &&
                commonIndex < workingDirectory.components.size &&
                components[commonIndex] == workingDirectory.components[commonIndex]
        ) {
            ++commonIndex
        }

        var relativeComponents = Components.parseUnix(".")

        // TODO: Simplify this logic
        when (commonIndex) {
            components.size if commonIndex == workingDirectory.components.size -> {
                // The paths are the same. This shouldn't occur.
            }
            components.size -> {
                // The working directory is deeper in the path, backtrack.
                for (i in 0..<workingDirectory.components.size - commonIndex) {
                    relativeComponents = relativeComponents.child("..")
                }
            }
            workingDirectory.components.size -> {
                // Working directory is shallower than the path, can just append the
                // non-common remainder of the path
                relativeComponents = relativeComponents.child(depth(commonIndex))
            }
            else -> {
                // The paths are siblings. Backtrack and append as needed.
                for (i in 0..<workingDirectory.components.size - commonIndex) {
                    relativeComponents = relativeComponents.child("..")
                }
                relativeComponents = relativeComponents.child(depth(commonIndex))
            }
        }

        return relativeComponents
    }

    private companion object {
        val WINDOWS_VOLUME_PREFIX_REGEX = Regex("^[A-Za-z]:\\\\.*")
        val PERCENT_HEX_REGEX = Regex("%[0-9a-fA-F]{2}")
    }
}
