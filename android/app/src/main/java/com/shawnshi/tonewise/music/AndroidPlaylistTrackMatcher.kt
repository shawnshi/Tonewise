/*
 * Copyright (c) 2026 Tonewise Project
 * AndroidPlaylistTrackMatcher.kt is part of Tonewise.
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

package com.shawnshi.tonewise.music

import java.text.Normalizer
import kotlin.math.abs
import kotlin.math.min
import org.oxycblt.musikr.Song
import org.oxycblt.musikr.tag.Name

/**
 * Pure-Kotlin metadata-based playlist track matcher with CJK / Chinese playlist parity:
 * - NFKC + Latin diacritic folding + Traditional-to-Simplified Chinese folding
 * - Bilingual parenthetical/slash title & artist splitting (`周杰伦 (Jay Chou)`, `晴天 (Sunny Day)`)
 * - Curated C-pop Chinese <-> English artist alias expansion
 * - Chinese language/edition/OST tag stripping with strict derivative guards (`伴奏/翻唱/混音/纯音乐`)
 * - Swapped `Title - Artist` fallback and artist-anchored CJK fuzzy recall
 */
class AndroidPlaylistTrackMatcher<T>(
    items: List<T>,
    private val refExtractor: (T) -> TrackRef,
) {
    data class TrackRef(
        val uid: String,
        val title: String,
        val artists: List<String>,
        val album: String = "",
        val durationMs: Long = 0L,
        val relPath: String = "",
        val composers: List<String> = emptyList(),
        val conductors: List<String> = emptyList(),
        val workName: String = "",
        val movementName: String = "",
        val albumArtists: List<String> = emptyList(),
        val genres: List<String> = emptyList(),
    )

    private data class StructuralMarkers(
        val numbers: Set<String>,
        val romans: Set<String>,
        val cjkOrdinals: Set<String>,
    )

    private data class TitleVariantInfo(
        val normalized: String,
        val compact: String,
        val markers: StructuralMarkers,
        val tier: Int,
    )

    private data class IndexedEntry<T>(
        val item: T,
        val ref: TrackRef,
        val titleVariants: List<TitleVariantInfo>,
        val workAndTitleMarkers: StructuralMarkers,
        val primaryArtistTokens: Set<String>,
        val performerCreditTokens: Set<String>,
        val allCreditTokens: Set<String>,
        val artistFlat: String,
        val hardQualifiers: Int,
        val softQualifiers: Int,
        val hasAlbumRemix: Boolean,
        val isLive: Boolean,
        val albumNorm: String,
        val originalIndex: Int,
    )

    private val entries: List<IndexedEntry<T>>
    private val byTitle = HashMap<String, MutableList<IndexedEntry<T>>>()
    private val byCompactTitle = HashMap<String, MutableList<IndexedEntry<T>>>()
    private val byArtistToken = HashMap<String, MutableList<IndexedEntry<T>>>()
    private val byArtistWord = HashMap<String, MutableList<IndexedEntry<T>>>()
    private val longLatinArtistTokenEntries: List<Pair<String, List<IndexedEntry<T>>>>
    private val longLatinArtistWordEntries: List<Pair<String, List<IndexedEntry<T>>>>

    init {
        val list = ArrayList<IndexedEntry<T>>(items.size)
        for ((index, item) in items.withIndex()) {
            val ref = refExtractor(item)
            val preTitle = preNormalizeUnicode(ref.title)
            val qualifiers = extractVersionQualifiers(preTitle)
            val preAlbum = preNormalizeUnicode(ref.album)
            val albumQualifiers = if (ref.album.isNotBlank()) extractVersionQualifiers(preAlbum) else 0
            val hasAlbumRemix = (albumQualifiers and QUALIFIER_REMIX) != 0
            val hasAlbumLive =
                (albumQualifiers and QUALIFIER_LIVE) != 0 ||
                    (preAlbum.isNotEmpty() && ALBUM_LIVE_EXTRA_REGEX.containsMatchIn(preAlbum))

            val variantInfos = ArrayList<TitleVariantInfo>(6)
            val seenNorm = HashMap<String, Int>()

            fun addVariantInfo(
                norm: String,
                tier: Int,
            ) {
                if (norm.isEmpty()) return
                val existingIdx = seenNorm[norm]
                if (existingIdx != null) {
                    if (tier > variantInfos[existingIdx].tier) {
                        variantInfos[existingIdx] = variantInfos[existingIdx].copy(tier = tier)
                    }
                    return
                }
                seenNorm[norm] = variantInfos.size
                variantInfos.add(
                    TitleVariantInfo(
                        normalized = norm,
                        compact = compactText(norm),
                        markers = extractStructuralMarkers(norm),
                        tier = tier,
                    )
                )
            }

            val hasWorkName = ref.workName.isNotBlank()
            val baseVariants = normalizeTitleVariants(ref.title)
            if (baseVariants.isNotEmpty()) {
                addVariantInfo(baseVariants[0], 1000)
                for (i in 1 until baseVariants.size) {
                    if (!hasWorkName || !isGenericClassicalMovementLabel(baseVariants[i])) {
                        addVariantInfo(baseVariants[i], 970)
                    }
                }
            }

            if (hasWorkName) {
                val workVariants = normalizeTitleVariants(ref.workName)
                if (workVariants.isNotEmpty()) {
                    addVariantInfo(workVariants[0], 985)
                    for (i in 1 until workVariants.size) {
                        if (!isGenericClassicalMovementLabel(workVariants[i])) {
                            addVariantInfo(workVariants[i], 965)
                        }
                    }
                }
                if (ref.movementName.isNotBlank()) {
                    addVariantInfo(normalizeTokenText("${ref.workName} ${ref.movementName}"), 990)
                    val movNorm = normalizeTokenText(ref.movementName)
                    if (!isGenericClassicalMovementLabel(movNorm)) {
                        addVariantInfo(movNorm, 970)
                    }
                }
            } else if (ref.movementName.isNotBlank()) {
                val movNorm = normalizeTokenText(ref.movementName)
                if (!isGenericClassicalMovementLabel(movNorm)) {
                    addVariantInfo(movNorm, 970)
                }
            }

            val workTitleBlob =
                buildString {
                    if (ref.title.isNotBlank()) append(normalizeTokenText(ref.title)).append(' ')
                    if (ref.workName.isNotBlank()) append(normalizeTokenText(ref.workName)).append(' ')
                    if (ref.movementName.isNotBlank()) append(normalizeTokenText(ref.movementName))
                }.trim()
            val workAndTitleMarkers = extractStructuralMarkers(workTitleBlob)

            val primaryArtistTokens = LinkedHashSet<String>()
            for (a in ref.artists) {
                primaryArtistTokens.addAll(extractArtistTokens(a))
            }
            val performerCreditTokens = LinkedHashSet<String>(primaryArtistTokens)
            val usefulAlbumArtists = ArrayList<String>()
            for (aa in ref.albumArtists) {
                if (aa.isBlank()) continue
                var addedUseful = false
                for (aat in extractArtistTokens(aa)) {
                    if (aat.isNotEmpty() && aat !in PLACEHOLDER_ARTISTS) {
                        performerCreditTokens.add(aat)
                        addedUseful = true
                    }
                }
                if (addedUseful) usefulAlbumArtists.add(aa)
            }
            for (c in ref.conductors) {
                for (ct in extractArtistTokens(c)) {
                    if (ct.isNotEmpty() && ct !in PLACEHOLDER_ARTISTS) {
                        performerCreditTokens.add(ct)
                    }
                }
            }

            val composerTokens = LinkedHashSet<String>()
            for (c in ref.composers) {
                composerTokens.addAll(extractArtistTokens(c))
            }
            val allowComposerOnlyMatch =
                composerTokens.isNotEmpty() &&
                    shouldAllowComposerOnlyMatch(ref, performerCreditTokens, composerTokens)

            val allCreditTokens = LinkedHashSet<String>(performerCreditTokens)
            if (allowComposerOnlyMatch) {
                allCreditTokens.addAll(composerTokens)
            }
            val artistFlat =
                normalizeTokenText(
                    buildList {
                        addAll(ref.artists)
                        addAll(usefulAlbumArtists)
                        addAll(ref.conductors)
                        if (allowComposerOnlyMatch) addAll(ref.composers)
                    }.filter { it.isNotBlank() }.joinToString(" ")
                )

            val indexed =
                IndexedEntry(
                    item = item,
                    ref = ref,
                    titleVariants = variantInfos,
                    workAndTitleMarkers = workAndTitleMarkers,
                    primaryArtistTokens = primaryArtistTokens,
                    performerCreditTokens = performerCreditTokens,
                    allCreditTokens = allCreditTokens,
                    artistFlat = artistFlat,
                    hardQualifiers = qualifiers and HARD_BLOCK_MASK,
                    softQualifiers = qualifiers and SOFT_FALLBACK_MASK,
                    hasAlbumRemix = hasAlbumRemix,
                    isLive = ((qualifiers and QUALIFIER_LIVE) != 0) || hasAlbumLive,
                    albumNorm = normalizeAlbum(ref.album),
                    originalIndex = index,
                )
            list.add(indexed)

            val seenCompact = HashSet<String>(variantInfos.size)
            for (vi in variantInfos) {
                byTitle.getOrPut(vi.normalized) { ArrayList(2) }.add(indexed)
                if (vi.compact.isNotEmpty() && seenCompact.add(vi.compact)) {
                    byCompactTitle.getOrPut(vi.compact) { ArrayList(2) }.add(indexed)
                }
            }
            for (at in allCreditTokens) {
                if (at !in PLACEHOLDER_ARTISTS) {
                    byArtistToken.getOrPut(at) { ArrayList(4) }.add(indexed)
                }
            }
            for (word in extractSignificantCreditWords(allCreditTokens)) {
                byArtistWord.getOrPut(word) { ArrayList(4) }.add(indexed)
            }
        }
        entries = list
        longLatinArtistTokenEntries =
            byArtistToken.entries
                .filter { it.key.length >= 5 && !containsEastAsian(it.key) }
                .map { it.key to it.value }
        longLatinArtistWordEntries =
            byArtistWord.entries
                .filter { it.key.length >= 5 && !containsEastAsian(it.key) }
                .map { it.key to it.value }
    }

    fun match(
        title: String?,
        artist: String? = null,
        album: String? = null,
        durationMs: Long = 0L,
    ): T? {
        val rawTitle = title?.trim().orEmpty()
        if (rawTitle.isEmpty()) return null

        matchCore(rawTitle, artist, album, durationMs, exactOnly = false)?.let { return it }

        // Swapped "Title - Artist" fallback for Chinese text exports
        val rawArtist = artist?.trim().orEmpty()
        if (rawArtist.isNotEmpty()) {
            matchCore(rawArtist, rawTitle, album, durationMs, exactOnly = true)?.let { return it }
        }
        return null
    }

    /**
     * Infers `(artist, title)` from a track filename like `周杰伦 - 晴天.flac` or `01. 晴天 - 周杰伦.mp3`.
     */
    fun matchByFilenameHint(fileName: String): T? {
        val stem = fileName.substringBeforeLast('.').trim()
        if (stem.isEmpty()) return null
        val discCleaned = DISC_TRACK_PREFIX_REGEX.replace(stem, "").trim().ifEmpty { stem }
        val cleaned = CHART_PREFIX_REGEX.replace(discCleaned, "").trim()
        val (artist, title) = splitArtistTitle(cleaned)
        if (artist != null) {
            match(title = title, artist = artist)?.let { return it }
        }
        return match(title = cleaned, artist = null)
    }

    private data class Stage1Hit<T>(
        val cand: IndexedEntry<T>,
        var titleScore: Int,
        val isSoftFallback: Boolean,
    )

    private fun areSoftQualifiersCompatible(
        seedSoft: Int,
        cand: IndexedEntry<T>,
    ): Pair<Boolean, Boolean> {
        if (seedSoft == cand.softQualifiers) return true to false
        if (seedSoft == QUALIFIER_REMIX && cand.softQualifiers == 0 && cand.hasAlbumRemix) return true to false
        if (seedSoft == 0 && cand.softQualifiers != 0) return true to true
        return false to false
    }

    private fun matchCore(
        title: String,
        artist: String?,
        album: String?,
        durationMs: Long,
        exactOnly: Boolean,
    ): T? {
        val seedVariants = normalizeTitleVariants(title)
        if (seedVariants.isEmpty()) return null
        val seedTitleFull = seedVariants[0]
        val preTitle = preNormalizeUnicode(title)
        val seedQualifiers = extractVersionQualifiers(preTitle)
        val seedHard = seedQualifiers and HARD_BLOCK_MASK
        val seedSoft = seedQualifiers and SOFT_FALLBACK_MASK
        val seedIsLive = (seedQualifiers and QUALIFIER_LIVE) != 0
        val seedMarkers = extractStructuralMarkers(seedTitleFull)
        val seedArtistTokens = extractArtistTokens(artist)
        val artistMissing = seedArtistTokens.isEmpty() || seedArtistTokens.all { it in PLACEHOLDER_ARTISTS }
        val seedArtistFlat = if (artistMissing) "" else normalizeTokenText(artist)
        val seedAlbumNorm = normalizeAlbum(album)

        // Stage 1: Exact normalized or compact title hits
        val exactCandidates = LinkedHashMap<IndexedEntry<T>, Stage1Hit<T>>()
        fun upsertStage1(
            cand: IndexedEntry<T>,
            titleScore: Int,
            isSoftFallback: Boolean,
        ) {
            val prev = exactCandidates[cand]
            if (prev == null || titleScore > prev.titleScore) {
                exactCandidates[cand] = Stage1Hit(cand, titleScore, isSoftFallback)
            }
        }

        for ((vi, tv) in seedVariants.withIndex()) {
            if (vi > 0 && isGenericClassicalMovementLabel(tv)) continue
            byTitle[tv]?.forEach { cand ->
                if (cand.hardQualifiers != seedHard) return@forEach
                val (compat, isSoft) = areSoftQualifiersCompatible(seedSoft, cand)
                if (!compat) return@forEach
                val variantInfo = cand.titleVariants.firstOrNull { it.normalized == tv }
                val candTier = variantInfo?.tier ?: 970
                if ((vi > 0 || candTier < 990) && !areStructurallyCompatible(seedMarkers, cand.workAndTitleMarkers)) {
                    return@forEach
                }
                val baseScore =
                    when {
                        vi == 0 && candTier >= 1000 -> 1000
                        vi <= 1 && candTier >= 1000 -> 995
                        vi <= 1 && candTier >= 990 -> 990
                        else -> 960
                    } - (if (isSoft) 200 else 0)
                upsertStage1(cand, baseScore, isSoft)
            }
            val cv = compactText(tv)
            if (cv.isNotEmpty()) {
                byCompactTitle[cv]?.forEach { cand ->
                    if (cand.hardQualifiers != seedHard) return@forEach
                    val (compat, isSoft) = areSoftQualifiersCompatible(seedSoft, cand)
                    if (!compat) return@forEach
                    val variantInfo = cand.titleVariants.firstOrNull { it.compact == cv }
                    val candTier = variantInfo?.tier ?: 970
                    if ((vi > 0 || candTier < 990) && !areStructurallyCompatible(seedMarkers, cand.workAndTitleMarkers)) {
                        return@forEach
                    }
                    val baseScore = (if (vi == 0 && candTier >= 990) 980 else 955) - (if (isSoft) 200 else 0)
                    upsertStage1(cand, baseScore, isSoft)
                }
            }
        }

        if (exactCandidates.isNotEmpty()) {
            if (artistMissing && !exactOnly) {
                val strictList = exactCandidates.values.filter { !it.isSoftFallback }
                if (strictList.size == 1) return strictList[0].cand.item
                if (strictList.isNotEmpty()) {
                    var best: IndexedEntry<T>? = null
                    var bestRank = Long.MIN_VALUE
                    var tie = false
                    for (hit in strictList) {
                        val rank = scoreCandidate(hit.titleScore, 0, seedIsLive, seedAlbumNorm, durationMs, hit.cand)
                        if (rank > bestRank) {
                            bestRank = rank
                            best = hit.cand
                            tie = false
                        } else if (rank == bestRank) {
                            tie = true
                        }
                    }
                    if (!tie && best != null) return best.item
                }
                return null
            } else if (!artistMissing) {
                var best: IndexedEntry<T>? = null
                var bestRank = Long.MIN_VALUE
                for (hit in exactCandidates.values) {
                    if (exactOnly && hit.isSoftFallback) continue
                    val aScore = scoreArtistMatch(seedArtistTokens, seedArtistFlat, hit.cand)
                    if (exactOnly && aScore < 3) continue
                    if (aScore <= 0) continue
                    val rank = scoreCandidate(hit.titleScore, aScore, seedIsLive, seedAlbumNorm, durationMs, hit.cand)
                    if (rank > bestRank) {
                        bestRank = rank
                        best = hit.cand
                    }
                }
                if (best != null) return best.item
                if (!exactOnly && exactCandidates.size == 1) {
                    val solo = exactCandidates.values.first()
                    if (!solo.isSoftFallback && solo.cand.allCreditTokens.isEmpty()) {
                        return solo.cand.item
                    }
                }
            }
        }

        if (exactOnly || artistMissing) return null

        // Stage 2: Artist-anchored fuzzy/prefix CJK & Latin recall
        val artistPool = LinkedHashSet<IndexedEntry<T>>()
        for (st in seedArtistTokens) {
            if (st in PLACEHOLDER_ARTISTS) continue
            byArtistToken[st]?.let { artistPool.addAll(it) }
        }
        for (word in extractSignificantCreditWords(seedArtistTokens)) {
            byArtistWord[word]?.let { artistPool.addAll(it) }
        }

        fun evaluatePool(pool: Collection<IndexedEntry<T>>): IndexedEntry<T>? {
            var bestFuzzy: IndexedEntry<T>? = null
            var bestFuzzyRank = Long.MIN_VALUE
            for (cand in pool) {
                if (cand.hardQualifiers != seedHard) continue
                val (compat, isSoft) = areSoftQualifiersCompatible(seedSoft, cand)
                if (!compat) continue
                val aScore = scoreArtistMatch(seedArtistTokens, seedArtistFlat, cand)
                if (aScore <= 0) continue
                val sim = computeFuzzyTitleSimilarity(seedVariants, seedMarkers, cand)
                if (sim <= 0.0) continue
                var tScore = (sim * 900.0).toInt()
                if (isSoft) tScore = maxOf(1, tScore - 200)
                val rank = scoreCandidate(tScore, aScore, seedIsLive, seedAlbumNorm, durationMs, cand)
                if (rank > bestFuzzyRank) {
                    bestFuzzyRank = rank
                    bestFuzzy = cand
                }
            }
            return bestFuzzy
        }

        if (artistPool.isNotEmpty()) {
            evaluatePool(artistPool)?.let { return it.item }
        }

        // Bounded fuzzy-artist fallback for transliteration differences (e.g. Rachmaninoff vs Rachmaninov)
        val longLatinTokens = seedArtistTokens.filter { it.length >= 5 && !containsEastAsian(it) && it !in PLACEHOLDER_ARTISTS }
        val longLatinWords = extractSignificantCreditWords(seedArtistTokens).filter { it.length >= 5 && !containsEastAsian(it) }
        if (longLatinTokens.isEmpty() && longLatinWords.isEmpty()) return null

        val fuzzyArtistPool = LinkedHashSet<IndexedEntry<T>>()
        for (q in longLatinTokens) {
            for ((key, tracks) in longLatinArtistTokenEntries) {
                if (fuzzyArtistPool.size >= 2000) break
                if (isFuzzyArtistTokenMatch(q, key)) fuzzyArtistPool.addAll(tracks)
            }
        }
        for (q in longLatinWords) {
            for ((key, tracks) in longLatinArtistWordEntries) {
                if (fuzzyArtistPool.size >= 2000) break
                if (isFuzzyArtistTokenMatch(q, key)) fuzzyArtistPool.addAll(tracks)
            }
        }
        return if (fuzzyArtistPool.isNotEmpty()) evaluatePool(fuzzyArtistPool)?.item else null
    }

    private fun scoreCandidate(
        titleScore: Int,
        artistScore: Int,
        seedIsLive: Boolean,
        seedAlbumNorm: String,
        seedDurationMs: Long,
        cand: IndexedEntry<T>,
    ): Long {
        val liveMatch = if (seedIsLive == cand.isLive) 2 else 0
        val albumScore =
            when {
                seedAlbumNorm.isEmpty() || cand.albumNorm.isEmpty() -> 0
                seedAlbumNorm == cand.albumNorm -> 4
                isWholeWordContainment(
                    if (seedAlbumNorm.length <= cand.albumNorm.length) seedAlbumNorm else cand.albumNorm,
                    if (seedAlbumNorm.length <= cand.albumNorm.length) cand.albumNorm else seedAlbumNorm,
                ) -> 2
                else -> 0
            }
        val durationScore =
            if (seedDurationMs > 0L && cand.ref.durationMs > 0L) {
                val diff = abs(seedDurationMs - cand.ref.durationMs)
                when {
                    diff <= 3_000L -> 3
                    diff <= 10_000L -> 2
                    diff <= 25_000L -> 1
                    diff > 60_000L -> -1
                    else -> 0
                }
            } else {
                0
            }
        return titleScore.toLong() * 1_000_000L +
            artistScore.toLong() * 10_000L +
            liveMatch * 1_000L +
            albumScore * 100L +
            durationScore * 10L
    }

    private fun scoreArtistMatch(
        seedTokens: Set<String>,
        seedFlat: String,
        cand: IndexedEntry<T>,
    ): Int {
        val effective = seedTokens.filter { it.isNotEmpty() && it !in PLACEHOLDER_ARTISTS }
        if (effective.isEmpty() || cand.allCreditTokens.isEmpty()) return 0
        val nonPlaceholderCand = cand.allCreditTokens.filter { it.isNotEmpty() && it !in PLACEHOLDER_ARTISTS }
        if (nonPlaceholderCand.isEmpty()) return 0

        var score = 0
        var matchedPerformer = false
        val matchedSet = HashSet<String>(effective.size)
        for (st in effective) {
            if (st in cand.allCreditTokens) {
                score += 4
                matchedSet.add(st)
                if (st in cand.performerCreditTokens) matchedPerformer = true
                continue
            }
            var bestPartial = 0
            var partialMatchedPerformer = false
            for (ct in nonPlaceholderCand) {
                val candScore =
                    when {
                        isWholeWordContainment(st, ct) || isInitialSurnameMatch(st, ct) -> 3
                        bestPartial < 2 && isFuzzyArtistTokenMatch(st, ct) -> 2
                        else -> 0
                    }
                if (candScore > bestPartial) {
                    bestPartial = candScore
                    partialMatchedPerformer = ct in cand.performerCreditTokens
                } else if (candScore > 0 && candScore == bestPartial && ct in cand.performerCreditTokens) {
                    partialMatchedPerformer = true
                }
            }
            if (bestPartial > 0) {
                score += bestPartial
                matchedSet.add(st)
                if (partialMatchedPerformer) matchedPerformer = true
            }
        }
        if (score > 0 &&
            cand.primaryArtistTokens.isNotEmpty() &&
            matchedSet.size == effective.size &&
            effective.size == cand.primaryArtistTokens.size &&
            effective.all { it in cand.primaryArtistTokens }
        ) {
            score += 1
        }
        if (score > 0 && matchedPerformer) {
            score += 1
        }
        if (score == 0 && seedFlat.isNotEmpty() && cand.artistFlat.isNotEmpty()) {
            if (isWholeWordContainment(seedFlat, cand.artistFlat)) score = 1
        }
        return score
    }

    private fun isWholeWordContainment(a: String, b: String): Boolean {
        val shorter = if (a.length <= b.length) a else b
        val longer = if (a.length <= b.length) b else a
        val minOk = if (containsEastAsian(shorter)) shorter.length >= 2 else shorter.length >= 3
        if (!minOk) return false
        if (containsEastAsian(shorter)) return longer.contains(shorter)
        var idx = 0
        while (true) {
            val found = longer.indexOf(shorter, idx)
            if (found < 0) return false
            val leftOk = found == 0 || longer[found - 1] == ' '
            val end = found + shorter.length
            val rightOk = end == longer.length || longer[end] == ' '
            if (leftOk && rightOk) return true
            idx = found + 1
        }
    }

    private fun isInitialSurnameMatch(a: String, b: String): Boolean {
        if (containsEastAsian(a) || containsEastAsian(b)) return false
        if (' ' !in a || ' ' !in b) return false
        val wa = a.split(' ').filter { it.isNotEmpty() }
        val wb = b.split(' ').filter { it.isNotEmpty() }
        if (wa.size < 2 || wb.size < 2) return false
        val surnameA = wa.last()
        val surnameB = wb.last()
        if (surnameA.length < 3 || surnameA != surnameB) return false
        val shorterFirst = if (wa.size <= wb.size) wa.dropLast(1) else wb.dropLast(1)
        val longerFirst = if (wa.size <= wb.size) wb.dropLast(1) else wa.dropLast(1)
        if (shorterFirst.size != longerFirst.size) return false
        for (i in shorterFirst.indices) {
            val f1 = shorterFirst[i]
            val f2 = longerFirst[i]
            if (f1 == f2) continue
            if (f1.length == 1 && f2.startsWith(f1[0])) continue
            if (f2.length == 1 && f1.startsWith(f2[0])) continue
            return false
        }
        return true
    }

    private fun isFuzzyArtistTokenMatch(a: String, b: String): Boolean {
        if (a.length < 5 || b.length < 5 || containsEastAsian(a) || containsEastAsian(b)) return false
        if (normalizedLevenshteinSimilarity(a, b) >= 0.80) return true
        val aHasSpace = ' ' in a
        val bHasSpace = ' ' in b
        if (!aHasSpace && bHasSpace) {
            for (w in b.split(' ')) {
                if (w.length >= 5 && normalizedLevenshteinSimilarity(a, w) >= 0.80) return true
            }
        } else if (aHasSpace && !bHasSpace) {
            for (w in a.split(' ')) {
                if (w.length >= 5 && normalizedLevenshteinSimilarity(b, w) >= 0.80) return true
            }
        } else if (aHasSpace && bHasSpace) {
            val surA = a.substringAfterLast(' ')
            val surB = b.substringAfterLast(' ')
            if (surA.length >= 7 && surB.length >= 7 && normalizedLevenshteinSimilarity(surA, surB) >= 0.80) return true
        }
        return false
    }

    private fun normalizedLevenshteinSimilarity(a: String, b: String): Double {
        val maxLen = maxOf(a.length, b.length)
        if (maxLen == 0) return 0.0
        val maxDist = (maxLen * 0.25).toInt() + 1
        val dist = boundedLevenshtein(a, b, maxDist)
        return if (dist > maxDist) 0.0 else 1.0 - dist.toDouble() / maxLen
    }

    private fun computeFuzzyTitleSimilarity(
        seedVariants: List<String>,
        fullSeedMarkers: StructuralMarkers,
        cand: IndexedEntry<T>,
    ): Double {
        if (seedVariants.isEmpty()) return 0.0
        if (!areStructurallyCompatible(fullSeedMarkers, cand.workAndTitleMarkers)) return 0.0

        var best = 0.0
        for ((sIdx, sNorm) in seedVariants.withIndex()) {
            if (isGenericClassicalMovementLabel(sNorm)) continue
            val sCompact = compactText(sNorm)
            val minOk = if (containsEastAsian(sCompact)) sCompact.length >= 2 else sCompact.length >= 4
            if (!minOk) continue
            val weight = if (sIdx == 0) 1.0 else 0.98
            val sMarkers = if (sIdx == 0) fullSeedMarkers else extractStructuralMarkers(sNorm)
            var seedWords: List<String>? = null

            for (variant in cand.titleVariants) {
                if (isGenericClassicalMovementLabel(variant.normalized)) continue
                if (!areStructurallyCompatible(sMarkers, variant.markers)) continue
                val cNorm = variant.normalized
                val cCompact = variant.compact
                var sim = 0.0
                if (sCompact.isNotEmpty() && sCompact == cCompact) {
                    sim = 0.98
                } else {
                    sim = maxOf(sim, scoreLevenshteinTitle(sNorm, cNorm))
                    if (sCompact != sNorm || cCompact != cNorm) {
                        sim = maxOf(sim, scoreLevenshteinTitle(sCompact, cCompact))
                    }
                    if (sim < 0.95) {
                        val sW = seedWords ?: sNorm.split(' ').filter { it.isNotEmpty() }.also { seedWords = it }
                        val cW = cNorm.split(' ').filter { it.isNotEmpty() }
                        val shorterTokens = if (sW.size <= cW.size) sW else cW
                        val longerTokens = if (sW.size <= cW.size) cW else sW
                        val shorterStr = if (sW.size <= cW.size) sNorm else cNorm
                        val longerStr = if (sW.size <= cW.size) cNorm else sNorm
                        val shorterMarkers = if (sW.size <= cW.size) sMarkers else variant.markers

                        if (shorterTokens.size >= 2 && minOf(sNorm.length, cNorm.length) >= 6) {
                            val matched = countFuzzyMatchedTokens(shorterTokens, longerTokens)
                            val recall = matched.toDouble() / shorterTokens.size
                            val unionCount = shorterTokens.size + longerTokens.size - matched
                            val jaccard = if (unionCount > 0) matched.toDouble() / unionCount else 0.0
                            if ((recall >= 0.80 && jaccard >= 0.55) ||
                                (shorterMarkers.numbers.isNotEmpty() && shorterTokens.size >= 4 && recall >= 1.0 && jaccard >= 0.40)
                            ) {
                                sim = maxOf(sim, 0.78 + 0.16 * jaccard)
                            }
                        }

                        if (sim < 0.82) {
                            if (containsEastAsian(shorterStr) && shorterStr.length >= 2 && longerStr.startsWith("$shorterStr ")) {
                                sim = maxOf(sim, 0.82)
                            } else if (!containsEastAsian(shorterStr) &&
                                sIdx == 0 &&
                                variant.tier >= 985 &&
                                shorterStr.length >= 6 &&
                                !isGenericClassicalMovementLabel(shorterStr) &&
                                longerStr.startsWith("$shorterStr ")
                            ) {
                                sim = maxOf(sim, 0.82)
                            } else if (!containsEastAsian(shorterStr) &&
                                sIdx == 0 &&
                                variant.tier >= 985 &&
                                shorterStr.length >= 8 &&
                                shorterTokens.size >= 2 &&
                                !isGenericClassicalMovementLabel(shorterStr) &&
                                shorterStr.length.toDouble() / longerStr.length.toDouble() >= 0.45 &&
                                longerStr.endsWith(" $shorterStr")
                            ) {
                                sim = maxOf(sim, 0.82)
                            }
                        }

                        val shorterComp = if (sCompact.length <= cCompact.length) sCompact else cCompact
                        val longerComp = if (sCompact.length <= cCompact.length) cCompact else sCompact
                        if (containsEastAsian(shorterComp) &&
                            shorterComp.length >= 3 &&
                            longerComp.length > shorterComp.length &&
                            longerComp.startsWith(shorterComp) &&
                            shorterComp.length.toDouble() / longerComp.length.toDouble() >= 0.45
                        ) {
                            sim = maxOf(sim, 0.82)
                        }

                        if (sim < 0.95) {
                            sim = maxOf(sim, scoreHanziToPinyinMatch(sNorm, sCompact, cNorm, cCompact))
                        }
                    }
                }
                if (sim > 0.0) {
                    best = maxOf(best, sim * weight)
                }
            }
        }
        return best
    }

    private fun countFuzzyMatchedTokens(
        shorterTokens: List<String>,
        longerTokens: List<String>,
    ): Int {
        val used = BooleanArray(longerTokens.size)
        var matched = 0
        for (st in shorterTokens) {
            var hitIdx = -1
            for (j in longerTokens.indices) {
                if (!used[j] && st == longerTokens[j]) {
                    hitIdx = j
                    break
                }
            }
            if (hitIdx < 0 && st.length >= 6 && !containsEastAsian(st)) {
                for (j in longerTokens.indices) {
                    if (!used[j] && longerTokens[j].length >= 6 && !containsEastAsian(longerTokens[j]) &&
                        boundedLevenshtein(st, longerTokens[j], 1) <= 1
                    ) {
                        hitIdx = j
                        break
                    }
                }
            }
            if (hitIdx >= 0) {
                used[hitIdx] = true
                matched++
            }
        }
        return matched
    }

    private fun scoreHanziToPinyinMatch(
        seedNorm: String,
        seedCompact: String,
        candNorm: String,
        candCompact: String,
    ): Double {
        val seedHanzi = HanziPinyinConverter.containsHanzi(seedNorm) && !containsKanaOrHangul(seedNorm)
        val candHanzi = HanziPinyinConverter.containsHanzi(candNorm) && !containsKanaOrHangul(candNorm)
        if (seedHanzi == candHanzi) return 0.0

        val hanziNorm = if (seedHanzi) seedNorm else candNorm
        val hanziCompact = if (seedHanzi) seedCompact else candCompact
        val latinCompact = if (seedHanzi) candCompact else seedCompact

        if (hanziCompact.length < 2 || latinCompact.length < 4 || !HanziPinyinConverter.isPureAsciiAlphaNum(latinCompact)) {
            return 0.0
        }

        val variants = HanziPinyinConverter.toCompactPinyinVariants(hanziNorm)
        for (v in variants) {
            if (v == latinCompact) return 0.96
        }
        return 0.0
    }

    private fun containsKanaOrHangul(s: String): Boolean {
        for (c in s) {
            if (c in '\u3040'..'\u30FF' ||
                c in '\u31F0'..'\u31FF' ||
                c in '\uAC00'..'\uD7AF' ||
                c in '\u1100'..'\u11FF' ||
                c in '\u3130'..'\u318F'
            ) {
                return true
            }
        }
        return false
    }

    private fun scoreLevenshteinTitle(a: String, b: String): Double {
        val maxLen = maxOf(a.length, b.length)
        if (maxLen == 0) return 0.0
        val isCjk = containsEastAsian(a) || containsEastAsian(b)
        return if (isCjk) {
            val minLen = if (containsKanaOrHangul(a) || containsKanaOrHangul(b)) 8 else 4
            if (maxLen < minLen) return 0.0
            val dist = boundedLevenshtein(a, b, 1)
            if (dist != 1 || hasConflictingCjkNumeralDiff(a, b)) 0.0 else 1.0 - dist.toDouble() / maxLen
        } else {
            if (maxLen < 6) return 0.0
            val allowed = if (maxLen >= 10) (maxLen * 0.20).toInt() else 1
            val dist = boundedLevenshtein(a, b, allowed)
            if (dist > allowed) return 0.0
            val sim = 1.0 - dist.toDouble() / maxLen
            if ((maxLen >= 6 && sim >= 0.83) || (maxLen >= 10 && sim >= 0.80)) sim else 0.0
        }
    }

    private fun hasConflictingCjkNumeralDiff(a: String, b: String): Boolean {
        var prefix = 0
        val minLen = minOf(a.length, b.length)
        while (prefix < minLen && a[prefix] == b[prefix]) prefix++
        var suffix = 0
        while (suffix < minLen - prefix && a[a.length - 1 - suffix] == b[b.length - 1 - suffix]) suffix++
        for (i in prefix until a.length - suffix) {
            if (a[i] in CJK_NUMERAL_CHARS) return true
        }
        for (j in prefix until b.length - suffix) {
            if (b[j] in CJK_NUMERAL_CHARS) return true
        }
        return false
    }

    private fun boundedLevenshtein(a: String, b: String, maxDist: Int): Int {
        if (a == b) return 0
        if (abs(a.length - b.length) > maxDist) return maxDist + 1
        var prev = IntArray(b.length + 1) { it }
        var curr = IntArray(b.length + 1)
        for (i in 1..a.length) {
            curr[0] = i
            var rowMin = i
            val ca = a[i - 1]
            for (j in 1..b.length) {
                val cost = if (ca == b[j - 1]) 0 else 1
                val v = min(min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost)
                curr[j] = v
                if (v < rowMin) rowMin = v
            }
            if (rowMin > maxDist) return maxDist + 1
            val tmp = prev
            prev = curr
            curr = tmp
        }
        return prev[b.length]
    }

    companion object {
        private const val QUALIFIER_REMIX = 1 shl 0
        private const val QUALIFIER_INSTRUMENTAL = 1 shl 1
        private const val QUALIFIER_KARAOKE = 1 shl 2
        private const val QUALIFIER_ACOUSTIC = 1 shl 3
        private const val QUALIFIER_ACAPELLA = 1 shl 4
        private const val QUALIFIER_DEMO = 1 shl 5
        private const val QUALIFIER_COVER = 1 shl 6
        private const val QUALIFIER_SPEED = 1 shl 7
        private const val QUALIFIER_LIVE = 1 shl 8
        private const val HARD_BLOCK_MASK =
            QUALIFIER_INSTRUMENTAL or
                QUALIFIER_KARAOKE or
                QUALIFIER_ACAPELLA or
                QUALIFIER_DEMO or
                QUALIFIER_COVER or
                QUALIFIER_SPEED
        private const val SOFT_FALLBACK_MASK = QUALIFIER_REMIX or QUALIFIER_ACOUSTIC

        private val CJK_NUMERAL_CHARS =
            setOf('一', '二', '三', '四', '五', '六', '七', '八', '九', '十', '零', '百', '千')
        private val ROMAN_MOVEMENT_TOKENS =
            setOf("i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii")
        private val GENERIC_CLASSICAL_MOVEMENT_WORDS =
            setOf(
                "allegro", "adagio", "andante", "presto", "largo", "lento", "moderato", "vivace",
                "allegretto", "prestissimo", "larghetto", "adagietto", "grave", "scherzo", "menuetto",
                "menuet", "minuet", "minuetto", "finale", "prelude", "praeludium", "overture",
                "ouverture", "act", "scene", "part", "introduction", "introduzione", "coda", "rondo",
                "aria", "trio", "variation", "variations", "movement", "satz", "intermezzo",
                "interlude", "prologue", "epilogue", "no", "nr", "op", "non", "troppo", "molto",
                "ma", "con", "brio", "moto", "assai", "cantabile", "maestoso", "giocoso",
                "sostenuto", "grazioso", "espressivo", "lamentoso", "spirito", "fuoco", "piu",
                "poco", "quasi", "vivacissimo", "tranquillo", "pesante", "appassionato", "animato",
                "e", "et", "un", "a", "in", "the", "de", "la", "di", "da", "il", "and", "or",
                "mit", "sehr", "nicht", "zu", "doch",
            )

        private fun isGenericClassicalMovementLabel(normalized: String): Boolean {
            if (normalized.isBlank() || containsEastAsian(normalized)) return false
            var count = 0
            for (token in normalized.split(' ')) {
                if (token.isEmpty()) continue
                count++
                if (token.all { it in '0'..'9' } ||
                    token in ROMAN_MOVEMENT_TOKENS ||
                    token in GENERIC_CLASSICAL_MOVEMENT_WORDS
                ) {
                    continue
                }
                return false
            }
            return count > 0
        }
        private val PLACEHOLDER_ARTISTS =
            setOf("various artists", "various", "va", "群星", "合辑", "unknown artist", "unknown", "未知艺术家", "未知歌手")
        private val CREDIT_STOP_WORDS =
            setOf(
                "the", "and", "von", "van", "de", "la", "der", "den", "des", "di", "da", "del",
                "orchestra", "philharmonic", "philharmoniker", "symphony", "sinfonieorchester",
                "ensemble", "quartet", "quintet", "trio", "choir", "chorus", "band", "chamber",
                "royal", "london", "berlin", "vienna", "new", "york", "national", "state",
                "radio", "feat", "with", "featuring", "presents",
            )

        private val DISC_TRACK_PREFIX_REGEX = Regex("""^\s*\d{1,2}\s*-\s*\d{2,3}\s+(?=\S)""")
        private val SLASH_TITLE_SPLIT_REGEX = Regex("""\s*[/／]\s*""")
        private val CHART_PREFIX_REGEX = Regex("""^\s*\d+\s*(?:[\.\)）、]\s*|\s+(?=[^\d\s]))""")
        private val MULTI_SPACE_REGEX = Regex("""\s+""")
        private val CJK_ORDINAL_REGEX = Regex("""第[一二三四五六七八九十零百千]+""")
        private val BRACKET_REGEX = Regex("""[\(\[【]\s*([^\)\]】]+?)\s*[\)\]】]""")
        private val SCRIPT_RUN_REGEX =
            Regex("""[\u3040-\u30FF\u31F0-\u31FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF]+|[a-z0-9]+(?:\s+[a-z0-9]+)*""")
        private val ARTIST_SPLIT_REGEX =
            Regex("""(?:\s*[,&/;+×、，；／＋]\s*|\s+(?:feat\.?|ft\.?|featuring|with|x|and|vs\.?)\s+)""", RegexOption.IGNORE_CASE)
        private val FEAT_PAREN_REGEX =
            Regex("""[\(\[【]\s*(?:feat\.?|ft\.?|featuring|with|from\s+[^\)\]】]+)(?:\s+[^\)\]】]*)?[\)\]】]""", RegexOption.IGNORE_CASE)
        private val EDITION_PAREN_REGEX =
            Regex(
                """[\(\[【]\s*(?!.*?\b(?:remix(?:es|ed)?|rework|bootleg|instrumental|off\s+vocal|minus\s+one|karaoke|ktv|acoustic|unplugged|a\s*capp?ell?a|demo|cover|mashup|medley|sped\s+up|speed\s+up|slowed|nightcore)\b)(?!.*?(?:伴奏|纯音乐|消音|无人声|乐器版|卡拉ok|翻唱|不插电|木吉他版|清唱|阿卡贝拉|小样|混音|变速|加速版|降调))[^\)\]】]*(?:remaster(?:ed)?|version|radio\s+edit|album\s+edit|single\s+edit|radio\s+version|album\s+version|spanish\s+version|english\s+version|french\s+version|japanese\s+version|korean\s+version|clean(?:\s+ver(?:sion)?\.?)?|explicit(?:\s+ver(?:sion)?\.?)?|official\s+(?:music\s+|4k\s+|lyric\s+|audio\s+)?video|live|deluxe|mono|stereo|ost|重制|重置|完整版|正式版|专辑版|单曲版|原版|现场|演唱会|音乐会|母带|纯净版|无损|主题曲|插曲|片尾曲|片头曲|推广曲|影视原声|国语版?|粤语版?|普通话版?|中文版|英文版|tv版|独唱版|合唱版|经典版|珍藏版)\s*[^\)\]】]*[\)\]】]|[\(\[【]\s*(?:19|20)\d{2}\s*(?:版|年)?\s*[\)\]】]""",
                RegexOption.IGNORE_CASE,
            )
        private val DASH_EDITION_REGEX =
            Regex(
                """(?:\s+[-–—]\s+(?!.*?\b(?:remix(?:es|ed)?|rework|bootleg|instrumental|off\s+vocal|minus\s+one|karaoke|ktv|acoustic|unplugged|a\s*capp?ell?a|demo|cover|mashup|medley|sped\s+up|speed\s+up|slowed|nightcore)\b)(?!.*?(?:伴奏|纯音乐|消音|无人声|乐器版|卡拉ok|翻唱|不插电|木吉他版|清唱|阿卡贝拉|小样|混音|变速|加速版|降调))(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|radio\s+edit|album\s+edit|single\s+edit|radio\s+version|album\s+version|single\s+version|spanish\s+version|english\s+version|french\s+version|japanese\s+version|korean\s+version|clean(?:\s+ver(?:sion)?\.?)?|explicit(?:\s+ver(?:sion)?\.?)?|official\s+(?:music\s+|4k\s+|lyric\s+|audio\s+)?video|single|original\s+mix|live(?:\s+[^\n]*)?|mono|stereo|from\s+[^\n]*|[^\n]*\bsoundtrack\b[^\n]*|(?:\d{4}\s*)?(?:重制版?|重置版?)|完整版|正式版|专辑版|单曲版|原版|(?:\d{4}\s*)?(?:现场版?|live版)|母带版|纯净版|无损版?|国语版?|粤语版?|普通话版?|中文版|英文版|tv版|独唱版|合唱版|(?:电影|电视剧|网剧|动画|游戏)[^\n]*(?:主题曲|插曲|片尾曲|片头曲|推广曲)|影视原声))+$""",
                RegexOption.IGNORE_CASE,
            )
        private val DASH_REMIX_SUFFIX_REGEX =
            Regex("""(?:\s+[-–—]\s+[^\n]*\b(?:remix(?:es|ed)?|rework|club\s+mix|dub\s+mix|extended\s+mix|vip\s+mix)\b[^\n]*)+$""", RegexOption.IGNORE_CASE)
        private val ALBUM_EDITION_PAREN_REGEX =
            Regex("""[\(\[【]\s*[^\)\]】]*(?:deluxe|remaster(?:ed)?|expanded|special|anniversary|collector|bonus\s+track|standard|explicit|clean|edition|豪华版|精选辑|精选集|重制版|重置版|典藏版|纪念版|周年版)[^\)\]】]*[\)\]】]""")
        private val ALBUM_EDITION_DASH_REGEX =
            Regex("""(?:\s+[-–—]\s+(?:deluxe(?:\s+edition)?|expanded(?:\s+edition)?|special(?:\s+edition)?|remaster(?:ed)?(?:\s+\d{4})?|single|ep|豪华版|精选辑|重制版))+$""")

        private val ALBUM_LIVE_EXTRA_REGEX = Regex("""(?:\bin\s+concert\b|\bunplugged\b)""")
        private val SOUNDTRACK_GENRE_REGEX = Regex("""\b(?:soundtrack|score|ost|film)\b""")
        private val QUALIFIER_REMIX_REGEX =
            Regex("""\b(?:remix(?:es|ed)?|rework|bootleg|club\s+mix|dub\s+mix)\b""")
        private val QUALIFIER_INSTRUMENTAL_REGEX =
            Regex("""\b(?:instrumental|off\s+vocal|minus\s+one)\b""")
        private val QUALIFIER_KARAOKE_REGEX = Regex("""\b(?:karaoke|ktv)\b""")
        private val QUALIFIER_ACOUSTIC_REGEX = Regex("""\b(?:acoustic|unplugged)\b""")
        private val QUALIFIER_ACAPELLA_REGEX = Regex("""\ba\s*capp?ell?a\b""")
        private val QUALIFIER_DEMO_REGEX = Regex("""\bdemo\b""")
        private val QUALIFIER_COVER_REGEX = Regex("""\bcover\b""")
        private val QUALIFIER_SPEED_REGEX = Regex("""\b(?:sped\s+up|speed\s+up|slowed|nightcore)\b""")
        private val QUALIFIER_LIVE_REGEX = Regex("""(?<![a-z])live(?![a-z])""")
        private val CLASSICAL_CATALOGUE_REGEX = Regex("""\b(?:op|bwv|kv|hob|rv|woo|twv|hwv|sz|wwv)\.?\s*\d+""")
        private val ENSEMBLE_CREDIT_INDICATORS =
            listOf(
                "orchestra", "philharmonic", "philharmoniker", "symphony", "sinfonie",
                "ensemble", "quartet", "quintet", "trio", "choir", "chorus",
                "交响", "爱乐", "管弦", "乐团", "合唱",
            )
        private val CLASSICAL_REPERTOIRE_KEYWORDS =
            listOf(
                "symphon", "concerto", "sonata", "nocturne", "etude", "prelude", "fugue",
                "suite", "quartet", "quintet", "trio", "serenade", "waltz", "valse",
                "mazurka", "polonaise", "impromptu", "rhapsody", "overture", "ouverture",
                "requiem", "cantata", "oratorio", "opera", "aria", "adagio", "allegro",
                "andante", "largo", "presto", "scherzo", "minuet", "menuet", "rondo",
                "variation", "fantasia", "fantasy", "ballade", "berceuse", "barcarolle",
                "intermezzo", "partita", "toccata", "sonatina", "divertimento", "caprice",
                "capriccio", "humoresque", "elegy", "elegie", "romance", "pavane",
                "bolero", "gymnopedie", "gnossienne", "clair de lune", "arabesque",
                "canon", "passacaglia", "chaconne", "stabat mater", "ave maria",
                "交响", "协奏", "奏鸣", "夜曲", "练习曲", "前奏曲", "赋格", "组曲",
                "四重奏", "小夜曲", "圆舞曲", "狂想曲", "序曲", "安魂曲", "乐章", "变奏曲",
            )
        private val KNOWN_CLASSICAL_COMPOSERS: Set<String> by lazy {
            setOf(
                "johann sebastian bach", "j s bach", "bach",
                "ludwig van beethoven", "beethoven",
                "wolfgang amadeus mozart", "mozart",
                "frederic chopin", "chopin",
                "johannes brahms", "brahms",
                "joseph haydn", "haydn",
                "franz schubert", "schubert",
                "robert schumann", "schumann",
                "franz liszt", "liszt",
                "claude debussy", "debussy",
                "maurice ravel", "ravel",
                "pyotr ilyich tchaikovsky", "peter iljitsch tschaikowsky", "tchaikovsky", "чайковский", "петр ильич чайковский",
                "sergei rachmaninoff", "sergei rachmaninov", "rachmaninoff", "rachmaninov",
                "antonio vivaldi", "vivaldi",
                "gustav mahler", "mahler",
                "antonin dvorak", "dvorak",
                "dmitri shostakovich", "shostakovich",
                "sergei prokofiev", "sergei prokofieff", "prokofiev",
                "igor stravinsky", "stravinsky",
                "felix mendelssohn", "mendelssohn",
                "george frideric handel", "georg friedrich handel", "handel",
                "richard wagner", "wagner",
                "giuseppe verdi", "verdi",
                "giacomo puccini", "puccini",
                "georges bizet", "bizet",
                "edouard lalo", "lalo",
                "camille saint saens", "saint saens",
                "jean sibelius", "sibelius",
                "edward elgar", "elgar",
                "edvard grieg", "grieg",
                "bela bartok", "bartok",
                "erik satie", "satie",
                "alexander scriabin", "scriabin",
                "nikolai rimsky korsakov", "rimsky korsakov",
                "modest mussorgsky", "mussorgsky",
                "alexander borodin", "borodin",
                "domenico scarlatti", "scarlatti",
                "max bruch", "bruch",
                "pablo de sarasate", "sarasate",
                "niccolo paganini", "paganini",
                "anton webern", "webern",
                "alban berg", "berg",
                "arnold schoenberg", "schoenberg",
                "richard strauss", "johann strauss", "johann strauss ii",
                "sergei lyapunov", "lyapunov",
                "alexander glazunov", "glazunov",
                "nikolai medtner", "medtner",
                "aram khachaturian", "khachaturian",
                "alfred schnittke", "schnittke",
                "myroslav skoryk", "skoryk",
                "arvo part", "part",
                "philip glass", "max richter", "ludovico einaudi",
                "ennio morricone", "john williams", "hans zimmer", "howard shore", "james horner",
                "joe hisaishi", "久石让",
                "ryuichi sakamoto", "坂本龙一",
                "alexandre desplat", "tan dun", "谭盾",
                "shigeru umebayashi", "梅林茂",
                "toru takemitsu", "武满彻",
                "nobuo uematsu", "植松伸夫",
                "kaoru wada", "和田薰",
                "makoto yoshimori", "吉森信",
                "toshio masuda", "增田俊郎",
                "seiji yokoyama", "横山菁儿",
                "yuki kajiura", "梶浦由记",
                "yoko kanno", "菅野洋子",
                "kenji kawai", "川井宪次",
                "hiroyuki sawano", "泽野弘之",
                "shiro sagisu", "鹭巢诗郎",
                "naoki sato", "佐藤直纪",
                "shinkichi mitsumune", "光宗信吉",
                "kow otani", "大谷幸",
                "shunsuke kikuchi", "菊池俊辅",
                "michiru oshima", "大岛满",
                "taku iwasaki", "岩崎琢",
                "kohei tanaka", "田中公平",
                "taro iwashiro", "岩代太郎",
                "yasuharu takanashi", "高梨康治",
                "masaru yokoyama", "横山克",
                "cong su", "苏聪",
                "zhao jiping", "赵季平",
                "james wong", "黄霑",
                "joseph koo", "顾嘉辉",
                "michael lai", "黎小田",
                "comfort chan", "陈光荣",
                "peter kam", "金培达",
                "san bao", "三宝",
                "sergey khmelevsky", "sergei khmelevsky", "сергей хмелевский",
            )
        }

        private fun isSoundtrackGenre(genres: List<String>): Boolean {
            for (g in genres) {
                if (g.isBlank()) continue
                val preNorm = preNormalizeUnicode(g)
                if (SOUNDTRACK_GENRE_REGEX.containsMatchIn(preNorm) ||
                    preNorm.contains("stage & screen") ||
                    preNorm.contains("配乐") ||
                    preNorm.contains("原声")
                ) {
                    return true
                }
            }
            return false
        }

        private fun shouldAllowComposerOnlyMatch(
            ref: TrackRef,
            performerTokens: Set<String>,
            composerTokens: Set<String>,
        ): Boolean {
            if (isSoundtrackGenre(ref.genres)) return true
            if (ref.conductors.any { it.isNotBlank() }) return true
            for (pt in performerTokens) {
                for (ind in ENSEMBLE_CREDIT_INDICATORS) {
                    if (ind in pt) return true
                }
            }
            val combined = preNormalizeUnicode("${ref.title} ${ref.workName} ${ref.movementName}")
            if (combined.isNotEmpty()) {
                if (CLASSICAL_CATALOGUE_REGEX.containsMatchIn(combined)) return true
                for (kw in CLASSICAL_REPERTOIRE_KEYWORDS) {
                    if (kw in combined) return true
                }
            }
            return composerTokens.any { it in KNOWN_CLASSICAL_COMPOSERS }
        }

        private val PROTECTED_ARTISTS: List<String> by lazy {
            (loadProtectedArtists() + "brand x music")
                .distinct()
                .sortedByDescending { it.length }
        }

        private fun loadProtectedArtists(): List<String> {
            val stream = AndroidPlaylistTrackMatcher::class.java.getResourceAsStream("/tonewise/artist_whitelist.json")
                ?: return emptyList()
            val text = stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val startIdx = text.indexOf("\"protected_names\"")
            if (startIdx < 0) return emptyList()
            val arrayStart = text.indexOf('[', startIdx)
            val arrayEnd = text.indexOf(']', arrayStart)
            if (arrayStart < 0 || arrayEnd <= arrayStart) return emptyList()
            val body = text.substring(arrayStart + 1, arrayEnd)
            val items =
                Regex("\"((?:\\\\.|[^\"\\\\])*)\"")
                    .findAll(body)
                    .map { preNormalizeUnicode(it.groupValues[1]) }
                    .filter { it.isNotEmpty() }
                    .distinct()
                    .sortedByDescending { it.length }
                    .toList()
            return items
        }

        private val TRADITIONAL_TO_SIMPLIFIED: Map<Char, Char> =
            hashMapOf(
                '妳' to '你', '牠' to '它', '祂' to '他', '裡' to '里', '裏' to '里', '臺' to '台',
                '峯' to '峰', '煙' to '烟', '豔' to '艳', '啟' to '启', '迴' to '回', '遊' to '游',
                '麼' to '么', '麽' to '么', '爲' to '为', '為' to '为', '衛' to '卫', '歷' to '历',
                '曆' to '历', '餘' to '余', '後' to '后', '麵' to '面', '髮' to '发', '發' to '发',
                '隻' to '只', '準' to '准', '雲' to '云', '鐘' to '钟', '鍾' to '钟', '衝' to '冲',
                '劃' to '划', '畫' to '画', '藉' to '借', '乾' to '干', '鬆' to '松', '係' to '系',
                '範' to '范', '複' to '复', '覆' to '复', '穀' to '谷', '闆' to '板', '夥' to '伙',
                '愛' to '爱', '戀' to '恋', '夢' to '梦', '聽' to '听', '說' to '说', '風' to '风',
                '歲' to '岁', '時' to '时', '間' to '间', '無' to '无', '與' to '与', '這' to '这',
                '個' to '个', '們' to '们', '國' to '国', '語' to '语', '粵' to '粤', '聲' to '声',
                '樂' to '乐', '動' to '动', '電' to '电', '視' to '视', '劇' to '剧', '紅' to '红',
                '塵' to '尘', '溫' to '温', '獨' to '独', '淚' to '泪', '傷' to '伤', '飛' to '飞',
                '遠' to '远', '舊' to '旧', '憶' to '忆', '難' to '难', '終' to '终', '於' to '于',
                '點' to '点', '星' to '星', '島' to '岛', '嶼' to '屿', '邊' to '边', '藍' to '蓝',
                '開' to '开', '葉' to '叶', '黃' to '黄', '謝' to '谢', '鋒' to '锋', '張' to '张',
                '學' to '学', '榮' to '荣', '陳' to '陈', '傑' to '杰', '倫' to '伦', '鄧' to '邓',
                '孫' to '孙', '宏' to '宏', '靜' to '静', '劉' to '刘', '華' to '华', '麗' to '丽',
                '蘇' to '苏', '綠' to '绿', '萬' to '万', '東' to '东', '沒' to '没', '對' to '对',
                '計' to '计', '火' to '火', '車' to '车', '盧' to '卢', '廣' to '广', '韋' to '韦',
                '禮' to '礼', '瑩' to '莹', '靚' to '靓', '穎' to '颖', '鄭' to '郑', '許' to '许',
                '芸' to '芸', '楊' to '杨', '嬅' to '嬅', '兒' to '儿', '軒' to '轩', '趙' to '赵',
                '許' to '许', '樹' to '树', '竇' to '窦', '鈞' to '钧', '陰' to '阴', '陽' to '阳',
                '晴' to '晴', '誰' to '谁', '讓' to '让', '還' to '还', '來' to '来', '會' to '会',
                '過' to '过', '進' to '进', '退' to '退', '實' to '实', '真' to '真', '假' to '假',
                '寫' to '写', '詞' to '词', '詩' to '诗', '話' to '话', '語' to '语', '請' to '请',
                '問' to '问', '總' to '总', '結' to '结', '經' to '经', '過' to '过', '錯' to '错',
                '對' to '对', '當' to '当', '從' to '从', '開' to '开', '關' to '关', '門' to '门',
                '窗' to '窗', '燈' to '灯', '火' to '火', '燒' to '烧', '熱' to '热', '冷' to '冷',
                '雙' to '双', '對' to '对', '單' to '单', '純' to '纯', '淨' to '净', '版' to '版',
                '專' to '专', '輯' to '辑', '選' to '选', '精' to '精', '典' to '典', '藏' to '藏',
                '現' to '现', '場' to '场', '演' to '演', '唱' to '唱', '會' to '会', '音' to '音',
                '響' to '响', '節' to '节', '奏' to '奏', '旋' to '旋', '律' to '律', '調' to '调',
                '變' to '变', '速' to '速', '降' to '降', '清' to '清', '翻' to '翻', '混' to '混',
                '樣' to '样', '長' to '长', '短' to '短', '高' to '高', '低' to '低', '深' to '深',
                '淺' to '浅', '輕' to '轻', '重' to '重', '新' to '新', '舊' to '旧', '老' to '老',
                '少' to '少', '多' to '多', '滿' to '满', '空' to '空', '盡' to '尽', '頭' to '头',
                '尾' to '尾', '心' to '心', '靈' to '灵', '魂' to '魂', '體' to '体', '身' to '身',
                '手' to '手', '眼' to '眼', '淚' to '泪', '笑' to '笑', '哭' to '哭', '痛' to '痛',
                '快' to '快', '樂' to '乐', '悲' to '悲', '歡' to '欢', '離' to '离',
                '合' to '合', '聚' to '聚', '散' to '散', '別' to '别', '見' to '见', '忘' to '忘',
                '記' to '记', '念' to '念', '想' to '想', '思' to '思', '情' to '情', '緣' to '缘',
                '份' to '份', '夢' to '梦', '醒' to '醒', '醉' to '醉', '夜' to '夜', '晚' to '晚',
                '晨' to '晨', '光' to '光', '影' to '影', '色' to '色', '彩' to '彩', '黑' to '黑',
                '白' to '白', '灰' to '灰', '銀' to '银', '金' to '金', '鐵' to '铁', '銅' to '铜',
                '線' to '线', '網' to '网', '絡' to '络', '鏈' to '链', '鎖' to '锁', '鑰' to '钥',
                '匙' to '匙', '橋' to '桥', '路' to '路', '街' to '街', '道' to '道', '城' to '城',
                '鄉' to '乡', '村' to '村', '鎮' to '镇', '區' to '区', '國' to '国', '家' to '家',
                '親' to '亲', '友' to '友', '朋' to '朋', '伴' to '伴', '侶' to '侣', '歸' to '归',
                '來' to '来', '去' to '去', '留' to '留', '走' to '走', '跑' to '跑', '跳' to '跳',
                '飄' to '飘', '落' to '落', '升' to '升', '起' to '起', '滅' to '灭', '生' to '生',
                '死' to '死', '活' to '活', '著' to '着', '過' to '过', '為' to '为', '從' to '从',
                '向' to '向', '對' to '对', '給' to '给', '讓' to '让', '被' to '被', '把' to '把',
                '將' to '将', '應' to '应', '該' to '该', '能' to '能', '夠' to '够', '願' to '愿',
                '希' to '希', '望' to '望', '信' to '信', '仰' to '仰', '義' to '义', '禮' to '礼',
                '智' to '智', '慧' to '慧', '勇' to '勇', '氣' to '气', '力' to '力', '量' to '量',
                '龍' to '龙', '鳳' to '凤', '鶴' to '鹤', '鳥' to '鸟', '魚' to '鱼', '馬' to '马',
                '羊' to '羊', '牛' to '牛', '虎' to '虎', '獅' to '狮', '貓' to '猫', '狗' to '狗',
                '花' to '花', '草' to '草', '木' to '木', '林' to '林', '森' to '森', '山' to '山',
                '水' to '水', '河' to '河', '江' to '江', '湖' to '湖', '海' to '海', '洋' to '洋',
                '波' to '波', '浪' to '浪', '濤' to '涛', '潮' to '潮', '雪' to '雪', '霜' to '霜',
                '露' to '露', '霧' to '雾', '雷' to '雷', '電' to '电', '閃' to '闪', '耀' to '耀',
                '燦' to '灿', '爛' to '烂', '輝' to '辉', '煌' to '煌', '麗' to '丽', '美' to '美',
                '醜' to '丑', '好' to '好', '壞' to '坏', '強' to '强', '弱' to '弱', '勝' to '胜',
                '敗' to '败', '戰' to '战', '爭' to '争', '和' to '和', '平' to '平', '安' to '安',
                '靜' to '静', '寧' to '宁', '穩' to '稳', '亂' to '乱', '繁' to '繁', '華' to '华',
                '簡' to '简', '繁' to '繁', '體' to '体', '字' to '字', '號' to '号', '碼' to '码',
                '題' to '题', '目' to '目', '名' to '名', '稱' to '称', '傳' to '传', '奇' to '奇',
                '故' to '故', '事' to '事', '歷' to '历', '史' to '史', '記' to '记', '錄' to '录',
                '書' to '书', '信' to '信', '紙' to '纸', '筆' to '笔', '墨' to '墨', '紙' to '纸',
                '張' to '张', '頁' to '页', '篇' to '篇', '章' to '章', '節' to '节', '段' to '段',
                '句' to '句', '首' to '首', '曲' to '曲', '歌' to '歌', '謠' to '谣', '舞' to '舞',
                '臺' to '台', '劇' to '剧', '院' to '院', '場' to '场', '廳' to '厅', '館' to '馆',
                '寶' to '宝', '貝' to '贝', '珠' to '珠', '玉' to '玉', '石' to '石', '磚' to '砖',
                '瓦' to '瓦', '牆' to '墙', '樓' to '楼', '閣' to '阁', '亭' to '亭', '軒' to '轩',
                '園' to '园', '庭' to '庭', '院' to '院', '宮' to '宫', '殿' to '殿', '廟' to '庙',
                '寺' to '寺', '塔' to '塔', '鐘' to '钟', '鼓' to '鼓', '琴' to '琴', '瑟' to '瑟',
                '箏' to '筝', '笛' to '笛', '簫' to '箫', '弦' to '弦', '管' to '管', '號' to '号',
                '號' to '号', '響' to '响', '鳴' to '鸣', '啼' to '啼', '嘯' to '啸', '歌' to '歌',
                '聖' to '圣', '償' to '偿', '曉' to '晓', '團' to '团', '隊' to '队', '組' to '组',
                // Japanese Shinjitai -> Simplified Chinese equivalents
                '譲' to '让', '薫' to '薰', '増' to '增', '児' to '儿', '満' to '满', '姫' to '姬',
                '沢' to '泽', '広' to '广', '竜' to '龙', '桜' to '樱', '絵' to '绘', '黒' to '黑',
                '辺' to '边', '実' to '实', '伝' to '传', '闘' to '斗', '鉄' to '铁', '剣' to '剑',
                '気' to '气', '駅' to '站', '郷' to '乡', '斉' to '齐', '斎' to '斋', '滝' to '泷',
                '蛍' to '萤', '聴' to '听',
            )

        private fun loadCanonicalAliasGroups(): List<List<String>> {
            val stream = AndroidPlaylistTrackMatcher::class.java.getResourceAsStream("/tonewise/artist_aliases.json")
                ?: return emptyList()
            val text = stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val startIdx = text.indexOf("\"canonical_aliases\"")
            if (startIdx < 0) return emptyList()
            val objStart = text.indexOf('{', startIdx)
            val objEnd = text.indexOf('}', objStart)
            if (objStart < 0 || objEnd <= objStart) return emptyList()
            val body = text.substring(objStart + 1, objEnd)
            val grouped = LinkedHashMap<String, MutableList<String>>()
            val pairRegex = Regex("\"((?:\\\\.|[^\"\\\\])*)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
            for (m in pairRegex.findAll(body)) {
                val alias = m.groupValues[1].trim()
                val canonical = m.groupValues[2].trim()
                if (alias.isEmpty() || canonical.isEmpty()) continue
                val keyLower = canonical.lowercase(java.util.Locale.ROOT)
                val list = grouped.getOrPut(keyLower) { arrayListOf(canonical) }
                if (list.none { it.equals(alias, ignoreCase = true) }) {
                    list.add(alias)
                }
            }
            return grouped.values.filter { it.size >= 2 }
        }

        private val CJK_ARTIST_ALIASES: Map<String, List<String>> by lazy {
            val groups =
                listOf(
                    listOf("周杰伦", "jay chou"),
                    listOf("陈奕迅", "eason chan"),
                    listOf("王菲", "faye wong"),
                    listOf("林俊杰", "jj lin"),
                    listOf("邓紫棋", "gem", "g e m"),
                    listOf("张学友", "jacky cheung"),
                    listOf("张国荣", "leslie cheung"),
                    listOf("五月天", "mayday"),
                    listOf("蔡依林", "jolin tsai"),
                    listOf("孙燕姿", "stefanie sun"),
                    listOf("陶喆", "david tao"),
                    listOf("王力宏", "leehom wang"),
                    listOf("梁静茹", "fish leong"),
                    listOf("莫文蔚", "karen mok"),
                    listOf("刘德华", "andy lau"),
                    listOf("张惠妹", "a mei", "amei"),
                    listOf("邓丽君", "teresa teng"),
                    listOf("方大同", "khalil fong"),
                    listOf("林忆莲", "sandy lam"),
                    listOf("梅艳芳", "anita mui"),
                    listOf("谭咏麟", "alan tam"),
                    listOf("陈百强", "danny chan"),
                    listOf("李宗盛", "jonathan lee"),
                    listOf("罗大佑", "lo ta yu"),
                    listOf("周华健", "wakin chau"),
                    listOf("张信哲", "jeff chang"),
                    listOf("刘若英", "rene liu"),
                    listOf("伍佰", "wu bai"),
                    listOf("田馥甄", "hebe tien"),
                    listOf("林宥嘉", "yoga lin"),
                    listOf("萧敬腾", "jam hsiao"),
                    listOf("杨千嬅", "miriam yeung"),
                    listOf("容祖儿", "joey yung"),
                    listOf("古巨基", "leo ku"),
                    listOf("李克勤", "hacken lee"),
                    listOf("谢霆锋", "nicholas tse"),
                    listOf("张敬轩", "hins cheung"),
                    listOf("周深", "zhou shen"),
                    listOf("薛之谦", "joker xue"),
                    listOf("李荣浩", "ronghao li"),
                    listOf("毛不易", "mao buyi"),
                    listOf("苏打绿", "sodagreen", "鱼丁糸", "oaeen"),
                    listOf("告五人", "accusefive"),
                    listOf("草东没有派对", "no party for cao dong"),
                    listOf("万能青年旅店", "omnipotent youth society"),
                    listOf("落日飞车", "sunset rollercoaster"),
                    listOf("新裤子", "new pants"),
                    listOf("逃跑计划", "escape plan"),
                    listOf("动力火车", "power station"),
                    listOf("蔡琴", "tsai chin"),
                    listOf("齐秦", "chyi chin"),
                    listOf("齐豫", "chyi yu"),
                    listOf("费玉清", "fei yu ching"),
                    listOf("任贤齐", "richie jen"),
                    listOf("光良", "michael wong"),
                    listOf("戴佩妮", "penny tai"),
                    listOf("陈绮贞", "cheer chen"),
                    listOf("徐佳莹", "lala hsu"),
                    listOf("魏如萱", "waa wei"),
                    listOf("卢广仲", "crowd lu"),
                    listOf("韦礼安", "weibird"),
                    listOf("张杰", "jason zhang"),
                    listOf("张靓颖", "jane zhang"),
                    listOf("林子祥", "george lam"),
                    listOf("叶倩文", "sally yeh"),
                    listOf("许冠杰", "sam hui"),
                    listOf("郑秀文", "sammi cheng"),
                    listOf("陈慧琳", "kelly chen"),
                    listOf("梁咏琪", "gigi leung"),
                    listOf("黎明", "leon lai"),
                    listOf("郭富城", "aaron kwok"),
                    listOf("安溥", "张悬", "anpu", "deserts chang"),
                    listOf("黄丽玲", "a lin", "alin"),
                    listOf("袁娅维", "tia ray"),
                    listOf("李健", "li jian"),
                    listOf("那英", "na ying"),
                    listOf("韩红", "han hong"),
                    listOf("林志炫", "terry lin"),
                    listOf("杨宗纬", "aska yang"),
                    listOf("曹格", "gary chaw"),
                    listOf("范晓萱", "mavis fan"),
                    listOf("许茹芸", "valen hsu"),
                    listOf("华晨宇", "hua chenyu"),
                    listOf("郁可唯", "yisa yu"),
                    listOf("凤凰传奇", "phoenix legend"),
                    listOf("林家谦", "terence lam"),
                    listOf("卫兰", "janice vidal"),
                    listOf("谢安琪", "kay tse"),
                    listOf("陈粒", "chen li"),
                    listOf("赵雷", "zhao lei"),
                    listOf("宋冬野", "song dongye"),
                    listOf("崔健", "cui jian"),
                    listOf("许巍", "xu wei"),
                    listOf("朴树", "pu shu"),
                    listOf("汪峰", "wang feng"),
                    listOf("窦唯", "dou wei"),
                    listOf("张楚", "zhang chu"),
                    listOf("郑钧", "zheng jun"),
                    listOf("痛仰", "miserable faith"),
                    listOf("黑豹", "黑豹乐队"),
                    listOf("飞儿乐团", "f i r", "fir"),
                    listOf("s h e", "she"),
                    listOf("петр ильич чайковский", "чайковский", "pyotr ilyich tchaikovsky", "tchaikovsky"),
                    listOf("сергей васильевич рахманинов", "рахманинов", "sergei rachmaninoff", "rachmaninoff", "rachmaninov"),
                    listOf("дмитрий дмитриевич шостакович", "шостакович", "dmitri shostakovich", "shostakovich"),
                    listOf("сергей сергеевич прокофьев", "прокофьев", "sergei prokofiev", "prokofiev"),
                    listOf("игорь федорович стравинский", "стравинский", "igor stravinsky", "stravinsky"),
                    listOf("модест петрович мусоргский", "мусоргский", "modest mussorgsky", "mussorgsky"),
                    listOf("николай андреевич римский корсаков", "римский корсаков", "nikolai rimsky korsakov", "rimsky korsakov"),
                    listOf("john cougar", "john cougar mellencamp", "john mellencamp"),
                    listOf("snoop doggy dogg", "snoop dogg"),
                    listOf("puff daddy", "p diddy", "diddy"),
                    listOf("久石让", "久石譲", "joe hisaishi", "hisaishi joe"),
                    listOf("坂本龙一", "ryuichi sakamoto", "sakamoto ryuichi"),
                    listOf("梅林茂", "shigeru umebayashi", "umebayashi shigeru"),
                    listOf("谭盾", "tan dun"),
                    listOf("泽野弘之", "hiroyuki sawano", "sawano hiroyuki"),
                    listOf("马友友", "yo yo ma"),
                    listOf("朗朗", "郎朗", "lang lang"),
                    listOf("王羽佳", "yuja wang"),
                    listOf("喜多郎", "kitaro"),
                    listOf("川井宪次", "kenji kawai"),
                    listOf("菅野洋子", "yoko kanno"),
                    listOf("鹭巢诗郎", "shiro sagisu"),
                    listOf("梶浦由记", "yuki kajiura"),
                    listOf("神前晓", "satoru kosaki"),
                    listOf("上原ひろみ", "上原广美", "hiromi uehara", "hiromi"),
                    listOf("吉森信", "makoto yoshimori"),
                    listOf("和田薰", "kaoru wada"),
                    listOf("增田俊郎", "toshio masuda"),
                    listOf("横山菁儿", "seiji yokoyama"),
                    listOf("中村由利子", "yuriko nakamura"),
                    listOf("武满彻", "toru takemitsu"),
                    listOf("植松伸夫", "nobuo uematsu"),
                    listOf("佐藤直纪", "naoki sato"),
                    listOf("光宗信吉", "shinkichi mitsumune"),
                    listOf("大谷幸", "kow otani", "ko otani"),
                    listOf("菊池俊辅", "shunsuke kikuchi"),
                    listOf("麻枝准", "jun maeda"),
                    listOf("西村由纪江", "yukie nishimura"),
                    listOf("姬神", "himekami"),
                    listOf("大岛满", "大島ミチル", "michiru oshima"),
                    listOf("大野克夫", "大野克夫バンド", "katsuo ohno", "katsuo ono"),
                    listOf("岩崎琢", "taku iwasaki"),
                    listOf("田中公平", "kohei tanaka"),
                    listOf("加藤达也", "tatsuya kato"),
                    listOf("吉田兄弟", "yoshida brothers"),
                    listOf("林有三", "yuzo hayashi"),
                    listOf("都留教博", "norihiro tsuru"),
                    listOf("岩代太郎", "taro iwashiro"),
                    listOf("高梨康治", "yasuharu takanashi"),
                    listOf("横山克", "masaru yokoyama"),
                    listOf("村松崇继", "takatsugu muramatsu"),
                    listOf("河野伸", "shin kono"),
                    listOf("宗次郎", "sojiro"),
                    listOf("阵内一真", "kazuma jinnouchi"),
                    listOf("野见祐二", "yuji nomi"),
                    listOf("佐桥俊彦", "toshihiko sahashi"),
                    listOf("朝川朋之", "tomoyuki asakawa"),
                    listOf("山下康介", "kosuke yamashita"),
                    listOf("菊谷知树", "tomoki kikuya"),
                    listOf("西乡宪一郎", "kenichiro saigo"),
                    listOf("井内龙次", "ryuji iuchi"),
                    listOf("오준성", "吴俊成", "oh joon sung", "oh joonsung"),
                    listOf("정경화", "郑京和", "kyung wha chung"),
                    listOf("苏聪", "cong su", "su cong"),
                    listOf("黄霑", "james wong"),
                    listOf("陈光荣", "comfort chan", "chan kwong wing", "kwong wing chan"),
                    listOf("三宝", "san bao"),
                    listOf("赵季平", "zhao jiping", "jiping zhao"),
                    listOf("顾嘉辉", "joseph koo"),
                    listOf("黎小田", "michael lai"),
                    listOf("卢冠廷", "lowell lo"),
                    listOf("袁卓繁", "richard yuen"),
                    listOf("金培达", "peter kam"),
                    listOf("黄英华", "raymond wong"),
                    listOf("陈勋奇", "frankie chan"),
                    listOf("鲍比达", "chris babida"),
                    listOf("雷颂德", "mark lui"),
                    listOf("贾鹏芳", "jia peng fang", "jia pengfang"),
                    listOf("сергей хмелевский", "sergey khmelevsky", "sergei khmelevsky"),
                    listOf("святослав рихтер", "святослав теофилович рихтер", "sviatoslav richter"),
                    listOf("давид федорович ойстрах", "давид ойстрах", "david oistrakh"),
                    listOf("российский национальный оркестр", "russian national orchestra"),
                    listOf("юлия лежнева", "julia lezhneva"),
                    listOf("анатолий угорский", "anatol ugorski"),
                    listOf("李云迪", "yundi li", "yundi"),
                    listOf("张昊辰", "haochen zhang"),
                    listOf("内田光子", "mitsuko uchida"),
                    listOf("小川典子", "noriko ogawa"),
                    listOf("铃木雅明", "masaaki suzuki"),
                )
            val map = HashMap<String, List<String>>()
            fun mergeGroup(rawAliases: List<String>) {
                val normalized = ArrayList<String>(rawAliases.size)
                for (raw in rawAliases) {
                    val norm = normalizeTokenText(raw)
                    if (norm.length < 2 || norm in PLACEHOLDER_ARTISTS) continue
                    if (norm !in normalized) normalized.add(norm)
                }
                if (normalized.size < 2) return
                val mergedSet = LinkedHashSet<String>(normalized)
                for (k in normalized) {
                    map[k]?.let { mergedSet.addAll(it) }
                }
                val mergedList = mergedSet.toList()
                for (k in mergedList) {
                    map[k] = mergedList
                }
            }
            for (g in groups) mergeGroup(g)
            for (cg in loadCanonicalAliasGroups()) mergeGroup(cg)
            map
        }

        private val COLLECTIVE_ARTIST_ALIASES: Map<String, List<String>> =
            mapOf(
                "two steps from hell" to listOf("thomas bergersen", "nick phoenix"),
                "immediate music" to listOf("yoav goren", "jeffrey fayman", "aleksandar dimitrijevic", "richard friedman"),
                "audiomachine" to listOf("paul dinletir", "kevin rix"),
                "brand x music" to listOf("john sponsler", "tom gire"),
                "secret garden" to listOf("rolf lovland"),
            )

        private val icuTransliterator: Any? by lazy {
            runCatching {
                    val cls = Class.forName("android.icu.text.Transliterator")
                    val getInstance = cls.getMethod("getInstance", String::class.java)
                    getInstance.invoke(null, "Traditional-Simplified")
                }
                .getOrNull()
        }

        private val icuTransliterateMethod by lazy {
            runCatching {
                    val cls = Class.forName("android.icu.text.Transliterator")
                    cls.getMethod("transliterate", String::class.java)
                }
                .getOrNull()
        }

        fun toSimplifiedChinese(input: String): String {
            if (input.isEmpty()) return input
            val icuResult =
                runCatching {
                        val tr = icuTransliterator
                        val m = icuTransliterateMethod
                        if (tr != null && m != null) m.invoke(tr, input) as? String else null
                    }
                    .getOrNull() ?: input
            var sb: StringBuilder? = null
            for (i in icuResult.indices) {
                val ch = icuResult[i]
                val mapped = TRADITIONAL_TO_SIMPLIFIED[ch]
                if (mapped != null && mapped != ch) {
                    if (sb == null) {
                        sb = StringBuilder(icuResult.length).append(icuResult, 0, i)
                    }
                    sb.append(mapped)
                } else {
                    sb?.append(ch)
                }
            }
            return sb?.toString() ?: icuResult
        }

        fun preNormalizeUnicode(raw: String?): String {
            if (raw.isNullOrBlank()) return ""
            var s = raw.replace('\u00A0', ' ').trim()
            s = toSimplifiedChinese(s)
            s = Normalizer.normalize(s, Normalizer.Form.NFKC)
            s = foldLatinAndLowercase(s)
            return MULTI_SPACE_REGEX.replace(s, " ").trim()
        }

        private fun foldLatinAndLowercase(input: String): String {
            val lower = input.lowercase(java.util.Locale.ROOT)
            val sb = StringBuilder(lower.length)
            for (c in lower) {
                when (c) {
                    'ß' -> sb.append("ss")
                    'æ' -> sb.append("ae")
                    'œ' -> sb.append("oe")
                    'ø' -> sb.append('o')
                    'ł' -> sb.append('l')
                    'đ', 'ð' -> sb.append('d')
                    'þ' -> sb.append("th")
                    'ё' -> sb.append('е')
                    in '\u0300'..'\u036F' -> {}
                    in '\u00C0'..'\u024F', in '\u1E00'..'\u1EFF' -> {
                        val dec = Normalizer.normalize(c.toString(), Normalizer.Form.NFD)
                        for (dc in dec) {
                            if (Character.getType(dc) != Character.NON_SPACING_MARK.toInt()) {
                                sb.append(dc)
                            }
                        }
                    }
                    else -> sb.append(c)
                }
            }
            return sb.toString()
        }

        fun normalizeTokenText(raw: String?): String {
            if (raw.isNullOrBlank()) return ""
            return normalizePreNormTokenText(preNormalizeUnicode(raw))
        }

        private fun normalizePreNormTokenText(preNorm: String): String {
            if (preNorm.isBlank()) return ""
            val sb = StringBuilder(preNorm.length)
            var prevSpace = true
            for (c in preNorm) {
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u02BC') continue
                if (Character.isLetterOrDigit(c)) {
                    sb.append(c)
                    prevSpace = false
                } else if (!prevSpace) {
                    sb.append(' ')
                    prevSpace = true
                }
            }
            return sb.toString().trim()
        }

        fun compactText(normalized: String): String =
            if (normalized.isEmpty()) "" else normalized.replace(" ", "")

        fun containsEastAsian(s: String): Boolean {
            for (c in s) {
                if (c in '\u4E00'..'\u9FFF' ||
                    c in '\u3400'..'\u4DBF' ||
                    c in '\u3040'..'\u30FF' ||
                    c in '\u31F0'..'\u31FF' ||
                    c in '\uAC00'..'\uD7AF'
                ) {
                    return true
                }
            }
            return false
        }

        fun normalizeTitleVariants(raw: String?): List<String> {
            if (raw.isNullOrBlank()) return emptyList()
            var preNorm = preNormalizeUnicode(raw)
            if (preNorm.isEmpty()) return emptyList()
            val discStripped = DISC_TRACK_PREFIX_REGEX.replace(preNorm, "").trim()
            if (discStripped.isNotEmpty()) {
                preNorm = discStripped
            }
            var cleaned = DASH_EDITION_REGEX.replace(preNorm, "")
            cleaned = FEAT_PAREN_REGEX.replace(cleaned, " ")
            cleaned = EDITION_PAREN_REGEX.replace(cleaned, " ").trim()
            if (normalizePreNormTokenText(cleaned).isEmpty()) {
                cleaned = preNorm
            }

            val variants = ArrayList<String>(6)
            fun addUnique(v: String) {
                if (v.isNotEmpty() && v !in variants) variants.add(v)
            }
            addUnique(normalizePreNormTokenText(cleaned))

            val bracketMatches = BRACKET_REGEX.findAll(cleaned).toList()
            if (bracketMatches.isNotEmpty()) {
                val strippedOuter =
                    BRACKET_REGEX.replace(cleaned) { m ->
                        val inner = m.groupValues[1]
                        if ((extractVersionQualifiers(inner) and HARD_BLOCK_MASK) != 0) m.value else " "
                    }
                addUnique(normalizePreNormTokenText(strippedOuter))
                for (m in bracketMatches) {
                    val inner = m.groupValues[1]
                    if ((extractVersionQualifiers(inner) and HARD_BLOCK_MASK) == 0) {
                        val innerNorm = normalizePreNormTokenText(inner)
                        if (containsEastAsian(innerNorm) && compactText(innerNorm).length >= 2) {
                            addUnique(innerNorm)
                        }
                    }
                }
            }

            if (cleaned.indexOf('/') >= 0 || cleaned.indexOf('／') >= 0) {
                val slashSegs = SLASH_TITLE_SPLIT_REGEX.split(cleaned)
                if (slashSegs.size in 2..4) {
                    val segNorms = ArrayList<String>(slashSegs.size)
                    var anyPureDigitsOrEmpty = false
                    var anyEastAsian = false
                    var allEastAsian = true
                    for (seg in slashSegs) {
                        val segNorm = normalizePreNormTokenText(seg)
                        if (segNorm.isEmpty() || segNorm.all { it in '0'..'9' }) {
                            anyPureDigitsOrEmpty = true
                            break
                        }
                        val isEa = containsEastAsian(segNorm)
                        anyEastAsian = anyEastAsian || isEa
                        allEastAsian = allEastAsian && isEa
                        segNorms.add(segNorm)
                    }
                    if (!anyPureDigitsOrEmpty && segNorms.size >= 2) {
                        if (anyEastAsian && !allEastAsian) {
                            for (sn in segNorms) addUnique(sn)
                        } else if (allEastAsian) {
                            for (sn in segNorms) {
                                if (compactText(sn).length >= 2) addUnique(sn)
                            }
                        } else if (segNorms.size <= 3 && (" / " in cleaned || " ／ " in cleaned)) {
                            for (sn in segNorms) {
                                if (compactText(sn).length >= 6 && !isGenericClassicalMovementLabel(sn)) {
                                    addUnique(sn)
                                }
                            }
                        }
                    }
                }
            }

            val dashIdx = cleaned.indexOf(" - ")
            if (dashIdx > 0 && dashIdx + 3 < cleaned.length) {
                val leftRaw = cleaned.substring(0, dashIdx)
                val rightRaw = cleaned.substring(dashIdx + 3)
                val left = normalizePreNormTokenText(leftRaw)
                val right = normalizePreNormTokenText(rightRaw)
                if (left.isNotEmpty() &&
                    right.isNotEmpty() &&
                    containsEastAsian(left) != containsEastAsian(right) &&
                    (extractVersionQualifiers(leftRaw) and HARD_BLOCK_MASK) == 0 &&
                    (extractVersionQualifiers(rightRaw) and HARD_BLOCK_MASK) == 0
                ) {
                    addUnique(left)
                    addUnique(right)
                }
            }

            val dashRemixStripped = DASH_REMIX_SUFFIX_REGEX.replace(cleaned, "").trim()
            if (dashRemixStripped.isNotEmpty() && dashRemixStripped != cleaned) {
                addUnique(normalizePreNormTokenText(dashRemixStripped))
            }

            val firstColon = cleaned.indexOfAny(charArrayOf(':', '：'))
            if (firstColon > 0 && firstColon + 1 < cleaned.length) {
                val firstColonNorm = normalizePreNormTokenText(cleaned.substring(firstColon + 1))
                if (!isGenericClassicalMovementLabel(firstColonNorm)) {
                    addUnique(firstColonNorm)
                }
                val lastColon = cleaned.lastIndexOfAny(charArrayOf(':', '：'))
                if (lastColon > firstColon && lastColon + 1 < cleaned.length) {
                    val lastColonNorm = normalizePreNormTokenText(cleaned.substring(lastColon + 1))
                    if (!isGenericClassicalMovementLabel(lastColonNorm)) {
                        addUnique(lastColonNorm)
                    }
                }
            }
            return variants
        }

        private fun extractStructuralMarkers(normalized: String): StructuralMarkers {
            val numbers = HashSet<String>()
            val romans = HashSet<String>()
            val cjkOrdinals = HashSet<String>()
            if (normalized.isBlank()) return StructuralMarkers(numbers, romans, cjkOrdinals)
            for (token in normalized.split(' ')) {
                if (token.isEmpty()) continue
                if (token.all { it in '0'..'9' }) {
                    val trimmed = token.trimStart('0')
                    numbers.add(if (trimmed.isEmpty()) "0" else trimmed)
                } else if (token in ROMAN_MOVEMENT_TOKENS) {
                    romans.add(token)
                }
            }
            for (m in CJK_ORDINAL_REGEX.findAll(normalized)) {
                cjkOrdinals.add(m.value)
            }
            return StructuralMarkers(numbers, romans, cjkOrdinals)
        }

        private fun areStructurallyCompatible(a: StructuralMarkers, b: StructuralMarkers): Boolean {
            if (a.numbers.isNotEmpty() && b.numbers.isNotEmpty() &&
                !b.numbers.containsAll(a.numbers) && !a.numbers.containsAll(b.numbers)
            ) {
                return false
            }
            if (a.romans.isNotEmpty() && b.romans.isNotEmpty() &&
                !b.romans.containsAll(a.romans) && !a.romans.containsAll(b.romans)
            ) {
                return false
            }
            if (a.cjkOrdinals.isNotEmpty() && b.cjkOrdinals.isNotEmpty() &&
                !b.cjkOrdinals.containsAll(a.cjkOrdinals) && !a.cjkOrdinals.containsAll(b.cjkOrdinals)
            ) {
                return false
            }
            return true
        }

        private fun extractSignificantCreditWords(tokens: Set<String>): List<String> {
            val words = LinkedHashSet<String>()
            for (token in tokens) {
                if (token.isEmpty() || token in PLACEHOLDER_ARTISTS) continue
                for (w in token.split(' ')) {
                    if (w.isEmpty() || w in CREDIT_STOP_WORDS || w in PLACEHOLDER_ARTISTS) continue
                    val keep = if (containsEastAsian(w)) w.length >= 2 else w.length >= 4
                    if (keep) words.add(w)
                }
            }
            return words.toList()
        }

        private fun normalizeAlbum(raw: String?): String {
            if (raw.isNullOrBlank()) return ""
            val preNorm = preNormalizeUnicode(raw)
            var stripped = ALBUM_EDITION_PAREN_REGEX.replace(preNorm, " ")
            stripped = ALBUM_EDITION_DASH_REGEX.replace(stripped, "")
            val norm = normalizePreNormTokenText(stripped)
            return norm.ifEmpty { normalizePreNormTokenText(preNorm) }
        }

        private val BAND_SUFFIXES = arrayOf("乐队", "乐团", "组合")

        private fun addChineseArtistDerivations(
            token: String,
            tokens: MutableSet<String>,
        ) {
            if (token.isEmpty() || !HanziPinyinConverter.containsHanzi(token)) return
            for (suffix in BAND_SUFFIXES) {
                if (token.endsWith(suffix) && token.length - suffix.length >= 2) {
                    val stripped = token.substring(0, token.length - suffix.length).trim()
                    if (stripped.length >= 2) tokens.add(stripped)
                }
            }
            if (' ' !in token) {
                val idx = token.indexOf('与')
                if (idx >= 2 && idx + 3 <= token.length) {
                    val left = token.substring(0, idx)
                    val right = token.substring(idx + 1)
                    if (left.length >= 2 && right.length >= 2 && '与' !in right) {
                        tokens.add(left)
                        tokens.add(right)
                    }
                }
            }
        }

        fun extractArtistTokens(raw: String?): Set<String> {
            if (raw.isNullOrBlank()) return emptySet()
            var working = preNormalizeUnicode(raw)
            if (working.isEmpty()) return emptySet()

            var replacements: MutableList<Pair<String, String>>? = null
            var seq = 0
            for (protectedName in PROTECTED_ARTISTS) {
                if (!working.contains(protectedName)) continue
                var searchStart = 0
                while (searchStart < working.length) {
                    val idx = working.indexOf(protectedName, searchStart)
                    if (idx < 0) break
                    val leftBoundary = idx == 0 || !Character.isLetterOrDigit(working[idx - 1])
                    val end = idx + protectedName.length
                    val rightBoundary = end == working.length || !Character.isLetterOrDigit(working[end])
                    if (!leftBoundary || !rightBoundary) {
                        searchStart = idx + 1
                        continue
                    }
                    val list = replacements ?: ArrayList<Pair<String, String>>().also { replacements = it }
                    val marker = "__twprot${seq++}__"
                    working = working.substring(0, idx) + marker + working.substring(end)
                    list.add(marker to protectedName)
                    searchStart = idx + marker.length
                }
            }

            val repList = replacements
            val tokens = LinkedHashSet<String>()
            for (part in ARTIST_SPLIT_REGEX.split(working)) {
                var restored = part
                if (repList != null) {
                    for ((marker, orig) in repList) {
                        if (restored.contains(marker)) restored = restored.replace(marker, orig)
                    }
                }
                val clean = normalizePreNormTokenText(restored)
                if (clean.isNotEmpty()) {
                    tokens.add(clean)
                    addChineseArtistDerivations(clean, tokens)
                }
                val parenMatches = BRACKET_REGEX.findAll(restored).toList()
                if (parenMatches.isNotEmpty()) {
                    val outer = normalizePreNormTokenText(BRACKET_REGEX.replace(restored, " "))
                    if (outer.isNotEmpty()) {
                        tokens.add(outer)
                        addChineseArtistDerivations(outer, tokens)
                    }
                    for (pm in parenMatches) {
                        val inner = normalizePreNormTokenText(pm.groupValues[1])
                        if (inner.isNotEmpty()) {
                            tokens.add(inner)
                            addChineseArtistDerivations(inner, tokens)
                        }
                    }
                }
                if (containsEastAsian(clean)) {
                    val runs = SCRIPT_RUN_REGEX.findAll(clean).map { it.value.trim() }.toList()
                    if (runs.size > 1) {
                        val cjk = runs.filter { containsEastAsian(it) }.joinToString(" ").trim()
                        val latin = runs.filter { !containsEastAsian(it) }.joinToString(" ").trim()
                        if (cjk.length >= 2) {
                            tokens.add(cjk)
                            addChineseArtistDerivations(cjk, tokens)
                        }
                        if (latin.length >= 2) tokens.add(latin)
                    }
                }
            }
            val extra = ArrayList<String>()
            for (t in tokens) {
                CJK_ARTIST_ALIASES[t]?.let { extra.addAll(it) }
                COLLECTIVE_ARTIST_ALIASES[t]?.let { extra.addAll(it) }
            }
            tokens.addAll(extra)
            return tokens
        }

        private fun extractVersionQualifiers(preNorm: String): Int {
            if (preNorm.isEmpty()) return 0
            var q = 0
            if (preNorm.contains("混音") || QUALIFIER_REMIX_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_REMIX
            if (preNorm.contains("伴奏") || preNorm.contains("纯音乐") || preNorm.contains("消音") || preNorm.contains("无人声") || preNorm.contains("乐器版") || QUALIFIER_INSTRUMENTAL_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_INSTRUMENTAL
            if (preNorm.contains("卡拉ok") || QUALIFIER_KARAOKE_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_KARAOKE
            if (preNorm.contains("不插电") || preNorm.contains("木吉他版") || QUALIFIER_ACOUSTIC_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_ACOUSTIC
            if (preNorm.contains("清唱") || preNorm.contains("阿卡贝拉") || QUALIFIER_ACAPELLA_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_ACAPELLA
            if (preNorm.contains("小样") || QUALIFIER_DEMO_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_DEMO
            if (preNorm.contains("翻唱") || QUALIFIER_COVER_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_COVER
            if (preNorm.contains("变速") || preNorm.contains("加速版") || preNorm.contains("降调") || QUALIFIER_SPEED_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_SPEED
            if (preNorm.contains("现场") || preNorm.contains("演唱会") || preNorm.contains("音乐会") || QUALIFIER_LIVE_REGEX.containsMatchIn(preNorm)) q = q or QUALIFIER_LIVE
            return q
        }

        private fun splitArtistTitle(raw: String): Pair<String?, String> {
            for (sep in listOf(" - ", " – ", " — ", " —— ", " － ", " / ", "：")) {
                val idx = raw.indexOf(sep)
                if (idx > 0 && idx + sep.length < raw.length) {
                    val left = raw.substring(0, idx).trim()
                    val right = raw.substring(idx + sep.length).trim()
                    if (left.isNotEmpty() && right.isNotEmpty()) return left to right
                }
            }
            return null to raw.trim()
        }

        fun fromSongs(songs: Collection<Song>): AndroidPlaylistTrackMatcher<Song> =
            AndroidPlaylistTrackMatcher(songs.toList()) { song ->
                val artistNames = song.artists.mapNotNull { (it.name as? Name.Known)?.raw }
                val albumArtistNames = song.album.artists.mapNotNull { (it.name as? Name.Known)?.raw }
                val genreNames = song.genres.mapNotNull { (it.name as? Name.Known)?.raw }
                val albumName = (song.album.name as? Name.Known)?.raw.orEmpty()
                TrackRef(
                    uid = song.uid.toString(),
                    title = song.name.raw,
                    artists = artistNames,
                    album = albumName,
                    durationMs = song.durationMs,
                    relPath = song.path.components.components.joinToString("/"),
                    composers = song.composers,
                    conductors = song.conductors,
                    workName = song.workName.orEmpty(),
                    movementName = song.movementName.orEmpty(),
                    albumArtists = albumArtistNames,
                    genres = genreNames,
                )
            }
    }
}
