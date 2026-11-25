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
import com.shawnshi.tonewise.playback.ClassicalPlaybackActions
import com.shawnshi.tonewise.sync.AutoDjEngine
import com.shawnshi.tonewise.sync.SceneRuleEvaluator
import com.shawnshi.tonewise.sync.SessionSlotItem
import com.shawnshi.tonewise.sync.SessionSlotStore
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
        assertFalse(quickplaySrc.contains("sec_slots"))
        assertTrue(quickplaySrc.contains("sections.newlyAdded.chunked(2)"))

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

        assertTrue(panelSrc.contains("fmt_classical_view_composer"))
        assertTrue(panelSrc.contains("fmt_classical_view_conductor"))
        assertTrue(panelSrc.contains("detailModel.showComposer"))
        assertTrue(panelSrc.contains("detailModel.showConductor"))
    }

    @Test
    fun classicalPlaybackActions_identifiesClassicalTracksAndPrimaryCredits() {
        assertTrue(ClassicalPlaybackActions.isClassicalGenreName("Classical"))
        assertTrue(ClassicalPlaybackActions.isClassicalGenreName("古典交响"))
        assertFalse(ClassicalPlaybackActions.isClassicalGenreName("Indie Pop"))

        assertTrue(ClassicalPlaybackActions.isClassicalTrack("Symphony No. 9", emptyList(), emptyList()))
        assertTrue(ClassicalPlaybackActions.isClassicalTrack(null, listOf("Baroque"), emptyList()))
        assertTrue(ClassicalPlaybackActions.isClassicalTrack(null, emptyList(), listOf("Herbert von Karajan")))
        assertFalse(ClassicalPlaybackActions.isClassicalTrack(null, listOf("Pop"), emptyList()))

        assertEquals(
            "Ludwig van Beethoven",
            ClassicalPlaybackActions.resolvePrimaryCredit(listOf("", "  Ludwig van Beethoven  ", "Mozart")),
        )
        assertNull(ClassicalPlaybackActions.resolvePrimaryCredit(listOf("", "   ")))
        // Explicit non-classical genre exclusion parity with PC TrackItem.IsExplicitNonClassicalGenre
        assertFalse(ClassicalPlaybackActions.isClassicalTrack(null, listOf("Pop"), listOf("Session Conductor")))
        assertFalse(ClassicalPlaybackActions.isClassicalTrack("Main Theme", listOf("Soundtrack"), emptyList()))
        assertTrue(ClassicalPlaybackActions.isClassicalTrack("Symphony No. 5", listOf("Soundtrack"), emptyList()))
    }

    @Test
    fun batchAAndB_securityAndParityGuards_holdAcrossSourcesAndLogic() {
        val repoRoot = findRepoRoot()

        // P0-C3: Valid 90-day rediscovery SceneCard JSON schema
        val rediscJson = SceneRuleEvaluator.REDISCOVERY_90D_SCENE_JSON
        assertTrue(rediscJson.contains("\"title\":"))
        assertTrue(rediscJson.contains("\"updated_at_ms\":"))
        assertTrue(rediscJson.contains("\"values\":[90]"))

        // P1: Unlove rating normalized to "0" across PlaybackPanelFragment & QuickTagDialog
        val panelSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackPanelFragment.kt")
                .readText(Charsets.UTF_8)
        assertFalse("PlaybackPanelFragment must unlove to rating=0, not 3", panelSrc.contains("else \"3\""))
        assertFalse("Callbacks must use isSongClassical", panelSrc.contains("isClassical = !"))

        val quickTagSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/sync/QuickTagDialog.kt")
                .readText(Charsets.UTF_8)
        assertFalse("QuickTagDialog must unlove to rating=0, not 3", quickTagSrc.contains("else \"3\""))

        // P0-D4: AudioOutputManager gates BIT_PERFECT mixer BEFORE calling applyBitPerfectMixer
        val audioOutSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/audio/AudioOutputManager.kt")
                .readText(Charsets.UTF_8)
        val gateIdx = audioOutSrc.indexOf("!usbEarConfirmed")
        val applyIdx = audioOutSrc.indexOf("applyBitPerfectMixer(")
        assertTrue("USB ear-protection gate must execute before applyBitPerfectMixer", gateIdx > 0 && applyIdx > gateIdx)

        // P0-S1 / P0-D3: CoverProvider allow-list + ManagedSyncStorage deletedOld guard + SecretBox preservation
        val coverProvSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/image/CoverProvider.kt")
                .readText(Charsets.UTF_8)
        assertTrue(coverProvSrc.contains("isIndexedCoverId"))
        val secretBoxSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/sync/SecretBox.kt")
                .readText(Charsets.UTF_8)
        assertTrue(secretBoxSrc.contains("KeyPermanentlyInvalidatedException"))

        // AutoDJ UUID mapping covers Focus (1111...) & Pop (6666...)
        val autoDjSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/sync/AutoDjEngine.kt")
                .readText(Charsets.UTF_8)
        assertTrue(autoDjSrc.contains("11111111-1111-4111-8111-111111111111"))
        assertTrue(autoDjSrc.contains("66666666-6666-4666-8666-666666666666"))
    }

    @Test
    fun batchCAndD_dspZeroDbContinuity_relPathIndex_andStabilityProguardGuards() {
        // C4: TonewiseDspEngine keeps 0 dB filter slots at unity without resetting state
        val activeCfg =
            com.shawnshi.tonewise.playback.dsp.DspChainConfig(
                enabled = true,
                limiterEnabled = false,
                bands =
                    listOf(
                        com.shawnshi.tonewise.playback.dsp.BiquadBand(
                            type = com.shawnshi.tonewise.playback.dsp.BiquadType.PEAKING,
                            freqHz = 1000.0,
                            gainDb = 3.5,
                            q = 1.41,
                            enabled = true,
                        ),
                    ),
            )
        val zeroCfg =
            activeCfg.copy(
                bands = listOf(activeCfg.bands[0].copy(gainDb = 0.0)),
            )
        val engine = com.shawnshi.tonewise.playback.dsp.TonewiseDspEngine().apply {
            configure(48000, 1)
            updateConfig(activeCfg)
        }
        val warm = DoubleArray(128) { i -> 0.4 * kotlin.math.sin(2.0 * Math.PI * 440.0 * i / 48000.0) }
        engine.processInterleaved(warm, warm.size, 0.0)

        engine.updateConfig(zeroCfg)
        val zeroBlock = DoubleArray(32) { i -> 0.4 * kotlin.math.sin(2.0 * Math.PI * 440.0 * (128 + i) / 48000.0) }
        engine.processInterleaved(zeroBlock, zeroBlock.size, 0.0)
        for (i in zeroBlock.indices) {
            assertTrue("Zero-dB filter frame $i must remain finite", zeroBlock[i].isFinite())
        }

        val identityFilter =
            com.shawnshi.tonewise.playback.dsp.BiquadFilter64(zeroCfg.bands[0], 48000, 1)
        assertEquals(0.42, identityFilter.processSample(0.42, 0), 1e-12)

        // C3: SongRelPathIndex matches exact, Music/, Tonewise/, and unique stem paths identically
        val vol =
            object : org.oxycblt.musikr.fs.Volume.Internal {
                override val mediaStoreName: String? = "primary"
                override val components: org.oxycblt.musikr.fs.Components? = null
                override fun resolveName(context: android.content.Context): String = "Internal"
            }
        fun fakeSong(seed: String, unixRel: String): org.oxycblt.musikr.Song =
            object : org.oxycblt.musikr.Song {
                override val uid =
                    requireNotNull(
                        org.oxycblt.musikr.Music.UID.fromString(
                            "uas${java.util.UUID.nameUUIDFromBytes(seed.toByteArray())}"
                        )
                    )
                override val name = org.oxycblt.musikr.tag.interpret.Naming.simple().name(seed, null)
                override val track: Int? = null
                override val disc: org.oxycblt.musikr.tag.Disc? = null
                override val date: org.oxycblt.musikr.tag.Date? = null
                override val uri: android.net.Uri get() = error("unused")
                override val path =
                    org.oxycblt.musikr.fs.Path(
                        vol,
                        org.oxycblt.musikr.fs.Components.parseUnix(unixRel),
                    )
                override val format: org.oxycblt.musikr.fs.Format get() = error("unused")
                override val size: Long = 0L
                override val durationMs: Long = 180_000L
                override val bitrateKbps: Int = 900
                override val sampleRateHz: Int = 44100
                override val replayGainAdjustment = org.oxycblt.musikr.tag.ReplayGainAdjustment(null, null)
                override val modifiedMs: Long = 0L
                override val addedMs: Long = 0L
                override val cover: org.oxycblt.musikr.covers.Cover? = null
                override val album: org.oxycblt.musikr.Album get() = error("unused")
                override val artists: List<org.oxycblt.musikr.Artist> = emptyList()
                override val genres: List<org.oxycblt.musikr.Genre> = emptyList()
                override val composers: List<String> = emptyList()
                override val conductors: List<String> = emptyList()
            }

        val s1 = fakeSong("s1", "Music/Classical/Beethoven/01.flac")
        val s2 = fakeSong("s2", "Tonewise/Jazz/Miles/02.m4a")
        val songs = listOf(s1, s2)
        val legacyList = com.shawnshi.tonewise.sync.PlaylistM3u8Helper.buildSongsByRelSuffixMap(songs)
        val fastIndex = com.shawnshi.tonewise.sync.PlaylistM3u8Helper.buildSongRelPathIndex(songs)
        for (query in listOf("Classical/Beethoven/01.flac", "Jazz/Miles/02.m4a", "Classical/Beethoven/01.mp3", "missing/99.flac")) {
            assertEquals(
                com.shawnshi.tonewise.sync.PlaylistM3u8Helper.findMatchingSongByRelPath(query, legacyList)?.uid,
                com.shawnshi.tonewise.sync.PlaylistM3u8Helper.findMatchingSongByRelPath(query, fastIndex)?.uid,
            )
        }

        // C1, C2, C5 & D1 source contracts
        val repoRoot = findRepoRoot()
        for (rel in listOf(
            "android/app/src/main/java/com/shawnshi/tonewise/playback/service/ExoPlaybackStateHolder.kt",
            "android/app/src/main/java/com/shawnshi/tonewise/playback/service/PlaybackServiceFragment.kt",
            "android/app/src/main/java/com/shawnshi/tonewise/music/service/MusicServiceFragment.kt",
        )) {
            assertTrue("$rel must use SupervisorJob()", File(repoRoot, rel).readText(Charsets.UTF_8).contains("SupervisorJob()"))
        }
        val backdropSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/ui/ZuneMosaicBackdropView.kt")
                .readText(Charsets.UTF_8)
        assertTrue(backdropSrc.contains("onWindowVisibilityChanged"))
        assertTrue(backdropSrc.contains("onVisibilityChanged"))

        val httpClientSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/sync/LanHttpClient.kt")
                .readText(Charsets.UTF_8)
        assertTrue(httpClientSrc.contains("catch (ste: SocketTimeoutException)"))

        val proguardSrc = File(repoRoot, "android/app/proguard-rules.pro").readText(Charsets.UTF_8)
        assertTrue(proguardSrc.contains("FloatingActionButton"))
        assertTrue(proguardSrc.contains("mBackgroundTintHelper"))
        assertTrue(proguardSrc.contains("mRecyclerView"))
        assertTrue(proguardSrc.contains("mTouchSlop"))
    }

    @Test
    fun stage1_destructiveInteractionAndGestureGuardsHoldAcrossSources() {
        val repoRoot = findRepoRoot()

        val barSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackBarFragment.kt")
                .readText(Charsets.UTF_8)
        assertFalse("Mini player must not carry hidden slot-switch long press", barSrc.contains("hotSwitchNextSlot"))

        val classicalSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/classical/ClassicalHierarchyFragment.kt")
                .readText(Charsets.UTF_8)
        val recAbIdx = classicalSrc.indexOf("onOpenRecordingsAb")
        val togglePinIdx = classicalSrc.indexOf("onTogglePinWork")
        assertTrue(recAbIdx > 0 && togglePinIdx > recAbIdx)
        val recAbBlock = classicalSrc.substring(recAbIdx, togglePinIdx)
        assertFalse("Opening N rec dialog must not overwrite queue beforehand", recAbBlock.contains("restoreSessionQueue"))
        assertTrue(recAbBlock.contains("WorkRecordingsDialog.newInstance(work.key)"))

        val vmSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackViewModel.kt")
                .readText(Charsets.UTF_8)
        assertTrue(vmSrc.contains("data class DjFreezeBackup"))

        val panelSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackPanelFragment.kt")
                .readText(Charsets.UTF_8)
        assertTrue(panelSrc.contains("playbackModel.djFreezeBackup"))
        assertTrue(panelSrc.contains("positionMs = existingBackup.positionMs"))

        val quickplaySrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/quickplay/QuickplayFragment.kt")
                .readText(Charsets.UTF_8)
        assertTrue(quickplaySrc.contains("pinPendingUnpin"))
        assertTrue(quickplaySrc.contains("requestDisallowInterceptTouchEvent(true)"))

        val syncSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/sync/ScenesAndSyncDialog.kt")
                .readText(Charsets.UTF_8)
        assertTrue(syncSrc.contains("showUnpairConfirm"))
        assertTrue(syncSrc.contains("slotPendingOverwrite"))
        val restoreIdx = syncSrc.indexOf("onRestoreSlot =")
        val onSyncIdx = syncSrc.indexOf("onSyncUrl =")
        assertTrue(restoreIdx > 0 && onSyncIdx > restoreIdx)
        assertFalse("Restoring slot must not rewrite playlist file on disk", syncSrc.substring(restoreIdx, onSyncIdx).contains("scheduleM3u8WriteBack"))
    }

    @Test
    fun stage2_accessibilityTouchTargetsAndContrastContractsHold() {
        val repoRoot = findRepoRoot()

        val hubXml = File(repoRoot, "android/app/src/main/res/layout/fragment_collection_hub.xml").readText(Charsets.UTF_8)
        assertTrue(hubXml.contains("android:minHeight=\"48dp\""))
        assertTrue(hubXml.contains("android:layout_width=\"48dp\""))

        val seekXml = File(repoRoot, "android/app/src/main/res/layout/view_seek_bar.xml").readText(Charsets.UTF_8)
        assertTrue(seekXml.contains("android:layout_height=\"48dp\""))
        assertTrue(seekXml.contains("app:thumbRadius=\"4dp\""))

        val propXml = File(repoRoot, "android/app/src/main/res/layout/item_song_property.xml").readText(Charsets.UTF_8)
        assertTrue(propXml.contains("android:minHeight=\"48dp\""))
        assertTrue(propXml.contains("android:contentDescription="))

        for (dir in listOf("layout-h520dp", "layout-h360dp")) {
            val panelXml = File(repoRoot, "android/app/src/main/res/$dir/fragment_playback_panel.xml").readText(Charsets.UTF_8)
            assertTrue(panelXml.contains("android:contentDescription=\"@string/lbl_more\""))
        }

        val barXml = File(repoRoot, "android/app/src/main/res/layout/fragment_playback_bar.xml").readText(Charsets.UTF_8)
        assertTrue(barXml.contains("android:contentDescription=\"@string/desc_skip_next\""))

        val locXml = File(repoRoot, "android/app/src/main/res/layout/dialog_music_locations.xml").readText(Charsets.UTF_8)
        assertTrue(locXml.contains("android:importantForAccessibility=\"no\""))

        val errXml = File(repoRoot, "android/app/src/main/res/layout/dialog_error_details.xml").readText(Charsets.UTF_8)
        assertTrue(errXml.contains("android:contentDescription=\"@android:string/copy\""))

        val clockKt = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/ui/clock/DeskClockDialog.kt").readText(Charsets.UTF_8)
        assertTrue(clockKt.contains("0xFF828894"))
        assertFalse(clockKt.contains("0xFF5A606B"))

        val classicalKt = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/classical/ClassicalHierarchyFragment.kt").readText(Charsets.UTF_8)
        assertTrue(classicalKt.contains("OnBackPressedCallback"))
        assertTrue(classicalKt.contains("verticalScroll(rememberScrollState())"))
        assertTrue(classicalKt.contains("desc_classical_jump_hint"))

        val seekKt = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/ui/StyledSeekBar.kt").readText(Charsets.UTF_8)
        assertTrue(seekKt.contains("setLabelFormatter"))
        assertTrue(seekKt.contains("ViewCompat.setStateDescription"))

        val barKt = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackBarFragment.kt").readText(Charsets.UTF_8)
        assertTrue(barKt.contains("R.string.desc_pause"))
        val panelKt = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackPanelFragment.kt").readText(Charsets.UTF_8)
        assertTrue(panelKt.contains("R.string.desc_pause"))
    }

    @Test
    fun stage3_rectilinearTheme_DynamicAccent_AndI18nParityHold() {
        val darkContrast = ZuneColorMath.contrastRatio(ZuneColorMath.DEFAULT_DARK_MAGENTA, ZuneColorMath.BLACK_SURFACE)
        val whiteOnDarkContrast = ZuneColorMath.contrastRatio(0xFFFFFFFF.toInt(), ZuneColorMath.DEFAULT_DARK_MAGENTA)
        assertTrue("DEFAULT_DARK_MAGENTA on black must pass AA (got $darkContrast)", darkContrast >= 4.5)
        assertTrue("White on DEFAULT_DARK_MAGENTA must pass AA (got $whiteOnDarkContrast)", whiteOnDarkContrast >= 4.5)

        val repoRoot = findRepoRoot()
        val stylesUi = File(repoRoot, "android/app/src/main/res/values/styles_ui.xml").readText(Charsets.UTF_8)
        assertTrue(stylesUi.contains("ShapeAppearance.Tonewise.Zero"))
        assertTrue(stylesUi.contains("ThemeOverlay.Tonewise.Rectilinear"))

        val mainActivity = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/MainActivity.kt").readText(Charsets.UTF_8)
        assertTrue(mainActivity.contains("ThemeOverlay_Tonewise_Rectilinear"))

        for (rel in listOf(
            "android/app/src/main/java/com/shawnshi/tonewise/playback/dsp/DspControlDialog.kt",
            "android/app/src/main/java/com/shawnshi/tonewise/playback/audio/SignalPathDialog.kt",
            "android/app/src/main/java/com/shawnshi/tonewise/detail/WorkRecordingsDialog.kt",
            "android/app/src/main/java/com/shawnshi/tonewise/detail/BookletPdfDialog.kt",
        )) {
            val kt = File(repoRoot, rel).readText(Charsets.UTF_8)
            assertFalse("$rel must not hardcode RoundedCornerShape(12.dp)", kt.contains("RoundedCornerShape(12.dp)"))
            assertFalse("$rel must not hardcode RoundedCornerShape(14.dp)", kt.contains("RoundedCornerShape(14.dp)"))
            assertTrue("$rel must bind roundMode & dynamicAccentArgb", kt.contains("dynamicAccentArgb ="))
        }

        val docFactory = javax.xml.parsers.DocumentBuilderFactory.newInstance()
        fun parseNames(relPath: String): Set<String> {
            val doc = docFactory.newDocumentBuilder().parse(File(repoRoot, relPath))
            val out = LinkedHashSet<String>()
            val nodes = doc.documentElement.childNodes
            for (i in 0 until nodes.length) {
                val n = nodes.item(i)
                if (n is org.w3c.dom.Element && n.hasAttribute("name")) {
                    out.add(n.getAttribute("name"))
                }
            }
            return out
        }

        val baseKeys = parseNames("android/app/src/main/res/values/strings.xml")
        val cnKeys = parseNames("android/app/src/main/res/values-zh-rCN/strings.xml")
        val twKeys = parseNames("android/app/src/main/res/values-zh-rTW/strings.xml")
        assertTrue("Missing zh-rCN keys: ${baseKeys - cnKeys}", (baseKeys - cnKeys).isEmpty())
        assertTrue("Missing zh-rTW keys: ${baseKeys - twKeys}", (baseKeys - twKeys).isEmpty())
    }

    @Test
    fun stage4_performancePagerLimit_QuickplayCache_MosaicPause_AndReducedMotionHold() {
        val repoRoot = findRepoRoot()

        val homeFrag = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/HomeFragment.kt").readText(Charsets.UTF_8)
        assertTrue(homeFrag.contains("offscreenPageLimit = 1"))
        assertTrue(homeFrag.contains("ValueAnimator.areAnimatorsEnabled()"))

        val homeVm = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/HomeViewModel.kt").readText(Charsets.UTF_8)
        assertTrue(homeVm.contains("val quickplaySections: StateFlow<QuickplaySections>"))
        assertTrue(homeVm.contains("lastQuickplayCacheKey"))
        assertTrue(homeVm.contains("delay(180L)"))

        val quickplayFrag = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/quickplay/QuickplayFragment.kt").readText(Charsets.UTF_8)
        assertTrue(quickplayFrag.contains("homeModel.quickplaySections"))

        val mosaicView = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/ui/ZuneMosaicBackdropView.kt").readText(Charsets.UTF_8)
        assertTrue(mosaicView.contains("setPlaybackSheetExpanded"))
        assertTrue(mosaicView.contains("!isSheetExpanded"))

        val playbackVm = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackViewModel.kt").readText(Charsets.UTF_8)
        assertTrue(playbackVm.contains("isPlaybackSheetExpanded"))

        val mainFrag = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/MainFragment.kt").readText(Charsets.UTF_8)
        assertTrue(mainFrag.contains("setPlaybackSheetExpanded"))

        val panelFrag = File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/playback/PlaybackPanelFragment.kt").readText(Charsets.UTF_8)
        assertTrue(panelFrag.contains("isPlaybackSheetExpanded"))

        val wavySlider = File(repoRoot, "android/app/src/main/java/com/google/android/material/slider/WavySlider.kt").readText(Charsets.UTF_8)
        assertTrue(wavySlider.contains("ValueAnimator.areAnimatorsEnabled()"))
    }

    @Test
    fun zunePanoramaHeader_AndDefaultRelaySlotsPresetsHold() {
        val defaults = SessionSlotStore.withDefaultPresets(emptyList())
        assertEquals(listOf(1, 2, 3, 4, 5), defaults.map { it.slotIndex })
        assertEquals(
            listOf("Focus", "Coding", "Relax", "Energy", "Classical"),
            defaults.map { it.name },
        )
        assertTrue(defaults.all { it.updatedAtMs == 0L })
        assertTrue(defaults.none { SessionSlotStore.isSlotPopulated(it) })

        val customSlot2 =
            SessionSlotItem(
                slotIndex = 2,
                name = "Deep Code",
                sceneId = "coding",
                updatedAtMs = 1763251200000L,
                queueTrackUids = listOf("uid_a", "uid_b"),
                currentIndex = 1,
                positionMs = 42_000L,
            )
        val merged = SessionSlotStore.withDefaultPresets(listOf(customSlot2))
        assertEquals(5, merged.size)
        assertEquals("Focus", merged[0].name)
        assertEquals("Deep Code", merged[1].name)
        assertTrue(SessionSlotStore.isSlotPopulated(merged[1]))
        assertEquals("Classical", merged[4].name)
        assertFalse(SessionSlotStore.isSlotPopulated(merged[4]))

        val repoRoot = findRepoRoot()
        val headerSrc =
            File(repoRoot, "android/app/src/main/java/com/shawnshi/tonewise/home/ZunePanoramaHeaderView.kt")
                .readText(Charsets.UTF_8)
        assertTrue(headerSrc.contains("maxLines = 1"))
        assertTrue(headerSrc.contains("isSingleLine = true"))
        assertTrue(headerSrc.contains("override fun onMeasure"))
        assertTrue(headerSrc.contains("MeasureSpec.UNSPECIFIED"))
        assertTrue(headerSrc.contains("maxScroll = (strip.measuredWidth - width).coerceAtLeast(0)"))
    }
}
