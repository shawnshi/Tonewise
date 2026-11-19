use crate::fingerprint::compute_audio_frame_fingerprint;
use serde::{Deserialize, Serialize};
use std::io::{self, Read, Seek, SeekFrom, Write};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TagMutationEvent {
    pub track_uid: String,
    pub field: String,
    pub value: serde_json::Value,
    pub lww_ts_ms: u64,
    pub device_id: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct LwwTrackState {
    pub track_uid: String,
    #[serde(default)]
    pub dj_scene: Vec<String>,
    #[serde(default)]
    pub dj_scene_ts: u64,
    pub dj_vocals: Option<String>,
    #[serde(default)]
    pub dj_vocals_ts: u64,
    pub dj_energy: Option<String>,
    #[serde(default)]
    pub dj_energy_ts: u64,
    pub tempo_bucket: Option<String>,
    #[serde(default)]
    pub tempo_ts: u64,
    #[serde(default)]
    pub mood: Vec<String>,
    #[serde(default)]
    pub mood_ts: u64,
    #[serde(default)]
    pub rating: u8,
    #[serde(default)]
    pub rating_ts: u64,
}

/// Applies per-tag LWW vector clock mutations onto `state`, ensuring concurrent edits across
/// distinct 5D dimensions never clobber one another.
pub fn apply_lww_mutations(state: &mut LwwTrackState, mutations: &[TagMutationEvent]) -> usize {
    let mut applied = 0usize;
    for ev in mutations {
        if ev.track_uid != state.track_uid {
            continue;
        }
        match ev.field.as_str() {
            "dj_scene" if ev.lww_ts_ms >= state.dj_scene_ts => {
                if let Some(arr) = ev.value.as_array() {
                    state.dj_scene = arr
                        .iter()
                        .filter_map(|v| v.as_str().map(str::to_string))
                        .collect();
                    state.dj_scene_ts = ev.lww_ts_ms;
                    applied += 1;
                }
            }
            "dj_vocals" if ev.lww_ts_ms >= state.dj_vocals_ts => {
                state.dj_vocals = ev.value.as_str().map(str::to_string);
                state.dj_vocals_ts = ev.lww_ts_ms;
                applied += 1;
            }
            "dj_energy" if ev.lww_ts_ms >= state.dj_energy_ts => {
                state.dj_energy = ev.value.as_str().map(str::to_string);
                state.dj_energy_ts = ev.lww_ts_ms;
                applied += 1;
            }
            "tempo_bucket" if ev.lww_ts_ms >= state.tempo_ts => {
                state.tempo_bucket = ev.value.as_str().map(str::to_string);
                state.tempo_ts = ev.lww_ts_ms;
                applied += 1;
            }
            "mood" if ev.lww_ts_ms >= state.mood_ts => {
                if let Some(arr) = ev.value.as_array() {
                    state.mood = arr
                        .iter()
                        .filter_map(|v| v.as_str().map(str::to_string))
                        .collect();
                    state.mood_ts = ev.lww_ts_ms;
                    applied += 1;
                }
            }
            "rating" if ev.lww_ts_ms >= state.rating_ts => {
                if let Some(r) = ev.value.as_u64() {
                    state.rating = (r.min(5)) as u8;
                    state.rating_ts = ev.lww_ts_ms;
                    applied += 1;
                }
            }
            _ => {}
        }
    }
    applied
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "status", rename_all = "snake_case")]
pub enum HeaderPatchOutcome {
    Applied {
        bytes_written: usize,
        padding_slack: usize,
    },
    HeaderHashMismatch {
        expected: String,
        actual: String,
    },
    PaddingInsufficient {
        required: usize,
        available: u64,
    },
}

