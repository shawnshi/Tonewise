/*
 * Copyright (c) 2026 Tonewise Project
 * ClassicalMetadataEngine.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.music

import java.util.Locale

data class ParsedWorkMovement(
    val work: String,
    val movement: String,
    val movementNumber: Int?,
)

data class SeparatedPerformers(
    val soloists: List<String>,
    val ensemble: String?,
)

data class ClassicalNowPlayingLines(
    val line1: String,
    val line2: String,
    val line3Tiers: List<String>,
)

/**
 * Parity mirror of Rust `tonewise_metadata::classical` governed by
 * `protocol/dictionaries/composer_metadata.json` and `protocol/fixtures/classical_golden.json`.
 */
object ClassicalMetadataEngine {
    private data class Tables(
        val composerEras: Map<String, String>,
        val ensembleAbbreviations: Map<String, String>,
        val canonicalAliases: Map<String, String>,
        val composerShortAliases: Map<String, String>,
    )

    private val tables: Tables by lazy { loadTables() }

    private fun loadTables(): Tables {
        val metaText = readResourceText("/tonewise/composer_metadata.json")
        val aliasText = readResourceText("/tonewise/artist_aliases.json")

        val eras = LinkedHashMap<String, String>()
        val abbrs = LinkedHashMap<String, String>()
        if (metaText != null) {
            extractStringMapSection(metaText, "composer_eras").forEach { (k, v) ->
                val cleanK = k.trim().lowercase(Locale.ROOT)
                val cleanV = v.trim().lowercase(Locale.ROOT)
                if (cleanK.isNotEmpty() && cleanV in setOf("baroque", "classical", "romantic", "modern")) {
                    eras[cleanK] = cleanV
                }
            }
            extractStringMapSection(metaText, "ensemble_abbreviations").forEach { (k, v) ->
                val cleanK = k.trim().lowercase(Locale.ROOT)
                val cleanV = v.trim()
                if (cleanK.isNotEmpty() && cleanV.isNotEmpty()) {
                    abbrs[cleanK] = cleanV
                }
            }
        }

        val canonical = LinkedHashMap<String, String>()
        val composerShort = LinkedHashMap<String, String>()
        if (aliasText != null) {
            extractStringMapSection(aliasText, "canonical_aliases").forEach { (k, v) ->
                val ck = k.trim().lowercase(Locale.ROOT)
                val cv = v.trim()
                if (ck.isNotEmpty() && cv.isNotEmpty()) {
                    canonical[ck] = cv
                }
            }
            extractStringMapSection(aliasText, "composer_short_aliases").forEach { (k, v) ->
                val ck = k.trim().lowercase(Locale.ROOT)
                val cv = v.trim()
                if (ck.isNotEmpty() && cv.isNotEmpty()) {
                    composerShort[ck] = cv
                }
            }
        }

        return Tables(eras, abbrs, canonical, composerShort)
    }

    private fun readResourceText(path: String): String? {
        val stream = ClassicalMetadataEngine::class.java.getResourceAsStream(path) ?: return null
        return stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
    }

