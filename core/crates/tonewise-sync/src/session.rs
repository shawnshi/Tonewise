use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SessionSlot {
    pub slot_index: u8,
    pub name: String,
    #[serde(default)]
    pub scene_id: Option<String>,
    pub updated_at_ms: u64,
    #[serde(default)]
    pub queue_track_uids: Vec<String>,
    pub current_index: usize,
    pub position_ms: u64,
}

/// Merges up to 5 named queue session slots across endpoints using per-slot LWW timestamps.
pub fn merge_session_slots(local: &[SessionSlot], remote: &[SessionSlot]) -> Vec<SessionSlot> {
    let mut by_slot: BTreeMap<u8, SessionSlot> = BTreeMap::new();
    for slot in local.iter().chain(remote.iter()) {
        if !(1..=5).contains(&slot.slot_index) {
            continue;
        }
        match by_slot.get(&slot.slot_index) {
            Some(existing) if existing.updated_at_ms > slot.updated_at_ms => {}
            _ => {
                by_slot.insert(slot.slot_index, slot.clone());
            }
        }
    }
    by_slot.into_values().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn merges_5_session_slots_by_latest_timestamp() {
        let local = vec![
            SessionSlot {
                slot_index: 1,
                name: "Morning Focus".into(),
                scene_id: Some("focus".into()),
                updated_at_ms: 1000,
                queue_track_uids: vec!["u1".into(), "u2".into()],
                current_index: 0,
                position_ms: 12_340,
            },
            SessionSlot {
                slot_index: 2,
                name: "Night Jazz".into(),
                scene_id: None,
                updated_at_ms: 3000,
                queue_track_uids: vec!["u9".into()],
                current_index: 0,
                position_ms: 45_000,
            },
        ];
        let remote = vec![SessionSlot {
            slot_index: 1,
            name: "Morning Focus".into(),
            scene_id: Some("focus".into()),
            updated_at_ms: 2500,
            queue_track_uids: vec!["u1".into(), "u2".into()],
            current_index: 1,
            position_ms: 88_120,
        }];

        let merged = merge_session_slots(&local, &remote);
        assert_eq!(merged.len(), 2);
        assert_eq!(merged[0].slot_index, 1);
        assert_eq!(merged[0].position_ms, 88_120);
        assert_eq!(merged[1].slot_index, 2);
        assert_eq!(merged[1].position_ms, 45_000);
    }
}
