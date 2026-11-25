use std::collections::BTreeMap;
use std::f64::consts::PI;

use serde::{Deserialize, Serialize};

use crate::{BiquadFilter, DspProfile, FilterKind};

pub const STANDARD_TEN_BAND_FREQS: [f32; 10] = [
    31.0, 62.0, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0,
];

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CanonicalGenre {
    Classical,
    Jazz,
    PopRnb,
    RockMetal,
    FolkAcoustic,
    ElectronicDance,
    OstAmbient,
    Other,
}

impl CanonicalGenre {
    pub const ALL: [Self; 8] = [
        Self::Classical,
        Self::Jazz,
        Self::PopRnb,
        Self::RockMetal,
        Self::FolkAcoustic,
        Self::ElectronicDance,
        Self::OstAmbient,
        Self::Other,
    ];

    pub fn slot_key(self) -> &'static str {
        match self {
            Self::Classical => "classical",
            Self::Jazz => "jazz",
            Self::PopRnb => "pop_rnb",
            Self::RockMetal => "rock_metal",
            Self::FolkAcoustic => "folk_acoustic",
            Self::ElectronicDance => "electronic_dance",
            Self::OstAmbient => "ost_ambient",
            Self::Other => "other",
        }
    }

    pub fn display_name(self) -> &'static str {
        match self {
            Self::Classical => "古典",
            Self::Jazz => "爵士",
            Self::PopRnb => "流行 / R&B",
            Self::RockMetal => "摇滚 / 金属",
            Self::FolkAcoustic => "民谣 / 原声",
            Self::ElectronicDance => "电子 / 舞曲",
            Self::OstAmbient => "原声 / 氛围",
            Self::Other => "其他",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum VocalClass {
    Vocal,
    Instrumental,
    Unknown,
}

impl VocalClass {
    pub const ALL: [Self; 3] = [Self::Vocal, Self::Instrumental, Self::Unknown];

    pub fn slot_key(self) -> &'static str {
        match self {
            Self::Vocal => "vocal",
            Self::Instrumental => "instrumental",
            Self::Unknown => "unknown",
        }
    }

    pub fn display_name(self) -> &'static str {
        match self {
            Self::Vocal => "人声",
            Self::Instrumental => "纯器乐",
            Self::Unknown => "未知",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AutoEqRuleOverride {
    #[serde(default)]
    pub preamp_db: f32,
    #[serde(default)]
    pub filters: Vec<BiquadFilter>,
    #[serde(default)]
    pub crossfeed_override: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AutoEqPolicy {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default = "default_true")]
    pub classical_auto_crossfeed: bool,
    #[serde(default = "default_true")]
    pub lock_within_classical_work: bool,
    #[serde(default)]
    pub updated_at_ms: u64,
    #[serde(default)]
    pub custom_rules: BTreeMap<String, AutoEqRuleOverride>,
}

impl Default for AutoEqPolicy {
    fn default() -> Self {
        Self {
            enabled: false,
            classical_auto_crossfeed: true,
            lock_within_classical_work: true,
            updated_at_ms: 0,
            custom_rules: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct TrackAutoEqInput {
    #[serde(default)]
    pub genres: Vec<String>,
    #[serde(default)]
    pub dj_vocals: Option<String>,
    #[serde(default)]
    pub work_name: Option<String>,
    #[serde(default)]
    pub work_key: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ResolvedAutoEq {
    pub applied: bool,
    pub genre_family: CanonicalGenre,
    pub vocal_class: VocalClass,
    pub matched_rule_key: String,
    pub display_label: String,
    pub is_custom_rule: bool,
    pub locked_to_previous_work: bool,
    pub preamp_db: f32,
    pub filters: Vec<BiquadFilter>,
    pub enable_crossfeed: bool,
    pub has_non_zero_eq: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AutoEqCatalogEntry {
    pub slot_key: String,
    pub genre_family: CanonicalGenre,
    pub vocal_class: VocalClass,
    pub label: String,
    pub default_preamp_db: f32,
    pub default_filters: Vec<BiquadFilter>,
    pub default_crossfeed: bool,
}

fn split_genre_tokens(genres: &[String]) -> Vec<String> {
    let mut out = Vec::new();
    for raw in genres {
        for part in raw.split([';', '/', ',', '、', '|']) {
            let trimmed = part.trim();
            if !trimmed.is_empty() {
                out.push(trimmed.to_lowercase());
            }
        }
    }
    out
}

fn contains_any(haystack: &str, needles: &[&str]) -> bool {
    needles.iter().any(|n| haystack.contains(n))
}

fn is_classical_token(t: &str) -> bool {
    contains_any(
        t,
        &[
            "classical",
            "古典",
            "交响",
            "协奏",
            "奏鸣",
            "室内乐",
            "歌剧",
            "巴洛克",
            "浪漫派",
            "symphon",
            "concerto",
            "konzert",
            "sonat",
            "chamber",
            "opera",
            "baroque",
            "choral",
            "orchestra",
            "quartet",
            "quintet",
            "partita",
            "cantata",
            "oratorio",
            "requiem",
            "fugal",
            "fugue",
            "nocturne",
            "prelude",
            "etude",
        ],
    )
}

pub fn classify_genre(genres: &[String], work_name: Option<&str>) -> CanonicalGenre {
    let tokens = split_genre_tokens(genres);
    for t in &tokens {
        if is_classical_token(t) {
            return CanonicalGenre::Classical;
        }
        if contains_any(
            t,
            &["jazz", "爵士", "swing", "bebop", "bossa", "smooth jazz", "fusion"],
        ) {
            return CanonicalGenre::Jazz;
        }
        if contains_any(
            t,
            &[
                "pop",
                "r&b",
                "rnb",
                "soul",
                "hip-hop",
                "hip hop",
                "rap",
                "mandopop",
                "cantopop",
                "c-pop",
                "j-pop",
                "k-pop",
                "tai-pop",
                "ballad",
                "disco",
                "funk",
                "reggaeton",
                "流行",
                "说唱",
                "嘻哈",
                "节奏布鲁斯",
            ],
        ) {
            return CanonicalGenre::PopRnb;
        }
        if contains_any(
            t,
            &[
                "rock",
                "metal",
                "punk",
                "indie",
                "alternative",
                "grunge",
                "post-rock",
                "shoegaze",
                "摇滚",
                "金属",
                "朋克",
                "独立",
            ],
        ) {
            return CanonicalGenre::RockMetal;
        }
        if contains_any(
            t,
            &[
                "folk",
                "acoustic",
                "country",
                "blues",
                "unplugged",
                "bluegrass",
                "singer-songwriter",
                "民谣",
                "原声木吉他",
                "乡村",
                "蓝调",
                "布鲁斯",
            ],
        ) {
            return CanonicalGenre::FolkAcoustic;
        }
        if contains_any(
            t,
            &[
                "electronic",
                "edm",
                "dance",
                "techno",
                "house",
                "trance",
                "synth",
                "electro",
                "dubstep",
                "drum & bass",
                "drum and bass",
                "dnb",
                "lo-fi",
                "lofi",
                "电子",
                "舞曲",
            ],
        ) {
            return CanonicalGenre::ElectronicDance;
        }
        if contains_any(
            t,
            &[
                "soundtrack",
                "ost",
                "ambient",
                "new age",
                "film score",
                "score",
                "anime",
                "acg",
                "game",
                "cinematic",
                "原声",
                "配乐",
                "新世纪",
                "氛围",
                "电影原声",
            ],
        ) {
            return CanonicalGenre::OstAmbient;
        }
    }

    if tokens.is_empty()
        && let Some(work) = work_name
    {
        let w = work.trim().to_lowercase();
        if is_classical_token(&w) {
            return CanonicalGenre::Classical;
        }
    }

    CanonicalGenre::Other
}

pub fn classify_vocal(
    dj_vocals: Option<&str>,
    genres: &[String],
    work_name: Option<&str>,
) -> VocalClass {
    if let Some(raw) = dj_vocals {
        let v = raw.trim().to_lowercase();
        if v == "vocal" {
            return VocalClass::Vocal;
        }
        if v == "instrumental" {
            return VocalClass::Instrumental;
        }
    }

    let mut combined = split_genre_tokens(genres).join(" ");
    if let Some(w) = work_name {
        combined.push(' ');
        combined.push_str(&w.trim().to_lowercase());
    }

    let vocal_signals = [
        "opera",
        "歌剧",
        "choral",
        "choir",
        "合唱",
        "lieder",
        "art song",
        "声乐",
        "cantata",
        "oratorio",
        "requiem",
        "vocal jazz",
        "vocal",
        "rap",
        "hip-hop",
        "hip hop",
        "mandopop",
        "cantopop",
        "c-pop",
        "j-pop",
        "k-pop",
        "pop",
        "ballad",
        "r&b",
        "soul",
        "singer-songwriter",
    ];
    if contains_any(&combined, &vocal_signals) {
        return VocalClass::Vocal;
    }

    let instrumental_signals = [
        "instrumental",
        "纯音乐",
        "纯器乐",
        "器乐",
        "symphon",
        "concerto",
        "konzert",
        "sonat",
        "quartet",
        "quintet",
        "trio",
        "etude",
        "étude",
        "nocturne",
        "prelude",
        "prélude",
        "fugue",
        "partita",
        "chamber",
        "室内乐",
        "solo piano",
        "piano solo",
        "string quartet",
    ];
    if contains_any(&combined, &instrumental_signals) {
        return VocalClass::Instrumental;
    }

    VocalClass::Unknown
}

fn default_ten_bands() -> Vec<BiquadFilter> {
    STANDARD_TEN_BAND_FREQS
        .iter()
        .map(|&freq_hz| BiquadFilter {
            kind: FilterKind::Peaking,
            freq_hz,
            gain_db: 0.0,
            q: 1.41,
        })
        .collect()
}

fn make_rule_bands(edits: &[(usize, FilterKind, f32, f32)]) -> Vec<BiquadFilter> {
    let mut bands = default_ten_bands();
    for &(idx, kind, gain_db, q) in edits {
        if let Some(b) = bands.get_mut(idx) {
            b.kind = kind;
            b.gain_db = gain_db;
            b.q = q;
        }
    }
    bands
}

fn normalize_filter_slots(mut filters: Vec<BiquadFilter>) -> Vec<BiquadFilter> {
    if filters.is_empty() {
        return default_ten_bands();
    }
    if filters.len() < 10 {
        for freq in STANDARD_TEN_BAND_FREQS.iter().skip(filters.len()) {
            filters.push(BiquadFilter {
                kind: FilterKind::Peaking,
                freq_hz: *freq,
                gain_db: 0.0,
                q: 1.41,
            });
        }
    } else if filters.len() > 16 {
        filters.truncate(16);
    }
    filters
}

fn has_non_zero_filters(filters: &[BiquadFilter], preamp_db: f32) -> bool {
    if preamp_db.abs() > 1e-4 {
        return true;
    }
    filters.iter().any(|f| {
        f.gain_db.abs() > 1e-4 || matches!(f.kind, FilterKind::LowPass | FilterKind::HighPass)
    })
}

fn default_spec(genre: CanonicalGenre, vocal: VocalClass) -> (f32, Vec<BiquadFilter>, bool) {
    use CanonicalGenre::*;
    use FilterKind::*;
    use VocalClass::*;

    match (genre, vocal) {
        (Classical, Instrumental) | (Classical, Unknown) => (0.0, default_ten_bands(), true),
        (Classical, Vocal) => (
            0.0,
            make_rule_bands(&[
                (6, Peaking, 1.0, 1.0),
                (7, Peaking, 0.5, 1.41),
                (8, Peaking, -0.5, 1.41),
            ]),
            false,
        ),
        (Jazz, Vocal) => (
            0.0,
            make_rule_bands(&[
                (3, Peaking, 0.8, 1.2),
                (6, Peaking, 1.5, 1.2),
                (8, HighShelf, 0.8, 0.71),
            ]),
            false,
        ),
        (Jazz, Instrumental) => (
            0.0,
            make_rule_bands(&[(1, LowShelf, 1.0, 0.71), (8, HighShelf, 1.0, 0.71)]),
            false,
        ),
        (Jazz, Unknown) => (
            0.0,
            make_rule_bands(&[(1, LowShelf, 0.8, 0.71), (8, HighShelf, 0.8, 0.71)]),
            false,
        ),
        (PopRnb, Vocal) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.5, 0.71),
                (3, Peaking, -0.8, 1.2),
                (6, Peaking, 1.2, 1.2),
                (7, Peaking, 0.8, 1.41),
                (8, HighShelf, 1.0, 0.71),
            ]),
            false,
        ),
        (PopRnb, Instrumental) => (
            0.0,
            make_rule_bands(&[(1, LowShelf, 1.5, 0.71), (8, HighShelf, 1.0, 0.71)]),
            false,
        ),
        (PopRnb, Unknown) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.2, 0.71),
                (3, Peaking, -0.6, 1.2),
                (6, Peaking, 1.0, 1.2),
                (8, HighShelf, 0.8, 0.71),
            ]),
            false,
        ),
        (RockMetal, Vocal) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.5, 0.71),
                (3, Peaking, -1.0, 1.2),
                (6, Peaking, 1.5, 1.2),
                (7, Peaking, -0.8, 1.41),
            ]),
            false,
        ),
        (RockMetal, Instrumental) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.8, 0.71),
                (3, Peaking, -1.0, 1.2),
                (7, Peaking, -0.5, 1.41),
            ]),
            false,
        ),
        (RockMetal, Unknown) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.5, 0.71),
                (3, Peaking, -1.0, 1.2),
                (7, Peaking, -0.5, 1.41),
            ]),
            false,
        ),
        (FolkAcoustic, Vocal) => (
            0.0,
            make_rule_bands(&[
                (3, Peaking, -0.5, 1.2),
                (6, Peaking, 1.2, 1.2),
                (8, HighShelf, 1.2, 0.71),
            ]),
            false,
        ),
        (FolkAcoustic, Instrumental) => (
            0.0,
            make_rule_bands(&[(3, Peaking, -0.5, 1.2), (8, HighShelf, 1.2, 0.71)]),
            false,
        ),
        (FolkAcoustic, Unknown) => (
            0.0,
            make_rule_bands(&[(3, Peaking, -0.5, 1.2), (8, HighShelf, 1.0, 0.71)]),
            false,
        ),
        (ElectronicDance, Vocal) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.8, 0.71),
                (3, Peaking, -1.0, 1.2),
                (6, Peaking, 1.2, 1.2),
            ]),
            false,
        ),
        (ElectronicDance, Instrumental) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 2.0, 0.71),
                (3, Peaking, -1.0, 1.2),
                (8, HighShelf, 1.2, 0.71),
            ]),
            false,
        ),
        (ElectronicDance, Unknown) => (
            0.0,
            make_rule_bands(&[
                (1, LowShelf, 1.8, 0.71),
                (3, Peaking, -1.0, 1.2),
                (8, HighShelf, 1.0, 0.71),
            ]),
            false,
        ),
        (OstAmbient, Vocal) => (0.0, make_rule_bands(&[(6, Peaking, 1.0, 1.2)]), false),
        (OstAmbient, Instrumental) => (
            0.0,
            make_rule_bands(&[(1, LowShelf, 1.0, 0.71), (8, HighShelf, 0.8, 0.71)]),
            false,
        ),
        (OstAmbient, Unknown) => (0.0, default_ten_bands(), false),
        (Other, Vocal) => (
            0.0,
            make_rule_bands(&[(3, Peaking, -0.6, 1.2), (6, Peaking, 1.2, 1.2)]),
            false,
        ),
        (Other, Instrumental) | (Other, Unknown) => (0.0, default_ten_bands(), false),
    }
}

