use crate::normalize_composer_name;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::OnceLock;

const COMPOSER_METADATA_JSON: &str =
    include_str!("../../../../protocol/dictionaries/composer_metadata.json");

#[derive(Debug, Clone, Deserialize)]
struct ComposerMetadataFile {
    #[serde(default)]
    composer_eras: HashMap<String, String>,
    #[serde(default)]
    ensemble_abbreviations: HashMap<String, String>,
}

struct ComposerMetadataTables {
    composer_eras: HashMap<String, &'static str>,
    ensemble_abbreviations: HashMap<String, String>,
}

static METADATA_TABLES: OnceLock<ComposerMetadataTables> = OnceLock::new();

fn metadata_tables() -> &'static ComposerMetadataTables {
    METADATA_TABLES.get_or_init(|| {
        let parsed: ComposerMetadataFile = serde_json::from_str(COMPOSER_METADATA_JSON)
            .expect("invalid protocol/dictionaries/composer_metadata.json");
        let mut composer_eras = HashMap::with_capacity(parsed.composer_eras.len());
        for (k, v) in parsed.composer_eras {
            let era = match v.trim().to_ascii_lowercase().as_str() {
                "baroque" => "baroque",
                "classical" => "classical",
                "romantic" => "romantic",
                "modern" => "modern",
                _ => continue,
            };
            composer_eras.insert(k.trim().to_lowercase(), era);
        }
        let mut ensemble_abbreviations = HashMap::with_capacity(parsed.ensemble_abbreviations.len());
        for (k, v) in parsed.ensemble_abbreviations {
            let clean_k = k.trim().to_lowercase();
            let clean_v = v.trim().to_string();
            if !clean_k.is_empty() && !clean_v.is_empty() {
                ensemble_abbreviations.insert(clean_k, clean_v);
            }
        }
        ComposerMetadataTables {
            composer_eras,
            ensemble_abbreviations,
        }
    })
}

const CLASSICAL_GENRE_TOKENS: &[&str] = &[
    "classical",
    "古典",
    "交响",
    "协奏",
    "奏鸣",
    "室内乐",
    "歌剧",
    "巴洛克",
    "orchestra",
    "symphon",
    "concerto",
    "sonata",
    "chamber",
    "opera",
    "baroque",
    "choral",
];

const NON_CLASSICAL_GENRE_TOKENS: &[&str] = &[
    "pop",
    "rock",
    "metal",
    "jazz",
    "hip-hop",
    "hip hop",
    "rap",
    "r&b",
    "electronic",
    "edm",
    "dance",
    "country",
    "folk",
    "blues",
    "reggae",
    "indie",
    "punk",
    "soul",
    "funk",
    "k-pop",
    "j-pop",
    "c-pop",
    "mandopop",
    "cantopop",
    "tai-pop",
    "new age",
    "ambient",
    "traditional",
    "latin",
    "ballad",
    "disco",
    "alternative",
    "world music",
    "acoustic",
    "fado",
    "reggaeton",
    "acg",
    "anime",
    "soundtrack",
    "ost",
    "game",
    "流行",
    "摇滚",
    "爵士",
    "电子",
    "民谣",
    "说唱",
    "嘻哈",
    "原声",
];

const CLASSICAL_WORK_SIGNAL_TOKENS: &[&str] = &[
    "symphony",
    "symphonie",
    "concerto",
    "konzert",
    "sonata",
    "sonate",
    "quartet",
    "quintet",
    "trio",
    "variation",
    "prelude",
    "prélude",
    "nocturne",
    "etude",
    "étude",
    "fugue",
    "toccata",
    "suite",
    "partita",
    "requiem",
    "cantata",
    "oratorio",
    "overture",
    "ouvertüre",
    "rhapsody in ",
    "rhapsody on ",
    "serenade",
    "divertimento",
    "scherzo",
    "chaconne",
    "passacaglia",
    "mass in",
    "op.",
    "bwv",
    "kv",
    "k.",
    "hob.",
    "rv ",
    "woo",
    "交响",
    "协奏",
    "奏鸣",
    "四重奏",
    "变奏曲",
    "前奏曲",
    "夜曲",
    "练习曲",
    "组曲",
    "弥撒",
    "安魂曲",
    "歌剧",
];

