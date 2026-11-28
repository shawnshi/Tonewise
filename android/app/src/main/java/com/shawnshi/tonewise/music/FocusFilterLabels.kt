/*
 * Copyright (c) 2026 Tonewise Project
 * FocusFilterLabels.kt is part of Tonewise.
 */

package com.shawnshi.tonewise.music

import androidx.annotation.StringRes
import com.shawnshi.tonewise.R

/**
 * Maps internal canonical snake_case / English 5D tag keys to localized UI display labels
 * while keeping persisted LWW tags & FocusFilterSpec keys strictly canonical.
 */
object FocusFilterLabels {
    @StringRes
    fun labelResForKey(rawKey: String): Int? =
        when (rawKey.trim().lowercase()) {
            // Scenes
            "focus" -> R.string.lbl_tag_scene_focus
            "coding" -> R.string.lbl_tag_scene_coding
            "relax" -> R.string.lbl_tag_scene_relax
            "energy" -> R.string.lbl_tag_scene_energy
            "pop" -> R.string.lbl_tag_scene_pop
            // Vocals
            "instrumental" -> R.string.lbl_tag_vocals_instrumental
            "vocal" -> R.string.lbl_tag_vocals_vocal
            // Energy
            "low" -> R.string.lbl_tag_energy_low
            "medium" -> R.string.lbl_tag_energy_medium
            "high" -> R.string.lbl_tag_energy_high
            // Tempo
            "very slow" -> R.string.lbl_tag_tempo_very_slow
            "slow" -> R.string.lbl_tag_tempo_slow
            "moderate" -> R.string.lbl_tag_tempo_moderate
            "fast" -> R.string.lbl_tag_tempo_fast
            "very fast" -> R.string.lbl_tag_tempo_very_fast
            // Mood clusters
            "calm_relaxed" -> R.string.lbl_tag_mood_calm_relaxed
            "bright_joyful" -> R.string.lbl_tag_mood_bright_joyful
            "deep_introspective" -> R.string.lbl_tag_mood_deep_introspective
            "warm_romantic" -> R.string.lbl_tag_mood_warm_romantic
            "high_tension" -> R.string.lbl_tag_mood_high_tension
            // Sort arcs
            "flat" -> R.string.lbl_arc_flat
            "energy_asc" -> R.string.lbl_arc_energy_asc
            "energy_desc_sleep" -> R.string.lbl_arc_energy_desc_sleep
            "rediscovery_90d" -> R.string.lbl_arc_rediscovery_90d
            "work_shuffle" -> R.string.lbl_arc_work_shuffle
            // Unknown
            "unknown" -> R.string.lbl_tag_unknown
            else -> null
        }
}
