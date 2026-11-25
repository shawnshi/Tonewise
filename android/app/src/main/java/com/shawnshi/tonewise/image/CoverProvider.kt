/*
 * Copyright (c) 2025 Auxio Project
 * CoverProvider.kt is part of Auxio.
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
 
package com.shawnshi.tonewise.image

import android.content.ContentProvider
import android.content.ContentResolver
import android.content.ContentValues
import android.content.UriMatcher
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import java.lang.ref.WeakReference
import kotlinx.coroutines.runBlocking
import com.shawnshi.tonewise.BuildConfig
import com.shawnshi.tonewise.image.covers.SettingCovers
import com.shawnshi.tonewise.music.MusicRepository
import org.oxycblt.musikr.Library
import org.oxycblt.musikr.covers.CoverResult

@EntryPoint
@InstallIn(SingletonComponent::class)
interface CoverProviderEntryPoint {
    fun musicRepository(): MusicRepository
}

class CoverProvider : ContentProvider() {
    private val allowListLock = Any()
    @Volatile private var cachedLibraryRef: WeakReference<Library>? = null
    @Volatile private var cachedAllowedIds: Set<String> = emptySet()

    override fun onCreate(): Boolean = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? {
        if (mode != "r" || uriMatcher.match(uri) != 1) {
            return null
        }
        val id = uri.lastPathSegment?.takeIf { it.isNotBlank() } ?: return null
        if (id.contains("..") || id.contains('\u0000')) {
            return null
        }
        val ctx = context ?: return null
        if (!isIndexedCoverId(ctx.applicationContext, id)) {
            return null
        }
        return runBlocking {
            when (val result = SettingCovers.immutable(ctx).obtain(id)) {
                is CoverResult.Hit -> result.cover.fd()
                else -> null
            }
        }
    }

    private fun isIndexedCoverId(appContext: android.content.Context, id: String): Boolean {
        val library =
            runCatching {
                EntryPointAccessors.fromApplication(appContext, CoverProviderEntryPoint::class.java)
                    .musicRepository()
                    .library
            }.getOrNull() ?: return false

        val allowed =
            if (cachedLibraryRef?.get() === library) {
                cachedAllowedIds
            } else {
                synchronized(allowListLock) {
                    if (cachedLibraryRef?.get() === library) {
                        cachedAllowedIds
                    } else {
                        HashSet<String>(library.albums.size * 2 + 16).apply {
                            for (song in library.songs) {
                                song.cover?.id?.let(::add)
                            }
                            for (album in library.albums) {
                                for (cover in album.covers.covers) add(cover.id)
                            }
                            for (artist in library.artists) {
                                for (cover in artist.covers.covers) add(cover.id)
                            }
                            for (genre in library.genres) {
                                for (cover in genre.covers.covers) add(cover.id)
                            }
                            for (playlist in library.playlists) {
                                for (cover in playlist.covers.covers) add(cover.id)
                            }
                        }.also { built ->
                            cachedAllowedIds = built
                            cachedLibraryRef = WeakReference(library)
                        }
                    }
                }
            }
        return id in allowed
    }

    override fun getType(uri: Uri): String {
        check(uriMatcher.match(uri) == 1) { "Unknown URI: $uri" }
        return "image/*"
    }

    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?,
    ): Cursor = throw UnsupportedOperationException()

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0

    override fun update(
        uri: Uri,
        values: ContentValues?,
        selection: String?,
        selectionArgs: Array<out String>?,
    ): Int = 0

    companion object {
        private const val AUTHORITY = "${BuildConfig.APPLICATION_ID}.image.CoverProvider"
        private const val IMAGES_PATH = "covers"
        private val uriMatcher =
            UriMatcher(UriMatcher.NO_MATCH).apply { addURI(AUTHORITY, "$IMAGES_PATH/*", 1) }

        val CONTENT_URI: Uri =
            Uri.Builder()
                .scheme(ContentResolver.SCHEME_CONTENT)
                .authority(AUTHORITY)
                .appendPath(IMAGES_PATH)
                .build()
    }
}
