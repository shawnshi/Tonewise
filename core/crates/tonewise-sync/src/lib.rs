pub mod fingerprint;
pub mod patch;
pub mod session;
pub mod waveform;

pub use fingerprint::{AudioFrameFingerprint, compute_audio_frame_fingerprint};
pub use patch::{
    HeaderPatchOutcome, LwwTrackState, TagMutationEvent, apply_header_delta_patch,
    apply_lww_mutations,
};
pub use session::{SessionSlot, merge_session_slots};
pub use waveform::{WAVEFORM_POINTS, WaveformArchive, decode_hex_prefix16};

use serde::{Deserialize, Serialize};
use std::collections::HashSet;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MergeConflict {
    pub index: usize,
    pub ours: Vec<String>,
    pub theirs: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct M3u8MergeResult {
    pub merged: Vec<String>,
    pub conflicts: Vec<MergeConflict>,
}

/// Normalizes a playlist path entry into UTF-8 POSIX relative form (`/` separators, no BOM).
pub fn normalize_m3u8_path(raw: &str) -> String {
    raw.trim()
        .trim_start_matches('\u{feff}')
        .replace('\\', "/")
}

/// Performs an ordered-set 3-way merge of M3U8 playlist entries against a common ancestor `base`.
pub fn merge_m3u8_three_way(
    base: &[String],
    ours: &[String],
    theirs: &[String],
) -> M3u8MergeResult {
    let base_norm: Vec<String> = base.iter().map(|s| normalize_m3u8_path(s)).collect();
    let ours_norm: Vec<String> = ours.iter().map(|s| normalize_m3u8_path(s)).collect();
    let theirs_norm: Vec<String> = theirs.iter().map(|s| normalize_m3u8_path(s)).collect();

    let base_set: HashSet<&String> = base_norm.iter().collect();
    let ours_set: HashSet<&String> = ours_norm.iter().collect();
    let theirs_set: HashSet<&String> = theirs_norm.iter().collect();

    let mut merged = Vec::new();
    let mut seen = HashSet::new();
    let mut conflicts = Vec::new();

    // 1. Keep base entries that were not deleted by either side.
    for item in &base_norm {
        if ours_set.contains(item) && theirs_set.contains(item) && seen.insert(item.clone()) {
            merged.push(item.clone());
        }
    }

    // 2. Collect additions from both sides preserving relative order.
    let ours_added: Vec<String> = ours_norm
        .iter()
        .filter(|item| !base_set.contains(*item))
        .cloned()
        .collect();
    let theirs_added: Vec<String> = theirs_norm
        .iter()
        .filter(|item| !base_set.contains(*item))
        .cloned()
        .collect();

    if !ours_added.is_empty() && !theirs_added.is_empty() && ours_added != theirs_added {
        conflicts.push(MergeConflict {
            index: merged.len(),
            ours: ours_added.clone(),
            theirs: theirs_added.clone(),
        });
    }

    for item in ours_added.into_iter().chain(theirs_added) {
        if seen.insert(item.clone()) {
            merged.push(item);
        }
    }

    M3u8MergeResult { merged, conflicts }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HistoryEvent {
    pub track_uid: String,
    pub utc_ts_ms: u64,
    pub device_id: String,
    pub listened_ms: u64,
    #[serde(default = "default_true")]
    pub completed: bool,
    #[serde(default)]
    pub scene_id: Option<String>,
}

fn default_true() -> bool {
    true
}

/// Idempotently merges append-only playback logs using `(track_uid, utc_ts_ms, device_id)`.
pub fn merge_history_events(events: Vec<HistoryEvent>) -> Vec<HistoryEvent> {
    let mut seen = HashSet::new();
    let mut deduped = Vec::new();
    for ev in events {
        let key = (ev.track_uid.clone(), ev.utc_ts_ms, ev.device_id.clone());
        if seen.insert(key) {
            deduped.push(ev);
        }
    }
    deduped.sort_by_key(|e| (e.utc_ts_ms, e.track_uid.clone(), e.device_id.clone()));
    deduped
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Deserialize)]
    struct M3u8GoldenFile {
        cases: Vec<M3u8GoldenCase>,
    }

    #[derive(Deserialize)]
    struct M3u8GoldenCase {
        name: String,
        base: Vec<String>,
        ours: Vec<String>,
        theirs: Vec<String>,
        expected_merged: Vec<String>,
        expected_conflict_count: usize,
    }

    #[test]
    fn golden_m3u8_three_way_merge_matches_exactly() {
        let raw = include_str!("../../../../protocol/fixtures/m3u8_merge_golden.json");
        let golden: M3u8GoldenFile =
            serde_json::from_str(raw).expect("valid m3u8_merge_golden.json");

        for case in &golden.cases {
            let res = merge_m3u8_three_way(&case.base, &case.ours, &case.theirs);
            assert_eq!(
                res.merged, case.expected_merged,
                "merged mismatch on case '{}'",
                case.name
            );
            assert_eq!(
                res.conflicts.len(),
                case.expected_conflict_count,
                "conflict count mismatch on case '{}'",
                case.name
            );
        }
    }

    #[test]
    fn history_events_deduplicate_idempotently() {
        let e1 = HistoryEvent {
            track_uid: "u1".into(),
            utc_ts_ms: 1000,
            device_id: "phone".into(),
            listened_ms: 210_000,
            completed: true,
            scene_id: None,
        };
        let e2 = e1.clone();
        let e3 = HistoryEvent {
            track_uid: "u2".into(),
            utc_ts_ms: 500,
            device_id: "pc".into(),
            listened_ms: 180_000,
            completed: true,
            scene_id: Some("focus".into()),
        };

        let merged = merge_history_events(vec![e1, e2, e3]);
        assert_eq!(merged.len(), 2);
        assert_eq!(merged[0].track_uid, "u2");
        assert_eq!(merged[1].track_uid, "u1");
    }
}
