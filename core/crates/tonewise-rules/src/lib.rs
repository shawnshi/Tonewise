use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;
use tonewise_metadata::{DjEnergy, DjVocals, TempoBucket};

const MOOD_CLUSTERS_JSON: &str =
    include_str!("../../../../protocol/dictionaries/mood_clusters.json");

#[derive(Debug, Clone, Deserialize)]
struct MoodClustersFile {
    clusters: Vec<MoodClusterDef>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct MoodClusterDef {
    pub id: String,
    pub label_zh: String,
    pub label_en: String,
    pub synonyms: Vec<String>,
}

pub struct MoodDictionary {
    synonym_to_cluster: HashMap<String, String>,
    clusters: Vec<MoodClusterDef>,
}

static MOOD_DICT: OnceLock<MoodDictionary> = OnceLock::new();

impl MoodDictionary {
    pub fn global() -> &'static MoodDictionary {
        MOOD_DICT.get_or_init(|| {
            let parsed: MoodClustersFile = serde_json::from_str(MOOD_CLUSTERS_JSON)
                .expect("invalid protocol/dictionaries/mood_clusters.json");
            let mut map = HashMap::new();
            for cluster in &parsed.clusters {
                map.insert(cluster.id.to_lowercase(), cluster.id.clone());
                for syn in &cluster.synonyms {
                    map.insert(syn.trim().to_lowercase(), cluster.id.clone());
                }
            }
            MoodDictionary {
                synonym_to_cluster: map,
                clusters: parsed.clusters,
            }
        })
    }

    pub fn cluster_count(&self) -> usize {
        self.clusters.len()
    }