    private fun extractStringMapSection(json: String, sectionName: String): Map<String, String> {
        val idx = json.indexOf("\"$sectionName\"")
        if (idx < 0) return emptyMap()
        val braceStart = json.indexOf('{', idx)
        if (braceStart < 0) return emptyMap()
        var depth = 0
        var inStr = false
        var escape = false
        var braceEnd = -1
        for (i in braceStart until json.length) {
            val ch = json[i]
            if (escape) {
                escape = false
                continue
            }
            if (ch == '\\' && inStr) {
                escape = true
                continue
            }
            if (ch == '"') {
                inStr = !inStr
                continue
            }
            if (!inStr) {
                if (ch == '{') depth++
                else if (ch == '}') {
                    depth--
                    if (depth == 0) {
                        braceEnd = i
                        break
                    }
                }
            }
        }
        if (braceEnd <= braceStart) return emptyMap()
        val body = json.substring(braceStart + 1, braceEnd)
        val out = LinkedHashMap<String, String>()
        val regex = Regex("\"((?:\\\\.|[^\"\\\\])*)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
        for (m in regex.findAll(body)) {
            out[unescapeJsonString(m.groupValues[1])] = unescapeJsonString(m.groupValues[2])
        }
        return out
    }

    private fun unescapeJsonString(raw: String): String {
        if (!raw.contains('\\')) return raw
        val sb = StringBuilder(raw.length)
        var i = 0
        while (i < raw.length) {
            val ch = raw[i]
            if (ch == '\\' && i + 1 < raw.length) {
                when (val next = raw[i + 1]) {
                    '\\', '"', '/' -> {
                        sb.append(next)
                        i += 2
                        continue
                    }
                    'u' if i + 5 < raw.length -> {
                        val hex = raw.substring(i + 2, i + 6).toIntOrNull(16)
                        if (hex != null) {
                            sb.append(hex.toChar())
                            i += 6
                            continue
                        }
                    }
                }
            }
            sb.append(ch)
            i++
        }
        return sb.toString()
    }

    private val CLASSICAL_GENRE_TOKENS = listOf(
        "classical", "古典", "交响", "协奏", "奏鸣", "室内乐", "歌剧", "巴洛克",
        "orchestra", "symphon", "concerto", "sonata", "chamber", "opera", "baroque", "choral",
    )

    private val NON_CLASSICAL_GENRE_TOKENS = listOf(
        "pop", "rock", "metal", "jazz", "hip-hop", "hip hop", "rap", "r&b", "electronic", "edm",
        "dance", "country", "folk", "blues", "reggae", "indie", "punk", "soul", "funk", "k-pop",
        "j-pop", "c-pop", "mandopop", "cantopop", "tai-pop", "new age", "ambient", "traditional",
        "latin", "ballad", "disco", "alternative", "world music", "acoustic", "fado", "reggaeton",
        "acg", "anime", "soundtrack", "ost", "game", "流行", "摇滚", "爵士", "电子", "民谣", "说唱", "嘻哈", "原声",
    )

    private val CLASSICAL_WORK_SIGNAL_TOKENS = listOf(
        "symphony", "symphonie", "concerto", "konzert", "sonata", "sonate", "quartet", "quintet",
        "trio", "variation", "prelude", "prélude", "nocturne", "etude", "étude", "fugue", "toccata",
        "suite", "partita", "requiem", "cantata", "oratorio", "overture", "ouvertüre", "rhapsody in ",
        "rhapsody on ", "serenade", "divertimento", "scherzo", "chaconne", "passacaglia", "mass in",
        "op.", "bwv", "kv", "k.", "hob.", "rv ", "woo", "交响", "协奏", "奏鸣", "四重奏", "变奏曲",
        "前奏曲", "夜曲", "练习曲", "组曲", "弥撒", "安魂曲", "歌剧",
    )

    private val ENSEMBLE_TOKENS = listOf(
        "orchestra", "philharmonic", "philharmoniker", "symphoniker", "sinfonia", "quartet",
        "quintet", "sextet", "octet", "trio", "ensemble", "choir", "chorus", "academy", "consort",
        "staatskapelle", "gewandhaus", "orchestre", "orchester", "camerata", "solisti", "virtuosi",
        "乐团", "爱乐", "交响", "合唱团", "四重奏", "五重奏", "三重奏", "室内乐团",
    )

    private val CATALOG_MARKERS = listOf(
        "hob.", "hob", "op.", "op", "bwv", "k.", "kv", "rv", "woo",
    )

    fun isClassicalGenre(genre: String?): Boolean {
        val lower = genre?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return lower.isNotEmpty() && CLASSICAL_GENRE_TOKENS.any { lower.contains(it) }
    }

    fun isExplicitNonClassicalGenre(genre: String?): Boolean {
        val lower = genre?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return lower.isNotEmpty() && NON_CLASSICAL_GENRE_TOKENS.any { lower.contains(it) }
    }

    fun hasClassicalWorkSignal(text: String?): Boolean {
        val lower = text?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return lower.isNotEmpty() && CLASSICAL_WORK_SIGNAL_TOKENS.any { lower.contains(it) }
    }

    fun isClassicalTrack(
        genre: String?,
        work: String?,
        composer: String?,
        conductor: String?,
        title: String?,
    ): Boolean {
        if (isClassicalGenre(genre)) return true
        val workClean = work?.trim().orEmpty()
        if (isExplicitNonClassicalGenre(genre)) {
            return hasClassicalWorkSignal(workClean) || hasClassicalWorkSignal(title)
        }
        if (workClean.isNotEmpty() || !conductor.isNullOrBlank()) return true
        return !composer.isNullOrBlank()
    }

    fun parseRomanNumeral(raw: String?): Int? {
        val s = raw?.trim()?.uppercase(Locale.ROOT).orEmpty()
        if (s.isEmpty() || s.any { it != 'I' && it != 'V' && it != 'X' && it != 'L' && it != 'C' }) {
            return null
        }
        var total = 0
        var prev = 0
        for (i in s.length - 1 downTo 0) {
            val valNum = when (s[i]) {
                'I' -> 1
                'V' -> 5
                'X' -> 10
                'L' -> 50
                'C' -> 100
                else -> return null
            }
            if (valNum < prev) {
                total -= valNum
            } else {
                total += valNum
                prev = valNum
            }
        }
        if (total !in 1..99) return null
        return if (formatRomanNumeral(total) == s) total else null
    }

    fun formatRomanNumeral(n: Int): String {
        if (n !in 1..99) return n.toString()
        val tens = arrayOf("", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC")
        val ones = arrayOf("", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX")
        return tens[n / 10] + ones[n % 10]
    }

    fun parseMovementPrefix(raw: String?): Pair<Int?, String> {
        val trimmed = raw?.trim().orEmpty()
        if (trimmed.isEmpty()) return null to ""

        var firstNonRoman = 0
        while (firstNonRoman < trimmed.length && trimmed[firstNonRoman].uppercaseChar() in charArrayOf('I', 'V', 'X', 'L', 'C')) {
            firstNonRoman++
        }
        if (firstNonRoman in 1 until trimmed.length) {
            val token = trimmed.substring(0, firstNonRoman)
            val rest = trimmed.substring(firstNonRoman)
            val delimited = rest.startsWith('.') ||
                rest.startsWith(':') ||
                rest.startsWith('：') ||
                rest.startsWith(" - ") ||
                rest.startsWith(" – ") ||
                rest.startsWith(" — ")
            val num = if (delimited) parseRomanNumeral(token) else null
            if (num != null) {
                val after = rest.trimStart('.', ':', '：', '-', '–', '—', ' ').trim()
                return num to after
            }
        }

        var firstNonDigit = 0
        while (firstNonDigit < trimmed.length && trimmed[firstNonDigit] in '0'..'9') {
            firstNonDigit++
        }
        if (firstNonDigit in 1 until trimmed.length) {
            val digits = trimmed.substring(0, firstNonDigit)
            val rest = trimmed.substring(firstNonDigit)
            if (rest.startsWith('.') || rest.startsWith(" - ") || rest.startsWith(" – ")) {
                val num = digits.toIntOrNull()
                if (num != null && num in 1..99) {
                    val after = rest.trimStart('.', '-', '–', ' ').trim()
                    if (after.isNotEmpty()) {
                        return num to after
                    }
                }
            }
        }

        return null to trimmed
    }

    private fun isProtectedCatalogColon(s: String, colonIdx: Int): Boolean {
        if (colonIdx + 1 >= s.length || !s[colonIdx + 1].isDigit()) return false
        val before = s.substring(0, colonIdx).trimEnd().lowercase(Locale.ROOT)
        val windowStart = maxOf(0, before.length - 24)
        val tail = before.substring(windowStart)
        return CATALOG_MARKERS.any { tail.contains(it) }
    }

    private fun findWorkMovementSplit(title: String): Pair<String, String>? {
        for (i in title.indices) {
            val ch = title[i]
            if (ch == ':' && !isProtectedCatalogColon(title, i)) {
                val left = title.substring(0, i).trim()
                val right = title.substring(i + 1).trim()
                if (left.isNotEmpty() && right.isNotEmpty()) return left to right
            } else if (ch == '：') {
                val left = title.substring(0, i).trim()
                val right = title.substring(i + 1).trim()
                if (left.isNotEmpty() && right.isNotEmpty()) return left to right
            }
        }

        for (sep in listOf(" — ", " – ", " - ")) {
            val pos = title.indexOf(sep)
            if (pos >= 0) {
                val left = title.substring(0, pos).trim()
                val right = title.substring(pos + sep.length).trim()
                if (left.isNotEmpty() && right.isNotEmpty()) return left to right
            }
        }
        return null
    }

    fun splitWorkAndMovement(
        rawTitle: String?,
        explicitWork: String? = null,
        explicitMovement: String? = null,
        explicitMovementNumber: Int? = null,
    ): ParsedWorkMovement {
        val title = rawTitle?.trim().orEmpty()
        val expWork = explicitWork?.trim()?.takeIf { it.isNotEmpty() }
        val expMvt = explicitMovement?.trim()?.takeIf { it.isNotEmpty() }

        if (expWork != null) {
            val foundSplit = findWorkMovementSplit(title)
            val rawMvtSource = when {
                expMvt != null -> expMvt
                foundSplit != null && foundSplit.first.equals(expWork, ignoreCase = true) -> foundSplit.second
                else -> title
            }
            val (parsedNum, cleanMvt) = parseMovementPrefix(rawMvtSource)
            val finalMvt = if (cleanMvt.equals(expWork, ignoreCase = true)) "" else cleanMvt
            return ParsedWorkMovement(expWork, finalMvt, explicitMovementNumber ?: parsedNum)
        }

        val found = findWorkMovementSplit(title)
        if (found != null) {
            val mvtSource = expMvt ?: found.second
            val (parsedNum, cleanMvt) = parseMovementPrefix(mvtSource)
            return ParsedWorkMovement(found.first, cleanMvt, explicitMovementNumber ?: parsedNum)
        }

        val (titleNum, cleanTitle) = parseMovementPrefix(title)
        if (titleNum != null) {
            return ParsedWorkMovement(cleanTitle, expMvt ?: cleanTitle, explicitMovementNumber ?: titleNum)
        }

        return ParsedWorkMovement(title, expMvt ?: "", explicitMovementNumber)
    }

    fun normalizeComposerName(raw: String?): String {
        val trimmed = raw?.trim().orEmpty()
        if (trimmed.isEmpty()) return ""
        val lower = trimmed.lowercase(Locale.ROOT)
        val expanded = tables.composerShortAliases[lower] ?: trimmed
        val expLower = expanded.lowercase(Locale.ROOT)
        return tables.canonicalAliases[expLower] ?: expanded
    }

    fun classifyComposerEra(composer: String?, rawGenre: String? = null): String? {
        val trimmed = composer?.trim().orEmpty()
        if (trimmed.isNotEmpty()) {
            val norm = normalizeComposerName(trimmed).lowercase(Locale.ROOT)
            tables.composerEras[norm]?.let { return it }
            tables.composerEras[trimmed.lowercase(Locale.ROOT)]?.let { return it }
        }
        val g = rawGenre?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return when {
            g.contains("baroque") || g.contains("巴洛克") -> "baroque"
            g.contains("romantic") || g.contains("浪漫") -> "romantic"
            g.contains("modern") || g.contains("contemporary") || g.contains("20th") || g.contains("现代") -> "modern"
            g.contains("classical era") || g.contains("古典主义") -> "classical"
            else -> null
        }
    }

    fun classifyWorkForm(workTitle: String?, rawGenre: String? = null): String {
        val combined = "${workTitle?.trim().orEmpty()} ${rawGenre?.trim().orEmpty()}".lowercase(Locale.ROOT)
        return when {
            combined.contains("symphon") || combined.contains("sinfonia") || combined.contains("交响") -> "symphonies"
            combined.contains("concerto") || combined.contains("konzert") || combined.contains("concertino") || combined.contains("协奏") -> "concertos"
            combined.contains("quartet") || combined.contains("quintet") || combined.contains("sextet") ||
                combined.contains("septet") || combined.contains("octet") || combined.contains("string trio") ||
                combined.contains("piano trio") || combined.contains("chamber") || combined.contains("四重奏") ||
                combined.contains("五重奏") || combined.contains("三重奏") || combined.contains("室内乐") -> "chamber"
            combined.contains("sonata") || combined.contains("sonate") || combined.contains("partita") ||
                combined.contains("variation") || combined.contains("nocturne") || combined.contains("etude") ||
                combined.contains("étude") || combined.contains("prelude") || combined.contains("prélude") ||
                combined.contains("fugue") || combined.contains("toccata") || combined.contains("impromptu") ||
                combined.contains("ballade") || combined.contains("scherzo") || combined.contains("mazurka") ||
                combined.contains("polonaise") || combined.contains("waltz") || combined.contains("valse") ||
                combined.contains("chaconne") || combined.contains("solo") || combined.contains("奏鸣") ||
                combined.contains("变奏曲") || combined.contains("夜曲") || combined.contains("练习曲") ||
                combined.contains("前奏曲") ||
                (combined.contains("suite") && !combined.contains("orchestral suite")) ||
                combined.contains("组曲") -> "solo"
            else -> "other"
        }
    }

    fun isEnsembleName(name: String?): Boolean {
        val lower = name?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return lower.isNotEmpty() && ENSEMBLE_TOKENS.any { lower.contains(it) }
    }

    fun separatePerformers(
        artists: List<String>,
        albumArtist: String?,
        conductors: List<String>,
        explicitOrchestra: String? = null,
        explicitSoloists: List<String> = emptyList(),
    ): SeparatedPerformers {
        val conductorKeys = conductors.map { it.trim().lowercase(Locale.ROOT) }.filter { it.isNotEmpty() }.toSet()
        var ensemble = explicitOrchestra?.trim()?.takeIf { it.isNotEmpty() }
        val soloists = explicitSoloists.map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()

        for (raw in artists) {
            val clean = raw.trim()
            if (clean.isEmpty()) continue
            if (isEnsembleName(clean)) {
                if (ensemble == null) ensemble = clean
                continue
            }
            if (clean.lowercase(Locale.ROOT) in conductorKeys) continue
            if (soloists.none { it.equals(clean, ignoreCase = true) }) {
                soloists.add(clean)
            }
        }

        if (ensemble == null && !albumArtist.isNullOrBlank() && isEnsembleName(albumArtist)) {
            ensemble = albumArtist.trim()
        }

        return SeparatedPerformers(soloists, ensemble)
    }

    fun abbreviateEnsemble(name: String?): String {
        val trimmed = name?.trim().orEmpty()
        if (trimmed.isEmpty()) return ""
        tables.ensembleAbbreviations[trimmed.lowercase(Locale.ROOT)]?.let { return it }

        var out = trimmed
        val replacements = listOf(
            "Philharmonic Orchestra" to "PO",
            "Symphony Orchestra" to "SO",
            "Chamber Orchestra" to "CO",
            "Radio Symphony Orchestra" to "RSO",
            "Philharmoniker" to "Phil.",
            "Symphoniker" to "Sym.",
        )
        for ((pat, rep) in replacements) {
            if (out.contains(pat)) out = out.replace(pat, rep)
        }
        return out
    }

    private fun isCjkChar(c: Char): Boolean {
        val code = c.code
        return code in 0x4E00..0x9FFF ||
            code in 0x3400..0x4DBF ||
            code in 0x3040..0x30FF ||
            code in 0xAC00..0xD7AF
    }

    fun shortSurname(name: String?): String {
        val trimmed = name?.trim().orEmpty()
        if (trimmed.isEmpty()) return ""
        val commaIdx = trimmed.indexOf(',')
        if (commaIdx > 0) {
            val beforeComma = trimmed.substring(0, commaIdx).trim()
            if (beforeComma.isNotEmpty()) return beforeComma
        }
        val parts = trimmed.split(Regex("\\s+")).filter { it.isNotEmpty() }
        if (parts.isEmpty()) return trimmed
        if (parts.all { p -> p.all(::isCjkChar) }) {
            return parts.joinToString("")
        }
        if (parts.size >= 2) {
            val prev = parts[parts.size - 2].lowercase(Locale.ROOT)
            if (prev in COMPOUND_SURNAME_PREFIXES) {
                return "${parts[parts.size - 2]} ${parts.last()}"
            }
        }
        return parts.last()
    }

    private val COMPOUND_SURNAME_PREFIXES =
        setOf("vaughan", "de", "di", "da", "del", "della", "dos", "das", "du", "la", "le", "saint", "st.")

    fun buildClassicalCreditTiers(
        composer: String?,
        conductors: List<String>,
        soloists: List<String>,
        ensemble: String?,
        year: Int?,
    ): List<String> {
        val cleanComposer = composer?.trim().orEmpty()
        val cleanConductors = conductors.map { it.trim() }.filter { it.isNotEmpty() }
        val cleanSoloists = soloists.map { it.trim() }.filter { it.isNotEmpty() }
        val cleanEnsemble = ensemble?.trim()?.takeIf { it.isNotEmpty() }
        val yearStr = year?.takeIf { it > 0 }?.toString()

        fun joinParts(vararg parts: String?): String =
            parts.mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }.joinToString(" · ")

        fun formatPerformerCredit(cList: List<String>, sList: List<String>): String? {
            val c = cList.takeIf { it.isNotEmpty() }?.joinToString(", ")
            val s = sList.takeIf { it.isNotEmpty() }?.joinToString(", ")
            return when {
                c != null && s != null -> "$c / $s"
                c != null -> c
                s != null -> s
                else -> null
            }
        }

        val perfAll = formatPerformerCredit(cleanConductors, cleanSoloists)
        val firstSolo = cleanSoloists.firstOrNull()?.let { listOf(it) } ?: emptyList()
        val perfFirstSolo = formatPerformerCredit(cleanConductors, firstSolo)
        val conductorOnly = if (cleanConductors.isNotEmpty()) {
            cleanConductors.joinToString(", ")
        } else {
            firstSolo.firstOrNull()
        }
        val abbrEnsemble = cleanEnsemble?.let(::abbreviateEnsemble)
        val compOpt = cleanComposer.takeIf { it.isNotEmpty() }

        val rawTiers = listOf(
            joinParts(compOpt, perfAll, cleanEnsemble, yearStr),
            joinParts(compOpt, perfAll, abbrEnsemble, yearStr),
            joinParts(compOpt, perfFirstSolo, abbrEnsemble, yearStr),
            joinParts(compOpt, conductorOnly, yearStr),
            joinParts(
                cleanComposer.takeIf { it.isNotEmpty() }?.let(::shortSurname),
                conductorOnly?.let(::shortSurname),
                yearStr,
            ),
        )

        val deduped = ArrayList<String>(rawTiers.size)
        for (tier in rawTiers) {
            if (tier.isNotEmpty() && tier !in deduped) {
                deduped.add(tier)
            }
        }
        return deduped
    }

    fun formatClassicalComposerWorkLine(
        composer: String?,
        workTitle: String?,
        fallbackTitle: String? = null,
    ): String {
        val cleanComposer = composer?.trim().orEmpty()
        val cleanWork =
            workTitle?.trim()?.takeIf { it.isNotEmpty() } ?: fallbackTitle?.trim().orEmpty()
        if (cleanComposer.isEmpty()) return cleanWork
        if (cleanWork.isEmpty()) return cleanComposer

        val surname = shortSurname(cleanComposer)
        if (
            startsWithComposerPrefix(cleanWork, cleanComposer) ||
                (surname.isNotEmpty() &&
                    !surname.equals(cleanComposer, ignoreCase = true) &&
                    startsWithComposerPrefix(cleanWork, surname))
        ) {
            return cleanWork
        }
        return "$cleanComposer - $cleanWork"
    }

    private fun startsWithComposerPrefix(work: String, prefix: String): Boolean {
        if (prefix.isEmpty()) return false
        return work.startsWith("$prefix:", ignoreCase = true) ||
            work.startsWith("$prefix：", ignoreCase = true) ||
            work.startsWith("$prefix - ", ignoreCase = true) ||
            work.startsWith("$prefix — ", ignoreCase = true) ||
            work.startsWith("$prefix – ", ignoreCase = true)
    }

    fun buildClassicalPerformerCreditTiers(
        artists: List<String>,
        conductors: List<String>,
        year: Int?,
    ): List<String> {
        val cleanConductors = ArrayList<String>()
        val conductorKeys = HashSet<String>()
        for (raw in conductors) {
            val clean = raw.trim()
            val key = clean.lowercase(Locale.ROOT)
            if (clean.isNotEmpty() && conductorKeys.add(key)) {
                cleanConductors.add(clean)
            }
        }

        val cleanArtists = ArrayList<String>()
        val artistKeys = HashSet<String>()
        for (raw in artists) {
            val clean = raw.trim()
            val key = clean.lowercase(Locale.ROOT)
            if (clean.isNotEmpty() && key !in conductorKeys && artistKeys.add(key)) {
                cleanArtists.add(clean)
            }
        }

        val yearStr = year?.takeIf { it > 0 }?.toString()

        fun joinParts(vararg parts: String?): String =
            parts.mapNotNull { it?.trim()?.takeIf(String::isNotEmpty) }.joinToString(" · ")

        val abbrArtists =
            cleanArtists.map { if (isEnsembleName(it)) abbreviateEnsemble(it) else it }
        val artistsFull = cleanArtists.takeIf { it.isNotEmpty() }?.joinToString(", ")
        val artistsAbbr = abbrArtists.takeIf { it.isNotEmpty() }?.joinToString(", ")
        val firstArtistAbbr = abbrArtists.firstOrNull()
        val conductorsFull = cleanConductors.takeIf { it.isNotEmpty() }?.joinToString(", ")
        val shortArtist =
            cleanArtists.firstOrNull()?.let { first ->
                if (isEnsembleName(first)) abbrArtists.first() else shortSurname(first)
            }
        val shortConductor = cleanConductors.firstOrNull()?.let(::shortSurname)

        val rawTiers =
            listOf(
                joinParts(artistsFull, conductorsFull, yearStr),
                joinParts(artistsAbbr, conductorsFull, yearStr),
                joinParts(firstArtistAbbr, conductorsFull, yearStr),
                joinParts(shortArtist, shortConductor, yearStr),
            )

        val deduped = ArrayList<String>(rawTiers.size)
        for (tier in rawTiers) {
            if (tier.isNotEmpty() && tier !in deduped) {
                deduped.add(tier)
            }
        }
        return deduped
    }

    fun formatClassicalNowPlaying(
        rawTitle: String?,
        explicitWork: String?,
        explicitMovement: String?,
        explicitMovementNumber: Int?,
        album: String?,
        composers: List<String>,
        artists: List<String>,
        conductors: List<String>,
        year: Int?,
    ): ClassicalNowPlayingLines {
        val parsed =
            splitWorkAndMovement(
                rawTitle = rawTitle,
                explicitWork = explicitWork,
                explicitMovement = explicitMovement,
                explicitMovementNumber = explicitMovementNumber,
            )
        val fallbackTitle = rawTitle?.trim().orEmpty()
        val workTitle = parsed.work.ifEmpty { fallbackTitle }
        val movementLine =
            if (parsed.movement.isNotEmpty()) {
                if (parsed.movementNumber != null) {
                    "${formatRomanNumeral(parsed.movementNumber)}. ${parsed.movement}"
                } else {
                    parsed.movement
                }
            } else {
                album?.trim().orEmpty()
            }

        val cleanComposers = ArrayList<String>()
        val seenComposers = HashSet<String>()
        for (raw in composers) {
            val clean = raw.trim()
            val key = clean.lowercase(Locale.ROOT)
            if (clean.isNotEmpty() && seenComposers.add(key)) {
                cleanComposers.add(clean)
            }
        }
        val composerCredit = cleanComposers.joinToString(", ")

        val line1 = formatClassicalComposerWorkLine(composerCredit, workTitle, fallbackTitle)
        val line3Tiers = buildClassicalPerformerCreditTiers(artists, conductors, year)
        return ClassicalNowPlayingLines(
            line1 = line1,
            line2 = movementLine,
            line3Tiers = line3Tiers,
        )
    }
}