const ENSEMBLE_TOKENS: &[&str] = &[
    "orchestra",
    "philharmonic",
    "philharmoniker",
    "symphoniker",
    "sinfonia",
    "quartet",
    "quintet",
    "sextet",
    "octet",
    "trio",
    "ensemble",
    "choir",
    "chorus",
    "academy",
    "consort",
    "staatskapelle",
    "gewandhaus",
    "orchestre",
    "orchester",
    "camerata",
    "solisti",
    "virtuosi",
    "乐团",
    "爱乐",
    "交响",
    "合唱团",
    "四重奏",
    "五重奏",
    "三重奏",
    "室内乐团",
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ParsedWorkMovement {
    pub work: String,
    pub movement: String,
    pub movement_number: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SeparatedPerformers {
    pub soloists: Vec<String>,
    pub ensemble: Option<String>,
}

pub fn is_classical_genre(genre: &str) -> bool {
    let lower = genre.trim().to_lowercase();
    !lower.is_empty() && CLASSICAL_GENRE_TOKENS.iter().any(|t| lower.contains(t))
}

pub fn is_explicit_non_classical_genre(genre: &str) -> bool {
    let lower = genre.trim().to_lowercase();
    !lower.is_empty() && NON_CLASSICAL_GENRE_TOKENS.iter().any(|t| lower.contains(t))
}

pub fn has_classical_work_signal(text: &str) -> bool {
    let lower = text.trim().to_lowercase();
    !lower.is_empty()
        && CLASSICAL_WORK_SIGNAL_TOKENS
            .iter()
            .any(|t| lower.contains(t))
}

pub fn is_classical_track(
    genre: &str,
    work: Option<&str>,
    composer: Option<&str>,
    conductor: Option<&str>,
    title: &str,
) -> bool {
    if is_classical_genre(genre) {
        return true;
    }
    let work_clean = work.unwrap_or("").trim();
    if is_explicit_non_classical_genre(genre) {
        return has_classical_work_signal(work_clean) || has_classical_work_signal(title);
    }
    if !work_clean.is_empty() || !conductor.unwrap_or("").trim().is_empty() {
        return true;
    }
    !composer.unwrap_or("").trim().is_empty()
}

pub fn parse_roman_numeral(raw: &str) -> Option<u32> {
    let s = raw.trim().to_ascii_uppercase();
    if s.is_empty() || !s.chars().all(|c| matches!(c, 'I' | 'V' | 'X')) {
        return None;
    }
    let mut total: i32 = 0;
    let mut prev: i32 = 0;
    for ch in s.chars().rev() {
        let val = match ch {
            'I' => 1,
            'V' => 5,
            'X' => 10,
            _ => return None,
        };
        if val < prev {
            total -= val;
        } else {
            total += val;
            prev = val;
        }
    }
    if total <= 0 || total > 39 {
        return None;
    }
    let n = total as u32;
    if format_roman_numeral(n) == s {
        Some(n)
    } else {
        None
    }
}

pub fn format_roman_numeral(n: u32) -> String {
    if n == 0 || n > 39 {
        return n.to_string();
    }
    let tens = ["", "X", "XX", "XXX"];
    let ones = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
    format!("{}{}", tens[(n / 10) as usize], ones[(n % 10) as usize])
}

/// Parses a leading movement ordinal such as `I. Allegro`, `IV - Presto`, or `2. Adagio`
/// into `(Some(number), remaining_title)`.
pub fn parse_movement_prefix(raw: &str) -> (Option<u32>, String) {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return (None, String::new());
    }

    // Try Roman numeral prefix: e.g., "I. Allegro", "II - Adagio", "IV: Presto", "III Allegro"
    let first_non_roman = trimmed
        .char_indices()
        .find(|(_, c)| !matches!(c.to_ascii_uppercase(), 'I' | 'V' | 'X'))
        .map(|(i, _)| i)
        .unwrap_or(trimmed.len());

    if first_non_roman > 0 && first_non_roman < trimmed.len() {
        let token = &trimmed[..first_non_roman];
        let rest = &trimmed[first_non_roman..];
        let is_delimited = rest.starts_with('.')
            || rest.starts_with(':')
            || rest.starts_with('：')
            || rest.starts_with(" - ")
            || rest.starts_with(" – ")
            || rest.starts_with(" — ");
        if is_delimited && let Some(num) = parse_roman_numeral(token) {
            let after = rest
                .trim_start_matches(['.', ':', '：', '-', '–', '—'])
                .trim();
            return (Some(num), after.to_string());
        }
    }

    // Try Arabic numeral prefix: e.g., "1. Allegro", "02 - Adagio"
    let first_non_digit = trimmed
        .char_indices()
        .find(|(_, c)| !c.is_ascii_digit())
        .map(|(i, _)| i)
        .unwrap_or(trimmed.len());
    if first_non_digit > 0 && first_non_digit < trimmed.len() {
        let digits = &trimmed[..first_non_digit];
        let rest = &trimmed[first_non_digit..];
        if (rest.starts_with('.') || rest.starts_with(" - ") || rest.starts_with(" – "))
            && let Ok(num) = digits.parse::<u32>()
            && num > 0
            && num <= 99
        {
            let after = rest.trim_start_matches(['.', '-', '–']).trim();
            if !after.is_empty() {
                return (Some(num), after.to_string());
            }
        }
    }

    (None, trimmed.to_string())
}

fn is_protected_catalog_colon(s: &str, colon_byte: usize) -> bool {
    let after = &s[colon_byte + 1..];
    if !after
        .chars()
        .next()
        .is_some_and(|c| c.is_ascii_digit())
    {
        return false;
    }
    let before = s[..colon_byte].trim_end();
    let lower_before = before.to_ascii_lowercase();
    let catalog_markers = ["hob.", "hob", "op.", "op", "bwv", "k.", "kv", "rv", "woo"];
    // Check last ~24 bytes before colon for catalog prefix like "Hob. XVI" or "Op. 27"
    let window_start = lower_before
        .char_indices()
        .rev()
        .nth(24)
        .map(|(i, _)| i)
        .unwrap_or(0);
    let tail = &lower_before[window_start..];
    catalog_markers.iter().any(|m| tail.contains(m))
}

fn find_work_movement_split(title: &str) -> Option<(String, String)> {
    // 1. First look for unprotected ':' or '：'
    for (idx, ch) in title.char_indices() {
        if ch == ':' && !is_protected_catalog_colon(title, idx) {
            let left = title[..idx].trim();
            let right = title[idx + 1..].trim();
            if !left.is_empty() && !right.is_empty() {
                return Some((left.to_string(), right.to_string()));
            }
        } else if ch == '：' {
            let left = title[..idx].trim();
            let right = title[idx + ch.len_utf8()..].trim();
            if !left.is_empty() && !right.is_empty() {
                return Some((left.to_string(), right.to_string()));
            }
        }
    }

    // 2. Look for spaced dash separators (" — ", " – ", " - ")
    for sep in [" — ", " – ", " - "] {
        if let Some(pos) = title.find(sep) {
            let left = title[..pos].trim();
            let right = title[pos + sep.len()..].trim();
            if !left.is_empty() && !right.is_empty() {
                return Some((left.to_string(), right.to_string()));
            }
        }
    }

    None
}

pub fn split_work_and_movement(
    raw_title: &str,
    explicit_work: Option<&str>,
    explicit_movement: Option<&str>,
    explicit_mvt_num: Option<u32>,
) -> ParsedWorkMovement {
    let title = raw_title.trim();
    let exp_work = explicit_work.map(str::trim).filter(|s| !s.is_empty());
    let exp_mvt = explicit_movement.map(str::trim).filter(|s| !s.is_empty());

    if let Some(work) = exp_work {
        let raw_mvt_source = if let Some(mvt) = exp_mvt {
            mvt.to_string()
        } else if let Some((left, right)) = find_work_movement_split(title)
            && left.eq_ignore_ascii_case(work)
        {
            right
        } else {
            title.to_string()
        };

        let (parsed_num, clean_mvt) = parse_movement_prefix(&raw_mvt_source);
        let final_mvt = if clean_mvt.eq_ignore_ascii_case(work) {
            String::new()
        } else {
            clean_mvt
        };
        return ParsedWorkMovement {
            work: work.to_string(),
            movement: final_mvt,
            movement_number: explicit_mvt_num.or(parsed_num),
        };
    }

    if let Some((left, right)) = find_work_movement_split(title) {
        let mvt_source = exp_mvt.unwrap_or(&right);
        let (parsed_num, clean_mvt) = parse_movement_prefix(mvt_source);
        return ParsedWorkMovement {
            work: left,
            movement: clean_mvt,
            movement_number: explicit_mvt_num.or(parsed_num),
        };
    }

    let (parsed_num, clean_title) = parse_movement_prefix(title);
    if parsed_num.is_some() {
        return ParsedWorkMovement {
            work: clean_title.clone(),
            movement: exp_mvt.unwrap_or(&clean_title).to_string(),
            movement_number: explicit_mvt_num.or(parsed_num),
        };
    }

    ParsedWorkMovement {
        work: title.to_string(),
        movement: exp_mvt.unwrap_or("").to_string(),
        movement_number: explicit_mvt_num,
    }
}

pub fn classify_composer_era(composer: &str, raw_genre: &str) -> Option<&'static str> {
    let tables = metadata_tables();
    let trimmed = composer.trim();
    if !trimmed.is_empty() {
        let norm = normalize_composer_name(trimmed).to_lowercase();
        if let Some(&era) = tables.composer_eras.get(&norm) {
            return Some(era);
        }
        let raw_lower = trimmed.to_lowercase();
        if let Some(&era) = tables.composer_eras.get(&raw_lower) {
            return Some(era);
        }
    }

    let g = raw_genre.trim().to_lowercase();
    if g.contains("baroque") || g.contains("巴洛克") {
        Some("baroque")
    } else if g.contains("romantic") || g.contains("浪漫") {
        Some("romantic")
    } else if g.contains("modern") || g.contains("contemporary") || g.contains("20th") || g.contains("现代") {
        Some("modern")
    } else if g.contains("classical era") || g.contains("古典主义") {
        Some("classical")
    } else {
        None
    }
}

