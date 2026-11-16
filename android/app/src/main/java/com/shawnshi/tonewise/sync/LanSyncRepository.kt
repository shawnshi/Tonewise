/*
 * Copyright (c) 2026 Tonewise Project
 * LanSyncRepository.kt is part of Tonewise.
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

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.os.Environment
import dagger.hilt.android.qualifiers.ApplicationContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import com.shawnshi.tonewise.music.MusicRepository
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import com.shawnshi.tonewise.playback.ui.WaveformRepository
import org.json.JSONArray
import org.json.JSONObject
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.core.TonewiseCore
import org.oxycblt.musikr.fs.Components
import org.oxycblt.musikr.tag.Name
import timber.log.Timber as L

data class DiscoveredSyncHost(val serviceName: String, val host: String, val port: Int) {
    val baseUrl: String
        get() = "http://$host:$port"
}

data class SceneBanner(
    val id: String,
    val title: String,
    val subtitle: String,
    val accentHex: String,
    val rawSceneJson: String,
)

@Singleton
class LanSyncRepository
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val musicRepository: MusicRepository,
    private val playCountRepository: PlayCountRepository,
    private val sessionSlotStore: SessionSlotStore,
    private val waveformRepository: WaveformRepository,
) {
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private var activeDiscoveryListener: NsdManager.DiscoveryListener? = null

    private val _discoveredHosts = MutableStateFlow<List<DiscoveredSyncHost>>(emptyList())
    val discoveredHosts: StateFlow<List<DiscoveredSyncHost>> = _discoveredHosts

    private val _scenes = MutableStateFlow(loadCachedOrDefaultScenes())
    val scenes: StateFlow<List<SceneBanner>> = _scenes

    private val _syncStatus = MutableStateFlow("Ready for LAN Sync (_tonewise._tcp)")
    val syncStatus: StateFlow<String> = _syncStatus

    private val writeBackScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var pendingWriteBackJob: Job? = null

    fun scheduleM3u8WriteBack(sceneName: String, songs: List<Song>) {
        pendingWriteBackJob?.cancel()
        val snapshot = songs.toList()
        val safeName = sanitizePlaylistFilename(sceneName)
        pendingWriteBackJob =
            writeBackScope.launch {
                delay(2_000L)
                val payload = formatM3u8Content(snapshot).toByteArray(Charsets.UTF_8)
                runCatching {
                    val internalDir = File(context.filesDir, ".tonewise/playlists")
                    internalDir.mkdirs()
                    File(internalDir, "$safeName.m3u8").writeBytes(payload)
                }
                runCatching {
                    @Suppress("DEPRECATION")
                    val musicDir =
                        Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC)
                    val extDir = File(musicDir, ".tonewise/playlists")
                    if (extDir.exists() || extDir.mkdirs()) {
                        File(extDir, "$safeName.m3u8").writeBytes(payload)
                    }
                }
                L.d("Debounced .m3u8 write-back flushed for $safeName (${snapshot.size} tracks)")
            }
    }

    fun startDiscovery() {
        val manager = nsdManager ?: return
        if (activeDiscoveryListener != null) return

        val listener =
            object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(regType: String) {
                    L.d("NSD discovery started for $regType")
                }

                override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                    if (serviceInfo.serviceType.contains("_tonewise._tcp")) {
                        @Suppress("DEPRECATION")
                        manager.resolveService(
                            serviceInfo,
                            object : NsdManager.ResolveListener {
                                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                                    L.w("NSD resolve failed: $errorCode")
                                }

                                override fun onServiceResolved(resolved: NsdServiceInfo) {
                                    @Suppress("DEPRECATION")
                                    val host = resolved.host?.hostAddress ?: return
                                    val entry =
                                        DiscoveredSyncHost(
                                            resolved.serviceName,
                                            host,
                                            resolved.port,
                                        )
                                    val updated =
                                        (_discoveredHosts.value.filterNot {
                                            it.host == entry.host && it.port == entry.port
                                        } + entry)
                                    _discoveredHosts.value = updated
                                }
                            },
                        )
                    }
                }

                override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                    _discoveredHosts.value =
                        _discoveredHosts.value.filterNot { it.serviceName == serviceInfo.serviceName }
                }

                override fun onDiscoveryStopped(serviceType: String) {}

                override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                    activeDiscoveryListener = null
                }

                override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            }

        activeDiscoveryListener = listener
        runCatching {
            manager.discoverServices("_tonewise._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
        }.onFailure { activeDiscoveryListener = null }
    }

    fun stopDiscovery() {
        val listener = activeDiscoveryListener ?: return
        activeDiscoveryListener = null
        runCatching { nsdManager?.stopServiceDiscovery(listener) }
    }

    suspend fun syncWithHost(baseUrl: String) {
        withContext(Dispatchers.IO) {
            val cleanBase = baseUrl.trim().trimEnd('/')
            _syncStatus.value = "Connecting to $cleanBase ..."
            try {
                // 1. Check host status
                val statusJson = httpGetString("$cleanBase/api/v1/status")
                val statusObj = JSONObject(statusJson)

                // 2. Pull scenes_5d.json
                val scenesRaw = httpGetString("$cleanBase/api/v1/scenes")
                cacheFile("scenes_5d.json", scenesRaw.toByteArray(Charsets.UTF_8))
                _scenes.value = parseScenesJson(scenesRaw)

                // 3. Pull waveforms.bin if available
                var wfMessage = "no waveforms"
                if (statusObj.optBoolean("has_waveforms", false)) {
                    val wfBytes = httpGetBytes("$cleanBase/api/v1/waveforms")
                    cacheFile("waveforms.bin", wfBytes)
                    waveformRepository.invalidateArchiveCache()
                    wfMessage = "${wfBytes.size / 1024} KB waveforms"
                }

                // 4. Push local play-count snapshot events & merge 5-slot Handoff sessions
                val pushedCount = pushLocalPlayCounts(cleanBase)
                val mergedSlotsJson =
                    httpPostJson("$cleanBase/api/v1/handoff", sessionSlotStore.exportSlotsJson())
                val mergedSlots = sessionSlotStore.mergeRemoteJson(mergedSlotsJson)

                _syncStatus.value =
                    "Synced OK: ${_scenes.value.size} scenes · $wfMessage · $pushedCount counts · ${mergedSlots.size} slots"
            } catch (e: Exception) {
                L.w(e, "LAN sync failed")
                _syncStatus.value = "Sync failed: ${e.message}"
            }
        }
    }

    fun matchSongsForScene(scene: SceneBanner): List<Song> {
        val songs = musicRepository.library?.songs ?: return emptyList()
        val matchedUids =
            TonewiseCore.evaluateScene(scene.rawSceneJson, buildTracksJson(songs)).toHashSet()
        return songs.filter { it.uid.toString() in matchedUids }
    }

    fun matchSongsByDjFilter(scene: String?, vocals: String?, energy: String?): List<Song> {
        val songs = musicRepository.library?.songs ?: return emptyList()
        val predicates = JSONArray()
        scene?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.put(predicateJson("dj_scene", "in", it))
        }
        vocals?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.put(predicateJson("dj_vocals", "eq", it))
        }
        energy?.takeIf { it.isNotBlank() && it != "all" }?.let {
            predicates.put(predicateJson("dj_energy", "eq", it))
        }
        if (predicates.length() == 0) {
            return songs.toList()
        }
        val sceneJson =
            JSONObject()
                .put("id", "00000000-0000-4000-8000-000000000002")
                .put("title", "DJ Tag Filter")
                .put("updated_at_ms", 0)
                .put("sort_arc", "flat")
                .put("tempo_null_passthrough", true)
                .put("rule", JSONObject().put("all", predicates))
                .toString()
        val matchedUids = TonewiseCore.evaluateScene(sceneJson, buildTracksJson(songs)).toHashSet()
        return songs.filter { it.uid.toString() in matchedUids }
    }

    private fun predicateJson(field: String, op: String, value: String): JSONObject =
        JSONObject()
            .put(
                "predicate",
                JSONObject()
                    .put("field", field)
                    .put("op", op)
                    .put("values", JSONArray(listOf(value))),
            )

    private fun buildTracksJson(songs: Collection<Song>): String {
        val tracksJson = JSONArray()
        for (song in songs) {
            val genres = song.genres.mapNotNull { (it.name as? Name.Known)?.raw }.firstOrNull() ?: ""
            val obj =
                JSONObject()
                    .put("track_uid", song.uid.toString())
                    .put("title", song.name.raw)
                    .put("dj_scene", JSONArray(song.djScenes))
                    .put("dj_vocals", song.djVocals ?: "unknown")
                    .put("dj_energy", song.djEnergy ?: "unknown")
                    .put("tempo_bucket", JSONObject.NULL)
                    .put("moods", JSONArray())
                    .put("genre", genres)
                    .put("rating", 4)
                    .put("unplayed_days", if (playCountRepository.getPlayCount(song) == 0) 95 else 1)
            tracksJson.put(obj)
        }
        return tracksJson.toString()
    }

    private fun pushLocalPlayCounts(baseUrl: String): Int {
        val songs = musicRepository.library?.songs ?: return 0
        val events = JSONArray()
        val now = System.currentTimeMillis()
        for (song in songs) {
            val count = playCountRepository.getPlayCount(song)
            if (count > 0) {
                events.put(
                    JSONObject()
                        .put("track_uid", song.uid.toString())
                        .put("utc_ts_ms", now)
                        .put("device_id", Build.MODEL ?: "android")
                        .put("listened_ms", song.durationMs)
                        .put("completed", true)
                )
            }
        }
        if (events.length() == 0) return 0
        val payload =
            JSONObject()
                .put("device_id", Build.MODEL ?: "android")
                .put("events", events)
                .toString()
        httpPostJson("$baseUrl/api/v1/history/push", payload)
        return events.length()
    }

    private fun cacheFile(name: String, bytes: ByteArray) {
        val dir = File(context.filesDir, ".tonewise")
        if (!dir.exists()) dir.mkdirs()
        File(dir, name).writeBytes(bytes)
    }

    private fun loadCachedOrDefaultScenes(): List<SceneBanner> {
        val cached = File(context.filesDir, ".tonewise/scenes_5d.json")
        val raw =
            if (cached.isFile) {
                runCatching { cached.readText(Charsets.UTF_8) }.getOrNull()
            } else {
                null
            } ?: DEFAULT_SCENES_JSON
        return runCatching { parseScenesJson(raw) }.getOrDefault(emptyList())
    }

    private fun parseScenesJson(raw: String): List<SceneBanner> {
        val root = JSONObject(raw)
        val arr = root.optJSONArray("scenes") ?: return emptyList()
        val list = ArrayList<SceneBanner>(arr.length())
        for (i in 0 until arr.length()) {
            val obj = arr.getJSONObject(i)
            if (obj.optBoolean("deleted", false)) continue
            list.add(
                SceneBanner(
                    id = obj.getString("id"),
                    title = obj.getString("title"),
                    subtitle = obj.optString("subtitle", ""),
                    accentHex = obj.optString("accent_hex", "#005EB8"),
                    rawSceneJson = obj.toString(),
                )
            )
        }
        return list
    }

    private fun httpGetString(url: String): String = String(httpGetBytes(url), Charsets.UTF_8)

    private fun httpGetBytes(url: String): ByteArray {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 5_000
            readTimeout = 15_000
            requestMethod = "GET"
        }
        return try {
            if (conn.responseCode !in 200..299) {
                throw IllegalStateException("HTTP ${conn.responseCode} for $url")
            }
            conn.inputStream.readBytes()
        } finally {
            conn.disconnect()
        }
    }

    private fun httpPostJson(url: String, body: String): String {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 5_000
            readTimeout = 15_000
            requestMethod = "POST"
            doOutput = true
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
        }
        return try {
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            if (conn.responseCode !in 200..299) {
                throw IllegalStateException("HTTP ${conn.responseCode} for $url")
            }
            String(conn.inputStream.readBytes(), Charsets.UTF_8)
        } finally {
            conn.disconnect()
        }
    }

    data class M3u8Entry(
        val durationMs: Long,
        val title: String,
        val artists: List<String>,
        val pathComponents: Components,
    )

    companion object {
        fun sanitizePlaylistFilename(raw: String): String =
            raw.trim().ifEmpty { "Default" }.replace(Regex("[\\\\/:*?\"<>|\\s]+"), "_")

        fun formatM3u8Content(songs: List<Song>): String =
            formatM3u8Entries(
                songs.map { song ->
                    M3u8Entry(
                        durationMs = song.durationMs,
                        title = song.name.raw,
                        artists = song.artists.mapNotNull { (it.name as? Name.Known)?.raw },
                        pathComponents = song.path.components,
                    )
                }
            )

        fun formatM3u8Entries(entries: List<M3u8Entry>): String {
            val sb = StringBuilder()
            sb.append("#EXTM3U\n")
            for (entry in entries) {
                val secs = (entry.durationMs / 1000L).coerceAtLeast(0L)
                val artist = entry.artists.filter { it.isNotBlank() }.joinToString(", ")
                val extinf = if (artist.isNotEmpty()) "$artist - ${entry.title}" else entry.title
                sb.append("#EXTINF:").append(secs).append(',').append(extinf).append('\n')
                val relPath = entry.pathComponents.components.joinToString("/").replace('\\', '/')
                sb.append(relPath).append('\n')
            }
            return sb.toString()
        }

        private const val DEFAULT_SCENES_JSON = """
            {
              "schema_version": 1,
              "scenes": [
                {
                  "id": "11111111-1111-4111-8111-111111111111",
                  "title": "Focus · 深度专注",
                  "subtitle": "DJ_SCENE = focus",
                  "accent_hex": "#2B5B84",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "flat",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["focus"] } }
                    ]
                  }
                },
                {
                  "id": "22222222-2222-4222-8222-222222222222",
                  "title": "Coding · 心流编程",
                  "subtitle": "DJ_SCENE = coding",
                  "accent_hex": "#3B7A57",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "work_shuffle",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["coding"] } }
                    ]
                  }
                },
                {
                  "id": "33333333-3333-4333-8333-333333333333",
                  "title": "Relax · 松弛休憩",
                  "subtitle": "DJ_SCENE = relax",
                  "accent_hex": "#5B6E8C",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "energy_desc_sleep",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["relax"] } }
                    ]
                  }
                },
                {
                  "id": "55555555-5555-4555-8555-555555555555",
                  "title": "Energy · 高能律动",
                  "subtitle": "DJ_SCENE = energy",
                  "accent_hex": "#C85A32",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "energy_asc",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["energy"] } }
                    ]
                  }
                },
                {
                  "id": "66666666-6666-4666-8666-666666666666",
                  "title": "Pop · 流行精选",
                  "subtitle": "DJ_SCENE = pop",
                  "accent_hex": "#9E4770",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "flat",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "dj_scene", "op": "in", "values": ["pop"] } }
                    ]
                  }
                },
                {
                  "id": "44444444-4444-4444-8444-444444444444",
                  "title": "沉睡馆藏唤醒",
                  "subtitle": "尚未播放或超过 90 天未重温的本地曲库",
                  "accent_hex": "#8B5E3C",
                  "updated_at_ms": 1763251200000,
                  "sort_arc": "rediscovery_90d",
                  "tempo_null_passthrough": true,
                  "rule": {
                    "all": [
                      { "predicate": { "field": "unplayed_days_gte", "op": "gte", "values": [90] } }
                    ]
                  }
                }
              ]
            }
        """
    }
}
