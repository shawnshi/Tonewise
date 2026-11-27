/*
 * Copyright (c) 2026 Tonewise Project
 * QuickplayCoverResolver.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.home.quickplay

import android.content.Context
import androidx.core.net.toUri
import java.io.File
import java.io.IOException
import java.io.InputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.Playlist
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.covers.Cover

internal sealed interface CachedCoverSpec {
    data class Stored(val coverId: String, val fileName: String) : CachedCoverSpec

    data class ContentUri(val coverId: String, val uriString: String) : CachedCoverSpec
}

internal fun parseCachedCoverSpec(coverId: String?): CachedCoverSpec? {
    val id = coverId?.trim()?.takeIf { it.isNotEmpty() } ?: return null
    return when {
        id.startsWith("mcs:") -> {
            val fileName = id.substring(4)
            if (
                fileName.isBlank() ||
                    fileName == "." ||
                    fileName == ".." ||
                    fileName.contains('/') ||
                    fileName.contains('\\') ||
                    fileName.contains('\u0000') ||
                    fileName.contains("..")
            ) {
                null
            } else {
                CachedCoverSpec.Stored(id, fileName)
            }
        }
        id.startsWith("mcf:") -> {
            val uri = id.substring(4)
            if (uri.startsWith("content://") && uri.length > "content://".length) {
                CachedCoverSpec.ContentUri(id, uri)
            } else {
                null
            }
        }
        else -> null
    }
}

fun prominentAlbumCoverId(album: Album): String =
    album.covers.covers
        .groupBy { it.id }
        .maxByOrNull { it.value.size }
        ?.value
        ?.firstOrNull()
        ?.id
        .orEmpty()

fun primaryCoverIdForTarget(target: Any?): String =
    when (target) {
        is Album -> prominentAlbumCoverId(target)
        is Song -> target.cover?.id.orEmpty()
        is Artist -> target.covers.covers.firstOrNull()?.id.orEmpty()
        is Playlist -> target.covers.covers.firstOrNull()?.id.orEmpty()
        else -> ""
    }

object QuickplayCoverResolver {
    fun fromId(context: Context, coverId: String?): Cover? {
        val spec = parseCachedCoverSpec(coverId) ?: return null
        return QuickplayCachedCover(context.applicationContext, spec)
    }
}

private class QuickplayCachedCover(
    private val appContext: Context,
    private val spec: CachedCoverSpec,
) : Cover {
    override val id: String =
        when (spec) {
            is CachedCoverSpec.Stored -> spec.coverId
            is CachedCoverSpec.ContentUri -> spec.coverId
        }

    override suspend fun open(): InputStream? =
        withContext(Dispatchers.IO) {
            when (spec) {
                is CachedCoverSpec.Stored -> {
                    try {
                        val coversDir = appContext.filesDir.resolve("covers").canonicalFile
                        val target = File(coversDir, spec.fileName).canonicalFile
                        if (target.parentFile == coversDir && target.isFile) {
                            target.inputStream()
                        } else {
                            null
                        }
                    } catch (_: IOException) {
                        null
                    } catch (_: SecurityException) {
                        null
                    }
                }
                is CachedCoverSpec.ContentUri -> {
                    try {
                        appContext.contentResolver.openInputStream(spec.uriString.toUri())
                    } catch (_: Exception) {
                        null
                    }
                }
            }
        }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is QuickplayCachedCover) return false
        return id == other.id
    }

    override fun hashCode(): Int = id.hashCode()
}