pub fn classify_work_form(work_title: &str, raw_genre: &str) -> &'static str {
    let combined = format!("{} {}", work_title.trim(), raw_genre.trim()).to_lowercase();
    if combined.contains("symphon") || combined.contains("sinfonia") || combined.contains("交响") {
        return "symphonies";
    }
    if combined.contains("concerto")
        || combined.contains("konzert")
        || combined.contains("concertino")
        || combined.contains("协奏")
    {
        return "concertos";
    }
    if combined.contains("quartet")
        || combined.contains("quintet")
        || combined.contains("sextet")
        || combined.contains("septet")
        || combined.contains("octet")
        || combined.contains("string trio")
        || combined.contains("piano trio")
        || combined.contains("chamber")
        || combined.contains("四重奏")
        || combined.contains("五重奏")
        || combined.contains("三重奏")
        || combined.contains("室内乐")
    {
        return "chamber";
    }
    if combined.contains("sonata")
        || combined.contains("sonate")
        || combined.contains("partita")
        || combined.contains("variation")
        || combined.contains("nocturne")
        || combined.contains("etude")
        || combined.contains("étude")
        || combined.contains("prelude")
        || combined.contains("prélude")
        || combined.contains("fugue")
        || combined.contains("toccata")
        || combined.contains("impromptu")
        || combined.contains("ballade")
        || combined.contains("scherzo")
        || combined.contains("mazurka")
        || combined.contains("polonaise")
        || combined.contains("waltz")
        || combined.contains("valse")
        || combined.contains("chaconne")
        || combined.contains("solo")
        || combined.contains("奏鸣")
        || combined.contains("变奏曲")
        || combined.contains("夜曲")
        || combined.contains("练习曲")
        || combined.contains("前奏曲")
        || (combined.contains("suite") && !combined.contains("orchestral suite"))
        || combined.contains("组曲")
    {
        return "solo";
    }
    "other"
}

