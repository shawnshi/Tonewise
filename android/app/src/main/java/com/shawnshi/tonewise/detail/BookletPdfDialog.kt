/*
 * Copyright (c) 2026 Tonewise Project
 * BookletPdfDialog.kt is part of Tonewise.
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

package com.shawnshi.tonewise.detail

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color as AndroidColor
import android.graphics.pdf.PdfRenderer
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ViewCompositionStrategy
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.fragment.app.activityViewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.android.material.bottomsheet.BackportBottomSheetDialogFragment
import dagger.hilt.android.AndroidEntryPoint
import java.io.File
import java.util.concurrent.Executors
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.MusicSettings
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.playback.PlaybackViewModel
import com.shawnshi.tonewise.ui.TonewiseComposeTheme
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.fs.Path

/**
 * Zero-dependency digital liner notes / booklet PDF viewer powered by Android's [PdfRenderer].
 */
@AndroidEntryPoint
class BookletPdfDialog : BackportBottomSheetDialogFragment() {
    private val playbackModel: PlaybackViewModel by activityViewModels()
    @Inject lateinit var musicSettings: MusicSettings

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?,
    ): View {
        return ComposeView(requireContext()).apply {
            setViewCompositionStrategy(ViewCompositionStrategy.DisposeOnViewTreeLifecycleDestroyed)
            setContent {
                val song by playbackModel.song.collectAsStateWithLifecycle()
                val context = LocalContext.current
                var resolved by remember { mutableStateOf<ResolvedBooklet?>(null) }
                var loading by remember(song?.uid) { mutableStateOf(true) }

                LaunchedEffect(song?.uid) {
                    loading = true
                    resolved = null
                    val targetSong = song
                    if (targetSong != null) {
                        val found =
                            withContext(Dispatchers.IO + NonCancellable) {
                                BookletLocator.findForSong(context, musicSettings, targetSong)
                            }
                        if (isActive) {
                            resolved = found
                        } else {
                            found?.closeQuietly()
                        }
                    }
                    loading = false
                }

                TonewiseComposeTheme {
                    BookletPdfSheet(
                        title = song?.album?.name?.resolve(context) ?: "",
                        subtitle = song?.workName ?: "",
                        loading = loading,
                        resolved = resolved,
                    )
                }
            }
        }
    }

    companion object {
        const val TAG = "BookletPdfDialog"

        fun newInstance() = BookletPdfDialog()
    }
}

private data class ResolvedBooklet(
    val displayName: String,
    val pfd: ParcelFileDescriptor,
) {
    fun closeQuietly() {
        runCatching { pfd.close() }
    }
}

private object BookletLocator {
    fun findForSong(
        context: Context,
        musicSettings: MusicSettings,
        song: Song,
    ): ResolvedBooklet? {
        findSyncedBooklet(context, song)?.let { return it }
        findSafBooklet(context, musicSettings, song.path)?.let { return it }
        return findExternalDirectoryBooklet(song.path)
    }