/// Applies an in-place header-only micro-patch `[0, header_size)` when `new_header_bytes` fits
/// inside the existing metadata/padding region before pure audio frames.
pub fn apply_header_delta_patch<RW: Read + Write + Seek>(
    stream: &mut RW,
    expected_old_header_blake3_hex: &str,
    new_header_bytes: &[u8],
) -> io::Result<HeaderPatchOutcome> {
    let fp = compute_audio_frame_fingerprint(stream)?;
    if !fp
        .header_blake3_hex
        .eq_ignore_ascii_case(expected_old_header_blake3_hex.trim())
    {
        return Ok(HeaderPatchOutcome::HeaderHashMismatch {
            expected: expected_old_header_blake3_hex.to_string(),
            actual: fp.header_blake3_hex,
        });
    }

    if (new_header_bytes.len() as u64) != fp.header_size {
        return Ok(HeaderPatchOutcome::PaddingInsufficient {
            required: new_header_bytes.len(),
            available: fp.header_size,
        });
    }

    stream.seek(SeekFrom::Start(0))?;
    stream.write_all(new_header_bytes)?;
    stream.flush()?;

    Ok(HeaderPatchOutcome::Applied {
        bytes_written: new_header_bytes.len(),
        padding_slack: 0,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn per_tag_lww_merges_concurrent_5d_edits_without_clobbering() {
        let mut state = LwwTrackState {
            track_uid: "trk_1".into(),
            dj_scene: vec!["relax".into()],
            dj_scene_ts: 100,
            tempo_bucket: None,
            tempo_ts: 50,
            ..Default::default()
        };

        let mutations = vec![
            TagMutationEvent {
                track_uid: "trk_1".into(),
                field: "dj_scene".into(),
                value: serde_json::json!(["relax", "focus"]),
                lww_ts_ms: 200,
                device_id: "android_1".into(),
            },
            TagMutationEvent {
                track_uid: "trk_1".into(),
                field: "tempo_bucket".into(),
                value: serde_json::json!("Moderate"),
                lww_ts_ms: 150,
                device_id: "pc_1".into(),
            },
            TagMutationEvent {
                track_uid: "trk_1".into(),
                field: "dj_scene".into(),
                value: serde_json::json!(["stale"]),
                lww_ts_ms: 90,
                device_id: "pc_stale".into(),
            },
        ];

        let applied = apply_lww_mutations(&mut state, &mutations);
        assert_eq!(applied, 2);
        assert_eq!(state.dj_scene, vec!["relax", "focus"]);
        assert_eq!(state.tempo_bucket.as_deref(), Some("Moderate"));
    }

    #[test]
    fn in_place_header_patch_preserves_audio_frame_blake3() {
        let audio = b"LOSSLESS_AUDIO_STREAM_BYTES_9988776655";
        let mut initial = Vec::new();
        initial.extend_from_slice(b"fLaC");
        // STREAMINFO (34B)
        initial.push(0x00);
        initial.extend_from_slice(&[0, 0, 34]);
        initial.extend_from_slice(&[1u8; 34]);
        // PADDING (200B), last
        initial.push(0x81);
        initial.extend_from_slice(&[0, 0, 200]);
        initial.extend_from_slice(&[0u8; 200]);
        initial.extend_from_slice(audio);

        let mut cursor = Cursor::new(initial);
        let before = compute_audio_frame_fingerprint(&mut cursor).unwrap();

        // Build new header with a 12-byte Vorbis comment + smaller 184-byte padding (exact same total header span)
        let mut new_hdr = Vec::new();
        new_hdr.extend_from_slice(b"fLaC");
        new_hdr.push(0x00);
        new_hdr.extend_from_slice(&[0, 0, 34]);
        new_hdr.extend_from_slice(&[1u8; 34]);
        new_hdr.push(0x04);
        new_hdr.extend_from_slice(&[0, 0, 12]);
        new_hdr.extend_from_slice(b"DJ_ENERGY=lo");
        new_hdr.push(0x81);
        new_hdr.extend_from_slice(&[0, 0, 184]);
        new_hdr.extend_from_slice(&[0u8; 184]);

        let outcome =
            apply_header_delta_patch(&mut cursor, &before.header_blake3_hex, &new_hdr).unwrap();
        assert!(matches!(
            outcome,
            HeaderPatchOutcome::Applied {
                padding_slack: 0,
                ..
            }
        ));

        let after = compute_audio_frame_fingerprint(&mut cursor).unwrap();
        assert_eq!(before.audio_frame_blake3_hex, after.audio_frame_blake3_hex);
        assert_ne!(before.header_blake3_hex, after.header_blake3_hex);
    }

    #[test]
    fn header_patch_rejects_mismatched_size_or_hash_without_corrupting_stream() {
        let audio = b"LOSSLESS_AUDIO_STREAM_BYTES_9988776655";
        let mut initial = Vec::new();
        initial.extend_from_slice(b"fLaC");
        initial.push(0x00);
        initial.extend_from_slice(&[0, 0, 34]);
        initial.extend_from_slice(&[1u8; 34]);
        initial.push(0x81);
        initial.extend_from_slice(&[0, 0, 64]);
        initial.extend_from_slice(&[0u8; 64]);
        initial.extend_from_slice(audio);

        let before = compute_audio_frame_fingerprint(&mut Cursor::new(&initial)).unwrap();

        // Shorter header (< header_size) must NOT zero-fill trailing bytes; must reject with PaddingInsufficient
        let mut short_hdr = initial[..(before.header_size as usize - 16)].to_vec();
        short_hdr[38] = 0x81; // pretend earlier last block
        let mut cursor_short = Cursor::new(initial.clone());
        let res_short =
            apply_header_delta_patch(&mut cursor_short, &before.header_blake3_hex, &short_hdr)
                .unwrap();
        assert!(matches!(res_short, HeaderPatchOutcome::PaddingInsufficient { .. }));
        assert_eq!(cursor_short.get_ref(), &initial);

        // Hash mismatch must leave stream untouched
        let mut cursor_mismatch = Cursor::new(initial.clone());
        let res_mismatch = apply_header_delta_patch(
            &mut cursor_mismatch,
            &"0".repeat(64),
            &initial[..before.header_size as usize],
        )
        .unwrap();
        assert!(matches!(res_mismatch, HeaderPatchOutcome::HeaderHashMismatch { .. }));
        assert_eq!(cursor_mismatch.get_ref(), &initial);
    }
}
