package com.shawnshi.tonewise.sync

import com.shawnshi.tonewise.music.AndroidPlaylistTrackMatcher
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class NonChinesePlaylistMatchingTest {
    @Test
    fun `matches classical metadata, structural guards, whitelist, and Strategy 2 soft fallback`() {
        val lib =
            listOf(
                // Standalone workName when movementName is empty
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_brahms",
                    title = "Klarinettenquintett in h-Moll, op. 115: I. Allegro",
                    artists = listOf("Johannes Brahms"),
                    composers = listOf("Johannes Brahms"),
                    workName = "Quintet for Clarinet, 2 Violins, Viola, and Cello in B minor, op. 115: I. Allegro",
                ),
                // Conductor disambiguation + colon/movement splitting
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_sym5_bern",
                    title = "I. Allegro con brio",
                    artists = listOf("Wiener Philharmoniker"),
                    composers = listOf("Ludwig van Beethoven"),
                    conductors = listOf("Leonard Bernstein"),
                    workName = "Symphony No. 5 in C Minor",
                    movementName = "I. Allegro con brio",
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_sym5_kara",
                    title = "I. Allegro con brio",
                    artists = listOf("Berliner Philharmoniker"),
                    composers = listOf("Ludwig van Beethoven"),
                    conductors = listOf("Herbert von Karajan"),
                    workName = "Symphony No. 5 in C Minor",
                    movementName = "I. Allegro con brio",
                ),
                // Structural guard & initial+surname + fuzzy Latin transliteration
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_noct_9_2",
                    title = "Nocturne Op. 9 No. 2",
                    artists = listOf("Arthur Rubinstein"),
                    composers = listOf("Frederic Chopin"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_bwv_1007",
                    title = "Cello Suite No. 1 BWV 1007",
                    artists = listOf("Yo-Yo Ma"),
                    composers = listOf("Johann Sebastian Bach"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_rach",
                    title = "Piano Concerto No. 2",
                    artists = listOf("Sergei Rachmaninoff"),
                    composers = listOf("Sergei Rachmaninoff"),
                ),
                // Cyrillic composer artist tag
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_tchaik",
                    title = "The Swan Lake (Suite), Op. 20a: I. Scene",
                    artists = listOf("Пётр Ильич Чайковский"),
                ),
                // Strategy 1 OFF guard
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_bach_gould",
                    title = "Partita No. 1 in B-Flat Major, BWV 825: I. Praeludium",
                    artists = listOf("Glenn Gould"),
                    composers = listOf("Johann Sebastian Bach"),
                ),
                // Protected whitelist compound names
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "wl_simon",
                    title = "The Sound of Silence",
                    artists = listOf("Simon & Garfunkel"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "wl_acdc",
                    title = "Back In Black",
                    artists = listOf("AC/DC"),
                ),
                // Single-word prefix & foreign edition & bilingual Japanese dash split
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_sunflower",
                    title = "Sunflower",
                    artists = listOf("Post Malone", "Swae Lee"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_bailando",
                    title = "Bailando",
                    artists = listOf("Enrique Iglesias"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_bolero",
                    title = "Boléro, M. 81",
                    artists = listOf("Maurice Ravel"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "jp_peace",
                    title = "ピースサイン",
                    artists = listOf("米津玄師"),
                ),
                // Strategy 2 soft fallback + album-remix + preference for studio original
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_despacito_rmx",
                    title = "Despacito (remix)",
                    artists = listOf("Luis Fonsi", "Daddy Yankee", "Justin Bieber"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_someday_unp",
                    title = "Someday (MTV Unplugged)",
                    artists = listOf("Mariah Carey"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_calma_alb",
                    title = "Calma",
                    artists = listOf("Pedro Capó"),
                    album = "Calma (Alan Walker Remix)",
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_dual_studio",
                    title = "Closer",
                    artists = listOf("The Chainsmokers", "Halsey"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_dual_rmx",
                    title = "Closer (R3hab Remix)",
                    artists = listOf("The Chainsmokers", "Halsey"),
                ),
                // Derivative safety guards
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_ibiza_studio",
                    title = "I Took A Pill In Ibiza",
                    artists = listOf("Mike Posner"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_hello_studio",
                    title = "Hello",
                    artists = listOf("Adele"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_hello_acoustic",
                    title = "Hello (Acoustic Version)",
                    artists = listOf("Adele"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "pop_coldplay_inst",
                    title = "Adventure of a Lifetime (instrumental)",
                    artists = listOf("Coldplay"),
                ),
            )

        val matcher = AndroidPlaylistTrackMatcher(lib) { it }

        assertEquals("cls_brahms", matcher.match("Clarinet Quintet in B minor, Op. 115 I. Allegro", "Brahms")?.uid)
        assertEquals("cls_sym5_kara", matcher.match("Beethoven: Symphony No. 5 in C Minor - I. Allegro con brio", "Ludwig van Beethoven, Herbert von Karajan")?.uid)
        assertEquals("cls_noct_9_2", matcher.match("Nocturne in E-Flat Major, Op. 9, No. 2", "Frederic Chopin")?.uid)
        assertNull(matcher.match("Nocturne Op. 9 No. 1", "Frederic Chopin"))
        assertEquals("cls_bwv_1007", matcher.match("Cello Suite No.1 BWV1007", "J.S. Bach")?.uid)
        assertNull(matcher.match("Cello Suite No. 1 BWV 1007", "C.P.E. Bach"))
        assertEquals("cls_rach", matcher.match("Piano Concerto No. 2", "Sergei Rachmaninov")?.uid)
        assertEquals("cls_tchaik", matcher.match("Swan Lake (Suite), Op. 20a: I. Scene", "Tchaikovsky")?.uid)
        // Strategy 1 OFF
        assertNull(matcher.match("J.S. Bach: Partita No. 1 in B-Flat Major, BWV 825: I. Praeludium", "Dinu Lipatti"))

        // Whitelist protection
        assertNull(matcher.match("The Sound of Silence", "Paul Simon"))
        assertEquals("wl_simon", matcher.match("The Sound of Silence", "Simon & Garfunkel")?.uid)
        assertEquals("wl_acdc", matcher.match("Back In Black", "AC/DC")?.uid)

        // Single-word prefix & Japanese dash split
        assertEquals("pop_sunflower", matcher.match("Sunflower - Spider-Man: Into the Spider-Verse", "Post Malone & Swae Lee")?.uid)
        assertEquals("pop_bailando", matcher.match("Bailando - Spanish Version", "Enrique Iglesias, Descemer Bueno")?.uid)
        assertEquals("cls_bolero", matcher.match("Boléro", "Ravel")?.uid)
        assertEquals("jp_peace", matcher.match("ピースサイン - Peace Sign", "米津玄師")?.uid)

        // Strategy 2 soft fallback + preference + guards
        assertEquals("pop_despacito_rmx", matcher.match("Despacito", "Luis Fonsi & Daddy Yankee Featuring Justin Bieber")?.uid)
        assertEquals("pop_someday_unp", matcher.match("Someday", "Mariah Carey")?.uid)
        assertEquals("pop_calma_alb", matcher.match("Calma - Remix", "Pedro Capó, Farruko")?.uid)
        assertEquals("pop_dual_studio", matcher.match("Closer", "The Chainsmokers")?.uid)
        assertEquals("pop_hello_acoustic", matcher.match("Hello (Acoustic Version)", "Adele")?.uid)
        assertNull(matcher.match("I Took A Pill In Ibiza - Seeb Remix", "Mike Posner, Seeb"))
        assertNull(matcher.match("Adventure Of A Lifetime (Official Video)", "Coldplay"))
    }

    @Test
    fun `rejects cross-work classical collisions via generic movement and tempo labels`() {
        val lib =
            listOf(
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "haydn_45",
                    title = "Haydn:Symphony No. 45 in F Sharp Minor \"Farewell\", Hob. 1/45 - II. Adagio",
                    artists = listOf("Leningrad Chamber Orchestra"),
                    composers = listOf("Joseph Haydn"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "lalo_cello",
                    title = "Cello Concerto in D minor: I. Prélude. Lento - Allegro maestoso (Live)",
                    artists = listOf("Jacqueline Du Pré"),
                    composers = listOf("Édouard Lalo"),
                    conductors = listOf("Daniel Barenboim"),
                    workName = "Concerto pour violoncelle et orchestre en ré majeur : I. Allegro",
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "bizet_pearls",
                    title = "Les Pêcheurs de perles: Prélude",
                    artists = listOf("Georges Bizet", "Pierrette Alarie"),
                    composers = listOf("Georges Bizet"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "tchaik_swan",
                    title = "Swan Lake Act IV No. 26 Scène",
                    artists = listOf("Пётр Ильич Чайковский"),
                    composers = listOf("Pyotr Ilyich Tchaikovsky"),
                    movementName = "Allegro non troppo",
                ),
            )

        val matcher = AndroidPlaylistTrackMatcher(lib) { it }

        assertNull(
            matcher.match(
                "Haydn: Cello Concerto No. 1 in C Major, Hob. VIIb:1: II. Adagio",
                "Mstislav Rostropovich, English Chamber Orchestra",
            )
        )
        assertNull(
            matcher.match(
                "Lalo: Symphonie espagnole in D Minor, Op. 21: I. Allegro non troppo",
                "Itzhak Perlman, Daniel Barenboim, Orchestre de Paris",
            )
        )
        assertNull(
            matcher.match(
                "Carmen: Prelude. Allegro giocoso - Andante moderato",
                "Georges Bizet, Herbert von Karajan",
            )
        )
        assertNull(matcher.match("Allegro non troppo", "Tchaikovsky"))
    }

    @Test
    fun `matches soundtrack albumArtist, shinjitai aliases, merged canonical json, collectives, and guards P2 covers`() {
        val lib =
            listOf(
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "tsfh_victory",
                    title = "Victory",
                    artists = listOf("Thomas Bergersen"),
                    album = "Battlecry",
                    albumArtists = listOf("Two Steps From Hell"),
                    genres = listOf("Soundtrack"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "jh_summer",
                    title = "Summer",
                    artists = listOf("久石譲"),
                    album = "菊次郎の夏",
                    albumArtists = listOf("久石譲"),
                    genres = listOf("Soundtrack"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "jp_wada",
                    title = "想いの果てにⅡ",
                    artists = listOf("和田薫"),
                    album = "犬夜叉: 音楽篇",
                    genres = listOf("Soundtrack"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "su_yumeji",
                    title = "Yumeji’s Theme / Li‐zhen’s Dialogue",
                    artists = listOf("梅林茂"),
                    album = "In the Mood for Love",
                    albumArtists = listOf("Various Artists"),
                    genres = listOf("Soundtrack"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "imm_romeo",
                    title = "Tales of the Electric Romeo",
                    artists = listOf("Immediate Music"),
                    album = "Trailerhead: Triumph",
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "arm_khach",
                    title = "Sabre Dance",
                    artists = listOf("Արամ Խաչատրյան"),
                    album = "Gayane",
                    genres = listOf("Classical"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "piano_java",
                    title = "La Javanaise",
                    artists = listOf("Khatia Buniatishvili"),
                    album = "Labyrinth",
                    composers = listOf("Serge Gainsbourg"),
                    workName = "La Javanaise",
                    genres = listOf("Classical"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "cls_elgar",
                    title = "Cello Concerto in E minor, Op. 85: I. Adagio",
                    artists = listOf("Jacqueline du Pré"),
                    composers = listOf("Edward Elgar"),
                    genres = listOf("Classical"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "faye_live",
                    title = "我願意",
                    artists = listOf("王菲"),
                    album = "菲比尋常Live!",
                    genres = listOf("Pop"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "faye_studio",
                    title = "我愿意",
                    artists = listOf("王菲"),
                    album = "迷",
                    genres = listOf("Pop"),
                ),
                AndroidPlaylistTrackMatcher.TrackRef(
                    uid = "solo_goren",
                    title = "Yoav Solo Cue",
                    artists = listOf("Yoav Goren"),
                    album = "Solo",
                    genres = listOf("Soundtrack"),
                ),
            )

        val matcher = AndroidPlaylistTrackMatcher(lib) { it }

        assertEquals("tsfh_victory", matcher.match("Victory", "Two Steps From Hell")?.uid)
        assertEquals("jh_summer", matcher.match("Summer", "Joe Hisaishi")?.uid)
        assertEquals("jp_wada", matcher.match("想いの果てにⅡ", "和田薰")?.uid)
        assertEquals("su_yumeji", matcher.match("Yumeji's Theme", "Shigeru Umebayashi")?.uid)
        assertEquals("imm_romeo", matcher.match("Electric Romeo", "Immediate Music")?.uid)
        assertEquals("arm_khach", matcher.match("Sabre Dance", "Aram Khachaturian")?.uid)
        assertNull(matcher.match("La Javanaise", "Serge Gainsbourg"))
        assertEquals("cls_elgar", matcher.match("Cello Concerto in E minor, Op. 85: I. Adagio", "Edward Elgar")?.uid)
        assertEquals("faye_studio", matcher.match("我愿意", "王菲")?.uid)
        assertNull(matcher.match("Yoav Solo Cue", "Jeffrey Fayman"))
    }
}