pub fn is_ensemble_name(name: &str) -> bool {
    let lower = name.trim().to_lowercase();
    !lower.is_empty() && ENSEMBLE_TOKENS.iter().any(|t| lower.contains(t))
}

pub fn separate_performers(
    artists: &[String],
    album_artist: Option<&str>,
    conductors: &[String],
    explicit_orchestra: Option<&str>,
    explicit_soloists: &[String],
) -> SeparatedPerformers {
    let conductor_keys: Vec<String> = conductors
        .iter()
        .map(|c| c.trim().to_lowercase())
        .filter(|c| !c.is_empty())
        .collect();

    let mut ensemble: Option<String> = explicit_orchestra
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string);
    let mut soloists: Vec<String> = explicit_soloists
        .iter()
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .collect();

    for raw in artists {
        let clean = raw.trim();
        if clean.is_empty() {
            continue;
        }
        let lower = clean.to_lowercase();
        if is_ensemble_name(clean) {
            if ensemble.is_none() {
                ensemble = Some(clean.to_string());
            }
            continue;
        }
        if conductor_keys.iter().any(|ck| ck == &lower) {
            continue;
        }
        if !soloists.iter().any(|s| s.eq_ignore_ascii_case(clean)) {
            soloists.push(clean.to_string());
        }
    }

    if ensemble.is_none()
        && let Some(aa) = album_artist.map(str::trim).filter(|s| !s.is_empty())
        && is_ensemble_name(aa)
    {
        ensemble = Some(aa.to_string());
    }

    SeparatedPerformers { soloists, ensemble }
}

