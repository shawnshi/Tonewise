/*
 * Copyright (c) 2026 Tonewise Project
 * MusicBioRepository.kt is part of Tonewise.
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
import android.content.SharedPreferences
import androidx.core.content.edit
import com.shawnshi.tonewise.R
import com.shawnshi.tonewise.music.resolve
import com.shawnshi.tonewise.music.resolveNames
import com.shawnshi.tonewise.playback.formatDurationMs
import com.shawnshi.tonewise.playback.persist.PlayCountRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.text.Normalizer
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import org.json.JSONObject
import org.oxycblt.musikr.Album
import org.oxycblt.musikr.Artist
import org.oxycblt.musikr.PersonArtist
import org.oxycblt.musikr.PersonRole
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.tag.Name

interface MusicBioRepository {
    val updates: StateFlow<Long>

    fun getAlbumBio(album: Album): String

    fun getArtistBio(artist: Artist): String
}

@Singleton
class MusicBioRepositoryImpl
@Inject
constructor(
    @ApplicationContext private val context: Context,
    private val playCountRepository: PlayCountRepository,
) : MusicBioRepository {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val prefs: SharedPreferences by lazy {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }
    private val memoryCache = ConcurrentHashMap<String, String>()
    private val inFlight = ConcurrentHashMap.newKeySet<String>()
    private val missedKeys = ConcurrentHashMap.newKeySet<String>()

    private val _updates = MutableStateFlow(0L)
    override val updates: StateFlow<Long> = _updates

    override fun getAlbumBio(album: Album): String {
        val isZh = isChineseLocale()
        val albumTitle = album.name.resolve(context)
        val rawTitle = (album.name as? Name.Known)?.raw
        val artistNames =
            album.artists.mapNotNull { (it.name as? Name.Known)?.raw }

        val curated =
            rawTitle?.let { findCuratedAlbumBio(it, isZh) }
                ?: resolveRemoteOrSchedule(
                    cacheKey = cacheKey("album", isZh, albumTitle),
                    queries = buildAlbumWikiQueries(rawTitle ?: albumTitle, artistNames),
                    isZh = isZh,
                )

        val summary = buildAlbumArchiveSummary(album, isZh)
        return joinParagraphs(curated, summary)
    }

    override fun getArtistBio(artist: Artist): String {
        val isZh = isChineseLocale()
        val role = (artist as? PersonArtist)?.role ?: PersonRole.ARTIST
        val displayName =
            (artist as? PersonArtist)?.rawName
                ?: (artist.name as? Name.Known)?.raw
                ?: artist.name.resolve(context)

        val curated =
            if (artist.name is Name.Known || artist is PersonArtist) {
                findCuratedPersonBio(displayName, role, isZh)
                    ?: resolveRemoteOrSchedule(
                        cacheKey = cacheKey(role.name.lowercase(), isZh, displayName),
                        queries = buildPersonWikiQueries(displayName, role, isZh),
                        isZh = isZh,
                    )
            } else {
                null
            }

        val summary = buildPersonArchiveSummary(artist, role, isZh)
        return joinParagraphs(curated, summary)
    }

    private fun isChineseLocale(): Boolean =
        context.resources.configuration.locales[0]?.language?.startsWith("zh", ignoreCase = true) ==
            true

    private fun joinParagraphs(primary: String?, secondary: String): String =
        if (!primary.isNullOrBlank()) {
            "$primary\n\n$secondary"
        } else {
            secondary
        }

    private fun buildAlbumArchiveSummary(album: Album, isZh: Boolean): String {
        val songs = album.songs
        val releaseType = album.releaseType.resolve(context)
        val artists = album.artists.resolveNames(context)
        val dateText = album.dates?.resolve(context) ?: context.getString(R.string.def_date)
        val durationText = album.durationMs.formatDurationMs(true)
        val composers = songs.flatMap { it.composers }.distinct()
        val conductors = songs.flatMap { it.conductors }.distinct()
        val genres =
            songs
                .flatMap { it.genres }
                .filter { it.name is Name.Known }
                .distinct()
                .map { it.name.resolve(context) }
        val discCount = songs.mapNotNull { it.disc?.number }.distinct().size
        val totalPlays = songs.sumOf { playCountRepository.getPlayCount(it) }
        val spec = summarizeAudioSpec(songs, isZh)

        return if (isZh) {
            buildString {
                append("馆藏档案：该")
                append(releaseType)
                append("由 ")
                append(artists)
                append(" 呈现，发行年份 ")
                append(dateText)
                append("，共收录 ")
                append(songs.size)
                append(" 首曲目")
                if (discCount > 1) {
                    append("（").append(discCount).append(" 碟）")
                }
                append("，总时长 ").append(durationText).append("。")
                if (genres.isNotEmpty()) {
                    append(" 流派涵盖 ").append(genres.joinToString(" / ")).append("。")
                }
                if (composers.isNotEmpty()) {
                    append(" 作曲家：").append(composers.joinToString("、")).append("。")
                }
                if (conductors.isNotEmpty()) {
                    append(" 指挥：").append(conductors.joinToString("、")).append("。")
                }
                if (spec != null) {
                    append(" 音频规格：").append(spec).append("。")
                }
                append(" 累计播放 ").append(totalPlays).append(" 次。")
            }
        } else {
            buildString {
                append("Library archive: ")
                append(releaseType)
                append(" by ")
                append(artists)
                append(" (")
                append(dateText)
                append("), ")
                append(songs.size)
                append(if (songs.size == 1) " track" else " tracks")
                if (discCount > 1) {
                    append(" across ").append(discCount).append(" discs")
                }
                append(", total duration ").append(durationText).append(".")
                if (genres.isNotEmpty()) {
                    append(" Genres: ").append(genres.joinToString(", ")).append(".")
                }
                if (composers.isNotEmpty()) {
                    append(" Composers: ").append(composers.joinToString(", ")).append(".")
                }
                if (conductors.isNotEmpty()) {
                    append(" Conductors: ").append(conductors.joinToString(", ")).append(".")
                }
                if (spec != null) {
                    append(" Audio: ").append(spec).append(".")
                }
                append(" Total plays: ").append(totalPlays).append(".")
            }
        }
    }

    private fun buildPersonArchiveSummary(
        artist: Artist,
        role: PersonRole,
        isZh: Boolean,
    ): String {
        val songs = artist.songs
        val albums = (artist.explicitAlbums + artist.implicitAlbums).distinct()
        val durationText = (artist.durationMs ?: 0L).formatDurationMs(true)
        val genres =
            artist.genres
                .filter { it.name is Name.Known }
                .distinct()
                .map { it.name.resolve(context) }
        val years = songs.mapNotNull { it.date?.year ?: it.album.dates?.min?.year }
        val yearSpan =
            when {
                years.isEmpty() -> null
                years.min() == years.max() -> years.min().toString()
                else -> "${years.min()}–${years.max()}"
            }
        val totalPlays = songs.sumOf { playCountRepository.getPlayCount(it) }
        val topAlbums =
            albums
                .sortedByDescending { it.songs.size }
                .take(3)
                .map { it.name.resolve(context) }

        val collaborators =
            when (role) {
                PersonRole.COMPOSER ->
                    songs
                        .flatMap { it.artists }
                        .filter { it.name is Name.Known }
                        .distinct()
                        .take(4)
                        .map { it.name.resolve(context) }
                PersonRole.CONDUCTOR ->
                    songs
                        .flatMap { it.composers }
                        .distinct()
                        .take(4)
                PersonRole.ARTIST ->
                    songs
                        .flatMap { it.conductors }
                        .distinct()
                        .take(3)
            }

        return if (isZh) {
            val roleLabel =
                when (role) {
                    PersonRole.COMPOSER -> "作曲家"
                    PersonRole.CONDUCTOR -> "指挥"
                    PersonRole.ARTIST -> "艺术家"
                }
            buildString {
                append("馆藏档案：当前曲库共收录该")
                append(roleLabel)
                append("相关专辑 ")
                append(albums.size)
                append(" 张、曲目 ")
                append(songs.size)
                append(" 首，总时长 ")
                append(durationText)
                append("。")
                if (yearSpan != null) {
                    append(" 录音/发行年份跨度 ").append(yearSpan).append("。")
                }
                if (genres.isNotEmpty()) {
                    append(" 主要流派：").append(genres.joinToString(" / ")).append("。")
                }
                if (collaborators.isNotEmpty()) {
                    val collabLabel =
                        when (role) {
                            PersonRole.COMPOSER -> "演绎艺术家/乐团"
                            PersonRole.CONDUCTOR -> "常演作曲家"
                            PersonRole.ARTIST -> "合作指挥"
                        }
                    append(" ").append(collabLabel).append("：")
                        .append(collaborators.joinToString("、")).append("。")
                }
                if (topAlbums.isNotEmpty()) {
                    append(" 代表专辑：").append(topAlbums.joinToString("、")).append("。")
                }
                append(" 累计播放 ").append(totalPlays).append(" 次。")
            }
        } else {
            val roleLabel =
                when (role) {
                    PersonRole.COMPOSER -> "composer"
                    PersonRole.CONDUCTOR -> "conductor"
                    PersonRole.ARTIST -> "artist"
                }
            buildString {
                append("Library archive: ")
                append(albums.size)
                append(if (albums.size == 1) " album" else " albums")
                append(" and ")
                append(songs.size)
                append(if (songs.size == 1) " track" else " tracks")
                append(" credited to this ").append(roleLabel)
                append(" (total duration ").append(durationText).append(").")
                if (yearSpan != null) {
                    append(" Years in library: ").append(yearSpan).append(".")
                }
                if (genres.isNotEmpty()) {
                    append(" Genres: ").append(genres.joinToString(", ")).append(".")
                }
                if (collaborators.isNotEmpty()) {
                    val collabLabel =
                        when (role) {
                            PersonRole.COMPOSER -> "Featured performers"
                            PersonRole.CONDUCTOR -> "Composers performed"
                            PersonRole.ARTIST -> "Conductors"
                        }
                    append(" ").append(collabLabel).append(": ")
                        .append(collaborators.joinToString(", ")).append(".")
                }
                if (topAlbums.isNotEmpty()) {
                    append(" Key releases: ").append(topAlbums.joinToString(", ")).append(".")
                }
                append(" Total plays: ").append(totalPlays).append(".")
            }
        }
    }

    private fun summarizeAudioSpec(songs: Collection<Song>, isZh: Boolean): String? {
        if (songs.isEmpty()) return null
        val formatName =
            songs
                .groupingBy { it.format.resolve(context) }
                .eachCount()
                .maxByOrNull { it.value }
                ?.key
                ?: return null
        val maxSampleRate = songs.maxOfOrNull { it.sampleRateHz } ?: 0
        val avgBitrate = songs.map { it.bitrateKbps }.filter { it > 0 }.average()
        val rateText =
            if (maxSampleRate > 0) {
                val khz = maxSampleRate / 1000.0
                if (maxSampleRate % 1000 == 0) "${maxSampleRate / 1000} kHz"
                else String.format("%.1f kHz", khz)
            } else {
                null
            }
        val bitrateText =
            if (!avgBitrate.isNaN() && avgBitrate > 0) "${avgBitrate.toInt()} kbps" else null
        return listOfNotNull(formatName, rateText, bitrateText).joinToString(if (isZh) " · " else " · ")
    }

    private fun cacheKey(kind: String, isZh: Boolean, rawName: String): String =
        "${kind}_${if (isZh) "zh" else "en"}_${normalizeKey(rawName)}"

    private fun resolveRemoteOrSchedule(
        cacheKey: String,
        queries: List<String>,
        isZh: Boolean,
    ): String? {
        memoryCache[cacheKey]?.let { return it }
        val persisted = prefs.getString(cacheKey, null)
        if (!persisted.isNullOrBlank()) {
            memoryCache[cacheKey] = persisted
            return persisted
        }
        if (missedKeys.contains(cacheKey) || queries.isEmpty()) {
            return null
        }
        if (inFlight.add(cacheKey)) {
            scope.launch {
                try {
                    val fetched = fetchWikipediaSummary(queries, isZh)
                    if (!fetched.isNullOrBlank()) {
                        memoryCache[cacheKey] = fetched
                        prefs.edit { putString(cacheKey, fetched) }
                        _updates.value += 1
                    } else {
                        missedKeys.add(cacheKey)
                    }
                } finally {
                    inFlight.remove(cacheKey)
                }
            }
        }
        return null
    }

    private fun fetchWikipediaSummary(queries: List<String>, isZh: Boolean): String? {
        val hosts =
            if (isZh) {
                listOf("zh.wikipedia.org", "en.wikipedia.org")
            } else {
                listOf("en.wikipedia.org")
            }
        for (host in hosts) {
            for (query in queries) {
                val summary = requestSummary(host, query)
                if (!summary.isNullOrBlank()) {
                    return summary
                }
            }
        }
        return null
    }

    private fun requestSummary(host: String, title: String): String? {
        val normalizedTitle = title.trim().replace(' ', '_')
        if (normalizedTitle.isEmpty()) return null
        var conn: HttpURLConnection? = null
        return try {
            val encoded = URLEncoder.encode(normalizedTitle, "UTF-8").replace("+", "%20")
            val url = URL("https://$host/api/rest_v1/page/summary/$encoded")
            conn =
                (url.openConnection() as HttpURLConnection).apply {
                    requestMethod = "GET"
                    connectTimeout = 4000
                    readTimeout = 4000
                    setRequestProperty("Accept", "application/json")
                    setRequestProperty("User-Agent", "Tonewise/4.1.5 (Android)")
                }
            if (conn.responseCode != HttpURLConnection.HTTP_OK) {
                return null
            }
            val body = conn.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val json = JSONObject(body)
            val type = json.optString("type")
            if (type == "disambiguation") return null
            val extract = json.optString("extract").trim()
            if (
                extract.length < 32 ||
                    extract.endsWith("may refer to:", ignoreCase = true) ||
                    extract.contains("可以指：")
            ) {
                null
            } else {
                extract
            }
        } catch (_: Exception) {
            null
        } finally {
            conn?.disconnect()
        }
    }

    private fun buildAlbumWikiQueries(albumTitle: String, artistNames: List<String>): List<String> {
        val cleaned = cleanQueryName(albumTitle)
        if (cleaned.isEmpty()) return emptyList()
        val firstArtist = artistNames.firstOrNull()?.let(::cleanQueryName)
        return buildList {
            if (!firstArtist.isNullOrEmpty()) {
                add("$cleaned ($firstArtist album)")
            }
            add("$cleaned (album)")
            add(cleaned)
        }.distinct()
    }

    private fun buildPersonWikiQueries(
        name: String,
        role: PersonRole,
        isZh: Boolean,
    ): List<String> {
        val cleaned = cleanQueryName(name)
        if (cleaned.isEmpty()) return emptyList()
        return buildList {
            when (role) {
                PersonRole.COMPOSER -> {
                    if (!isZh) add("$cleaned (composer)")
                    add(cleaned)
                }
                PersonRole.CONDUCTOR -> {
                    if (!isZh) add("$cleaned (conductor)")
                    add(cleaned)
                }
                PersonRole.ARTIST -> {
                    add(cleaned)
                }
            }
        }.distinct()
    }

    private fun cleanQueryName(raw: String): String =
        raw.replace(PAREN_SUFFIX_REGEX, "").trim()

    companion object {
        private const val PREFS_NAME = "tonewise_bio_cache"
        private val PAREN_SUFFIX_REGEX = Regex("\\s*[\\[(][^\\])]*[\\])]\\s*$")
        private val DIACRITICS_REGEX = Regex("\\p{InCombiningDiacriticalMarks}+")
        private val WHITESPACE_REGEX = Regex("\\s+")

        internal fun normalizeKey(raw: String): String {
            val cleaned =
                raw.replace(PAREN_SUFFIX_REGEX, "")
                    .replace('.', ' ')
                    .trim()
                    .lowercase()
            val decomposed = Normalizer.normalize(cleaned, Normalizer.Form.NFD)
            return DIACRITICS_REGEX.replace(decomposed, "")
                .replace(WHITESPACE_REGEX, " ")
                .trim()
        }

        internal fun findCuratedPersonBio(
            name: String,
            role: PersonRole,
            isZh: Boolean,
        ): String? {
            val norm = normalizeKey(name)
            val entry = PERSON_BIOS[norm] ?: return null
            return if (isZh) {
                when (role) {
                    PersonRole.COMPOSER -> entry.zhComposer ?: entry.zh
                    PersonRole.CONDUCTOR -> entry.zhConductor ?: entry.zh
                    PersonRole.ARTIST -> entry.zh
                }
            } else {
                when (role) {
                    PersonRole.COMPOSER -> entry.enComposer ?: entry.en
                    PersonRole.CONDUCTOR -> entry.enConductor ?: entry.en
                    PersonRole.ARTIST -> entry.en
                }
            }
        }

        internal fun findCuratedAlbumBio(albumTitle: String, isZh: Boolean): String? {
            val norm = normalizeKey(albumTitle)
            val entry =
                ALBUM_BIOS.entries.firstOrNull { (needle, _) ->
                    norm == needle || norm.contains(needle)
                }?.value
                    ?: return null
            return if (isZh) entry.zh else entry.en
        }

        private data class PersonBioEntry(
            val zh: String,
            val en: String,
            val zhComposer: String? = null,
            val enComposer: String? = null,
            val zhConductor: String? = null,
            val enConductor: String? = null,
        )

        private data class AlbumBioEntry(val zh: String, val en: String)

        private fun mapAliases(
            entry: PersonBioEntry,
            vararg aliases: String,
        ): List<Pair<String, PersonBioEntry>> = aliases.map { normalizeKey(it) to entry }

        private fun mapAlbumAliases(
            entry: AlbumBioEntry,
            vararg aliases: String,
        ): List<Pair<String, AlbumBioEntry>> = aliases.map { normalizeKey(it) to entry }

        private val PERSON_BIOS: Map<String, PersonBioEntry> =
            buildMap {
                fun putAll(vararg pairs: List<Pair<String, PersonBioEntry>>) {
                    for (group in pairs) {
                        for ((k, v) in group) put(k, v)
                    }
                }

                // --- Classical Composers ---
                putAll(
                    mapAliases(
                        PersonBioEntry(
                            zh = "约翰·塞巴斯蒂安·巴赫（Johann Sebastian Bach，1685–1750），德国巴洛克时期作曲家与管风琴家，西方古典音乐复调艺术的集大成者，代表作涵盖《勃兰登堡协奏曲》《哥德堡变奏曲》《无伴奏大提琴组曲》《马太受难曲》与《平均律钢琴曲集》。",
                            en = "Johann Sebastian Bach (1685–1750) was a German Baroque composer and organist revered for supreme mastery of counterpoint and harmonic architecture, including the Brandenburg Concertos, Goldberg Variations, Cello Suites, St Matthew Passion, and The Well-Tempered Clavier.",
                        ),
                        "Johann Sebastian Bach", "J.S. Bach", "J S Bach", "Bach", "巴赫", "约翰·塞巴斯蒂安·巴赫",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "路德维希·凡·贝多芬（Ludwig van Beethoven，1770–1827），德国作曲家与钢琴家，维也纳古典乐派向浪漫主义过渡的关键人物，九部交响曲、五部钢琴协奏曲、三十二首钢琴奏鸣曲与晚期弦乐四重奏奠定了西方交响与室内乐的现代范式。",
                            en = "Ludwig van Beethoven (1770–1827) was a German composer and pianist who bridged the Classical and Romantic eras. His nine symphonies, 32 piano sonatas, five piano concertos, and late string quartets reshaped Western art music.",
                        ),
                        "Ludwig van Beethoven", "Beethoven", "贝多芬", "路德维希·凡·贝多芬",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "沃尔夫冈·阿玛多伊斯·莫扎特（Wolfgang Amadeus Mozart，1756–1791），奥地利古典主义时期最具代表性的作曲家，创作横跨交响曲、钢琴协奏曲、歌剧与宗教音乐，以清澄旋律、均衡结构与戏剧张力著称。",
                            en = "Wolfgang Amadeus Mozart (1756–1791) was a prolific Classical-era composer whose mastery spanned symphonies, piano concertos, chamber works, and operas such as The Marriage of Figaro, Don Giovanni, and The Magic Flute.",
                        ),
                        "Wolfgang Amadeus Mozart", "W.A. Mozart", "Mozart", "莫扎特", "沃尔夫冈·阿玛多伊斯·莫扎特",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "约翰内斯·勃拉姆斯（Johannes Brahms，1833–1897），德国浪漫主义时期作曲家与钢琴家，将贝多芬式的严谨交响结构与深沉浪漫抒情融为一体，代表作有四部交响曲、《德意志安魂曲》与多部协奏曲及室内乐。",
                            en = "Johannes Brahms (1833–1897) was a German Romantic composer and pianist who fused Classical formal rigor with deeply expressive lyricism across four symphonies, concertos, chamber music, and A German Requiem.",
                        ),
                        "Johannes Brahms", "Brahms", "勃拉姆斯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "弗雷德里克·肖邦（Frédéric Chopin，1810–1849），波兰浪漫主义作曲家与钢琴家，被誉为“钢琴诗人”，几乎全部创作聚焦于钢琴音乐，以夜曲、叙事曲、练习曲、波洛涅兹与马祖卡展现极致的音色与诗意。",
                            en = "Frédéric Chopin (1810–1849) was a Polish Romantic composer and virtuoso pianist whose nocturnes, ballades, etudes, preludes, and mazurkas redefined the expressive range of the solo piano.",
                        ),
                        "Frederic Chopin", "Frédéric Chopin", "Chopin", "肖邦",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "彼得·伊里奇·柴可夫斯基（Pyotr Ilyich Tchaikovsky，1840–1893），俄罗斯浪漫主义作曲家，以宏阔管弦色彩与强烈戏剧抒情闻名，创作《天鹅湖》《胡桃夹子》《睡美人》、第四至第六交响曲及第一钢琴协奏曲。",
                            en = "Pyotr Ilyich Tchaikovsky (1840–1893) was a Russian Romantic composer celebrated for sweeping melodic invention and orchestral drama in Symphonies Nos. 4–6, the Violin Concerto, and ballets such as Swan Lake and The Nutcracker.",
                        ),
                        "Pyotr Ilyich Tchaikovsky", "P.I. Tchaikovsky", "Tchaikovsky", "柴可夫斯基",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "古斯塔夫·马勒（Gustav Mahler，1860–1911），奥地利作曲家与指挥家，后浪漫主义交响曲与艺术歌曲的巅峰代表，十部交响曲与《大地之歌》将日常声响、存在思辨与庞大管弦编制熔于一炉。",
                            en = "Gustav Mahler (1860–1911) was an Austro-Bohemian late-Romantic composer and celebrated conductor whose expansive symphonies and orchestral song cycles explore existence, irony, nature, and transcendence.",
                            zhConductor = "古斯塔夫·马勒（1860–1911）不仅是后浪漫主义伟大的交响曲作曲家，也是维也纳国家歌剧院与纽约爱乐历史上极具革新精神的指挥大师。",
                            enConductor = "Gustav Mahler (1860–1911) was one of the towering conductors of his era at the Vienna Court Opera and New York Philharmonic alongside his monumental symphonic compositions.",
                        ),
                        "Gustav Mahler", "Mahler", "马勒",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "克洛德·德彪西（Claude Debussy，1862–1918），法国印象派音乐奠基人，以全音阶、平行和弦与精微管弦光影打破传统调性束缚，代表作有《牧神午后》《大海》《意象集》与《月光》。",
                            en = "Claude Debussy (1862–1918) was a pioneering French composer of Impressionist music whose harmonic color, modal scales, and atmospheric textures shaped 20th-century composition in La Mer and Prélude à l'après-midi d'un faune.",
                        ),
                        "Claude Debussy", "Debussy", "德彪西",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "莫里斯·拉威尔（Maurice Ravel，1875–1937），法国作曲家与配器大师，以精巧如钟表般的结构、爵士和声色彩与绚丽管弦配器著称，代表作有《达夫尼斯与克洛埃》《波莱罗》《鹅妈妈》及《G大调钢琴协奏曲》。",
                            en = "Maurice Ravel (1875–1937) was a French composer renowned for crystalline craftsmanship, luminous orchestration, and works such as Daphnis et Chloé, Boléro, Le Tombeau de Couperin, and the Piano Concerto in G.",
                        ),
                        "Maurice Ravel", "Ravel", "拉威尔",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "弗朗茨·舒伯特（Franz Schubert，1797–1828），奥地利作曲家，维也纳古典与早期浪漫主义的桥梁，创作逾六百首艺术歌曲及《冬之旅》《未完成交响曲》《死神与少女》四重奏与《C大调弦乐五重奏》。",
                            en = "Franz Schubert (1797–1828) was an Austrian composer bridging Classical form and Romantic intimacy, authoring over 600 Lieder—including Winterreise—alongside the 'Unfinished' and 'Great' Symphonies and late chamber masterpieces.",
                        ),
                        "Franz Schubert", "Schubert", "舒伯特",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "罗伯特·舒曼（Robert Schumann，1810–1856），德国浪漫主义作曲家与音乐评论家，以充满文学意象的钢琴套曲（《狂欢节》《童年情景》《克莱斯勒偶记》）、艺术歌曲集及四部交响曲闻名。",
                            en = "Robert Schumann (1810–1856) was a German Romantic composer and critic famed for poetic piano cycles (Carnaval, Kinderszenen, Kreisleriana), Lieder, and four symphonies.",
                        ),
                        "Robert Schumann", "Schumann", "舒曼",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "安东尼奥·维瓦尔第（Antonio Vivaldi，1678–1741），意大利巴洛克作曲家与小提琴大师，以《四季》小提琴协奏曲及数百部协奏曲奠定三乐章独奏协奏曲范式，节奏明快而富有画面感。",
                            en = "Antonio Vivaldi (1678–1741) was an Italian Baroque composer and violinist whose hundreds of concertos—most famously The Four Seasons—established the blueprint of the three-movement solo concerto.",
                        ),
                        "Antonio Vivaldi", "Vivaldi", "维瓦尔第",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "格奥尔格·弗里德里希·亨德尔（George Frideric Handel，1685–1759），德裔英籍巴洛克作曲家，以清唱剧《弥赛亚》、管弦组曲《水上音乐》《皇家焰火音乐》及意大利歌剧闻名，兼擅宏伟合唱与戏剧性抒情。",
                            en = "George Frideric Handel (1685–1759) was a German-British Baroque master renowned for English oratorios such as Messiah, orchestral suites including Water Music, and dozens of Italian operas.",
                        ),
                        "George Frideric Handel", "G.F. Handel", "Handel", "亨德尔",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "约瑟夫·海顿（Joseph Haydn，1732–1809），奥地利古典主义奠基者，被尊为“交响曲之父”与“弦乐四重奏之父”，创作百余部交响曲与六十八部弦乐四重奏，兼具机智幽默与精严结构。",
                            en = "Joseph Haydn (1732–1809) was an Austrian Classical composer celebrated as the 'Father of the Symphony' and 'Father of the String Quartet' for over 100 symphonies, 68 quartets, and The Creation.",
                        ),
                        "Joseph Haydn", "Franz Joseph Haydn", "Haydn", "海顿",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "安东宁·德沃夏克（Antonín Dvořák，1841–1904），捷克浪漫主义作曲家，善于将波西米亚民族旋律融入古典交响结构，《第九交响曲“自新大陆”》《b小调大提琴协奏曲》与《斯拉夫舞曲》传世不衰。",
                            en = "Antonín Dvořák (1841–1904) was a Czech Romantic composer who infused classical symphonic and chamber forms with Bohemian folk lyricism, notably in Symphony No. 9 'From the New World' and the Cello Concerto.",
                        ),
                        "Antonin Dvorak", "Antonín Dvořák", "Dvorak", "德沃夏克",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "德米特里·肖斯塔科维奇（Dmitri Shostakovich，1906–1975），二十世纪苏联作曲家，十五部交响曲与十五部弦乐四重奏深刻折射时代张力，在冷峻反讽、悲怆张力与严谨赋格之间建立独特现代语言。",
                            en = "Dmitri Shostakovich (1906–1975) was a major 20th-century Soviet composer whose 15 symphonies and 15 string quartets balance biting irony, tragic intensity, and contrapuntal mastery.",
                        ),
                        "Dmitri Shostakovich", "Shostakovich", "肖斯塔科维奇",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "谢尔盖·拉赫玛尼诺夫（Sergei Rachmaninoff，1873–1943），俄裔作曲家、钢琴大师与指挥家，晚期浪漫主义代表，以深沉悠长的旋律、浓密和声与辉煌钢琴织体闻名，《第二、第三钢琴协奏曲》与《帕格尼尼主题狂想曲》久演不衰。",
                            en = "Sergei Rachmaninoff (1873–1943) was a Russian composer, pianist, and conductor celebrated for lush late-Romantic lyricism and pianistic grandeur in his Piano Concertos Nos. 2 & 3, Symphony No. 2, and Rhapsody on a Theme of Paganini.",
                        ),
                        "Sergei Rachmaninoff", "Sergei Rachmaninov", "Rachmaninoff", "Rachmaninov", "拉赫玛尼诺夫",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "费利克斯·门德尔松（Felix Mendelssohn，1809–1847），德国早期浪漫主义作曲家与指挥家，兼具古典平衡感与诗意画意，代表作包括《e小调小提琴协奏曲》《仲夏夜之梦》《苏格兰交响曲》及推动巴赫《马太受难曲》复兴。",
                            en = "Felix Mendelssohn (1809–1847) was a German Romantic composer and conductor whose Violin Concerto in E minor, A Midsummer Night's Dream, and Scottish Symphony unite Classical clarity with Romantic imagination.",
                        ),
                        "Felix Mendelssohn", "Mendelssohn", "门德尔松",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "理查·施特劳斯（Richard Strauss，1864–1949），德国后浪漫主义作曲家与指挥家，以宏伟交响诗（《查拉图斯特拉如是说》《唐璜》《阿尔卑斯交响曲》）、歌剧（《玫瑰骑士》）及晚年杰作《最后四首歌》闻名。",
                            en = "Richard Strauss (1864–1949) was a German late-Romantic composer and conductor renowned for tone poems (Also sprach Zarathustra, Don Juan, An Alpine Symphony), operas like Der Rosenkavalier, and Four Last Songs.",
                        ),
                        "Richard Strauss", "R. Strauss", "理查·施特劳斯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "理查德·瓦格纳（Richard Wagner，1813–1883），德国歌剧作曲家与指挥家，提出“整体艺术”（Gesamtkunstwerk）与主导动机体系，以《尼伯龙根的指环》《特里斯坦与伊索尔德》深远改变十九世纪和声与歌剧形态。",
                            en = "Richard Wagner (1813–1883) was a German composer and conductor whose music dramas—including The Ring Cycle and Tristan und Isolde—revolutionized orchestration, chromatic harmony, and theatrical staging.",
                        ),
                        "Richard Wagner", "Wagner", "瓦格纳",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "让·西贝柳斯（Jean Sibelius，1865–1957），芬兰民族乐派大师，七部交响曲、《芬兰颂》与《d小调小提琴协奏曲》以北欧寒冽光影与有机主题演进构筑独特声景。",
                            en = "Jean Sibelius (1865–1957) was a Finnish composer whose seven symphonies, Violin Concerto, and Finlandia evoke Nordic landscapes through organic thematic transformation.",
                        ),
                        "Jean Sibelius", "Sibelius", "西贝柳斯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "伊戈尔·斯特拉文斯基（Igor Stravinsky，1882–1971），俄裔现代主义作曲巨匠，创作横跨俄罗斯时期（《火鸟》《彼得鲁什卡》《春之祭》）、新古典主义与十二音序列，彻底革新二十世纪节奏与配器语汇。",
                            en = "Igor Stravinsky (1882–1971) was one of the most influential composers of the 20th century, moving from The Firebird and The Rite of Spring through Neoclassicism and serialism.",
                        ),
                        "Igor Stravinsky", "Stravinsky", "斯特拉文斯基",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "谢尔盖·普罗科菲耶夫（Sergei Prokofiev，1891–1953），俄罗斯作曲家与钢琴家，以凌厉现代节律、讽刺机锋与深挚抒情并存的风格著称，代表作有《罗密欧与朱丽叶》《彼得与狼》《古典交响曲》及钢琴奏鸣曲。",
                            en = "Sergei Prokofiev (1891–1953) was a Russian composer and pianist celebrated for energetic rhythms, sardonic wit, and lyrical depth in Romeo and Juliet, Alexander Nevsky, and his piano concertos and sonatas.",
                        ),
                        "Sergei Prokofiev", "Prokofiev", "普罗科菲耶夫",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "爱德华·埃尔加（Edward Elgar，1857–1934），英国晚期浪漫主义作曲家，代表作《谜语变奏曲》《e小调大提琴协奏曲》与《威风凛凛进行曲》兼有贵族式庄严与深切怀旧抒情。",
                            en = "Sir Edward Elgar (1857–1934) was an English late-Romantic composer acclaimed for the Enigma Variations, Cello Concerto in E minor, and Pomp and Circumstance Marches.",
                        ),
                        "Edward Elgar", "Elgar", "埃尔加",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "贝拉·巴托克（Béla Bartók，1881–1945），匈牙利现代主义作曲家与民族音乐学家，将东欧民间节奏与对称音高结构融为一体，《乐队协奏曲》与六部弦乐四重奏位列二十世纪最高经典。",
                            en = "Béla Bartók (1881–1945) was a Hungarian composer and ethnomusicologist whose Concerto for Orchestra, Music for Strings, Percussion and Celesta, and six string quartets synthesize folk rhythms with modernist rigor.",
                        ),
                        "Bela Bartok", "Béla Bartók", "Bartok", "巴托克",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "贾科莫·普契尼（Giacomo Puccini，1858–1924），意大利写实主义歌剧大师，《波希米亚人》《托斯卡》《蝴蝶夫人》《图兰朵》以炽烈旋律与精准戏剧节奏长居世界歌剧舞台核心。",
                            en = "Giacomo Puccini (1858–1924) was an Italian opera composer whose La bohème, Tosca, Madama Butterfly, and Turandot remain pillars of the operatic repertoire.",
                        ),
                        "Giacomo Puccini", "Puccini", "普契尼",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "朱塞佩·威尔第（Giuseppe Verdi，1813–1901），意大利歌剧巨匠，以《茶花女》《弄臣》《阿依达》《奥赛罗》及《安魂曲》展现深刻人性刻画与恢弘戏剧气魄。",
                            en = "Giuseppe Verdi (1813–1901) was the towering figure of 19th-century Italian opera, author of Rigoletto, La traviata, Aida, Otello, and his Requiem.",
                        ),
                        "Giuseppe Verdi", "Verdi", "威尔第",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "久石让（Joe Hisaishi，1950–），日本作曲家、指挥家与钢琴家，融合极简主义、管弦配器与日本抒情旋律，为宫崎骏、北野武电影创作大量经典原声音乐。",
                            en = "Joe Hisaishi (born 1950) is a Japanese composer, conductor, and pianist blending orchestral lyricism with minimalism, renowned for his film scores for Studio Ghibli and Takeshi Kitano.",
                        ),
                        "Joe Hisaishi", "久石让",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "坂本龙一（Ryuichi Sakamoto，1952–2023），日本作曲家、钢琴家与声音艺术家，横跨YMO电子实验、新古典钢琴室内乐及电影配乐（《末代皇帝》《圣诞快乐，劳伦斯先生》），晚年聚焦极简静音质感。",
                            en = "Ryuichi Sakamoto (1952–2023) was a Japanese composer, pianist, and electronic pioneer whose career spanned Yellow Magic Orchestra, acoustic chamber works, and Academy Award-winning film scores.",
                        ),
                        "Ryuichi Sakamoto", "坂本龙一",
                    ),

                    // --- Conductors ---
                    mapAliases(
                        PersonBioEntry(
                            zh = "卡洛斯·克莱伯（Carlos Kleiber，1930–2004），德裔奥地利传奇指挥大师，以极少而近乎全数封神的录音闻名，其执棒维也纳爱乐乐团的贝多芬第五、第七交响曲与勃拉姆斯第四交响曲以电光石火般的律动与歌唱性成为历史标杆。",
                            en = "Carlos Kleiber (1930–2004) was an Austrian-German conductor revered for electric intensity, elastic phrasing, and legendary scarcity of performances—most famously his Vienna Philharmonic Beethoven Symphonies Nos. 5 & 7 and Brahms Symphony No. 4.",
                        ),
                        "Carlos Kleiber", "卡洛斯·克莱伯", "克莱伯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "赫伯特·冯·卡拉扬（Herbert von Karajan，1908–1989），奥地利指挥大师，长期执掌柏林爱乐乐团三十余年，并深度领导萨尔茨堡音乐节与维也纳国家歌剧院，以丝绒般宏丽均匀的管弦音响与海量标杆级交响/歌剧唱片著称。",
                            en = "Herbert von Karajan (1908–1989) was an Austrian conductor who led the Berlin Philharmonic for over three decades, shaping an unmistakable legato orchestral sheen across an unmatched recorded legacy.",
                        ),
                        "Herbert von Karajan", "Karajan", "赫伯特·冯·卡拉扬", "卡拉扬",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "伦纳德·伯恩斯坦（Leonard Bernstein，1918–1990），美国指挥家、作曲家与音乐教育家，曾任纽约爱乐音乐总监并晚年携手维也纳爱乐留下经典马勒与贝多芬全集，以激情澎湃的舞台诠释与《西区故事》闻名。",
                            en = "Leonard Bernstein (1918–1990) was an American conductor, composer, and educator who championed Mahler, Beethoven, and American music with the New York Philharmonic and Vienna Philharmonic while composing West Side Story.",
                        ),
                        "Leonard Bernstein", "Bernstein", "伦纳德·伯恩斯坦", "伯恩斯坦",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "克劳迪奥·阿巴多（Claudio Abbado，1933–2014），意大利指挥大师，历任斯卡拉歌剧院、伦敦交响乐团、维也纳国家歌剧院、柏林爱乐乐团首席指挥及琉森节日管弦乐团创始指挥，以清透呼吸感、细腻声部对话与对当代音乐的扶持备受推崇。",
                            en = "Claudio Abbado (1933–2014) was an Italian conductor who led La Scala, the London Symphony Orchestra, Vienna State Opera, Berlin Philharmonic, and Lucerne Festival Orchestra with luminous clarity and chamber-like listening.",
                        ),
                        "Claudio Abbado", "Abbado", "克劳迪奥·阿巴多", "阿巴多",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "威廉·富特文格勒（Wilhelm Furtwängler，1886–1954），二十世纪德奥指挥学派的精神象征，长期领导柏林爱乐与维也纳爱乐，其指挥以建筑般的整体张力、主观即兴气息与深邃哲学感闻名于贝多芬、勃拉姆斯、布鲁克纳与瓦格纳诠释。",
                            en = "Wilhelm Furtwängler (1886–1954) was a towering German conductor associated with the Berlin and Vienna Philharmonics, famed for incandescent, spontaneous architectural sweep in Beethoven, Brahms, Bruckner, and Wagner.",
                        ),
                        "Wilhelm Furtwangler", "Wilhelm Furtwängler", "Furtwangler", "Furtwängler", "富特文格勒",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "卡尔·伯姆（Karl Böhm，1894–1981），奥地利指挥大师，与维也纳爱乐乐团及柏林爱乐乐团保持终身深厚合作，以严谨自然、毫不矫饰的莫扎特、贝多芬、瓦格纳与理查·施特劳斯诠释传世。",
                            en = "Karl Böhm (1894–1981) was an Austrian conductor prized for structural integrity and unforced eloquence in Mozart, Beethoven, Wagner, and the operas of Richard Strauss.",
                        ),
                        "Karl Bohm", "Karl Böhm", "Bohm", "Böhm", "卡尔·伯姆", "伯姆",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "约翰·艾略特·加德纳爵士（Sir John Eliot Gardiner，1943–），英国历史本真演奏学派核心指挥家，创办蒙特威尔第合唱团、英国巴洛克独奏家乐团与革命与浪漫管弦乐团，在巴赫康塔塔、贝多芬与柏辽兹本真还原上影响深远。",
                            en = "Sir John Eliot Gardiner (born 1943) is a British conductor and pioneer of historically informed performance, founding the Monteverdi Choir, English Baroque Soloists, and Orchestre Révolutionnaire et Romantique.",
                        ),
                        "John Eliot Gardiner", "Sir John Eliot Gardiner", "约翰·艾略特·加德纳", "加德纳",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "乔治·索尔蒂爵士（Sir Georg Solti，1912–1997），匈牙利裔英国指挥大师，长期担任芝加哥交响乐团音乐总监并在迪卡留下史上首部录音室《尼伯龙根的指环》全集，以凌厉精准的铜管爆发力与戏剧张力赢得三十余座格莱美奖。",
                            en = "Sir Georg Solti (1912–1997) was a Hungarian-British conductor renowned for his long tenure with the Chicago Symphony Orchestra and the landmark Decca studio recording of Wagner's Ring cycle.",
                        ),
                        "Georg Solti", "Sir Georg Solti", "Solti", "索尔蒂",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "奥托·克伦佩勒（Otto Klemperer，1885–1973），德国指挥巨擘，晚年于伦敦爱乐乐团（Philharmonia）录制了极具纪念碑气度的贝多芬、勃拉姆斯与马勒全集，以沉稳步速、木管清晰度与庄严结构见长。",
                            en = "Otto Klemperer (1885–1973) was a German conductor celebrated for monumental architectural authority, steady tempi, and crystalline wind balance in his Philharmonia Orchestra recordings.",
                        ),
                        "Otto Klemperer", "Klemperer", "克伦佩勒",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "布鲁诺·瓦尔特（Bruno Walter，1876–1962），德裔美籍指挥大师，马勒生前最亲密的弟子与助手，在莫扎特、贝多芬与马勒作品中以温厚人道关怀与歌唱性线条著称。",
                            en = "Bruno Walter (1876–1962) was a German-American conductor and close disciple of Gustav Mahler, cherished for warmth, humanity, and singing phrasing in Mozart, Beethoven, and Mahler.",
                        ),
                        "Bruno Walter", "布鲁诺·瓦尔特", "瓦尔特",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "小泽征尔（Seiji Ozawa，1935–2024），日本国际指挥大师，执掌波士顿交响乐团近三十年并任维也纳国家歌剧院音乐总监，兼具德奥浪漫传统与二十世纪现代配器驾驭力。",
                            en = "Seiji Ozawa (1935–2024) was a Japanese conductor who served as Music Director of the Boston Symphony Orchestra for 29 years and later led the Vienna State Opera.",
                        ),
                        "Seiji Ozawa", "小泽征尔",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "切利比达奇（Sergiu Celibidache，1912–1996），罗马尼亚指挥哲人，长期拒绝商业录音并执掌慕尼黑爱乐乐团，以极慢而通透的空间声学平衡与即兴呼吸感塑造独特现场仪式。",
                            en = "Sergiu Celibidache (1912–1996) was a Romanian conductor famous for his tenure with the Munich Philharmonic, phenomenological approach to acoustics, and broad, luminous phrasing.",
                        ),
                        "Sergiu Celibidache", "Celibidache", "切利比达奇",
                    ),

                    // --- Performers & Ensembles & Modern Artists ---
                    mapAliases(
                        PersonBioEntry(
                            zh = "格伦·古尔德（Glenn Gould，1932–1982），加拿大钢琴怪杰与音乐思想家，三十一岁退出舞台专注录音室创作，以极低琴凳、干爽清晰的复调声部控制与1955/1981两版《哥德堡变奏曲》重塑二十世纪巴赫演绎。",
                            en = "Glenn Gould (1932–1982) was a Canadian pianist famous for transparent contrapuntal clarity, his retreat from live concertizing to the recording studio, and his 1955 and 1981 recordings of Bach's Goldberg Variations.",
                        ),
                        "Glenn Gould", "格伦·古尔德", "古尔德",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "马友友（Yo-Yo Ma，1955–），华裔美籍大提琴演奏家，十九度格莱美奖得主，演奏跨足巴赫无伴奏大提琴组曲、浪漫派协奏曲及《丝绸之路》跨文化合作，以温润歌唱般的琴音与人文广度闻名。",
                            en = "Yo-Yo Ma (born 1955) is a French-born American cellist acclaimed for his deeply expressive tone across Bach's Cello Suites, classical concertos, Appalachian Waltz, and the cross-cultural Silk Road Ensemble.",
                        ),
                        "Yo-Yo Ma", "Yo Yo Ma", "马友友",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "穆雷·佩拉西亚（Murray Perahia，1947–），美国钢琴大师与指挥家，以晶莹剔透的触键、诗意分句与深厚室内乐修养著称，在莫扎特钢琴协奏曲全集、肖邦与巴赫《哥德堡变奏曲》上屡获权威赞誉。",
                            en = "Murray Perahia (born 1947) is an American pianist and conductor celebrated for refined cantabile tone and structural poise in Mozart, Chopin, Beethoven, and Bach's Goldberg Variations.",
                        ),
                        "Murray Perahia", "佩拉西亚",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "玛尔塔·阿格里奇（Martha Argerich，1941–），阿根廷传奇钢琴家，1965年肖邦国际钢琴比赛冠军，以炽烈能量、惊人技巧控制力与高度自发的室内乐默契享誉半个多世纪。",
                            en = "Martha Argerich (born 1941) is an Argentine concert pianist revered for volcanic temperament, dazzling virtuosity, and incandescent chamber music collaborations.",
                        ),
                        "Martha Argerich", "Argerich", "阿格里奇",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "毛里齐奥·波里尼（Maurizio Pollini，1942–2024），意大利钢琴大师，1960年肖邦大赛冠军，以如大理石般精准澄澈的音色与严密结构著称，横跨肖邦、贝多芬与二十世纪先锋作品。",
                            en = "Maurizio Pollini (1942–2024) was an Italian pianist renowned for crystalline clarity, architectural precision, and mastery spanning Beethoven, Chopin, and 20th-century repertoire.",
                        ),
                        "Maurizio Pollini", "Pollini", "波里尼",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "维也纳爱乐乐团（Wiener Philharmoniker），创立于1842年，以黄金大厅驻场演出、传统木管与维也纳圆号温厚音色闻名，是德奥交响与新年音乐会传统的核心载体。",
                            en = "The Vienna Philharmonic (Wiener Philharmoniker), founded in 1842, is renowned worldwide for its warm string resonance, distinctive Viennese winds and horn, and unbroken symphonic tradition.",
                        ),
                        "Wiener Philharmoniker", "Vienna Philharmonic", "维也纳爱乐乐团",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "柏林爱乐乐团（Berliner Philharmoniker），创立于1882年，世界顶尖交响乐团之一，历任尼基什、富特文格勒、卡拉扬、阿巴多、拉特尔与佩特连科，以雄浑齐整而高度灵活的合奏能力闻名。",
                            en = "The Berlin Philharmonic (Berliner Philharmoniker), founded in 1882, stands among the world's foremost orchestras under conductors from Furtwängler and Karajan to Abbado, Rattle, and Kirill Petrenko.",
                        ),
                        "Berliner Philharmoniker", "Berlin Philharmonic", "柏林爱乐乐团",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "亚斯明·威廉姆斯（Yasmin Williams），美国指弹吉他演奏家，以将木吉他平放于腿上双点弦、结合非洲拇指琴（Kalimba）与滑棒打击声景的独创演奏闻名。",
                            en = "Yasmin Williams is an American fingerstyle guitarist known for unorthodox lap-tapping techniques, open tunings, and luminous acoustic compositions blending folk, classical, and ambient textures.",
                        ),
                        "Yasmin Williams",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "安迪·麦基（Andy McKee），美国现代指弹原声吉他代表人物，以双手点弦、拍泛音与打击乐式箱体共鸣创作《Drifting》《Rylynn》等广受传颂的原声器乐佳作。",
                            en = "Andy McKee is an American fingerstyle guitarist celebrated for percussive acoustic techniques, altered tunings, and lyrical solo compositions.",
                        ),
                        "Andy McKee",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "热田公纪（Kiminori Atsuta），日本吉他演奏家与作曲家，长期活跃于指弹吉他与治愈系原声小品创作，旋律温润静谧，适合专注阅读与夜间聆听。",
                            en = "Kiminori Atsuta is a Japanese acoustic fingerstyle guitarist and composer known for delicate, contemplative instrumental melodies.",
                        ),
                        "Kiminori Atsuta", "热田公纪",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "寇比·凯蕾（Colbie Caillat，1985–），美国创作型流行民谣歌手，凭借首张专辑《Coco》及单曲《Bubbly》走红，作品常以阳光感原声木吉他与松弛人声营造轻松氛围。",
                            en = "Colbie Caillat (born 1985) is an American singer-songwriter known for breezy acoustic pop-folk melodies such as 'Bubbly' and 'Lucky'.",
                        ),
                        "Colbie Caillat",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "李宗盛（Jonathan Lee，1958–），华语流行乐坛最具影响力的词曲作者、制作人与歌手，代表作《山丘》《凡人歌》《鬼迷心窍》以叙事性白描与木吉他编曲著称。",
                            en = "Jonathan Lee (Lee Tsung-sheng, born 1958) is a seminal Taiwanese singer-songwriter and producer celebrated for narrative lyricism and acoustic folk-rock storytelling in Mandopop.",
                        ),
                        "李宗盛", "Jonathan Lee",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "张雨生（1966–1997），台湾音乐人、词曲作者与制作人，兼具标志性高音张力与前卫编曲视野，晚年代表作《口是心非》及早期清亮民谣作品均具高度艺术探索性。",
                            en = "Chang Yu-sheng (1966–1997) was an influential Taiwanese singer-songwriter and producer celebrated for crystalline vocal range and ambitious rock-pop arrangements.",
                        ),
                        "张雨生", "Chang Yu-sheng",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "卢家宏，台湾指弹木吉他演奏家与资深编曲人，常与华语创作歌手合作原声伴奏，并以细腻干净的指弹编配及原声教学贡献知名。",
                            en = "Lu Chia-hung is a Taiwanese fingerstyle guitarist, arranger, and session musician renowned for intricate acoustic accompaniments and solo guitar transcriptions.",
                        ),
                        "卢家宏", "Lu Chia-hung",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "Above & Beyond，英国知名电子音乐三人组，Anjunabeats 厂牌创始人，兼擅宏大情绪向 Trance 与纯原声管弦改编系列《Acoustic》。",
                            en = "Above & Beyond is a British electronic music trio and founders of Anjunabeats, known both for euphoric trance anthems and lush orchestral Acoustic reinterpretations.",
                        ),
                        "Above & Beyond",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "迈尔斯·戴维斯（Miles Davis，1926–1991），美国爵士乐小号手、乐队领袖与作曲家，几乎亲历并开创了酷派爵士、硬波普、调式爵士（《Kind of Blue》）与融合爵士各关键流派。",
                            en = "Miles Davis (1926–1991) was an American trumpeter, bandleader, and composer at the forefront of cool jazz, hard bop, modal jazz (Kind of Blue), and jazz fusion.",
                        ),
                        "Miles Davis", "迈尔斯·戴维斯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "比尔·埃文斯（Bill Evans，1929–1980），美国爵士钢琴大师，以如印象派画作般的根音省略和声、细腻触键及与 Scott LaFaro 组成的三重奏互动彻底改写现代爵士钢琴语汇。",
                            en = "Bill Evans (1929–1980) was an American jazz pianist famed for impressionistic voicings, introspective lyricism, and conversational trio interplay.",
                        ),
                        "Bill Evans", "比尔·埃文斯",
                    ),
                    mapAliases(
                        PersonBioEntry(
                            zh = "凯斯·贾瑞特（Keith Jarrett，1945–），美国爵士钢琴家与古典演奏家，以即兴独奏音乐会（《科隆音乐会》）及 Standards 三重奏享誉世界，横跨爵士与巴赫、肖斯塔科维奇古典录音。",
                            en = "Keith Jarrett (born 1945) is an American pianist famed for solo improvisational recitals such as The Köln Concert, his Standards Trio, and acclaimed classical recordings.",
                        ),
                        "Keith Jarrett", "凯斯·贾瑞特",
                    ),
                )
            }

        private val ALBUM_BIOS: Map<String, AlbumBioEntry> =
            buildMap {
                fun putAll(vararg pairs: List<Pair<String, AlbumBioEntry>>) {
                    for (group in pairs) {
                        for ((k, v) in group) put(k, v)
                    }
                }

                putAll(
                    mapAlbumAliases(
                        AlbumBioEntry(
                            zh = "《哥德堡变奏曲》（Goldberg Variations, BWV 988）为巴赫晚年键盘巨作，由一首咏叹调主题、三十段变奏与主题再现构成严谨对称的卡农与赋格建筑，素有键盘音乐“珠穆朗玛峰”之誉。",
                            en = "J.S. Bach's Goldberg Variations (BWV 988) comprises an Aria, thirty inventive variations—every third a canon—and a closing Aria da capo, forming one of the summits of Baroque keyboard architecture.",
                        ),
                        "Goldberg Variations", "哥德堡变奏曲", "BWV 988",
                    ),
                    mapAlbumAliases(
                        AlbumBioEntry(
                            zh = "贝多芬第五交响曲（“命运”）与第七交响曲常作为经典配对发行，前者以四音动机推动戏剧性凯旋，后者被瓦格纳赞为“舞蹈的礼赞”，卡洛斯·克莱伯与维也纳爱乐 1975–1976 年 DG 版更是公认历史名盘。",
                            en = "Beethoven's Symphonies Nos. 5 and 7 pair relentless motivic drama with Dionysian rhythmic momentum; Carlos Kleiber's 1975–76 Vienna Philharmonic recordings for Deutsche Grammophon stand as perennial reference benchmarks.",
                        ),
                        "Symphonien Nr. 5 & 7", "Symphonies Nos. 5 & 7", "Symphony No. 5 & 7",
                    ),
                    mapAlbumAliases(
                        AlbumBioEntry(
                            zh = "《Urban Driftwood》是美国指弹吉他演奏家 Yasmin Williams 2021 年发行的突破专辑，融双点弦吉他、非洲拇指琴、班卓与大提琴为澄净通透的现代原声声景。",
                            en = "Urban Driftwood (2021) is Yasmin Williams' acclaimed instrumental album weaving lap-tapped acoustic guitar, kalimba, banjo, and cello into luminous modern American acoustic landscapes.",
                        ),
                        "Urban Driftwood",
                    ),
                    mapAlbumAliases(
                        AlbumBioEntry(
                            zh = "《Kind of Blue》（1959）是迈尔斯·戴维斯领衔、比尔·埃文斯与约翰·柯川联袂录制的调式爵士里程碑，摒弃繁复和弦进行而转向调式音阶呼吸空间，位列爵士乐史最伟大的唱片之一。",
                            en = "Kind of Blue (1959) by Miles Davis—featuring Bill Evans, John Coltrane, and Cannonball Adderley—is the landmark modal jazz album celebrated for spacious, meditative lyricism.",
                        ),
                        "Kind of Blue",
                    ),
                )
            }
    }
}
