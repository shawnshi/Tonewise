package com.shawnshi.tonewise.sync

import com.shawnshi.tonewise.music.AndroidPlaylistTrackMatcher
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ChinesePlaylistMatchingTest {
    private val sampleLibrary =
        listOf(
            AndroidPlaylistTrackMatcher.TrackRef(
                uid = "u1",
                title = "晴天",
                artists = listOf("周杰伦"),
                album = "叶惠美",
                durationMs = 269_000L,
            ),
            AndroidPlaylistTrackMatcher.TrackRef(
                uid = "u2",
                title = "十年",
                artists = listOf("陈奕迅"),
                album = "黑白灰",
                durationMs = 205_000L,
            ),
            AndroidPlaylistTrackMatcher.TrackRef(
                uid = "u3",
                title = "红豆",
                artists = listOf("王菲"),
                album = "唱游",
                durationMs = 259_000L,
            ),
            AndroidPlaylistTrackMatcher.TrackRef(
                uid = "u4",
                title = "平凡之路",
                artists = listOf("朴树"),
                album = "猎户星座",
                durationMs = 301_000L,
            ),
            AndroidPlaylistTrackMatcher.TrackRef(
                uid = "u5",
                title = "珊瑚海",
                artists = listOf("周杰伦", "梁心颐"),
                album = "十一月的萧邦",
                durationMs = 256_000L,
            ),
        )

    @Test
    fun `matches bilingual artists, traditional variants, language suffixes, and swapped order`() {
        val matcher = AndroidPlaylistTrackMatcher(sampleLibrary) { it }

        assertEquals("u1", matcher.match("晴天 (Sunny Day)", "周杰伦 (Jay Chou)")?.uid)
        assertEquals("u2", matcher.match("十年 (国语)", "Eason Chan")?.uid)
        assertEquals("u3", matcher.match("紅豆", "Faye Wong")?.uid)
        assertEquals("u4", matcher.match("平凡之途", "朴树")?.uid)
        assertEquals("u5", matcher.match("珊瑚海", "周杰伦×梁心颐")?.uid)
        // Swapped Title - Artist order
        assertEquals("u1", matcher.match("周杰伦", "晴天")?.uid)
        // Filename hint fallback
        assertEquals("u2", matcher.matchByFilenameHint("02. 十年 - 陈奕迅.flac")?.uid)
    }

    @Test
    fun `rejects Chinese karaoke, instrumental, and cover derivatives`() {
        val matcher = AndroidPlaylistTrackMatcher(sampleLibrary) { it }

        assertNull(matcher.match("晴天 (伴奏)", "周杰伦"))
        assertNull(matcher.match("晴天 (翻唱)", "周杰伦"))
        assertNull(matcher.match("十年 (纯音乐)", "陈奕迅"))
    }

    @Test
    fun `matches Pinyin titles, CJK slash medleys, disc-track prefixes, and Chinese group tokens`() {
        val lib =
            listOf(
                AndroidPlaylistTrackMatcher.TrackRef("p1", "Hua Xin", listOf("周華健")),
                AndroidPlaylistTrackMatcher.TrackRef("p2", "Wéi Yī", listOf("王力宏")),
                AndroidPlaylistTrackMatcher.TrackRef("p3", "Ai Hen Jian Dan", listOf("陶喆")),
                AndroidPlaylistTrackMatcher.TrackRef("p4", "Chi Xin Jue Dui", listOf("李聖傑")),
                AndroidPlaylistTrackMatcher.TrackRef("p5", "唯一", listOf("五月天")),
                AndroidPlaylistTrackMatcher.TrackRef("m1", "償還 / 紅豆", listOf("王菲"), "菲比尋常Live!"),
                AndroidPlaylistTrackMatcher.TrackRef("m2", "Chang Huan", listOf("那英")),
                AndroidPlaylistTrackMatcher.TrackRef("d1", "我要我們在一起", listOf("范曉萱")),
                AndroidPlaylistTrackMatcher.TrackRef("a1", "如風往事", listOf("李宗盛與盧冠廷")),
                AndroidPlaylistTrackMatcher.TrackRef("a2", "Don't Break My Heart", listOf("黑豹")),
                AndroidPlaylistTrackMatcher.TrackRef("a3", "海阔天空", listOf("信")),
                AndroidPlaylistTrackMatcher.TrackRef("n1", "AI", listOf("王菲")),
            )

        val matcher = AndroidPlaylistTrackMatcher(lib) { it }

        // P0 #1: Hanzi <-> Pinyin (plain and tone-marked) with strict artist gate
        assertEquals("p1", matcher.match("花心", "周华健")?.uid)
        assertEquals("p2", matcher.match("唯一", "王力宏")?.uid)
        assertEquals("p3", matcher.match("爱，很简单", "陶喆")?.uid)
        assertEquals("p4", matcher.match("痴心绝对", "李圣杰")?.uid)
        // Reverse Pinyin seed -> Hanzi track
        assertEquals("p5", matcher.match("Wei Yi", "五月天")?.uid)
        // Negative safety: wrong artist cannot fall back, and 1-char Hanzi "爱" must NOT hit 2-char "AI"
        assertNull(matcher.match("唯一", "张学友"))
        assertNull(matcher.match("爱", "王菲"))

        // P0 #2: CJK/CJK slash medley + polyphone 还 -> huan
        assertEquals("m1", matcher.match("红豆", "王菲")?.uid)
        assertEquals("m2", matcher.match("偿还", "那英")?.uid)

        // P0 #3: Disc-track prefix (1-01 ) on title and filename hint
        assertEquals("d1", matcher.match("1-01 我要我們在一起", null)?.uid)
        assertEquals("d1", matcher.matchByFilenameHint("1-01 我要我們在一起.flac")?.uid)

        // P1: Chinese conjunction '与' + band suffix stripping (and protecting 1-char '信')
        assertEquals("a1", matcher.match("如风往事", "李宗盛")?.uid)
        assertEquals("a1", matcher.match("如风往事", "卢冠廷")?.uid)
        assertEquals("a2", matcher.match("Don't Break My Heart", "黑豹乐队")?.uid)
        assertNull(matcher.match("海阔天空", "信乐团"))
    }
}