pub fn abbreviate_ensemble(name: &str) -> String {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return String::new();
    }
    let tables = metadata_tables();
    if let Some(abbr) = tables.ensemble_abbreviations.get(&trimmed.to_lowercase()) {
        return abbr.clone();
    }

    let replacements = [
        ("Philharmonic Orchestra", "PO"),
        ("Symphony Orchestra", "SO"),
        ("Chamber Orchestra", "CO"),
        ("Radio Symphony Orchestra", "RSO"),
        ("Philharmoniker", "Phil."),
        ("Symphoniker", "Sym."),
    ];
    let mut out = trimmed.to_string();
    for (pat, rep) in replacements {
        if out.contains(pat) {
            out = out.replace(pat, rep);
        }
    }
    out
}

pub fn short_surname(name: &str) -> String {
    let trimmed = name.trim();
    if trimmed.is_empty() {
        return String::new();
    }
    trimmed
        .split_whitespace()
        .last()
        .unwrap_or(trimmed)
        .to_string()
}

pub fn build_classical_credit_tiers(
    composer: &str,
    conductors: &[String],
    soloists: &[String],
    ensemble: Option<&str>,
    year: Option<i32>,
) -> Vec<String> {
    let clean_composer = composer.trim();
    let clean_conductors: Vec<&str> = conductors
        .iter()
        .map(|s| s.trim())
        .filter(|s| !s.is_empty())
        .collect();
    let clean_soloists: Vec<&str> = soloists
        .iter()
        .map(|s| s.trim())
        .filter(|s| !s.is_empty())
        .collect();
    let clean_ensemble = ensemble.map(str::trim).filter(|s| !s.is_empty());
    let year_str = year.filter(|&y| y > 0).map(|y| y.to_string());

    fn join_parts(parts: &[Option<&str>]) -> String {
        parts
            .iter()
            .flatten()
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .collect::<Vec<_>>()
            .join(" · ")
    }

    fn format_performer_credit(conductors: &[&str], soloists: &[&str]) -> Option<String> {
        let c = if conductors.is_empty() {
            None
        } else {
            Some(conductors.join(", "))
        };
        let s = if soloists.is_empty() {
            None
        } else {
            Some(soloists.join(", "))
        };
        match (c, s) {
            (Some(cv), Some(sv)) => Some(format!("{cv} / {sv}")),
            (Some(cv), None) => Some(cv),
            (None, Some(sv)) => Some(sv),
            (None, None) => None,
        }
    }

    let perf_all = format_performer_credit(&clean_conductors, &clean_soloists);
    let first_solo = clean_soloists.first().copied();
    let perf_first_solo = format_performer_credit(
        &clean_conductors,
        first_solo.as_slice(),
    );
    let conductor_only = if clean_conductors.is_empty() {
        first_solo.map(str::to_string)
    } else {
        Some(clean_conductors.join(", "))
    };
    let abbr_ensemble = clean_ensemble.map(abbreviate_ensemble);

    let comp_opt = (!clean_composer.is_empty()).then_some(clean_composer);
    let yr_opt = year_str.as_deref();

    let raw_tiers = vec![
        join_parts(&[comp_opt, perf_all.as_deref(), clean_ensemble, yr_opt]),
        join_parts(&[comp_opt, perf_all.as_deref(), abbr_ensemble.as_deref(), yr_opt]),
        join_parts(&[
            comp_opt,
            perf_first_solo.as_deref(),
            abbr_ensemble.as_deref(),
            yr_opt,
        ]),
        join_parts(&[comp_opt, conductor_only.as_deref(), yr_opt]),
        join_parts(&[
            (!clean_composer.is_empty())
                .then(|| short_surname(clean_composer))
                .as_deref(),
            conductor_only.as_deref().map(short_surname).as_deref(),
            yr_opt,
        ]),
    ];

    let mut deduped = Vec::with_capacity(raw_tiers.len());
    for t in raw_tiers {
        if !t.is_empty() && !deduped.contains(&t) {
            deduped.push(t);
        }
    }
    deduped
}

