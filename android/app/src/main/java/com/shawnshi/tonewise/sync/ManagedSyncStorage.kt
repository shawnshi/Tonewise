/*
 * Copyright (c) 2026 Tonewise Project
 * ManagedSyncStorage.kt is part of Tonewise.
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

import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import com.shawnshi.tonewise.music.MusicSettings
import com.shawnshi.tonewise.music.locations.LocationMode
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.io.IOException
import javax.inject.Inject
import javax.inject.Singleton
import org.oxycblt.musikr.fs.Location
import timber.log.Timber as L

data class CommittedSyncFile(
    val relativePath: String,
    val documentUri: String,
    val fileSize: Long,
)

@Singleton
class ManagedSyncStorage
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val musicSettings: MusicSettings,
) {
    private val resolver = context.contentResolver

    fun commitAudioFile(track: OutboxTrackDto, partFile: File): CommittedSyncFile {
        if (!partFile.isFile) {
            throw IOException("临时文件不存在: ${partFile.name}")
        }
        val segments =
            LanSyncV2Client.sanitizeRelativePathSegments(track.targetRelPath).ifEmpty {
                listOf("Unsorted", "${track.trackUid}.flac")
            }
        val safeRelPath = segments.joinToString("/")
        val fileName = segments.last()
        val parentDirs = segments.dropLast(1)
        val mime = resolveAudioMimeType(fileName)

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            return commitAudioViaSaf(safLocation, parentDirs, fileName, safeRelPath, mime, partFile)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            return commitAudioViaMediaStore(track, parentDirs, fileName, safeRelPath, mime, partFile)
        }

        return commitAudioLegacy(parentDirs, fileName, safeRelPath, partFile)
    }

    fun commitAlbumCoverIfMissing(targetRelPath: String, coverBytes: ByteArray) {
        if (coverBytes.isEmpty()) return
        val segments = LanSyncV2Client.sanitizeRelativePathSegments(targetRelPath)
        val parentDirs = segments.dropLast(1)

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            runCatching {
                val rootUri = safLocation.uri
                val tonewiseDocId = ensureSafTonewiseRootDocId(safLocation)
                val targetDirId = ensureSafDirectoryPath(rootUri, tonewiseDocId, parentDirs)
                val existingCover = findSafChild(rootUri, targetDirId, "cover.jpg")
                if (existingCover == null) {
                    val createdUri =
                        DocumentsContract.createDocument(
                            resolver,
                            DocumentsContract.buildDocumentUriUsingTree(rootUri, targetDirId),
                            "image/jpeg",
                            "cover.jpg",
                        )
                    if (createdUri != null) {
                        resolver.openOutputStream(createdUri, "w")?.use { out -> out.write(coverBytes) }
                    }
                }
            }.onFailure { L.w(it, "SAF cover write failed for $targetRelPath") }
            return
        }

        // Always cache cover inside app private dir in MediaStore mode (MediaStore.Images disallows Music/ path)
        runCatching {
            val cacheDir = File(context.filesDir, ".tonewise/covers")
            cacheDir.mkdirs()
            val safeKey =
                (if (parentDirs.isNotEmpty()) parentDirs.joinToString("_") else "root")
                    .take(120)
            File(cacheDir, "$safeKey.jpg").writeBytes(coverBytes)
        }

        // Best-effort direct file write for external Music/Tonewise folder
        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            var dir = File(musicDir, "Tonewise")
            for (seg in parentDirs) dir = File(dir, seg)
            if (dir.exists() || dir.mkdirs()) {
                val coverFile = File(dir, "cover.jpg")
                if (!coverFile.exists()) coverFile.writeBytes(coverBytes)
            }
        }
    }

    fun commitPlaylistM3u8(name: String, relPaths: List<String>) {
        val safeName = LanSyncRepository.sanitizePlaylistFilename(name)
        val body =
            buildString {
                append("#EXTM3U\n")
                append("#PLAYLIST:").append(name.replace('\r', ' ').replace('\n', ' ').trim()).append('\n')
                for (rel in relPaths) {
                    val cleanSegs = LanSyncV2Client.sanitizeRelativePathSegments(rel)
                    if (cleanSegs.isNotEmpty()) {
                        append(cleanSegs.joinToString("/")).append('\n')
                    }
                }
            }
        val payload = body.toByteArray(Charsets.UTF_8)

        runCatching {
            val internalDir = File(context.filesDir, ".tonewise/playlists")
            internalDir.mkdirs()
            File(internalDir, "$safeName.m3u8").writeBytes(payload)
        }

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            runCatching {
                val rootUri = safLocation.uri
                val tonewiseDocId = ensureSafTonewiseRootDocId(safLocation)
                val playlistsDirId = ensureSafDirectoryPath(rootUri, tonewiseDocId, listOf("Playlists"))
                val fileName = "$safeName.m3u8"
                val existing = findSafChild(rootUri, playlistsDirId, fileName)
                val targetUri =
                    existing?.let { DocumentsContract.buildDocumentUriUsingTree(rootUri, it.docId) }
                        ?: DocumentsContract.createDocument(
                            resolver,
                            DocumentsContract.buildDocumentUriUsingTree(rootUri, playlistsDirId),
                            "audio/x-mpegurl",
                            fileName,
                        )
                if (targetUri != null) {
                    resolver.openOutputStream(targetUri, "wt")?.use { out -> out.write(payload) }
                }
            }.onFailure { L.w(it, "SAF playlist write failed for $safeName") }
            return
        }

        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            val extDir = File(musicDir, "Tonewise/Playlists")
            if (extDir.exists() || extDir.mkdirs()) {
                File(extDir, "$safeName.m3u8").writeBytes(payload)
            }
        }
    }

    fun existsInManagedStorage(entity: SyncedTrackEntity): Boolean {
        val rawUri = entity.documentUri?.takeIf { it.isNotBlank() } ?: return false
        return try {
            if (rawUri.startsWith("content://")) {
                val uri = Uri.parse(rawUri)
                resolver.openFileDescriptor(uri, "r")?.use { pfd ->
                    val size = pfd.statSize
                    entity.fileSize <= 0L || size <= 0L || size == entity.fileSize
                } ?: false
            } else {
                val f = File(rawUri)
                f.isFile && (entity.fileSize <= 0L || f.length() == entity.fileSize)
            }
        } catch (_: Exception) {
            false
        }
    }

    private fun commitAudioViaSaf(
        location: Location.Opened,
        parentDirs: List<String>,
        fileName: String,
        safeRelPath: String,
        mime: String,
        partFile: File,
    ): CommittedSyncFile {
        val rootUri = location.uri
        val tonewiseRootId = ensureSafTonewiseRootDocId(location)
        val parentDocId = ensureSafDirectoryPath(rootUri, tonewiseRootId, parentDirs)

        val expectedSize = partFile.length()
        val existing = findSafChild(rootUri, parentDocId, fileName)
        if (existing != null && existing.size == expectedSize && expectedSize > 0L) {
            val docUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, existing.docId)
            partFile.delete()
            return CommittedSyncFile(safeRelPath, docUri.toString(), expectedSize)
        }

        val targetDocUri =
            if (existing != null) {
                DocumentsContract.buildDocumentUriUsingTree(rootUri, existing.docId)
            } else {
                DocumentsContract.createDocument(
                    resolver,
                    DocumentsContract.buildDocumentUriUsingTree(rootUri, parentDocId),
                    mime,
                    fileName,
                ) ?: throw IOException("无法在 SAF 目录中创建文件: $safeRelPath")
            }

        resolver.openOutputStream(targetDocUri, "wt")?.use { out ->
            partFile.inputStream().use { input -> input.copyTo(out) }
            out.flush()
        } ?: throw IOException("无法写入 SAF 目标文件: $safeRelPath")

        partFile.delete()
        return CommittedSyncFile(safeRelPath, targetDocUri.toString(), expectedSize)
    }

    private fun commitAudioViaMediaStore(
        track: OutboxTrackDto,
        parentDirs: List<String>,
        fileName: String,
        safeRelPath: String,
        mime: String,
        partFile: File,
    ): CommittedSyncFile {
        val collection =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            } else {
                MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
            }
        val relativeDir =
            buildString {
                append(Environment.DIRECTORY_MUSIC).append("/Tonewise/")
                if (parentDirs.isNotEmpty()) {
                    append(parentDirs.joinToString("/")).append('/')
                }
            }
        val expectedSize = partFile.length()

        findExistingMediaStoreEntry(collection, relativeDir, fileName)?.let { (existingUri, existingSize, isPending) ->
            if (isPending == 0 && existingSize == expectedSize && expectedSize > 0L) {
                partFile.delete()
                return CommittedSyncFile(safeRelPath, existingUri.toString(), expectedSize)
            }
            runCatching { resolver.delete(existingUri, null, null) }
        }

        val values =
            ContentValues().apply {
                put(MediaStore.Audio.Media.DISPLAY_NAME, fileName)
                put(MediaStore.Audio.Media.MIME_TYPE, mime)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    put(MediaStore.Audio.Media.RELATIVE_PATH, relativeDir)
                    put(MediaStore.Audio.Media.IS_PENDING, 1)
                }
                if (track.title.isNotBlank()) put(MediaStore.Audio.Media.TITLE, track.title)
                if (track.artist.isNotBlank()) put(MediaStore.Audio.Media.ARTIST, track.artist)
                if (track.album.isNotBlank()) put(MediaStore.Audio.Media.ALBUM, track.album)
            }

        val itemUri =
            resolver.insert(collection, values)
                ?: throw IOException("无法通过 MediaStore 创建目标音频: $safeRelPath")

        try {
            resolver.openOutputStream(itemUri, "w")?.use { out ->
                partFile.inputStream().use { input -> input.copyTo(out) }
                out.flush()
            } ?: throw IOException("无法打开 MediaStore 输出流: $safeRelPath")

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val publish = ContentValues().apply { put(MediaStore.Audio.Media.IS_PENDING, 0) }
                resolver.update(itemUri, publish, null, null)
            }
            partFile.delete()
            return CommittedSyncFile(safeRelPath, itemUri.toString(), expectedSize)
        } catch (e: Exception) {
            runCatching { resolver.delete(itemUri, null, null) }
            throw e
        }
    }

    private fun commitAudioLegacy(
        parentDirs: List<String>,
        fileName: String,
        safeRelPath: String,
        partFile: File,
    ): CommittedSyncFile {
        @Suppress("DEPRECATION")
        val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
        var targetDir = File(musicDir, "Tonewise")
        for (seg in parentDirs) {
            targetDir = File(targetDir, seg)
        }
        if (!targetDir.exists() && !targetDir.mkdirs()) {
            throw IOException("无法创建目标目录: ${targetDir.absolutePath}")
        }
        val destFile = File(targetDir, fileName)
        val expectedSize = partFile.length()
        if (destFile.exists()) destFile.delete()
        if (!partFile.renameTo(destFile)) {
            partFile.inputStream().use { input ->
                destFile.outputStream().use { output -> input.copyTo(output) }
            }
            partFile.delete()
        }
        runCatching {
            MediaScannerConnection.scanFile(context, arrayOf(destFile.absolutePath), null, null)
        }
        return CommittedSyncFile(safeRelPath, destFile.absolutePath, expectedSize)
    }

    private fun findExistingMediaStoreEntry(
        collection: Uri,
        relativeDir: String,
        displayName: String,
    ): Triple<Uri, Long, Int>? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val projection =
            arrayOf(
                MediaStore.Audio.Media._ID,
                MediaStore.Audio.Media.SIZE,
                MediaStore.Audio.Media.IS_PENDING,
            )
        val selection =
            "${MediaStore.Audio.Media.RELATIVE_PATH} = ? AND ${MediaStore.Audio.Media.DISPLAY_NAME} = ?"
        val args = arrayOf(relativeDir, displayName)
        return runCatching {
            resolver.query(collection, projection, selection, args, null)?.use { cursor ->
                if (!cursor.moveToFirst()) return@use null
                val id = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.Audio.Media._ID))
                val size = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.SIZE))
                val pending = cursor.getInt(cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.IS_PENDING))
                Triple(ContentUris.withAppendedId(collection, id), size, pending)
            }
        }.getOrNull()
    }

    private fun pickWritableSafLocation(): Location.Opened? {
        if (musicSettings.locationMode != LocationMode.SAF) return null
        val sources = musicSettings.safQuery.source
        if (sources.isEmpty()) return null
        return sources.firstOrNull {
            it.path.components.components.lastOrNull()?.equals("Tonewise", ignoreCase = true) == true
        }
            ?: sources.firstOrNull {
                it.path.components.components.lastOrNull()?.equals("Music", ignoreCase = true) == true
            }
            ?: sources.first()
    }

    private fun ensureSafTonewiseRootDocId(location: Location.Opened): String {
        val rootDocId = DocumentsContract.getTreeDocumentId(location.uri)
        val lastSeg = location.path.components.components.lastOrNull()
        if (lastSeg != null && lastSeg.equals("Tonewise", ignoreCase = true)) {
            return rootDocId
        }
        return ensureSafChildDirectory(location.uri, rootDocId, "Tonewise")
    }

    private fun ensureSafDirectoryPath(
        rootUri: Uri,
        baseDocId: String,
        segments: List<String>,
    ): String {
        var current = baseDocId
        for (seg in segments) {
            current = ensureSafChildDirectory(rootUri, current, seg)
        }
        return current
    }

    private fun ensureSafChildDirectory(
        rootUri: Uri,
        parentDocId: String,
        name: String,
    ): String {
        val existing = findSafChild(rootUri, parentDocId, name)
        if (existing != null && existing.isDirectory) {
            return existing.docId
        }
        val parentUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, parentDocId)
        val created =
            DocumentsContract.createDocument(
                resolver,
                parentUri,
                DocumentsContract.Document.MIME_TYPE_DIR,
                name,
            ) ?: throw IOException("无法创建 SAF 目录: $name")
        return DocumentsContract.getDocumentId(created)
    }

    private data class SafChildInfo(val docId: String, val isDirectory: Boolean, val size: Long)

    private fun findSafChild(rootUri: Uri, parentDocId: String, targetName: String): SafChildInfo? {
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(rootUri, parentDocId)
        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
                DocumentsContract.Document.COLUMN_SIZE,
            )
        return runCatching {
            resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
                val idIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                val nameIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                val mimeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
                val sizeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_SIZE)
                while (cursor.moveToNext()) {
                    val name = cursor.getString(nameIdx) ?: continue
                    if (name.equals(targetName, ignoreCase = true)) {
                        val docId = cursor.getString(idIdx)
                        val mime = cursor.getString(mimeIdx)
                        val size = if (cursor.isNull(sizeIdx)) 0L else cursor.getLong(sizeIdx)
                        return@use SafChildInfo(
                            docId = docId,
                            isDirectory = mime == DocumentsContract.Document.MIME_TYPE_DIR,
                            size = size,
                        )
                    }
                }
                null
            }
        }.getOrNull()
    }

    private fun resolveAudioMimeType(fileName: String): String {
        val dot = fileName.lastIndexOf('.')
        val ext = if (dot >= 0) fileName.substring(dot).lowercase() else ""
        return when (ext) {
            ".flac" -> "audio/flac"
            ".mp3" -> "audio/mpeg"
            ".m4a", ".alac", ".aac", ".mp4" -> "audio/mp4"
            ".wav" -> "audio/wav"
            ".ogg" -> "audio/ogg"
            ".opus" -> "audio/opus"
            else -> "application/octet-stream"
        }
    }
}