pub fn default_auto_eq_catalog() -> Vec<AutoEqCatalogEntry> {
    let mut entries = Vec::with_capacity(CanonicalGenre::ALL.len() * VocalClass::ALL.len());
    for genre in CanonicalGenre::ALL {
        for vocal in VocalClass::ALL {
            let slot_key = format!("{}:{}", genre.slot_key(), vocal.slot_key());
            let label = format!("{} · {}", genre.display_name(), vocal.display_name());
            let (default_preamp_db, default_filters, default_crossfeed) = default_spec(genre, vocal);
            entries.push(AutoEqCatalogEntry {
                slot_key,
                genre_family: genre,
                vocal_class: vocal,
                label,
                default_preamp_db,
                default_filters,
                default_crossfeed,
            });
        }
    }
    entries
}

pub fn resolve_auto_eq(
    policy: &AutoEqPolicy,
    track: &TrackAutoEqInput,
    previous_work_key: Option<&str>,
    previous_resolved: Option<&ResolvedAutoEq>,
) -> ResolvedAutoEq {
    let genre_family = classify_genre(&track.genres, track.work_name.as_deref());
    let vocal_class = classify_vocal(
        track.dj_vocals.as_deref(),
        &track.genres,
        track.work_name.as_deref(),
    );
    let exact_key = format!("{}:{}", genre_family.slot_key(), vocal_class.slot_key());
    let label = format!("{} · {}", genre_family.display_name(), vocal_class.display_name());

    if !policy.enabled {
        return ResolvedAutoEq {
            applied: false,
            genre_family,
            vocal_class,
            matched_rule_key: exact_key,
            display_label: label,
            is_custom_rule: false,
            locked_to_previous_work: false,
            preamp_db: 0.0,
            filters: default_ten_bands(),
            enable_crossfeed: false,
            has_non_zero_eq: false,
        };
    }

    if policy.lock_within_classical_work
        && genre_family == CanonicalGenre::Classical
        && let (Some(prev_key), Some(cur_key), Some(prev_res)) = (
            previous_work_key.map(str::trim).filter(|s| !s.is_empty()),
            track.work_key.as_deref().map(str::trim).filter(|s| !s.is_empty()),
            previous_resolved,
        )
        && prev_key.eq_ignore_ascii_case(cur_key)
        && prev_res.applied
    {
        let mut locked = prev_res.clone();
        locked.locked_to_previous_work = true;
        locked.filters = normalize_filter_slots(locked.filters);
        locked.has_non_zero_eq = has_non_zero_filters(&locked.filters, locked.preamp_db);
        return locked;
    }

    let candidate_keys = [
        exact_key.clone(),
        format!("{}:any", genre_family.slot_key()),
        format!("any:{}", vocal_class.slot_key()),
    ];

    let (_, default_filters, default_crossfeed) = default_spec(genre_family, vocal_class);
    let classical_crossfeed =
        genre_family == CanonicalGenre::Classical && vocal_class != VocalClass::Vocal && policy.classical_auto_crossfeed;

    for key in &candidate_keys {
        if let Some(custom) = policy.custom_rules.get(key) {
            let filters = normalize_filter_slots(custom.filters.clone());
            let preamp_db = custom.preamp_db.clamp(-15.0, 6.0);
            let enable_crossfeed = custom
                .crossfeed_override
                .unwrap_or(default_crossfeed && policy.classical_auto_crossfeed);
            return ResolvedAutoEq {
                applied: true,
                genre_family,
                vocal_class,
                matched_rule_key: key.clone(),
                display_label: format!("{label} (自定义)"),
                is_custom_rule: true,
                locked_to_previous_work: false,
                preamp_db,
                has_non_zero_eq: has_non_zero_filters(&filters, preamp_db),
                filters,
                enable_crossfeed,
            };
        }
    }

    ResolvedAutoEq {
        applied: true,
        genre_family,
        vocal_class,
        matched_rule_key: exact_key,
        display_label: label,
        is_custom_rule: false,
        locked_to_previous_work: false,
        preamp_db: 0.0,
        has_non_zero_eq: has_non_zero_filters(&default_filters, 0.0),
        filters: default_filters,
        enable_crossfeed: if genre_family == CanonicalGenre::Classical {
            classical_crossfeed
        } else {
            default_crossfeed
        },
    }
}