    pub fn resolve_cluster(&self, mood: &str) -> Option<&str> {
        self.synonym_to_cluster
            .get(&mood.trim().to_lowercase())
            .map(String::as_str)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SortArc {
    Flat,
    EnergyAsc,
    EnergyDescSleep,
    WorkShuffle,
    #[serde(rename = "rediscovery_90d")]
    Rediscovery90d,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SceneCard {
    pub id: String,
    pub title: String,
    pub updated_at_ms: u64,
    pub sort_arc: SortArc,
    #[serde(default = "default_true")]
    pub tempo_null_passthrough: bool,
    pub rule: RuleExpr,
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum RuleExpr {
    All { all: Vec<RuleExpr> },
    Any { any: Vec<RuleExpr> },
    Not { not: Box<RuleExpr> },
    Leaf { predicate: Predicate },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Predicate {
    pub field: String,
    pub op: String,
    pub values: Vec<serde_json::Value>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct RuleTrack {
    pub track_uid: String,
    pub title: String,
    #[serde(default)]
    pub dj_scene: Vec<String>,
    pub dj_vocals: Option<DjVocals>,
    pub dj_energy: Option<DjEnergy>,
    pub tempo_bucket: Option<TempoBucket>,
    #[serde(default)]
    pub moods: Vec<String>,
    pub genre: Option<String>,
    #[serde(default)]
    pub rating: u8,
    #[serde(default)]
    pub unplayed_days: u32,
    #[serde(default)]
    pub work_key: Option<String>,
    #[serde(default)]
    pub movement_order: Option<i32>,
    #[serde(default)]
    pub composers: Vec<String>,
    #[serde(default)]
    pub conductors: Vec<String>,
    #[serde(default)]
    pub work_name: Option<String>,
}

fn normalized_work_key(track: &RuleTrack) -> Option<String> {
    track
        .work_key
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(|s| s.to_lowercase())
}

fn block_avg_energy_milli(block: &[(usize, &RuleTrack)]) -> u64 {
    if block.is_empty() {
        return 0;
    }
    let sum: u64 = block.iter().map(|(_, t)| energy_rank(t.dj_energy) as u64).sum();
    (sum * 1000) / (block.len() as u64)
}

pub fn evaluate_scene(scene: &SceneCard, tracks: &[RuleTrack]) -> Vec<String> {
    let dict = MoodDictionary::global();
    let matched: Vec<&RuleTrack> = tracks
        .iter()
        .filter(|track| eval_expr(&scene.rule, track, scene.tempo_null_passthrough, dict))
        .collect();

    if matches!(scene.sort_arc, SortArc::Flat | SortArc::WorkShuffle) {
        return matched.into_iter().map(|t| t.track_uid.clone()).collect();
    }

    let mut blocks: Vec<(usize, Vec<(usize, &RuleTrack)>)> = Vec::new();
    let mut work_indices: std::collections::HashMap<String, usize> = std::collections::HashMap::new();

    for (idx, track) in matched.into_iter().enumerate() {
        if let Some(work_key) = normalized_work_key(track) {
            if let Some(&slot) = work_indices.get(&work_key) {
                blocks[slot].1.push((idx, track));
            } else {
                let slot = blocks.len();
                work_indices.insert(work_key, slot);
                blocks.push((idx, vec![(idx, track)]));
            }
        } else {
            blocks.push((idx, vec![(idx, track)]));
        }
    }

    for (_, items) in &mut blocks {
        if items.len() > 1 {
            items.sort_by_key(|(idx, track)| {
                let mvt_key = match track.movement_order {
                    Some(m) if m > 0 => (0u8, m),
                    _ => (1u8, i32::MAX),
                };
                (mvt_key, *idx)
            });
        }
    }

    match scene.sort_arc {
        SortArc::Flat | SortArc::WorkShuffle => {}
        SortArc::EnergyAsc => {
            blocks.sort_by_key(|(first_idx, items)| (block_avg_energy_milli(items), *first_idx));
        }
        SortArc::EnergyDescSleep => {
            blocks.sort_by_key(|(first_idx, items)| {
                (std::cmp::Reverse(block_avg_energy_milli(items)), *first_idx)
            });
        }
        SortArc::Rediscovery90d => {
            blocks.sort_by_key(|(first_idx, items)| {
                let max_unplayed = items.iter().map(|(_, t)| t.unplayed_days).max().unwrap_or(0);
                (std::cmp::Reverse(max_unplayed), *first_idx)
            });
        }
    }

    blocks
        .into_iter()
        .flat_map(|(_, items)| items.into_iter().map(|(_, t)| t.track_uid.clone()))
        .collect()
}

fn energy_rank(energy: Option<DjEnergy>) -> u8 {
    match energy {
        Some(DjEnergy::Low) => 1,
        Some(DjEnergy::Medium) => 2,
        Some(DjEnergy::High) => 3,
        Some(DjEnergy::Unknown) | None => 0,
    }
}

fn eval_expr(
    expr: &RuleExpr,
    track: &RuleTrack,
    tempo_null_passthrough: bool,
    dict: &MoodDictionary,
) -> bool {
    match expr {
        RuleExpr::All { all } => all
            .iter()
            .all(|sub| eval_expr(sub, track, tempo_null_passthrough, dict)),
        RuleExpr::Any { any } => any
            .iter()
            .any(|sub| eval_expr(sub, track, tempo_null_passthrough, dict)),
        RuleExpr::Not { not } => !eval_expr(not, track, tempo_null_passthrough, dict),
        RuleExpr::Leaf { predicate } => {
            eval_predicate(predicate, track, tempo_null_passthrough, dict)
        }
    }
}

fn parse_json_u64(v: &serde_json::Value) -> Option<u64> {
    if let Some(u) = v.as_u64() {
        return Some(u);
    }
    if let Some(i) = v.as_i64() {
        return (i >= 0).then_some(i as u64);
    }
    if let Some(f) = v.as_f64() {
        return (f.is_finite() && f >= 0.0).then_some(f.round() as u64);
    }
    v.as_str().and_then(|s| s.trim().parse::<u64>().ok())
}

fn eval_numeric_predicate(actual: u64, op: &str, values: &[serde_json::Value]) -> bool {
    match op {
        "in" => {
            let parsed: Vec<u64> = values.iter().filter_map(parse_json_u64).collect();
            !parsed.is_empty() && parsed.into_iter().any(|v| v == actual)
        }
        "not_in" => {
            let parsed: Vec<u64> = values.iter().filter_map(parse_json_u64).collect();
            !parsed.is_empty() && parsed.into_iter().all(|v| v != actual)
        }
        _ => {
            let Some(threshold) = values.first().and_then(parse_json_u64) else {
                return false;
            };
            match op {
                "gte" | ">=" => actual >= threshold,
                "gt" | ">" => actual > threshold,
                "lte" | "<=" => actual <= threshold,
                "lt" | "<" => actual < threshold,
                "eq" | "=" | "==" => actual == threshold,
                "neq" | "!=" => actual != threshold,
                _ => false,
            }
        }
    }
}

fn eval_predicate(
    pred: &Predicate,
    track: &RuleTrack,
    tempo_null_passthrough: bool,
    dict: &MoodDictionary,
) -> bool {
    let op = pred.op.trim().to_lowercase();

    match pred.field.as_str() {
        "min_rating" | "rating" => {
            return eval_numeric_predicate(track.rating as u64, &op, &pred.values);
        }
        "unplayed_days_gte" | "unplayed_days" => {
            return eval_numeric_predicate(track.unplayed_days as u64, &op, &pred.values);
        }
        "tempo_bucket" if track.tempo_bucket.is_none() => {
            return match op.as_str() {
                "in" | "eq" | "not_in" | "neq" => tempo_null_passthrough,
                _ => false,
            };
        }
        _ => {}
    }

    let is_negated = match op.as_str() {
        "in" | "eq" => false,
        "not_in" | "neq" => true,
        _ => return false,
    };

    let str_values: HashSet<String> = pred
        .values
        .iter()
        .filter_map(|v| v.as_str().map(|s| s.trim().to_lowercase()))
        .collect();

    let hit = match pred.field.as_str() {
        "dj_scene" => track
            .dj_scene
            .iter()
            .any(|s| str_values.contains(&s.trim().to_lowercase())),
        "dj_vocals" => {
            let actual = track.dj_vocals.unwrap_or(DjVocals::Unknown).as_str();
            str_values.contains(actual)
        }
        "dj_energy" => {
            let actual = track.dj_energy.unwrap_or(DjEnergy::Unknown).as_str();
            str_values.contains(actual)
        }
        "tempo_bucket" => match track.tempo_bucket {
            None => tempo_null_passthrough,
            Some(bucket) => {
                let s = match bucket {
                    TempoBucket::VerySlow => "very slow",
                    TempoBucket::Slow => "slow",
                    TempoBucket::Moderate => "moderate",
                    TempoBucket::Fast => "fast",
                    TempoBucket::VeryFast => "very fast",
                };
                str_values.contains(s)
            }
        },
        "mood_cluster" => {
            let resolved_values: HashSet<String> = str_values
                .iter()
                .map(|v| dict.resolve_cluster(v).unwrap_or(v.as_str()).to_lowercase())
                .collect();
            track.moods.iter().any(|m| {
                dict.resolve_cluster(m)
                    .map(|c| resolved_values.contains(&c.to_lowercase()))
                    .unwrap_or(false)
            })
        }
        "genre" => track
            .genre
            .as_ref()
            .map(|g| {
                let whole = g.trim().to_lowercase();
                str_values.contains(&whole)
                    || g.split([';', ',', '/'])
                        .map(|part| part.trim().to_lowercase())
                        .any(|part| !part.is_empty() && str_values.contains(&part))
            })
            .unwrap_or(false),
        "composer" | "composers" => track.composers.iter().any(|c| {
            let whole = c.trim().to_lowercase();
            !whole.is_empty()
                && (str_values.contains(&whole)
                    || str_values.iter().any(|q| !q.is_empty() && whole.contains(q.as_str())))
        }),
        "conductor" | "conductors" => track.conductors.iter().any(|c| {
            let whole = c.trim().to_lowercase();
            !whole.is_empty()
                && (str_values.contains(&whole)
                    || str_values.iter().any(|q| !q.is_empty() && whole.contains(q.as_str())))
        }),
        "work_name" => track
            .work_name
            .as_ref()
            .map(|w| {
                let whole = w.trim().to_lowercase();
                !whole.is_empty()
                    && (str_values.contains(&whole)
                        || str_values.iter().any(|q| !q.is_empty() && whole.contains(q.as_str())))
            })
            .unwrap_or(false),
        _ => return false,
    };

    if is_negated { !hit } else { hit }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Deserialize)]
    struct GoldenFile {
        tracks: Vec<RuleTrack>,
        cases: Vec<GoldenCase>,
    }

    #[derive(Deserialize)]
    struct GoldenCase {
        name: String,
        scene: SceneCard,
        expected_track_uids: Vec<String>,
    }

    #[test]
    fn golden_rules_fixtures_match_exactly() {
        let raw = include_str!("../../../../protocol/fixtures/rules_golden.json");
        let golden: GoldenFile = serde_json::from_str(raw).expect("valid rules_golden.json");
        assert_eq!(MoodDictionary::global().cluster_count(), 5);

        for case in &golden.cases {
            let actual = evaluate_scene(&case.scene, &golden.tracks);
            assert_eq!(
                actual, case.expected_track_uids,
                "golden case '{}' mismatch",
                case.name
            );
        }
    }

    #[test]
    fn matches_unknown_vocals_energy_and_all_five_scenes() {
        let tracks = vec![
            RuleTrack {
                track_uid: "t_coding".into(),
                title: "Deep Code".into(),
                dj_scene: vec!["coding".into(), "focus".into()],
                dj_vocals: Some(DjVocals::Unknown),
                dj_energy: None,
                tempo_bucket: None,
                moods: vec![],
                genre: None,
                rating: 4,
                unplayed_days: 0,
                ..Default::default()
            },
            RuleTrack {
                track_uid: "t_pop".into(),
                title: "Upbeat Pop".into(),
                dj_scene: vec!["pop".into(), "energy".into()],
                dj_vocals: Some(DjVocals::Vocal),
                dj_energy: Some(DjEnergy::High),
                tempo_bucket: None,
                moods: vec![],
                genre: None,
                rating: 4,
                unplayed_days: 0,
                ..Default::default()
            },
        ];

        let scene = SceneCard {
            id: "55555555-5555-4555-8555-555555555555".into(),
            title: "Coding Unknown".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::Flat,
            tempo_null_passthrough: true,
            rule: RuleExpr::All {
                all: vec![
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "dj_scene".into(),
                            op: "in".into(),
                            values: vec![serde_json::json!("coding")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "dj_vocals".into(),
                            op: "eq".into(),
                            values: vec![serde_json::json!("unknown")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "dj_energy".into(),
                            op: "eq".into(),
                            values: vec![serde_json::json!("unknown")],
                        },
                    },
                ],
            },
        };

        assert_eq!(evaluate_scene(&scene, &tracks), vec!["t_coding"]);
    }

    #[test]
    fn phase2_operators_tempo_null_passthrough_and_multi_genre_behave_accurately() {
        let tracks = vec![
            RuleTrack {
                track_uid: "t_null_tempo".into(),
                title: "No Tempo".into(),
                dj_scene: vec!["focus".into()],
                dj_vocals: Some(DjVocals::Instrumental),
                dj_energy: Some(DjEnergy::Low),
                tempo_bucket: None,
                moods: vec!["Calm".into()],
                genre: Some("Classical; Chamber Music".into()),
                rating: 3,
                unplayed_days: 15,
                ..Default::default()
            },
            RuleTrack {
                track_uid: "t_fast_rock".into(),
                title: "Fast Rock".into(),
                dj_scene: vec!["energy".into()],
                dj_vocals: Some(DjVocals::Vocal),
                dj_energy: Some(DjEnergy::High),
                tempo_bucket: Some(TempoBucket::Fast),
                moods: vec!["Energetic".into()],
                genre: Some("Rock / Indie".into()),
                rating: 5,
                unplayed_days: 5,
                ..Default::default()
            },
        ];

        // 1. tempo_null_passthrough=false with not_in must NOT match track with null tempo_bucket
        let strict_not_in = SceneCard {
            id: "s1".into(),
            title: "Strict Not In".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::Flat,
            tempo_null_passthrough: false,
            rule: RuleExpr::Leaf {
                predicate: Predicate {
                    field: "tempo_bucket".into(),
                    op: "not_in".into(),
                    values: vec![serde_json::json!("slow")],
                },
            },
        };
        assert_eq!(evaluate_scene(&strict_not_in, &tracks), vec!["t_fast_rock"]);

        // 2. neq + multi-genre + numeric lt/eq
        let multi_op = SceneCard {
            id: "s2".into(),
            title: "Chamber <= 3".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::Flat,
            tempo_null_passthrough: true,
            rule: RuleExpr::All {
                all: vec![
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "dj_vocals".into(),
                            op: "neq".into(),
                            values: vec![serde_json::json!("vocal")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "genre".into(),
                            op: "in".into(),
                            values: vec![serde_json::json!("chamber music")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "rating".into(),
                            op: "lt".into(),
                            values: vec![serde_json::json!(4)],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "unplayed_days".into(),
                            op: "eq".into(),
                            values: vec![serde_json::json!(15)],
                        },
                    },
                ],
            },
        };
        assert_eq!(evaluate_scene(&multi_op, &tracks), vec!["t_null_tempo"]);

        // 3. Unknown operator must return false rather than acting like "in"
        let unknown_op = SceneCard {
            id: "s3".into(),
            title: "Bad Op".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::Flat,
            tempo_null_passthrough: true,
            rule: RuleExpr::Leaf {
                predicate: Predicate {
                    field: "genre".into(),
                    op: "bogus_op".into(),
                    values: vec![serde_json::json!("classical")],
                },
            },
        };
        assert!(evaluate_scene(&unknown_op, &tracks).is_empty());
    }

    #[test]
    fn classical_work_blocks_stay_atomic_across_sort_arcs_and_predicates() {
        let tracks = vec![
            RuleTrack {
                track_uid: "sym_m3".into(),
                title: "Sym 5 III".into(),
                dj_energy: Some(DjEnergy::High),
                genre: Some("Classical".into()),
                unplayed_days: 10,
                work_key: Some("beethoven_sym5::album_karajan".into()),
                movement_order: Some(3),
                composers: vec!["Ludwig van Beethoven".into()],
                conductors: vec!["Herbert von Karajan".into()],
                work_name: Some("Symphony No. 5 in C minor".into()),
                ..Default::default()
            },
            RuleTrack {
                track_uid: "solo_mid".into(),
                title: "Intermezzo".into(),
                dj_energy: Some(DjEnergy::Medium),
                genre: Some("Classical".into()),
                unplayed_days: 40,
                composers: vec!["Johannes Brahms".into()],
                ..Default::default()
            },
            RuleTrack {
                track_uid: "sym_m1".into(),
                title: "Sym 5 I".into(),
                dj_energy: Some(DjEnergy::Medium),
                genre: Some("Classical".into()),
                unplayed_days: 120,
                work_key: Some("beethoven_sym5::album_karajan".into()),
                movement_order: Some(1),
                composers: vec!["Ludwig van Beethoven".into()],
                conductors: vec!["Herbert von Karajan".into()],
                work_name: Some("Symphony No. 5 in C minor".into()),
                ..Default::default()
            },
            RuleTrack {
                track_uid: "sym_m2".into(),
                title: "Sym 5 II".into(),
                dj_energy: Some(DjEnergy::Low),
                genre: Some("Classical".into()),
                unplayed_days: 5,
                work_key: Some("beethoven_sym5::album_karajan".into()),
                movement_order: Some(2),
                composers: vec!["Ludwig van Beethoven".into()],
                conductors: vec!["Herbert von Karajan".into()],
                work_name: Some("Symphony No. 5 in C minor".into()),
                ..Default::default()
            },
            RuleTrack {
                track_uid: "solo_high".into(),
                title: "Toccata".into(),
                dj_energy: Some(DjEnergy::High),
                genre: Some("Classical".into()),
                unplayed_days: 20,
                composers: vec!["Sergei Prokofiev".into()],
                ..Default::default()
            },
        ];

        // 1. Composer + conductor + work_name filtering and negative predicate
        let filtered = SceneCard {
            id: "classical_filter".into(),
            title: "Beethoven by Karajan".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::EnergyAsc,
            tempo_null_passthrough: true,
            rule: RuleExpr::All {
                all: vec![
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "composer".into(),
                            op: "in".into(),
                            values: vec![serde_json::json!("Beethoven")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "conductor".into(),
                            op: "in".into(),
                            values: vec![serde_json::json!("Karajan")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "work_name".into(),
                            op: "in".into(),
                            values: vec![serde_json::json!("Symphony No. 5")],
                        },
                    },
                    RuleExpr::Leaf {
                        predicate: Predicate {
                            field: "composer".into(),
                            op: "not_in".into(),
                            values: vec![serde_json::json!("Brahms")],
                        },
                    },
                ],
            },
        };
        assert_eq!(
            evaluate_scene(&filtered, &tracks),
            vec!["sym_m1", "sym_m2", "sym_m3"]
        );

        // 2. EnergyAsc keeps multi-movement work atomic (avg energy 2000 == Medium) & movement-ordered
        let energy_asc = SceneCard {
            id: "classical_asc".into(),
            title: "Energy Asc".into(),
            updated_at_ms: 0,
            sort_arc: SortArc::EnergyAsc,
            tempo_null_passthrough: true,
            rule: RuleExpr::Leaf {
                predicate: Predicate {
                    field: "genre".into(),
                    op: "in".into(),
                    values: vec![serde_json::json!("classical")],
                },
            },
        };
        assert_eq!(
            evaluate_scene(&energy_asc, &tracks),
            vec!["sym_m1", "sym_m2", "sym_m3", "solo_mid", "solo_high"]
        );

        // 3. EnergyDescSleep keeps work atomic and places solo_high before the Medium-avg work block
        let sleep_arc = SceneCard {
            sort_arc: SortArc::EnergyDescSleep,
            ..energy_asc.clone()
        };
        assert_eq!(
            evaluate_scene(&sleep_arc, &tracks),
            vec!["solo_high", "sym_m1", "sym_m2", "sym_m3", "solo_mid"]
        );

        // 4. Rediscovery90d ranks work block by max unplayed_days (120) and keeps m1->m2->m3 intact
        let rediscovery = SceneCard {
            sort_arc: SortArc::Rediscovery90d,
            ..energy_asc
        };
        assert_eq!(
            evaluate_scene(&rediscovery, &tracks),
            vec!["sym_m1", "sym_m2", "sym_m3", "solo_mid", "solo_high"]
        );
    }
}