#[cfg(test)]
mod tests {
    use super::*;

    const GOLDEN_JSON: &str = include_str!("../../../../protocol/fixtures/classical_golden.json");

    #[derive(Deserialize)]
    struct GoldenFile {
        is_classical_cases: Vec<IsClassicalCase>,
        split_work_cases: Vec<SplitWorkCase>,
        era_cases: Vec<EraCase>,
        form_cases: Vec<FormCase>,
        performer_separation_cases: Vec<PerformerSepCase>,
        credit_tier_cases: Vec<CreditTierCase>,
    }

    #[derive(Deserialize)]
    struct IsClassicalCase {
        name: String,
        genre: String,
        work: String,
        composer: String,
        conductor: String,
        title: String,
        expected: bool,
    }

    #[derive(Deserialize)]
    struct SplitWorkCase {
        name: String,
        raw_title: String,
        explicit_work: Option<String>,
        explicit_movement: Option<String>,
        explicit_mvt_num: Option<u32>,
        expected_work: String,
        expected_movement: String,
        expected_mvt_num: Option<u32>,
    }

    #[derive(Deserialize)]
    struct EraCase {
        composer: String,
        genre: String,
        expected: Option<String>,
    }

    #[derive(Deserialize)]
    struct FormCase {
        work: String,
        genre: String,
        expected: String,
    }

    #[derive(Deserialize)]
    struct PerformerSepCase {
        artists: Vec<String>,
        album_artist: Option<String>,
        conductors: Vec<String>,
        explicit_orchestra: Option<String>,
        explicit_soloists: Vec<String>,
        expected_soloists: Vec<String>,
        expected_ensemble: Option<String>,
    }

    #[derive(Deserialize)]
    struct CreditTierCase {
        composer: String,
        conductors: Vec<String>,
        soloists: Vec<String>,
        ensemble: Option<String>,
        year: Option<i32>,
        expected_tiers: Vec<String>,
    }

    #[test]
    fn passes_all_classical_golden_fixtures() {
        let golden: GoldenFile =
            serde_json::from_str(GOLDEN_JSON).expect("valid classical_golden.json");

        for c in golden.is_classical_cases {
            let actual = is_classical_track(
                &c.genre,
                (!c.work.is_empty()).then_some(c.work.as_str()),
                (!c.composer.is_empty()).then_some(c.composer.as_str()),
                (!c.conductor.is_empty()).then_some(c.conductor.as_str()),
                &c.title,
            );
            assert_eq!(actual, c.expected, "is_classical case failed: {}", c.name);
        }

        for c in golden.split_work_cases {
            let actual = split_work_and_movement(
                &c.raw_title,
                c.explicit_work.as_deref(),
                c.explicit_movement.as_deref(),
                c.explicit_mvt_num,
            );
            assert_eq!(actual.work, c.expected_work, "work failed: {}", c.name);
            assert_eq!(
                actual.movement, c.expected_movement,
                "movement failed: {}",
                c.name
            );
            assert_eq!(
                actual.movement_number, c.expected_mvt_num,
                "mvt_num failed: {}",
                c.name
            );
        }

        for c in golden.era_cases {
            let actual = classify_composer_era(&c.composer, &c.genre).map(str::to_string);
            assert_eq!(actual, c.expected, "era failed for {}", c.composer);
        }

        for c in golden.form_cases {
            let actual = classify_work_form(&c.work, &c.genre);
            assert_eq!(actual, c.expected, "form failed for {}", c.work);
        }

        for c in golden.performer_separation_cases {
            let actual = separate_performers(
                &c.artists,
                c.album_artist.as_deref(),
                &c.conductors,
                c.explicit_orchestra.as_deref(),
                &c.explicit_soloists,
            );
            assert_eq!(actual.soloists, c.expected_soloists);
            assert_eq!(actual.ensemble, c.expected_ensemble);
        }

        for c in golden.credit_tier_cases {
            let actual = build_classical_credit_tiers(
                &c.composer,
                &c.conductors,
                &c.soloists,
                c.ensemble.as_deref(),
                c.year,
            );
            assert_eq!(actual, c.expected_tiers);
        }
    }
}