fn biquad_magnitude_at_hz(filter: &BiquadFilter, freq_hz: f64, sample_rate_hz: f64) -> f64 {
    if !matches!(filter.kind, FilterKind::LowPass | FilterKind::HighPass) && filter.gain_db.abs() <= 1e-4 {
        return 1.0;
    }
    let fs = sample_rate_hz.max(8000.0);
    let fc = (filter.freq_hz as f64).clamp(10.0, fs * 0.49);
    let w0 = 2.0 * PI * fc / fs;
    let cos_w0 = w0.cos();
    let sin_w0 = w0.sin();
    let q = (filter.q as f64).clamp(0.05, 30.0);
    let alpha = sin_w0 / (2.0 * q);
    let a = 10.0_f64.powf((filter.gain_db as f64).clamp(-24.0, 24.0) / 40.0);

    let (b0, b1, b2, a0, a1, a2) = match filter.kind {
        FilterKind::Peaking => (
            1.0 + alpha * a,
            -2.0 * cos_w0,
            1.0 - alpha * a,
            1.0 + alpha / a,
            -2.0 * cos_w0,
            1.0 - alpha / a,
        ),
        FilterKind::LowShelf => {
            let k = 2.0 * a.sqrt() * alpha;
            (
                a * ((a + 1.0) - (a - 1.0) * cos_w0 + k),
                2.0 * a * ((a - 1.0) - (a + 1.0) * cos_w0),
                a * ((a + 1.0) - (a - 1.0) * cos_w0 - k),
                (a + 1.0) + (a - 1.0) * cos_w0 + k,
                -2.0 * ((a - 1.0) + (a + 1.0) * cos_w0),
                (a + 1.0) + (a - 1.0) * cos_w0 - k,
            )
        }
        FilterKind::HighShelf => {
            let k = 2.0 * a.sqrt() * alpha;
            (
                a * ((a + 1.0) + (a - 1.0) * cos_w0 + k),
                -2.0 * a * ((a - 1.0) + (a + 1.0) * cos_w0),
                a * ((a + 1.0) + (a - 1.0) * cos_w0 - k),
                (a + 1.0) - (a - 1.0) * cos_w0 + k,
                2.0 * ((a - 1.0) - (a + 1.0) * cos_w0),
                (a + 1.0) - (a - 1.0) * cos_w0 - k,
            )
        }
        FilterKind::LowPass => (
            (1.0 - cos_w0) * 0.5,
            1.0 - cos_w0,
            (1.0 - cos_w0) * 0.5,
            1.0 + alpha,
            -2.0 * cos_w0,
            1.0 - alpha,
        ),
        FilterKind::HighPass => (
            (1.0 + cos_w0) * 0.5,
            -(1.0 + cos_w0),
            (1.0 + cos_w0) * 0.5,
            1.0 + alpha,
            -2.0 * cos_w0,
            1.0 - alpha,
        ),
    };

    let w = 2.0 * PI * freq_hz / fs;
    let cw = w.cos();
    let sw = w.sin();
    let c2w = (2.0 * w).cos();
    let s2w = (2.0 * w).sin();
    let nb0 = b0 / a0;
    let nb1 = b1 / a0;
    let nb2 = b2 / a0;
    let na1 = a1 / a0;
    let na2 = a2 / a0;
    let num_re = nb0 + nb1 * cw + nb2 * c2w;
    let num_im = -(nb1 * sw + nb2 * s2w);
    let den_re = 1.0 + na1 * cw + na2 * c2w;
    let den_im = -(na1 * sw + na2 * s2w);
    let den = den_re.hypot(den_im);
    if den > 1e-12 {
        num_re.hypot(num_im) / den
    } else {
        1.0
    }
}