    private fun findSyncedBooklet(context: Context, song: Song): ResolvedBooklet? {
        val bookletsDir = File(context.filesDir, ".tonewise/booklets")
        if (!bookletsDir.isDirectory) return null
        val files =
            bookletsDir.listFiles { file ->
                file.isFile && file.extension.equals("pdf", ignoreCase = true)
            } ?: return null

        val workSlug = song.workName?.let(::sanitizeKey)
        val albumSlug = song.album.name.resolve(context).let(::sanitizeKey)
        val candidate =
            files.firstOrNull { file ->
                val base = sanitizeKey(file.nameWithoutExtension)
                (workSlug != null && base.contains(workSlug)) ||
                    (albumSlug.isNotEmpty() && base.contains(albumSlug))
            } ?: files.firstOrNull { it.name.equals("booklet.pdf", ignoreCase = true) }

        return candidate?.let { file ->
            runCatching {
                    ResolvedBooklet(
                        file.name,
                        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY),
                    )
                }
                .getOrNull()
        }
    }

    private fun findSafBooklet(
        context: Context,
        musicSettings: MusicSettings,
        songPath: Path,
    ): ResolvedBooklet? {
        val dirComponents = songPath.directory.components.components
        for (location in musicSettings.safQuery.source) {
            val rootDocId =
                runCatching { DocumentsContract.getTreeDocumentId(location.uri) }.getOrNull()
                    ?: continue
            val rootParts = location.path.components.components
            val relParts =
                if (dirComponents.size >= rootParts.size &&
                    dirComponents.take(rootParts.size) == rootParts
                ) {
                    dirComponents.drop(rootParts.size)
                } else {
                    dirComponents
                }

            var currentDocId = rootDocId
            var navigated = true
            for (segment in relParts) {
                val child = findChildByName(context, location.uri, currentDocId, segment)
                if (child == null) {
                    navigated = false
                    break
                }
                currentDocId = child.docId
            }

            if (navigated) {
                pickPdfChild(context, location.uri, currentDocId)?.let { doc ->
                    openSafUri(context, doc)?.let { return it }
                }
            }

            // Fallback: check `.tonewise/booklets` inside this SAF mount
            val tonewiseDoc = findChildByName(context, location.uri, rootDocId, ".tonewise")
            val bookletsDoc =
                tonewiseDoc?.let { findChildByName(context, location.uri, it.docId, "booklets") }
            if (bookletsDoc != null) {
                pickPdfChild(context, location.uri, bookletsDoc.docId)?.let { doc ->
                    openSafUri(context, doc)?.let { return it }
                }
            }
        }
        return null
    }

    private fun findExternalDirectoryBooklet(songPath: Path): ResolvedBooklet? {
        val volumeDir = songPath.volume.components?.unixString?.let { File("/$it") }
        val dirsToCheck = buildList {
            if (volumeDir != null) {
                add(File(volumeDir, songPath.directory.components.unixString))
            }
            @Suppress("DEPRECATION")
            val musicDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
            add(File(musicDir, songPath.directory.components.unixString))
            add(File(musicDir, ".tonewise/booklets"))
        }

        for (dir in dirsToCheck) {
            if (!dir.isDirectory) continue
            val pdfs =
                dir.listFiles { file ->
                    file.isFile && file.extension.equals("pdf", ignoreCase = true)
                } ?: continue
            val target =
                pdfs.firstOrNull { it.name.equals("booklet.pdf", ignoreCase = true) }
                    ?: pdfs.firstOrNull()
                    ?: continue
            val opened =
                runCatching {
                        ResolvedBooklet(
                            target.name,
                            ParcelFileDescriptor.open(target, ParcelFileDescriptor.MODE_READ_ONLY),
                        )
                    }
                    .getOrNull()
            if (opened != null) return opened
        }
        return null
    }

    private fun openSafUri(context: Context, doc: SafDoc): ResolvedBooklet? {
        val docUri = DocumentsContract.buildDocumentUriUsingTree(doc.treeUri, doc.docId)
        val pfd = runCatching { context.contentResolver.openFileDescriptor(docUri, "r") }.getOrNull()
            ?: return null
        return ResolvedBooklet(doc.name, pfd)
    }

    private data class SafDoc(val treeUri: Uri, val docId: String, val name: String)

    private fun findChildByName(
        context: Context,
        treeUri: Uri,
        parentDocId: String,
        targetName: String,
    ): SafDoc? {
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentDocId)
        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            )
        return runCatching {
                context.contentResolver.query(childrenUri, projection, null, null, null)?.use { c ->
                    val idIdx = c.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                    val nameIdx =
                        c.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                    while (c.moveToNext()) {
                        val name = c.getString(nameIdx) ?: continue
                        if (name.equals(targetName, ignoreCase = true)) {
                            return@use SafDoc(treeUri, c.getString(idIdx), name)
                        }
                    }
                    null
                }
            }
            .getOrNull()
    }

    private fun pickPdfChild(context: Context, treeUri: Uri, parentDocId: String): SafDoc? {
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentDocId)
        val projection =
            arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            )
        return runCatching {
                context.contentResolver.query(childrenUri, projection, null, null, null)?.use { c ->
                    val idIdx = c.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                    val nameIdx =
                        c.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                    var fallback: SafDoc? = null
                    while (c.moveToNext()) {
                        val name = c.getString(nameIdx) ?: continue
                        if (!name.endsWith(".pdf", ignoreCase = true)) continue
                        val doc = SafDoc(treeUri, c.getString(idIdx), name)
                        if (name.equals("booklet.pdf", ignoreCase = true)) return@use doc
                        if (fallback == null) fallback = doc
                    }
                    fallback
                }
            }
            .getOrNull()
    }

    private fun sanitizeKey(raw: String): String =
        raw.lowercase().replace(Regex("[^a-z0-9\\u4e00-\\u9fff]+"), "")
}

