package com.shawnshi.tonewise.music

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test

class ClassicalMetadataEngineTest {
    private fun findRepoFile(relPath: String): File {
        var dir: File? = File(".").canonicalFile
        while (dir != null) {
            val candidate = File(dir, relPath)
            if (candidate.isFile) return candidate
            dir = dir.parentFile
        }
        error("Could not locate $relPath from ${File(".").canonicalPath}")
    }

    @Test
    fun resourceDictionaryMatchesProtocolFile() {
        val repoDict = findRepoFile("protocol/dictionaries/composer_metadata.json").readText(Charsets.UTF_8)
        val resStream = ClassicalMetadataEngine::class.java.getResourceAsStream("/tonewise/composer_metadata.json")
        assertNotNull("Android /tonewise/composer_metadata.json resource must exist", resStream)
        val resText = resStream!!.bufferedReader(Charsets.UTF_8).use { it.readText() }
        assertEquals(repoDict.replace("\r\n", "\n"), resText.replace("\r\n", "\n"))
    }

    @Test
    fun passesAllClassicalGoldenFixtures() {
        val text = findRepoFile("protocol/fixtures/classical_golden.json").readText(Charsets.UTF_8)
        val root = MiniJson.parseObject(text)

        @Suppress("UNCHECKED_CAST")
        for (obj in root["is_classical_cases"] as List<Map<String, Any?>>) {
            val name = obj["name"] as String
            val actual = ClassicalMetadataEngine.isClassicalTrack(
                genre = obj["genre"] as? String,
                work = obj["work"] as? String,
                composer = obj["composer"] as? String,
                conductor = obj["conductor"] as? String,
                title = obj["title"] as? String,
            )
            assertEquals("is_classical case failed: $name", obj["expected"] as Boolean, actual)
        }

        @Suppress("UNCHECKED_CAST")
        for (obj in root["split_work_cases"] as List<Map<String, Any?>>) {
            val name = obj["name"] as String
            val actual = ClassicalMetadataEngine.splitWorkAndMovement(
                rawTitle = obj["raw_title"] as? String,
                explicitWork = obj["explicit_work"] as? String,
                explicitMovement = obj["explicit_movement"] as? String,
                explicitMovementNumber = (obj["explicit_mvt_num"] as? Number)?.toInt(),
            )
            assertEquals("work failed: $name", obj["expected_work"] as String, actual.work)
            assertEquals("movement failed: $name", obj["expected_movement"] as String, actual.movement)
            assertEquals("mvt_num failed: $name", (obj["expected_mvt_num"] as? Number)?.toInt(), actual.movementNumber)
        }

        @Suppress("UNCHECKED_CAST")
        for (obj in root["era_cases"] as List<Map<String, Any?>>) {
            val actual = ClassicalMetadataEngine.classifyComposerEra(
                obj["composer"] as? String,
                obj["genre"] as? String,
            )
            assertEquals("era failed for ${obj["composer"]}", obj["expected"] as? String, actual)
        }

        @Suppress("UNCHECKED_CAST")
        for (obj in root["form_cases"] as List<Map<String, Any?>>) {
            val actual = ClassicalMetadataEngine.classifyWorkForm(
                obj["work"] as? String,
                obj["genre"] as? String,
            )
            assertEquals("form failed for ${obj["work"]}", obj["expected"] as String, actual)
        }

        @Suppress("UNCHECKED_CAST")
        for (obj in root["performer_separation_cases"] as List<Map<String, Any?>>) {
            val actual = ClassicalMetadataEngine.separatePerformers(
                artists = (obj["artists"] as List<*>).map { it as String },
                albumArtist = obj["album_artist"] as? String,
                conductors = (obj["conductors"] as List<*>).map { it as String },
                explicitOrchestra = obj["explicit_orchestra"] as? String,
                explicitSoloists = (obj["explicit_soloists"] as List<*>).map { it as String },
            )
            assertEquals(obj["expected_soloists"], actual.soloists)
            assertEquals(obj["expected_ensemble"], actual.ensemble)
        }

        @Suppress("UNCHECKED_CAST")
        for (obj in root["credit_tier_cases"] as List<Map<String, Any?>>) {
            val actual = ClassicalMetadataEngine.buildClassicalCreditTiers(
                composer = obj["composer"] as? String,
                conductors = (obj["conductors"] as List<*>).map { it as String },
                soloists = (obj["soloists"] as List<*>).map { it as String },
                ensemble = obj["ensemble"] as? String,
                year = (obj["year"] as? Number)?.toInt(),
            )
            assertEquals(obj["expected_tiers"], actual)
        }
    }

