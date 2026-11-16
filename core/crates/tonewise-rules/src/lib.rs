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

#[derive(Debug, Clone, Deserialize)]
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
}

pub fn evaluate_scene(scene: &SceneCard, tracks: &[RuleTrack]) -> Vec<String> {
    let dict = MoodDictionary::global();
    let mut matched: Vec<&RuleTrack> = tracks
        .iter()
        .filter(|track| eval_expr(&scene.rule, track, scene.tempo_null_passthrough, dict))
        .collect();

    match scene.sort_arc {
        SortArc::Flat | SortArc::WorkShuffle => {}
        SortArc::EnergyAsc => {
            matched.sort_by_key(|t| energy_rank(t.dj_energy));
        }
        SortArc::EnergyDescSleep => {
            matched.sort_by_key(|a| std::cmp::Reverse(energy_rank(a.dj_energy)));
        }
        SortArc::Rediscovery90d => {
            matched.sort_by_key(|a| std::cmp::Reverse(a.unplayed_days));
        }
    }

    matched.into_iter().map(|t| t.track_uid.clone()).collect()
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

fn eval_predicate(
    pred: &Predicate,
    track: &RuleTrack,
    tempo_null_passthrough: bool,
    dict: &MoodDictionary,
) -> bool {
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
            None => tempo_null_passthrough && pred.op != "not_in",
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
        "mood_cluster" => track.moods.iter().any(|m| {
            dict.resolve_cluster(m)
                .map(|c| str_values.contains(&c.to_lowercase()))
                .unwrap_or(false)
        }),
        "genre" => track
            .genre
            .as_ref()
            .map(|g| str_values.contains(&g.trim().to_lowercase()))
            .unwrap_or(false),
        "min_rating" => {
            let threshold = pred
                .values
                .first()
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u8;
            track.rating >= threshold
        }
        "unplayed_days_gte" => {
            let threshold = pred
                .values
                .first()
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u32;
            track.unplayed_days >= threshold
        }
        _ => false,
    };

    if pred.op == "not_in" {
        !hit
    } else {
        hit
    }
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
}