pub fn compose_dsp_profiles(device_profile: &DspProfile, content_profile: &DspProfile) -> DspProfile {
    let mut filters = Vec::with_capacity(
        (device_profile.filters.len() + content_profile.filters.len()).min(64),
    );
    for f in &device_profile.filters {
        if filters.len() >= 64 {
            break;
        }
        filters.push(f.clone());
    }
    for f in &content_profile.filters {
        if filters.len() >= 64 {
            break;
        }
        filters.push(f.clone());
    }

    let active_filters: Vec<&BiquadFilter> = filters
        .iter()
        .filter(|f| {
            f.gain_db.abs() > 1e-4 || matches!(f.kind, FilterKind::LowPass | FilterKind::HighPass)
        })
        .collect();

    let peak_boost_db = if active_filters.is_empty() {
        0.0f32
    } else {
        let f_min = 25.0_f64.ln();
        let f_max = 18000.0_f64.ln();
        let steps = 48usize;
        let mut max_db = 0.0_f64;
        for i in 0..steps {
            let freq = (f_min + (f_max - f_min) * (i as f64) / ((steps - 1) as f64)).exp();
            let mut mag = 1.0_f64;
            for f in &active_filters {
                mag *= biquad_magnitude_at_hz(f, freq, 48000.0);
            }
            if mag > 1.0 {
                let db = 20.0 * mag.log10();
                if db > max_db {
                    max_db = db;
                }
            }
        }
        max_db as f32
    };

    let explicit_sum = device_profile.preamp_db + content_profile.preamp_db;
    let preamp_db = explicit_sum.min(-peak_boost_db).clamp(-24.0, 6.0);
    DspProfile { preamp_db, filters }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_multilingual_and_multi_genre_tracks() {
        assert_eq!(
            classify_genre(&["交响曲; 古典".to_string()], None),
            CanonicalGenre::Classical
        );
        assert_eq!(
            classify_genre(&["Vocal Jazz / Swing".to_string()], None),
            CanonicalGenre::Jazz
        );
        assert_eq!(
            classify_genre(&["Mandopop".to_string()], None),
            CanonicalGenre::PopRnb
        );
        assert_eq!(
            classify_genre(&[], Some("Symphony No. 9 in D minor")),
            CanonicalGenre::Classical
        );
    }

    #[test]
    fn classifies_vocal_with_explicit_override_and_safe_heuristics() {
        assert_eq!(
            classify_vocal(Some("instrumental"), &["Pop".to_string()], None),
            VocalClass::Instrumental
        );
        assert_eq!(
            classify_vocal(Some("unknown"), &["Classical; Opera".to_string()], None),
            VocalClass::Vocal
        );
        assert_eq!(
            classify_vocal(None, &["Classical".to_string()], Some("Piano Concerto No. 2")),
            VocalClass::Instrumental
        );
        assert_eq!(
            classify_vocal(None, &["Alternative".to_string()], None),
            VocalClass::Unknown
        );
    }

    #[test]
    fn classical_instrumental_stays_flat_and_links_crossfeed() {
        let policy = AutoEqPolicy {
            enabled: true,
            classical_auto_crossfeed: true,
            lock_within_classical_work: true,
            ..Default::default()
        };
        let track = TrackAutoEqInput {
            genres: vec!["Classical".to_string()],
            dj_vocals: Some("instrumental".to_string()),
            work_name: Some("Symphony No. 5".to_string()),
            work_key: Some("alb|cond::sym5".to_string()),
        };
        let res = resolve_auto_eq(&policy, &track, None, None);
        assert!(res.applied);
        assert_eq!(res.genre_family, CanonicalGenre::Classical);
        assert_eq!(res.vocal_class, VocalClass::Instrumental);
        assert!(!res.has_non_zero_eq);
        assert!(res.filters.iter().all(|f| f.gain_db.abs() < 1e-6));
        assert!(res.enable_crossfeed);
    }

    #[test]
    fn locks_vocal_character_within_same_classical_work() {
        let policy = AutoEqPolicy {
            enabled: true,
            classical_auto_crossfeed: true,
            lock_within_classical_work: true,
            ..Default::default()
        };
        let mov1 = TrackAutoEqInput {
            genres: vec!["Classical".to_string()],
            dj_vocals: Some("instrumental".to_string()),
            work_name: Some("Symphony No. 9".to_string()),
            work_key: Some("beethoven9".to_string()),
        };
        let res1 = resolve_auto_eq(&policy, &mov1, None, None);
        let mov4 = TrackAutoEqInput {
            genres: vec!["Classical".to_string()],
            dj_vocals: Some("vocal".to_string()),
            work_name: Some("Symphony No. 9".to_string()),
            work_key: Some("beethoven9".to_string()),
        };
        let res4 = resolve_auto_eq(&policy, &mov4, Some("beethoven9"), Some(&res1));
        assert!(res4.locked_to_previous_work);
        assert!(!res4.has_non_zero_eq);
        assert!(res4.enable_crossfeed);
    }

    #[test]
    fn custom_rule_takes_precedence_and_compose_prevents_same_band_clipping() {
        let mut policy = AutoEqPolicy {
            enabled: true,
            ..Default::default()
        };
        policy.custom_rules.insert(
            "jazz:vocal".to_string(),
            AutoEqRuleOverride {
                preamp_db: -1.0,
                filters: vec![BiquadFilter {
                    kind: FilterKind::LowShelf,
                    freq_hz: 62.0,
                    gain_db: 3.0,
                    q: 0.71,
                }],
                crossfeed_override: Some(true),
            },
        );
        let track = TrackAutoEqInput {
            genres: vec!["Jazz".to_string()],
            dj_vocals: Some("vocal".to_string()),
            ..Default::default()
        };
        let resolved = resolve_auto_eq(&policy, &track, None, None);
        assert!(resolved.is_custom_rule);
        assert_eq!(resolved.filters.len(), 10);
        assert!(resolved.enable_crossfeed);

        let device = DspProfile {
            preamp_db: 0.0,
            filters: vec![BiquadFilter {
                kind: FilterKind::LowShelf,
                freq_hz: 62.0,
                gain_db: 4.0,
                q: 0.71,
            }],
        };
        let content = DspProfile {
            preamp_db: resolved.preamp_db,
            filters: resolved.filters,
        };
        let composed = compose_dsp_profiles(&device, &content);
        assert!(composed.preamp_db <= -6.8, "same-band boost must protect headroom, got {}", composed.preamp_db);

        let disjoint_device = DspProfile {
            preamp_db: 0.0,
            filters: vec![BiquadFilter {
                kind: FilterKind::Peaking,
                freq_hz: 8000.0,
                gain_db: 3.0,
                q: 1.41,
            }],
        };
        let disjoint_content = DspProfile {
            preamp_db: 0.0,
            filters: vec![BiquadFilter {
                kind: FilterKind::Peaking,
                freq_hz: 62.0,
                gain_db: 2.5,
                q: 1.41,
            }],
        };
        let composed_disjoint = compose_dsp_profiles(&disjoint_device, &disjoint_content);
        assert!(
            composed_disjoint.preamp_db > -3.5 && composed_disjoint.preamp_db <= -2.9,
            "disjoint bands should not over-attenuate sum of gains, got {}",
            composed_disjoint.preamp_db
        );
    }
}