@Composable
private fun BookletPdfSheet(
    title: String,
    subtitle: String,
    loading: Boolean,
    resolved: ResolvedBooklet?,
) {
    var pageIndex by remember(resolved) { mutableIntStateOf(0) }
    var pageCount by remember(resolved) { mutableIntStateOf(0) }
    var renderedPage by remember(resolved) { mutableStateOf<ImageBitmap?>(null) }
    var renderer by remember(resolved) { mutableStateOf<PdfRenderer?>(null) }

    val pdfExecutor = remember {
        Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "Tonewise-PdfRenderer").apply { isDaemon = true }
        }
    }
    val pdfDispatcher = remember(pdfExecutor) { pdfExecutor.asCoroutineDispatcher() }

    DisposableEffect(pdfExecutor) {
        onDispose {
            pdfDispatcher.close()
            pdfExecutor.shutdown()
        }
    }

    DisposableEffect(resolved) {
        var activeRenderer: PdfRenderer? = null
        val handle = resolved
        if (handle != null) {
            pdfExecutor.execute {
                val created = runCatching { PdfRenderer(handle.pfd) }.getOrNull()
                if (created != null) {
                    activeRenderer = created
                    renderer = created
                } else {
                    handle.closeQuietly()
                }
            }
        }
        onDispose {
            val toClose = activeRenderer ?: renderer
            renderer = null
            pdfExecutor.execute {
                if (toClose != null) {
                    runCatching { toClose.close() }
                } else {
                    handle?.closeQuietly()
                }
            }
        }
    }

    LaunchedEffect(renderer, pageIndex) {
        val active = renderer
        if (active == null) {
            pageCount = 0
            renderedPage = null
            return@LaunchedEffect
        }
        val (count, bmp) =
            withContext(pdfDispatcher) {
                if (active.pageCount <= 0) {
                    0 to null
                } else {
                    val clamped = pageIndex.coerceIn(0, active.pageCount - 1)
                    val rendered =
                        runCatching {
                                active.openPage(clamped).use { page ->
                                    val targetWidth = 1080
                                    val targetHeight =
                                        ((page.height.toFloat() / page.width.coerceAtLeast(1)) *
                                                targetWidth)
                                            .toInt()
                                            .coerceIn(480, 2400)
                                    val bitmap =
                                        Bitmap.createBitmap(
                                            targetWidth,
                                            targetHeight,
                                            Bitmap.Config.ARGB_8888,
                                        )
                                    Canvas(bitmap).drawColor(AndroidColor.WHITE)
                                    page.render(
                                        bitmap,
                                        null,
                                        null,
                                        PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY,
                                    )
                                    bitmap.asImageBitmap()
                                }
                            }
                            .getOrNull()
                    active.pageCount to rendered
                }
            }
        pageCount = count
        renderedPage = bmp
    }

    Surface(modifier = Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier.fillMaxWidth().navigationBarsPadding().padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.lbl_booklet_pdf),
                style = MaterialTheme.typography.titleLarge,
                fontWeight = FontWeight.SemiBold,
            )
            if (title.isNotEmpty() || subtitle.isNotEmpty()) {
                Text(
                    text = listOf(title, subtitle).filter { it.isNotEmpty() }.joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            if (loading) {
                Box(
                    modifier = Modifier.fillMaxWidth().height(180.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = "...",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else if (renderer == null || pageCount == 0 || renderedPage == null) {
                ElevatedCard(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Text(
                        text = stringResource(R.string.lbl_booklet_empty),
                        modifier = Modifier.padding(16.dp),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else {
                Text(
                    text = "${resolved?.displayName ?: "booklet.pdf"} · ${stringResource(R.string.fmt_booklet_page, pageIndex + 1, pageCount)}",
                    style = MaterialTheme.typography.labelMedium,
                    fontFamily = FontFamily.Monospace,
                    color = MaterialTheme.colorScheme.primary,
                )

                Image(
                    bitmap = renderedPage!!,
                    contentDescription = stringResource(R.string.lbl_booklet_pdf),
                    contentScale = ContentScale.FillWidth,
                    modifier =
                        Modifier.fillMaxWidth()
                            .heightIn(max = 460.dp)
                            .clip(RoundedCornerShape(8.dp)),
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    OutlinedButton(
                        onClick = { if (pageIndex > 0) pageIndex-- },
                        enabled = pageIndex > 0,
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_booklet_prev))
                    }
                    OutlinedButton(
                        onClick = { if (pageIndex + 1 < pageCount) pageIndex++ },
                        enabled = pageIndex + 1 < pageCount,
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.lbl_booklet_next))
                    }
                }
            }

            Spacer(modifier = Modifier.height(4.dp))
        }
    }
}
