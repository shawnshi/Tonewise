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
import android.provider.OpenableColumns
import com.shawnshi.tonewise.music.MusicSettings
import com.shawnshi.tonewise.music.locations.LocationMode
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.io.FileInputStream
import java.io.FileNotFoundException
import java.io.IOException
import java.io.OutputStream
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton
import org.oxycblt.musikr.fs.Location
import timber.log.Timber as L

data class CommittedSyncFile(
    val relativePath: String,
    val documentUri: String,
    val fileSize: Long,
)

data class ExistingManagedFile(
    val documentUri: String,
    val fileSize: Long,
    val mtimeMs: Long,
)

data class LocalMusicAudioFile(
    val relativePath: String,
    val documentUri: String,
    val fileSize: Long,
)

data class PruneExecutionResult(
    val deletedFiles: List<LocalMusicAudioFile>,
    val blockedForeignRelPaths: List<String>,
    val skippedPlayingRelPaths: List<String>,
    val removedEmptyDirs: Int,
)

@Singleton
class ManagedSyncStorage
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val musicSettings: MusicSettings,
) {
    private val resolver = context.contentResolver
    private val safCache = SafPathCache()
    private val uriInspectCache = ConcurrentHashMap<String, ExistingManagedFile>()
    @Volatile private var legacyMediaStoreTonewiseAbsent: Boolean? = null
    @Volatile private var legacyFileTonewiseAbsent: Boolean? = null

    fun invalidatePathCache() {
        safCache.clear()
        uriInspectCache.clear()
        legacyMediaStoreTonewiseAbsent = null
        legacyFileTonewiseAbsent = null
    }

    fun hasWritableMusicSafGrant(): Boolean = pickWritableSafLocation() != null

    fun isStandardPrimaryMusicStorage(): Boolean {
        val safLocation = pickWritableSafLocation() ?: return true
        val docId = runCatching { DocumentsContract.getTreeDocumentId(safLocation.uri) }.getOrNull()
        return isPrimaryMusicTreeDocumentId(docId)
    }

    fun triggerMediaScanForRelPaths(relPaths: List<String>) {
        if (relPaths.isEmpty()) return
        @Suppress("DEPRECATION")
        val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
        if (!musicDir.exists()) return
        val absPaths =
            relPaths.mapNotNull { rel ->
                val segments = LanSyncV2Client.sanitizeRelativePathSegments(rel)
                if (segments.isEmpty()) return@mapNotNull null
                val file = File(musicDir, segments.joinToString("/"))
                if (file.isFile) file.absolutePath else null
            }
        if (absPaths.isEmpty()) return
        for (chunk in absPaths.chunked(256)) {
            runCatching {
                MediaScannerConnection.scanFile(context, chunk.toTypedArray(), null, null)
            }
        }
    }

    private fun legacyFileTonewiseDirIfExists(musicDir: File): File? {
        if (legacyFileTonewiseAbsent == true) return null
        val root = File(musicDir, "Tonewise")
        val exists = root.exists()
        if (!exists) {
            legacyFileTonewiseAbsent = true
            return null
        }
        legacyFileTonewiseAbsent = false
        return root
    }

    private fun copyFileToOutputStream(src: File, out: OutputStream) {
        val buf = ByteArray(STREAM_COPY_BUFFER_BYTES)
        FileInputStream(src).use { input ->
            while (true) {
                val read = input.read(buf)
                if (read < 0) break
                if (read > 0) out.write(buf, 0, read)
            }
        }
        out.flush()
    }

    fun listAudioFilesInMusicRoot(): List<LocalMusicAudioFile> {
        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            return listAudioFilesViaSaf(safLocation)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            return listAudioFilesViaMediaStore()
        }
        return listAudioFilesLegacy()
    }

    fun deleteAudioFilesAndPruneEmptyDirs(
        filesToDelete: List<LocalMusicAudioFile>,
        currentlyPlayingUris: Set<String> = emptySet(),
        currentlyPlayingNormRelPaths: Set<String> = emptySet(),
    ): PruneExecutionResult {
        if (filesToDelete.isEmpty()) {
            return PruneExecutionResult(emptyList(), emptyList(), emptyList(), 0)
        }

        val deletedFiles = ArrayList<LocalMusicAudioFile>()
        val blockedForeignRelPaths = ArrayList<String>()
        val skippedPlayingRelPaths = ArrayList<String>()
        val touchedParentDirs = HashSet<String>()
        val legacyDeletedPaths = ArrayList<String>()

        for (file in filesToDelete) {
            val normRel = SyncMirrorPrunePlanner.normalizeRelPath(file.relativePath)
            if (file.documentUri in currentlyPlayingUris ||
                (normRel.isNotEmpty() && normRel in currentlyPlayingNormRelPaths)
            ) {
                skippedPlayingRelPaths.add(file.relativePath)
                continue
            }

            var deleted = false
            var blocked = false
            if (file.documentUri.startsWith("content://")) {
                val uri = Uri.parse(file.documentUri)
                if (uri.authority != MediaStore.AUTHORITY && file.documentUri.contains("/tree/")) {
                    deleted = runCatching { DocumentsContract.deleteDocument(resolver, uri) }.getOrDefault(false)
                } else {
                    try {
                        val rows = resolver.delete(uri, null, null)
                        if (rows > 0) deleted = true else blocked = true
                    } catch (_: SecurityException) {
                        blocked = true
                    } catch (e: Exception) {
                        L.w(e, "MediaStore delete failed for ${file.relativePath}")
                        blocked = true
                    }
                }
            } else {
                val f = File(file.documentUri)
                if (!f.exists() || f.delete()) {
                    deleted = true
                    legacyDeletedPaths.add(f.absolutePath)
                } else {
                    blocked = true
                }
            }

            if (deleted) {
                deletedFiles.add(file)
                uriInspectCache.remove(file.documentUri)
                val parentRel = file.relativePath.replace('\\', '/').substringBeforeLast('/', "")
                if (parentRel.isNotBlank()) {
                    collectAncestorSubDirs(parentRel, touchedParentDirs)
                }
            } else if (blocked) {
                blockedForeignRelPaths.add(file.relativePath)
            }
        }

        if (legacyDeletedPaths.isNotEmpty()) {
            runCatching {
                MediaScannerConnection.scanFile(context, legacyDeletedPaths.toTypedArray(), null, null)
            }
        }

        var removedEmptyDirs = 0
        if (deletedFiles.isNotEmpty() && touchedParentDirs.isNotEmpty()) {
            safCache.clear()
            val safLocation = pickWritableSafLocation()
            val sortedDirs =
                touchedParentDirs
                    .filter { dir ->
                        dir.isNotBlank() &&
                            !dir.startsWith(".tonewise", ignoreCase = true) &&
                            !dir.equals("Tonewise", ignoreCase = true) &&
                            !dir.startsWith("Tonewise/Playlists", ignoreCase = true)
                    }
                    .sortedWith(
                        compareByDescending<String> { it.count { ch -> ch == '/' } }
                            .thenByDescending { it.length }
                    )

            if (safLocation != null) {
                for (dirRel in sortedDirs) {
                    if (tryPruneEmptySafSubDir(safLocation, dirRel)) {
                        removedEmptyDirs++
                    }
                }
            } else if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
                @Suppress("DEPRECATION")
                val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
                val rootCanon = runCatching { musicDir.canonicalPath }.getOrNull()
                for (dirRel in sortedDirs) {
                    val dir = File(musicDir, dirRel)
                    val dirCanon = runCatching { dir.canonicalPath }.getOrNull()
                    if (dirCanon != null && dirCanon != rootCanon && dir.isDirectory) {
                        val children = dir.listFiles() ?: continue
                        val onlyCovers =
                            children.all { child -> !child.isDirectory && isManagedCoverFilename(child.name) }
                        if (onlyCovers) {
                            for (cover in children) {
                                cover.delete()
                            }
                            if (dir.delete()) {
                                removedEmptyDirs++
                            }
                        }
                    }
                }
            }
        }

        return PruneExecutionResult(
            deletedFiles = deletedFiles,
            blockedForeignRelPaths = blockedForeignRelPaths,
            skippedPlayingRelPaths = skippedPlayingRelPaths,
            removedEmptyDirs = removedEmptyDirs,
        )
    }

    private fun listAudioFilesViaSaf(location: Location.Opened): List<LocalMusicAudioFile> {
        val rootUri = location.uri
        val rootDocId = DocumentsContract.getTreeDocumentId(rootUri)
        val result = ArrayList<LocalMusicAudioFile>()
        val stack = ArrayDeque<Pair<String, String>>()
        stack.addLast(rootDocId to "")

        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
                DocumentsContract.Document.COLUMN_SIZE,
            )

        while (stack.isNotEmpty()) {
            val (parentDocId, prefix) = stack.removeLast()
            val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(rootUri, parentDocId)
            runCatching {
                resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
                    val idIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                    val nameIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                    val mimeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
                    val sizeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_SIZE)
                    while (cursor.moveToNext()) {
                        val name = cursor.getString(nameIdx) ?: continue
                        if (name.startsWith(".")) continue
                        val docId = cursor.getString(idIdx) ?: continue
                        val mime = cursor.getString(mimeIdx)
                        val isDir = mime == DocumentsContract.Document.MIME_TYPE_DIR
                        val rel = if (prefix.isEmpty()) name else "$prefix/$name"
                        if (isDir) {
                            if (rel.equals("Tonewise/Playlists", ignoreCase = true)) continue
                            stack.addLast(docId to rel)
                        } else if (isAudioFilename(name)) {
                            val size = if (cursor.isNull(sizeIdx)) 0L else cursor.getLong(sizeIdx)
                            val docUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, docId)
                            result.add(LocalMusicAudioFile(rel, docUri.toString(), size))
                        }
                    }
                }
            }.onFailure { L.w(it, "SAF scan failed under $prefix") }
        }
        return result
    }

    private fun listAudioFilesViaMediaStore(): List<LocalMusicAudioFile> {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return emptyList()
        val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val musicRootName = (Environment.DIRECTORY_MUSIC ?: "Music").trim('/').ifBlank { "Music" }
        val musicPrefix = "$musicRootName/"
        val projection =
            arrayOf(
                MediaStore.Audio.Media._ID,
                MediaStore.Audio.Media.DISPLAY_NAME,
                MediaStore.Audio.Media.RELATIVE_PATH,
                MediaStore.Audio.Media.SIZE,
            )
        val selection =
            "${MediaStore.Audio.Media.RELATIVE_PATH} LIKE ? AND ${MediaStore.Audio.Media.IS_PENDING} = 0"
        val args = arrayOf("$musicPrefix%")
        val result = ArrayList<LocalMusicAudioFile>()

        runCatching {
            resolver.query(collection, projection, selection, args, null)?.use { cursor ->
                val idIdx = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
                val nameIdx = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)
                val relDirIdx = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.RELATIVE_PATH)
                val sizeIdx = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.SIZE)
                while (cursor.moveToNext()) {
                    val name = cursor.getString(nameIdx) ?: continue
                    if (name.startsWith(".") || !isAudioFilename(name)) continue
                    val rawRelDir = (cursor.getString(relDirIdx) ?: "").replace('\\', '/')
                    if (!rawRelDir.startsWith(musicPrefix, ignoreCase = true)) continue
                    val subDir = rawRelDir.substring(musicPrefix.length).trim('/')
                    if (subDir.split('/').any { it.startsWith(".") }) continue
                    if (subDir.equals("Tonewise/Playlists", ignoreCase = true)) continue
                    val rel = if (subDir.isEmpty()) name else "$subDir/$name"
                    val id = cursor.getLong(idIdx)
                    val size = if (cursor.isNull(sizeIdx)) 0L else cursor.getLong(sizeIdx)
                    val uri = ContentUris.withAppendedId(collection, id)
                    result.add(LocalMusicAudioFile(rel, uri.toString(), size))
                }
            }
        }.onFailure { L.w(it, "MediaStore audio scan failed under $musicPrefix") }
        return result
    }

    private fun listAudioFilesLegacy(): List<LocalMusicAudioFile> {
        @Suppress("DEPRECATION")
        val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
        if (!musicDir.exists() || !musicDir.isDirectory) return emptyList()
        val result = ArrayList<LocalMusicAudioFile>()
        val stack = ArrayDeque<Pair<File, String>>()
        stack.addLast(musicDir to "")
        while (stack.isNotEmpty()) {
            val (dir, prefix) = stack.removeLast()
            val children = dir.listFiles() ?: continue
            for (child in children) {
                val name = child.name
                if (name.startsWith(".")) continue
                val rel = if (prefix.isEmpty()) name else "$prefix/$name"
                if (child.isDirectory) {
                    if (rel.equals("Tonewise/Playlists", ignoreCase = true)) continue
                    stack.addLast(child to rel)
                } else if (child.isFile && isAudioFilename(name)) {
                    result.add(LocalMusicAudioFile(rel, child.absolutePath, child.length()))
                }
            }
        }
        return result
    }

    private fun tryPruneEmptySafSubDir(location: Location.Opened, dirRel: String): Boolean {
        val rootUri = location.uri
        val rootDocId = DocumentsContract.getTreeDocumentId(rootUri)
        val segments = dirRel.split('/').filter { it.isNotBlank() }
        if (segments.isEmpty()) return false

        var currentDocId = rootDocId
        for (seg in segments) {
            val child = findSafChild(rootUri, currentDocId, seg, forceRefresh = true) ?: return false
            if (!child.isDirectory) return false
            currentDocId = child.docId
        }
        if (currentDocId == rootDocId) return false

        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(rootUri, currentDocId)
        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
            )
        val orphanCoverDocIds = ArrayList<String>()
        val canRemoveDir =
            runCatching {
                resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
                    val idIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                    val nameIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                    val mimeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
                    while (cursor.moveToNext()) {
                        val name = cursor.getString(nameIdx) ?: return@use false
                        val mime = cursor.getString(mimeIdx)
                        if (mime == DocumentsContract.Document.MIME_TYPE_DIR) return@use false
                        if (!isManagedCoverFilename(name)) return@use false
                        cursor.getString(idIdx)?.let(orphanCoverDocIds::add)
                    }
                    true
                } ?: false
            }.getOrDefault(false)

        if (!canRemoveDir) return false

        for (coverDocId in orphanCoverDocIds) {
            runCatching {
                DocumentsContract.deleteDocument(
                    resolver,
                    DocumentsContract.buildDocumentUriUsingTree(rootUri, coverDocId),
                )
            }
        }
        return runCatching {
            DocumentsContract.deleteDocument(
                resolver,
                DocumentsContract.buildDocumentUriUsingTree(rootUri, currentDocId),
            )
        }.getOrDefault(false)
    }

    private fun collectAncestorSubDirs(relDir: String, out: MutableSet<String>) {
        val parts = relDir.split('/').filter { it.isNotBlank() }
        val sb = StringBuilder()
        for (i in parts.indices) {
            if (i > 0) sb.append('/')
            sb.append(parts[i])
            out.add(sb.toString())
        }
    }

    fun commitAudioFile(
        track: OutboxTrackDto,
        partFile: File,
        forceOverwrite: Boolean = false,
    ): CommittedSyncFile {
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
        val committed =
            when {
                safLocation != null ->
                    commitAudioViaSaf(safLocation, parentDirs, fileName, safeRelPath, mime, partFile, forceOverwrite)
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q ->
                    commitAudioViaMediaStore(track, parentDirs, fileName, safeRelPath, mime, partFile, forceOverwrite)
                else ->
                    commitAudioLegacy(parentDirs, fileName, safeRelPath, partFile)
            }
        cleanupLegacyTonewiseDuplicate(safLocation, parentDirs, fileName)
        uriInspectCache.remove(committed.documentUri)
        return committed
    }

    fun commitAlbumCoverIfMissing(targetRelPath: String, coverBytes: ByteArray) {
        if (coverBytes.isEmpty()) return
        val segments = LanSyncV2Client.sanitizeRelativePathSegments(targetRelPath)
        val parentDirs = segments.dropLast(1)

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            runCatching {
                val rootUri = safLocation.uri
                val musicRootId = DocumentsContract.getTreeDocumentId(rootUri)
                val targetDirId = ensureSafDirectoryPath(rootUri, musicRootId, parentDirs)
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
                        safCache.putEntry(
                            rootUri.toString(),
                            targetDirId,
                            "cover.jpg",
                            SafChildInfo(
                                docId = DocumentsContract.getDocumentId(createdUri),
                                isDirectory = false,
                                size = coverBytes.size.toLong(),
                                lastModifiedMs = System.currentTimeMillis(),
                            ),
                        )
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

        // Best-effort direct file write directly under external Music folder
        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            var dir = musicDir
            for (seg in parentDirs) dir = File(dir, seg)
            if (dir.exists() || dir.mkdirs()) {
                val coverFile = File(dir, "cover.jpg")
                if (!coverFile.exists()) coverFile.writeBytes(coverBytes)
            }
        }
    }

    fun commitPlaylistM3u8(name: String, relPaths: List<String>) {
        val safeName = LanSyncRepository.sanitizePlaylistFilename(name)
        val legacyName = LanSyncRepository.legacyUnderscoredPlaylistFilename(name)
        val shouldCleanupLegacy = !legacyName.equals(safeName, ignoreCase = true)
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
            if (shouldCleanupLegacy) {
                File(internalDir, "$legacyName.m3u8").takeIf { it.isFile }?.delete()
            }
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
                    safCache.putEntry(
                        rootUri.toString(),
                        playlistsDirId,
                        fileName,
                        SafChildInfo(
                            docId = DocumentsContract.getDocumentId(targetUri),
                            isDirectory = false,
                            size = payload.size.toLong(),
                            lastModifiedMs = System.currentTimeMillis(),
                        ),
                    )
                }
                if (shouldCleanupLegacy) {
                    val legacy = findSafChild(rootUri, playlistsDirId, "$legacyName.m3u8")
                    if (legacy != null && !legacy.isDirectory) {
                        runCatching {
                            DocumentsContract.deleteDocument(
                                resolver,
                                DocumentsContract.buildDocumentUriUsingTree(rootUri, legacy.docId),
                            )
                        }
                        safCache.removeEntry(rootUri.toString(), playlistsDirId, "$legacyName.m3u8")
                    }
                }
            }.onFailure { L.w(it, "SAF playlist write failed for $safeName") }
            return
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            runCatching {
                commitPlaylistViaMediaStoreFiles(
                    safeFileName = "$safeName.m3u8",
                    legacyFileName = if (shouldCleanupLegacy) "$legacyName.m3u8" else null,
                    payload = payload,
                )
            }.onFailure { L.w(it, "MediaStore playlist write failed for $safeName") }
            return
        }

        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            val extDir = File(musicDir, "Tonewise/Playlists")
            if (extDir.exists() || extDir.mkdirs()) {
                File(extDir, "$safeName.m3u8").writeBytes(payload)
                if (shouldCleanupLegacy) {
                    File(extDir, "$legacyName.m3u8").takeIf { it.isFile }?.delete()
                }
            }
        }
    }

    fun deletePlaylistM3u8(name: String) {
        val safeName = LanSyncRepository.sanitizePlaylistFilename(name)
        val legacyName = LanSyncRepository.legacyUnderscoredPlaylistFilename(name)
        val candidates =
            listOf("$safeName.m3u8", "$legacyName.m3u8").distinctBy { it.lowercase() }

        runCatching {
            val internalDir = File(context.filesDir, ".tonewise/playlists")
            for (fileName in candidates) {
                File(internalDir, fileName).takeIf { it.isFile }?.delete()
            }
        }

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            runCatching {
                val rootUri = safLocation.uri
                val rootDocId = DocumentsContract.getTreeDocumentId(rootUri)
                val lastSeg = safLocation.path.components.components.lastOrNull()
                val tonewiseDocId =
                    if (lastSeg != null && lastSeg.equals("Tonewise", ignoreCase = true)) {
                        rootDocId
                    } else {
                        findSafChild(rootUri, rootDocId, "Tonewise")?.takeIf { it.isDirectory }?.docId
                    }
                if (tonewiseDocId != null) {
                    val playlistsDir = findSafChild(rootUri, tonewiseDocId, "Playlists")
                    if (playlistsDir != null && playlistsDir.isDirectory) {
                        for (fileName in candidates) {
                            val existing = findSafChild(rootUri, playlistsDir.docId, fileName)
                            if (existing != null && !existing.isDirectory) {
                                runCatching {
                                    DocumentsContract.deleteDocument(
                                        resolver,
                                        DocumentsContract.buildDocumentUriUsingTree(rootUri, existing.docId),
                                    )
                                }
                                safCache.removeEntry(rootUri.toString(), playlistsDir.docId, fileName)
                            }
                        }
                    }
                }
            }.onFailure { L.w(it, "SAF playlist delete failed for $safeName") }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            runCatching { deletePlaylistViaMediaStoreFiles(candidates) }
                .onFailure { L.w(it, "MediaStore playlist delete failed for $safeName") }
        }

        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            for (relDir in listOf("Tonewise/Playlists", ".tonewise/playlists")) {
                val extDir = File(musicDir, relDir)
                for (fileName in candidates) {
                    File(extDir, fileName).takeIf { it.isFile }?.delete()
                }
            }
        }
    }

    private fun commitPlaylistViaMediaStoreFiles(
        safeFileName: String,
        legacyFileName: String?,
        payload: ByteArray,
    ) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return
        val collection = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val relativeDir = buildMediaStoreTonewiseRelativeDir(listOf("Playlists"))
        val existingUri = findMediaStoreFileEntry(collection, relativeDir, safeFileName)
        var written = false
        if (existingUri != null) {
            written =
                runCatching {
                    resolver.openOutputStream(existingUri, "wt")?.use { out ->
                        out.write(payload)
                        out.flush()
                    } != null
                }.getOrDefault(false)
            if (!written) {
                runCatching { resolver.delete(existingUri, null, null) }
            }
        }
        if (!written) {
            val values =
                ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, safeFileName)
                    put(MediaStore.MediaColumns.MIME_TYPE, "audio/x-mpegurl")
                    put(MediaStore.MediaColumns.RELATIVE_PATH, relativeDir)
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                }
            val createdUri =
                resolver.insert(collection, values)
                    ?: throw IOException("无法在 MediaStore 创建歌单文件: $safeFileName")
            try {
                resolver.openOutputStream(createdUri, "w")?.use { out ->
                    out.write(payload)
                    out.flush()
                } ?: throw IOException("无法写入 MediaStore 歌单文件: $safeFileName")
                val publish = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
                if (resolver.update(createdUri, publish, null, null) <= 0) {
                    throw IOException("无法发布 MediaStore 歌单文件: $safeFileName")
                }
            } catch (t: Throwable) {
                runCatching { resolver.delete(createdUri, null, null) }
                throw t
            }
        }
        if (legacyFileName != null) {
            findMediaStoreFileEntry(collection, relativeDir, legacyFileName)?.let { legacyUri ->
                runCatching { resolver.delete(legacyUri, null, null) }
            }
        }
    }

    private fun deletePlaylistViaMediaStoreFiles(candidates: List<String>) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return
        val collection = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val relativeDir = buildMediaStoreTonewiseRelativeDir(listOf("Playlists"))
        for (fileName in candidates) {
            findMediaStoreFileEntry(collection, relativeDir, fileName)?.let { uri ->
                runCatching { resolver.delete(uri, null, null) }
            }
        }
    }

    private fun findMediaStoreFileEntry(
        collection: Uri,
        relativeDir: String,
        displayName: String,
    ): Uri? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val projection = arrayOf(MediaStore.MediaColumns._ID)
        val selection =
            "${MediaStore.MediaColumns.RELATIVE_PATH} = ? AND ${MediaStore.MediaColumns.DISPLAY_NAME} = ?"
        val args = arrayOf(relativeDir, displayName)
        return runCatching {
            resolver.query(collection, projection, selection, args, null)?.use { cursor ->
                if (!cursor.moveToFirst()) return@use null
                val id = cursor.getLong(cursor.getColumnIndexOrThrow(MediaStore.MediaColumns._ID))
                ContentUris.withAppendedId(collection, id)
            }
        }.getOrNull()
    }

    private fun cleanupLegacyTonewiseDuplicate(
        safLocation: Location.Opened?,
        parentDirs: List<String>,
        fileName: String,
    ) {
        val lastSeg = safLocation?.path?.components?.components?.lastOrNull()
        val safRootIsTonewise = lastSeg != null && lastSeg.equals("Tonewise", ignoreCase = true)
        if (safRootIsTonewise) return

        if (safLocation != null) {
            runCatching {
                val rootUri = safLocation.uri
                val rootDocId = DocumentsContract.getTreeDocumentId(rootUri)
                var currentDocId = findSafChild(rootUri, rootDocId, "Tonewise")?.takeIf { it.isDirectory }?.docId
                    ?: return@runCatching
                for (seg in parentDirs) {
                    currentDocId = findSafChild(rootUri, currentDocId, seg)?.takeIf { it.isDirectory }?.docId
                        ?: return@runCatching
                }
                val legacyDoc = findSafChild(rootUri, currentDocId, fileName)
                if (legacyDoc != null && !legacyDoc.isDirectory) {
                    DocumentsContract.deleteDocument(
                        resolver,
                        DocumentsContract.buildDocumentUriUsingTree(rootUri, legacyDoc.docId),
                    )
                    safCache.removeEntry(rootUri.toString(), currentDocId, fileName)
                }
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val legacyAbsent =
                legacyMediaStoreTonewiseAbsent ?: probeLegacyMediaStoreTonewiseAbsent().also {
                    legacyMediaStoreTonewiseAbsent = it
                }
            if (!legacyAbsent) {
                runCatching {
                    val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
                    val legacyDir = buildLegacyMediaStoreTonewiseRelativeDir(parentDirs)
                    findExistingMediaStoreEntry(collection, legacyDir, fileName)?.let { stale ->
                        resolver.delete(stale.uri, null, null)
                    }
                }
            }
        }

        runCatching {
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            var dir = legacyFileTonewiseDirIfExists(musicDir) ?: return@runCatching
            for (seg in parentDirs) {
                dir = File(dir, seg)
            }
            File(dir, fileName).takeIf { it.isFile }?.delete()
        }
    }

    fun existsInManagedStorage(entity: SyncedTrackEntity): Boolean {
        val inspected = inspectExistingManagedFile(entity) ?: return false
        return entity.fileSize <= 0L || inspected.fileSize <= 0L || inspected.fileSize == entity.fileSize
    }

    fun inspectExistingManagedFile(entity: SyncedTrackEntity): ExistingManagedFile? {
        val rawUri = entity.documentUri?.takeIf { it.isNotBlank() }
        if (rawUri != null) {
            inspectUriOrPath(rawUri)?.let { return it }
        }
        val relPath = entity.relativePath.takeIf { it.isNotBlank() } ?: return null
        return findExistingManagedFileByRelPath(relPath)
    }

    fun findExistingManagedFileByRelPath(targetRelPath: String): ExistingManagedFile? {
        val segments = LanSyncV2Client.sanitizeRelativePathSegments(targetRelPath)
        if (segments.isEmpty()) return null
        val fileName = segments.last()
        val parentDirs = segments.dropLast(1)

        val safLocation = pickWritableSafLocation()
        if (safLocation != null) {
            return findSafFileReadOnly(safLocation, parentDirs, fileName)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            val primaryDir = buildMediaStoreMusicRelativeDir(parentDirs)
            findExistingMediaStoreEntry(collection, primaryDir, fileName)
                ?.takeIf { it.isPending == 0 }
                ?.let { found ->
                    return ExistingManagedFile(found.uri.toString(), found.fileSize, found.mtimeMs)
                }
            val legacyDir = buildLegacyMediaStoreTonewiseRelativeDir(parentDirs)
            findExistingMediaStoreEntry(collection, legacyDir, fileName)
                ?.takeIf { it.isPending == 0 }
                ?.let { found ->
                    return ExistingManagedFile(found.uri.toString(), found.fileSize, found.mtimeMs)
                }
            return null
        }

        @Suppress("DEPRECATION")
        val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
        var targetDir = musicDir
        for (seg in parentDirs) {
            targetDir = File(targetDir, seg)
        }
        val file = File(targetDir, fileName)
        if (file.isFile) {
            return ExistingManagedFile(file.absolutePath, file.length(), file.lastModified())
        }
        var legacyTargetDir = legacyFileTonewiseDirIfExists(musicDir) ?: return null
        for (seg in parentDirs) {
            legacyTargetDir = File(legacyTargetDir, seg)
        }
        val legacyFile = File(legacyTargetDir, fileName)
        return if (legacyFile.isFile) {
            ExistingManagedFile(legacyFile.absolutePath, legacyFile.length(), legacyFile.lastModified())
        } else {
            null
        }
    }

    private fun probeLegacyMediaStoreTonewiseAbsent(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return true
        val collection = MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        val prefix = "${Environment.DIRECTORY_MUSIC ?: "Music"}/Tonewise/%"
        return runCatching {
            resolver.query(
                collection,
                arrayOf(MediaStore.Audio.Media._ID),
                "${MediaStore.Audio.Media.RELATIVE_PATH} LIKE ?",
                arrayOf(prefix),
                null,
            )?.use { cursor -> !cursor.moveToFirst() } ?: true
        }.getOrDefault(false)
    }

    private fun inspectUriOrPath(rawUri: String): ExistingManagedFile? {
        uriInspectCache[rawUri]?.let { return it }
        return try {
            if (rawUri.startsWith("content://")) {
                val uri = Uri.parse(rawUri)
                var queriedSize = -1L
                var queriedMtime = 0L
                var cursorMatchedRow = false
                val projection =
                    arrayOf(
                        OpenableColumns.SIZE,
                        DocumentsContract.Document.COLUMN_LAST_MODIFIED,
                        MediaStore.MediaColumns.DATE_MODIFIED,
                    )
                val cursor =
                    try {
                        resolver.query(uri, projection, null, null, null)
                    } catch (_: IllegalArgumentException) {
                        resolver.query(uri, null, null, null, null)
                    }
                runCatching {
                    cursor?.use { c ->
                        if (c.moveToFirst()) {
                            cursorMatchedRow = true
                            val sizeIdx = c.getColumnIndex(OpenableColumns.SIZE)
                            if (sizeIdx >= 0 && !c.isNull(sizeIdx)) {
                                queriedSize = c.getLong(sizeIdx)
                            }
                            val docModIdx = c.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                            if (docModIdx >= 0 && !c.isNull(docModIdx)) {
                                queriedMtime = c.getLong(docModIdx)
                            } else {
                                val msModIdx = c.getColumnIndex(MediaStore.MediaColumns.DATE_MODIFIED)
                                if (msModIdx >= 0 && !c.isNull(msModIdx)) {
                                    queriedMtime = c.getLong(msModIdx) * 1000L
                                }
                            }
                        }
                    }
                }
                val finalSize =
                    if (cursorMatchedRow && queriedSize > 0L) {
                        queriedSize
                    } else {
                        val statSize =
                            resolver.openFileDescriptor(uri, "r")?.use { pfd -> pfd.statSize }
                                ?: return null
                        if (statSize > 0L) statSize else queriedSize.coerceAtLeast(0L)
                    }
                ExistingManagedFile(rawUri, finalSize, queriedMtime).also {
                    if (uriInspectCache.size > 8192) uriInspectCache.clear()
                    uriInspectCache[rawUri] = it
                }
            } else {
                val f = File(rawUri)
                if (f.isFile) {
                    ExistingManagedFile(rawUri, f.length(), f.lastModified()).also {
                        if (uriInspectCache.size > 8192) uriInspectCache.clear()
                        uriInspectCache[rawUri] = it
                    }
                } else {
                    null
                }
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun commitAudioViaSaf(
        location: Location.Opened,
        parentDirs: List<String>,
        fileName: String,
        safeRelPath: String,
        mime: String,
        partFile: File,
        forceOverwrite: Boolean,
        allowRetryOnStaleCache: Boolean = true,
    ): CommittedSyncFile {
        val rootUri = location.uri
        val treeKey = rootUri.toString()
        val musicRootId = DocumentsContract.getTreeDocumentId(rootUri)
        val parentDocId = ensureSafDirectoryPath(rootUri, musicRootId, parentDirs)

        val expectedSize = partFile.length()
        val existing = findSafChild(rootUri, parentDocId, fileName)
        if (!forceOverwrite && existing != null && existing.size == expectedSize && expectedSize > 0L) {
            val docUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, existing.docId)
            partFile.delete()
            return CommittedSyncFile(safeRelPath, docUri.toString(), expectedSize)
        }

        val parentTreeUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, parentDocId)
        val targetDocUri =
            try {
                if (existing != null) {
                    commitSafAtomicReplace(
                        rootUri = rootUri,
                        parentDocId = parentDocId,
                        parentTreeUri = parentTreeUri,
                        existingDocId = existing.docId,
                        fileName = fileName,
                        safeRelPath = safeRelPath,
                        mime = mime,
                        partFile = partFile,
                    )
                } else {
                    val createdUri =
                        DocumentsContract.createDocument(resolver, parentTreeUri, mime, fileName)
                            ?: throw IOException("无法在 SAF 目录中创建文件: $safeRelPath")
                    try {
                        resolver.openOutputStream(createdUri, "wt")?.use { out ->
                            copyFileToOutputStream(partFile, out)
                            out.flush()
                        } ?: throw IOException("无法写入 SAF 目标文件: $safeRelPath")
                    } catch (e: Exception) {
                        runCatching { DocumentsContract.deleteDocument(resolver, createdUri) }
                        throw e
                    }
                    createdUri
                }
            } catch (stale: Exception) {
                if (allowRetryOnStaleCache &&
                    (stale is FileNotFoundException ||
                        stale is SecurityException ||
                        stale is IllegalArgumentException)
                ) {
                    safCache.invalidateDir(treeKey, parentDocId)
                    return commitAudioViaSaf(
                        location = location,
                        parentDirs = parentDirs,
                        fileName = fileName,
                        safeRelPath = safeRelPath,
                        mime = mime,
                        partFile = partFile,
                        forceOverwrite = forceOverwrite,
                        allowRetryOnStaleCache = false,
                    )
                }
                throw stale
            }

        safCache.putEntry(
            treeKey,
            parentDocId,
            fileName,
            SafChildInfo(
                docId = DocumentsContract.getDocumentId(targetDocUri),
                isDirectory = false,
                size = expectedSize,
                lastModifiedMs = System.currentTimeMillis(),
            ),
        )
        partFile.delete()
        return CommittedSyncFile(safeRelPath, targetDocUri.toString(), expectedSize)
    }

    private fun commitSafAtomicReplace(
        rootUri: Uri,
        parentDocId: String,
        parentTreeUri: Uri,
        existingDocId: String,
        fileName: String,
        safeRelPath: String,
        mime: String,
        partFile: File,
    ): Uri {
        val existingUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, existingDocId)
        val tempName = ".${fileName}.twtmp_${System.currentTimeMillis()}"
        findSafChild(rootUri, parentDocId, tempName)?.let { stale ->
            runCatching {
                DocumentsContract.deleteDocument(
                    resolver,
                    DocumentsContract.buildDocumentUriUsingTree(rootUri, stale.docId),
                )
            }
        }
        val tempUri =
            runCatching { DocumentsContract.createDocument(resolver, parentTreeUri, mime, tempName) }
                .getOrNull()
        if (tempUri != null) {
            try {
                resolver.openOutputStream(tempUri, "wt")?.use { out ->
                    copyFileToOutputStream(partFile, out)
                    out.flush()
                } ?: throw IOException("无法写入 SAF 临时文件: $safeRelPath")

                val deletedOld =
                    runCatching { DocumentsContract.deleteDocument(resolver, existingUri) }
                        .getOrDefault(false)
                if (deletedOld) {
                    val renamedUri =
                        runCatching { DocumentsContract.renameDocument(resolver, tempUri, fileName) }
                            .getOrNull()
                    if (renamedUri != null) {
                        return renamedUri
                    }
                    // Provider refused rename after deleting existing file: materialize exact target filename,
                    // stream full payload again, and clean up the staging temp file instead of returning .twtmp_.
                    val recreatedUri =
                        DocumentsContract.createDocument(resolver, parentTreeUri, mime, fileName)
                            ?: throw IOException("无法重建 SAF 目标文件: $safeRelPath")
                    try {
                        resolver.openOutputStream(recreatedUri, "wt")?.use { out ->
                            copyFileToOutputStream(partFile, out)
                            out.flush()
                        } ?: throw IOException("无法写入重建后的 SAF 文件: $safeRelPath")
                    } catch (recreateErr: Throwable) {
                        runCatching { DocumentsContract.deleteDocument(resolver, recreatedUri) }
                        throw recreateErr
                    }
                    runCatching { DocumentsContract.deleteDocument(resolver, tempUri) }
                    return recreatedUri
                }
            } catch (e: Exception) {
                runCatching { DocumentsContract.deleteDocument(resolver, tempUri) }
                throw e
            }
            // Temp copy succeeded but provider refused old document delete before rename: clean temp and fallback below
            runCatching { DocumentsContract.deleteDocument(resolver, tempUri) }
        }

        resolver.openOutputStream(existingUri, "wt")?.use { out ->
            copyFileToOutputStream(partFile, out)
            out.flush()
        } ?: throw IOException("无法覆盖 SAF 目标文件: $safeRelPath")
        return existingUri
    }

    private fun commitAudioViaMediaStore(
        track: OutboxTrackDto,
        parentDirs: List<String>,
        fileName: String,
        safeRelPath: String,
        mime: String,
        partFile: File,
        forceOverwrite: Boolean,
    ): CommittedSyncFile {
        val collection =
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            } else {
                MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
            }
        val relativeDir = buildMediaStoreMusicRelativeDir(parentDirs)
        val expectedSize = partFile.length()

        val existingEntry = findExistingMediaStoreEntry(collection, relativeDir, fileName)
        if (existingEntry != null && !forceOverwrite && existingEntry.isPending == 0 && existingEntry.fileSize == expectedSize && expectedSize > 0L) {
            partFile.delete()
            return CommittedSyncFile(safeRelPath, existingEntry.uri.toString(), expectedSize)
        }

        val stagingName =
            if (existingEntry != null) {
                ".twtmp_${System.currentTimeMillis()}_$fileName"
            } else {
                fileName
            }
        val values =
            ContentValues().apply {
                put(MediaStore.Audio.Media.DISPLAY_NAME, stagingName)
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
                copyFileToOutputStream(partFile, out)
                out.flush()
            } ?: throw IOException("无法打开 MediaStore 输出流: $safeRelPath")

            if (existingEntry != null) {
                val deleted =
                    try {
                        resolver.delete(existingEntry.uri, null, null)
                    } catch (sec: Exception) {
                        throw IOException("无法覆盖非本应用创建的音频文件，请改用 SAF 授权音乐目录: $safeRelPath", sec)
                    }
                if (deleted <= 0) {
                    throw IOException("无法覆盖现有 MediaStore 音频文件，请改用 SAF 授权音乐目录: $safeRelPath")
                }
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val publish =
                    ContentValues().apply {
                        put(MediaStore.Audio.Media.IS_PENDING, 0)
                        if (stagingName != fileName) {
                            put(MediaStore.Audio.Media.DISPLAY_NAME, fileName)
                        }
                    }
                val updatedRows = resolver.update(itemUri, publish, null, null)
                if (updatedRows <= 0) {
                    throw IOException("无法发布 MediaStore 音频文件: $safeRelPath")
                }
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
        var targetDir = musicDir
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
            destFile.outputStream().use { output -> copyFileToOutputStream(partFile, output) }
            partFile.delete()
        }
        runCatching {
            MediaScannerConnection.scanFile(context, arrayOf(destFile.absolutePath), null, null)
        }
        return CommittedSyncFile(safeRelPath, destFile.absolutePath, expectedSize)
    }

    private data class MediaStoreMatch(
        val uri: Uri,
        val fileSize: Long,
        val isPending: Int,
        val mtimeMs: Long,
    )

    private fun findExistingMediaStoreEntry(
        collection: Uri,
        relativeDir: String,
        displayName: String,
    ): MediaStoreMatch? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val projection =
            arrayOf(
                MediaStore.Audio.Media._ID,
                MediaStore.Audio.Media.SIZE,
                MediaStore.Audio.Media.IS_PENDING,
                MediaStore.Audio.Media.DATE_MODIFIED,
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
                val modIdx = cursor.getColumnIndex(MediaStore.Audio.Media.DATE_MODIFIED)
                val mtimeMs = if (modIdx >= 0 && !cursor.isNull(modIdx)) cursor.getLong(modIdx) * 1000L else 0L
                MediaStoreMatch(ContentUris.withAppendedId(collection, id), size, pending, mtimeMs)
            }
        }.getOrNull()
    }

    private fun findSafFileReadOnly(
        location: Location.Opened,
        parentDirs: List<String>,
        fileName: String,
    ): ExistingManagedFile? {
        val rootUri = location.uri
        val rootDocId = DocumentsContract.getTreeDocumentId(rootUri)
        findSafFileFromDocId(rootUri, rootDocId, parentDirs, fileName)?.let { return it }

        val lastSeg = location.path.components.components.lastOrNull()
        if (lastSeg == null || !lastSeg.equals("Tonewise", ignoreCase = true)) {
            val tonewiseDir = findSafChild(rootUri, rootDocId, "Tonewise")
            if (tonewiseDir != null && tonewiseDir.isDirectory) {
                return findSafFileFromDocId(rootUri, tonewiseDir.docId, parentDirs, fileName)
            }
        }
        return null
    }

    private fun findSafFileFromDocId(
        rootUri: Uri,
        baseDocId: String,
        parentDirs: List<String>,
        fileName: String,
    ): ExistingManagedFile? {
        var currentDocId = baseDocId
        for (seg in parentDirs) {
            val childDir = findSafChild(rootUri, currentDocId, seg)
            if (childDir == null || !childDir.isDirectory) return null
            currentDocId = childDir.docId
        }
        val childFile = findSafChild(rootUri, currentDocId, fileName)
        if (childFile == null || childFile.isDirectory) return null
        val docUri = DocumentsContract.buildDocumentUriUsingTree(rootUri, childFile.docId)
        return ExistingManagedFile(docUri.toString(), childFile.size, childFile.lastModifiedMs)
    }

    internal data class SafChildInfo(
        val docId: String,
        val isDirectory: Boolean,
        val size: Long,
        val lastModifiedMs: Long,
    )

    internal class SafPathCache {
        private val dirs = ConcurrentHashMap<String, ConcurrentHashMap<String, SafChildInfo>>()

        private fun dirKey(treeKey: String, parentDocId: String): String = "$treeKey|$parentDocId"

        fun lookup(
            treeKey: String,
            parentDocId: String,
            displayName: String,
        ): Pair<Boolean, SafChildInfo?> {
            val map = dirs[dirKey(treeKey, parentDocId)] ?: return false to null
            return true to map[displayName.lowercase()]
        }

        fun putDir(treeKey: String, parentDocId: String, entries: Map<String, SafChildInfo>) {
            if (dirs.size > 2048) {
                dirs.clear()
            }
            val copy = ConcurrentHashMap<String, SafChildInfo>(entries.size.coerceAtLeast(4))
            for ((k, v) in entries) {
                copy[k.lowercase()] = v
            }
            dirs[dirKey(treeKey, parentDocId)] = copy
        }

        fun putEntry(treeKey: String, parentDocId: String, displayName: String, info: SafChildInfo) {
            val key = dirKey(treeKey, parentDocId)
            dirs.compute(key) { _, existing ->
                val map = existing ?: ConcurrentHashMap()
                map[displayName.lowercase()] = info
                map
            }
        }

        fun removeEntry(treeKey: String, parentDocId: String, displayName: String) {
            dirs[dirKey(treeKey, parentDocId)]?.remove(displayName.lowercase())
        }

        fun invalidateDir(treeKey: String, parentDocId: String) {
            dirs.remove(dirKey(treeKey, parentDocId))
        }

        fun clear() {
            dirs.clear()
        }
    }

    private fun findSafChild(
        rootUri: Uri,
        parentDocId: String,
        targetName: String,
        forceRefresh: Boolean = false,
    ): SafChildInfo? {
        val treeKey = rootUri.toString()
        if (!forceRefresh) {
            val (dirCached, cachedInfo) = safCache.lookup(treeKey, parentDocId, targetName)
            if (dirCached) {
                return cachedInfo
            }
        }
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(rootUri, parentDocId)
        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
                DocumentsContract.Document.COLUMN_SIZE,
                DocumentsContract.Document.COLUMN_LAST_MODIFIED,
            )
        return runCatching {
            resolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
                val idIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                val nameIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                val mimeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
                val sizeIdx = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_SIZE)
                val modIdx = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                val entries = HashMap<String, SafChildInfo>()
                while (cursor.moveToNext()) {
                    val name = cursor.getString(nameIdx) ?: continue
                    val docId = cursor.getString(idIdx)
                    val mime = cursor.getString(mimeIdx)
                    val size = if (cursor.isNull(sizeIdx)) 0L else cursor.getLong(sizeIdx)
                    val mod = if (modIdx >= 0 && !cursor.isNull(modIdx)) cursor.getLong(modIdx) else 0L
                    entries[name.lowercase()] =
                        SafChildInfo(
                            docId = docId,
                            isDirectory = mime == DocumentsContract.Document.MIME_TYPE_DIR,
                            size = size,
                            lastModifiedMs = mod,
                        )
                }
                safCache.putDir(treeKey, parentDocId, entries)
                entries[targetName.lowercase()]
            }
        }.getOrNull()
    }

    private fun pickWritableSafLocation(): Location.Opened? {
        if (musicSettings.locationMode != LocationMode.SAF) return null
        val sources = musicSettings.safQuery.source
        if (sources.isEmpty()) return null
        val preferredIdx =
            selectPreferredMusicSafIndex(sources.map { it.path.components.components.lastOrNull() })
        return if (preferredIdx in sources.indices) sources[preferredIdx] else sources.first()
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
        val createdId = DocumentsContract.getDocumentId(created)
        safCache.putEntry(
            rootUri.toString(),
            parentDocId,
            name,
            SafChildInfo(
                docId = createdId,
                isDirectory = true,
                size = 0L,
                lastModifiedMs = System.currentTimeMillis(),
            ),
        )
        return createdId
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

    companion object {
        private const val STREAM_COPY_BUFFER_BYTES = 512 * 1024

        internal fun isPrimaryMusicTreeDocumentId(docId: String?): Boolean {
            if (docId.isNullOrBlank()) return false
            val normalized = docId.trim().replace('\\', '/').trimEnd('/')
            return normalized.equals("primary:music", ignoreCase = true)
        }

        private val AUDIO_EXTENSIONS =
            setOf(
                "flac",
                "mp3",
                "m4a",
                "alac",
                "aac",
                "wav",
                "ogg",
                "opus",
                "aiff",
                "aif",
                "dsf",
                "dff",
                "ape",
                "wv",
                "wma",
            )

        private val COVER_FILENAMES =
            setOf(
                "cover.jpg",
                "cover.jpeg",
                "cover.png",
                "folder.jpg",
                "folder.png",
                "front.jpg",
                "front.png",
            )

        fun isAudioFilename(fileName: String): Boolean {
            val dot = fileName.lastIndexOf('.')
            if (dot < 0 || dot == fileName.length - 1) return false
            val ext = fileName.substring(dot + 1).trim().lowercase()
            return ext in AUDIO_EXTENSIONS
        }

        internal fun isManagedCoverFilename(fileName: String): Boolean =
            fileName.trim().lowercase() in COVER_FILENAMES

        internal fun buildMediaStoreMusicRelativeDir(
            parentDirs: List<String>,
            musicRootName: String = Environment.DIRECTORY_MUSIC ?: "Music",
        ): String =
            buildString {
                append(musicRootName.ifBlank { "Music" }).append('/')
                if (parentDirs.isNotEmpty()) {
                    append(parentDirs.joinToString("/")).append('/')
                }
            }

        internal fun buildMediaStoreTonewiseRelativeDir(
            parentDirs: List<String>,
            musicRootName: String = Environment.DIRECTORY_MUSIC ?: "Music",
        ): String = buildLegacyMediaStoreTonewiseRelativeDir(parentDirs, musicRootName)

        internal fun buildLegacyMediaStoreTonewiseRelativeDir(
            parentDirs: List<String>,
            musicRootName: String = Environment.DIRECTORY_MUSIC ?: "Music",
        ): String =
            buildString {
                append(musicRootName.ifBlank { "Music" }).append("/Tonewise/")
                if (parentDirs.isNotEmpty()) {
                    append(parentDirs.joinToString("/")).append('/')
                }
            }

        internal fun selectPreferredMusicSafIndex(lastPathSegments: List<String?>): Int {
            if (lastPathSegments.isEmpty()) return -1
            val musicIdx = lastPathSegments.indexOfFirst { it.equals("Music", ignoreCase = true) }
            if (musicIdx >= 0) return musicIdx
            val nonTonewiseIdx = lastPathSegments.indexOfFirst { !it.equals("Tonewise", ignoreCase = true) }
            if (nonTonewiseIdx >= 0) return nonTonewiseIdx
            return 0
        }
    }
}
