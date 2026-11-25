/*
 * Copyright (c) 2026 Tonewise Project
 * ZuneMobileUiUpgradeTest.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.ui

import com.shawnshi.tonewise.home.classical.ClassicalHierarchyLogic
import com.shawnshi.tonewise.home.collection.CollectionPivot
import com.shawnshi.tonewise.home.quickplay.QuickplayAggregator
import com.shawnshi.tonewise.home.quickplay.QuickplayPinCodec
import com.shawnshi.tonewise.home.quickplay.QuickplayPinKind
import com.shawnshi.tonewise.home.quickplay.QuickplayPinRecord
import com.shawnshi.tonewise.home.quickplay.QuickplayTrackCandidate
import com.shawnshi.tonewise.music.ClassicalWork
import com.shawnshi.tonewise.music.MusicType
import com.shawnshi.tonewise.ui.accent.Accent
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ZuneMobileUiUpgradeTest {
    private fun findRepoRoot(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").canonicalFile
        while (dir != null) {
            if (File(dir, "protocol/schemas/manifest.sql").isFile && File(dir, "android").isDirectory) {
                return dir
            }
            dir = dir.parentFile
        }
        error("Could not locate Tonewise repo root from ${System.getProperty("user.dir")}")
    }

    @Test
    fun zuneColorMath_contrastClampMeetsWcagAaInBothModes() {
        val magentaDarkContrast =
            ZuneColorMath.contrastRatio(ZuneColorMath.DEFAULT_DARK_MAGENTA, ZuneColorMath.BLACK_SURFACE)
        val magentaLightContrast =
            ZuneColorMath.contrastRatio(ZuneColorMath.DEFAULT_LIGHT_MAGENTA, ZuneColorMath.WHITE_SURFACE)
        assertTrue("Dark Zune Magenta contrast >= 4.5 (was $magentaDarkContrast)", magentaDarkContrast >= 4.5)
        assertTrue("Light Zune Magenta contrast >= 4.5 (was $magentaLightContrast)", magentaLightContrast >= 4.5)

        val extremeSeeds =
            intArrayOf(
                0xFF0A0A14.toInt(), // Very dark navy
                0xFFFDFDC8.toInt(), // Pale yellow
                0xFF1E90FF.toInt(), // Vivid blue
                0xFF888888.toInt(), // Neutral gray -> falls back to Zune Magenta
                0xFF3A0020.toInt(), // Deep burgundy
            )
        for (seed in extremeSeeds) {
            val clampedDark = ZuneColorMath.clampAccentForContrast(seed, isDarkSurface = true)
            val clampedLight = ZuneColorMath.clampAccentForContrast(seed, isDarkSurface = false)
            assertTrue(
                "Seed ${Integer.toHexString(seed)} dark clamp contrast >= 4.5",
                ZuneColorMath.contrastRatio(clampedDark, ZuneColorMath.BLACK_SURFACE) >= 4.5,
            )
            assertTrue(
                "Seed ${Integer.toHexString(seed)} light clamp contrast >= 4.5",
                ZuneColorMath.contrastRatio(clampedLight, ZuneColorMath.WHITE_SURFACE) >= 4.5,
            )
        }
    }

    @Test
    fun zuneColorMath_selectVibrantSeedPrefersSaturatedOverGrayAndWhite() {
        val pixels =
            intArrayOf(
                0xFFFFFFFF.toInt(),
                0xFF000000.toInt(),
                0xFF7A7A7A.toInt(),
                0xFFE11D74.toInt(),
                0xFFCCCCCC.toInt(),
            )
        val picked = ZuneColorMath.selectVibrantSeed(pixels)
        assertEquals(0xFFE11D74.toInt(), picked)
        assertNull(ZuneColorMath.selectVibrantSeed(intArrayOf(0xFF000000.toInt(), 0xFF888888.toInt(), 0xFFFFFFFF.toInt())))
    }

    @Test
    fun quickplayPinCodec_roundtripsSpecialCharactersAndHandlesCorruptInput() {
        val pins =
            listOf(
                QuickplayPinRecord(
                    kind = QuickplayPinKind.ALBUM,
                    id = "alb-1",
                    title = "Beethoven: \"Symphony No. 9\"",
                    subtitle = "Karajan \\ Berlin · 1963",
                    pinnedAtMs = 1732500000000L,
                ),
                QuickplayPinRecord(
                    kind = QuickplayPinKind.WORK,
                    id = "beethoven::symphony no. 9",
                    title = "第九交响曲\n第一乐章",
                    subtitle = "Ludwig van Beethoven",
                    pinnedAtMs = 1732500001000L,
                ),
            )
        val encoded = QuickplayPinCodec.serializePins(pins)
        val decoded = QuickplayPinCodec.deserializePins(encoded)
        assertEquals(pins, decoded)
        assertTrue(QuickplayPinCodec.deserializePins("").isEmpty())
        assertTrue(QuickplayPinCodec.deserializePins("{not_an_array").isEmpty())
    }

    @Test
    fun quickplayAggregator_computesHistoryNewestAndRediscoveryCorrectly() {
        val dayMs = 86_400_000L
        val now = 200L * dayMs
        val candidates =
            listOf(
                QuickplayTrackCandidate("s1", "Track 1", "A1", "alb1", "Album Recent", 10L, 5, now - 1L * dayMs),
                QuickplayTrackCandidate("s2", "Track 2", "A1", "alb1", "Album Recent", 12L, 3, now - 2L * dayMs),
                QuickplayTrackCandidate("s3", "Track 3", "A2", "alb2", "Album Newest", 999L, 0, 0L),
                QuickplayTrackCandidate("s4", "Track 4", "A3", "alb3", "Album Dusty", 50L, 12, now - 120L * dayMs),
                QuickplayTrackCandidate("s5", "Track 5", "A4", "alb4", "Album Older Dusty", 40L, 8, now - 150L * dayMs),
            )
        val sections = QuickplayAggregator.computeSections(candidates, nowMs = now, limit = 12)
        assertEquals(listOf("alb1", "alb3", "alb4"), sections.history.map { it.albumUid })
        assertEquals("alb2", sections.newlyAdded.first().albumUid)
        assertEquals(listOf("alb4", "alb3"), sections.rediscovery.map { it.albumUid })
    }

    @Test
    fun classicalHierarchyLogic_groupsSortsAndExtractsInitials() {
        val works =
            listOf(
                ClassicalWork("k1", "Symphony No. 5", "Ludwig van Beethoven", emptyList()),
                ClassicalWork("k2", "Goldberg Variations", "Johann Sebastian Bach", emptyList()),
                ClassicalWork("k3", "梁祝小提琴协奏曲", "何占豪", emptyList()),
            )
        val groups = ClassicalHierarchyLogic.groupByComposer(works)
        assertEquals(listOf("Johann Sebastian Bach", "Ludwig van Beethoven", "何占豪"), groups.map { it.composerName })
        assertEquals(setOf("J", "L", "#"), ClassicalHierarchyLogic.extractAvailableInitials(groups))
    }

    @Test
    fun collectionPivot_preservesAllNineZuneSubPivots() {
        assertEquals(
            listOf("albums", "tracks", "artists", "classical", "composers", "conductors", "genres", "playlists", "scenes"),
            CollectionPivot.entries.map { it.label },
        )
    }

    @Test
    fun collectionPivot_respectsUserTabOrderAndVisibility_whilePreservingClassical() {
        val customOrder = listOf(MusicType.PLAYLISTS, MusicType.ARTISTS, MusicType.ALBUMS)
        val resolved = CollectionPivot.resolveVisiblePivots(customOrder)
        assertEquals(
            listOf(
                CollectionPivot.PLAYLISTS,
                CollectionPivot.ARTISTS,
                CollectionPivot.CLASSICAL,
                CollectionPivot.ALBUMS,
            ),
            resolved,
        )

        val noArtists = listOf(MusicType.SCENES, MusicType.COMPOSERS)
        assertEquals(
            listOf(CollectionPivot.SCENES, CollectionPivot.CLASSICAL, CollectionPivot.COMPOSERS),
            CollectionPivot.resolveVisiblePivots(noArtists),
        )
    }

    @Test
    fun accent_preservesDynamicAtIndex16_andDefaultsToZuneMagentaAt17() {
        assertEquals(16, Accent.INDEX_DYNAMIC)
        assertEquals(17, Accent.INDEX_ZUNE_MAGENTA)
        assertEquals(17, Accent.DEFAULT)
        assertEquals(17, Accent.from(17).index)
        assertEquals(17, Accent.from(999).index)
    }

    @Test
    fun sourceAndLayoutGuards_verifyZuneDefaultsDrawerAndMosaicBackdrop() {
        val repoRoot = findRepoRoot()
        val uiSettingsSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/ui/UISettings.kt")
                .readText(Charsets.UTF_8)
        assertTrue(uiSettingsSrc.contains("getBoolean(getString(R.string.set_key_black_theme), true)"))
        assertTrue(uiSettingsSrc.contains("getBoolean(getString(R.string.set_key_round_mode), false)"))
        assertTrue(uiSettingsSrc.contains("set_key_dynamic_cover_accent"))
        assertTrue(uiSettingsSrc.contains("Accent.INDEX_DYNAMIC"))

        val mainFragSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/MainFragment.kt")
                .readText(Charsets.UTF_8)
        assertTrue(mainFragSrc.contains("homeModel.panoramaPage.value == 1"))
        assertTrue(mainFragSrc.contains("homeModel.collectionPivot.value == CollectionPivot.PLAYLISTS"))

        val playbackBarXml =
            File(repoRoot, "android/app/src/main/res/layout/fragment_playback_bar.xml")
                .readText(Charsets.UTF_8)
        assertTrue(playbackBarXml.contains("playback_appbar_more"))
        assertTrue(playbackBarXml.contains("SizeOverlay.Material3Expressive.Button.IconButton.Small"))

        val mosaicSrc =
            File(
                    repoRoot,
                    "android/app/src/main/java/com/shawnshi/tonewise/playback/ui/ZuneMosaicBackdropView.kt",
                )
                .readText(Charsets.UTF_8)
        assertTrue(mosaicSrc.contains("MaterialColors.getColor"))

        val panelSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackPanelFragment.kt")
                .readText(Charsets.UTF_8)
        assertTrue(panelSrc.contains("android.R.attr.state_checked"))

        val menuImplSrc =
            File(
                    repoRoot,
                    "android/app/src/main/java/com/shawnshi/tonewise/list/menu/MenuDialogFragmentImpl.kt",
                )
                .readText(Charsets.UTF_8)
        assertTrue(menuImplSrc.contains("lbl_unpin_quickplay"))

        val quickplaySrc =
            File(
                    repoRoot,
                    "android/app/src/main/java/com/shawnshi/tonewise/home/quickplay/QuickplayFragment.kt",
                )
                .readText(Charsets.UTF_8)
        assertTrue(quickplaySrc.contains("onLongPressSlot"))

        val drawerSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/ui/ZuneAppBarDrawerSheet.kt")
                .readText(Charsets.UTF_8)
        assertTrue(drawerSrc.contains("ScenesAndSyncDialog"))
        assertTrue(drawerSrc.contains("FocusFilterDialog"))
        assertTrue(drawerSrc.contains("DeskClockDialog"))
        assertTrue(drawerSrc.contains("lbl_rescan_started"))

        val panelVariants =
            listOf(
                "android/app/src/main/res/layout/fragment_playback_panel.xml",
                "android/app/src/main/res/layout-h360dp/fragment_playback_panel.xml",
                "android/app/src/main/res/layout-h520dp/fragment_playback_panel.xml",
            )
        for (rel in panelVariants) {
            val xml = File(repoRoot, rel).readText(Charsets.UTF_8)
            assertNotNull(xml)
            assertTrue("$rel must contain ZuneMosaicBackdropView", xml.contains("ZuneMosaicBackdropView"))
            assertFalse("panel variant missing id", !xml.contains("playback_mosaic_backdrop"))
        }
    }
}