    @Test
    fun formatClassicalNowPlaying_usesComposerWork_Movement_ArtistsConductorYear() {
        val sym =
            ClassicalMetadataEngine.formatClassicalNowPlaying(
                rawTitle = "Symphony No. 9 in D minor, Op. 125: IV. Presto",
                explicitWork = "Symphony No. 9 in D minor, Op. 125",
                explicitMovement = "Presto",
                explicitMovementNumber = 4,
                album = "Beethoven 9",
                composers = listOf("Ludwig van Beethoven"),
                artists = listOf("Berliner Philharmoniker", "Herbert von Karajan"),
                conductors = listOf("Herbert von Karajan"),
                year = 1963,
            )
        assertEquals("Ludwig van Beethoven - Symphony No. 9 in D minor, Op. 125", sym.line1)
        assertEquals("IV. Presto", sym.line2)
        assertEquals(
            listOf(
                "Berliner Philharmoniker · Herbert von Karajan · 1963",
                "BPO · Herbert von Karajan · 1963",
                "BPO · Karajan · 1963",
            ),
            listOf(sym.line3Tiers[0], sym.line3Tiers[1], sym.line3Tiers.last()),
        )

        val solo =
            ClassicalMetadataEngine.formatClassicalNowPlaying(
                rawTitle = "Symphonic Etudes, Op. 13: Thema. Andante",
                explicitWork = "Symphonic Etudes, Op. 13",
                explicitMovement = "Thema. Andante",
                explicitMovementNumber = null,
                album = "Pogorelich Complete Recordings",
                composers = listOf("Robert Schumann"),
                artists = listOf("Ivo Pogorelich"),
                conductors = emptyList(),
                year = 2015,
            )
        assertEquals("Robert Schumann - Symphonic Etudes, Op. 13", solo.line1)
        assertEquals("Thema. Andante", solo.line2)
        assertEquals("Ivo Pogorelich · 2015", solo.line3Tiers[0])
        assertEquals(
            "Beethoven: Symphony No. 5",
            ClassicalMetadataEngine.formatClassicalComposerWorkLine(
                "Ludwig van Beethoven",
                "Beethoven: Symphony No. 5",
            ),
        )
    }

    private class MiniJson(private val src: String) {
        private var pos = 0

        companion object {
            @Suppress("UNCHECKED_CAST")
            fun parseObject(src: String): Map<String, Any?> = MiniJson(src).readValue() as Map<String, Any?>
        }

        private fun skipWs() {
            while (pos < src.length && src[pos].isWhitespace()) pos++
        }

        private fun readValue(): Any? {
            skipWs()
            return when (src[pos]) {
                '{' -> readObject()
                '[' -> readArray()
                '"' -> readString()
                't' -> { pos += 4; true }
                'f' -> { pos += 5; false }
                'n' -> { pos += 4; null }
                else -> readNumber()
            }
        }

        private fun readObject(): Map<String, Any?> {
            pos++ // '{'
            val map = LinkedHashMap<String, Any?>()
            skipWs()
            if (src[pos] == '}') { pos++; return map }
            while (pos < src.length) {
                skipWs()
                val key = readString()
                skipWs()
                pos++ // ':'
                val value = readValue()
                map[key] = value
                skipWs()
                if (src[pos] == '}') { pos++; break }
                if (src[pos] == ',') pos++
            }
            return map
        }

        private fun readArray(): List<Any?> {
            pos++ // '['
            val list = ArrayList<Any?>()
            skipWs()
            if (src[pos] == ']') { pos++; return list }
            while (pos < src.length) {
                list.add(readValue())
                skipWs()
                if (src[pos] == ']') { pos++; break }
                if (src[pos] == ',') pos++
            }
            return list
        }

        private fun readString(): String {
            pos++ // '"'
            val sb = StringBuilder()
            while (pos < src.length) {
                val c = src[pos++]
                if (c == '"') break
                if (c == '\\' && pos < src.length) {
                    val esc = src[pos++]
                    when (esc) {
                        '"', '\\', '/' -> sb.append(esc)
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            sb.append(src.substring(pos, pos + 4).toInt(16).toChar())
                            pos += 4
                        }
                        else -> sb.append(esc)
                    }
                } else {
                    sb.append(c)
                }
            }
            return sb.toString()
        }

        private fun readNumber(): Number {
            val start = pos
            while (pos < src.length && (src[pos].isDigit() || src[pos] == '-' || src[pos] == '.')) pos++
            val token = src.substring(start, pos)
            return if (token.contains('.')) token.toDouble() else token.toLong()
        }
    }
}
