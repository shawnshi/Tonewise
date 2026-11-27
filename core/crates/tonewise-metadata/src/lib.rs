use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;

pub mod classical;
pub use classical::{
    ParsedWorkMovement, SeparatedPerformers, abbreviate_ensemble, build_classical_credit_tiers,
    classify_composer_era, classify_work_form, format_roman_numeral, is_classical_track,
    parse_movement_prefix, parse_roman_numeral, separate_performers, split_work_and_movement,
};

const ARTIST_WHITELIST_JSON: &str =
    include_str!("../../../../protocol/dictionaries/artist_whitelist.json");
const ARTIST_ALIASES_JSON: &str =
    include_str!("../../../../protocol/dictionaries/artist_aliases.json");
const GENRE_ALIASES_JSON: &str =
    include_str!("../../../../protocol/dictionaries/genre_aliases.json");

#[derive(Debug, Clone, Deserialize)]
struct ArtistWhitelistFile {
    protected_names: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
struct ArtistAliasesFile {
    #[serde(default)]
    canonical_aliases: HashMap<String, String>,
    #[serde(default)]
    multi_value_aliases: HashMap<String, Vec<String>>,
    #[serde(default)]
    composer_short_aliases: HashMap<String, String>,
}

struct ArtistAliasTable {
    canonical_aliases: HashMap<String, String>,
    multi_value_aliases: HashMap<String, Vec<String>>,
    composer_short_aliases: HashMap<String, String>,
}

static WHITELIST: OnceLock<Vec<String>> = OnceLock::new();
static ALIASES: OnceLock<ArtistAliasTable> = OnceLock::new();

pub fn artist_whitelist() -> &'static [String] {
    WHITELIST
        .get_or_init(|| {
            let parsed: ArtistWhitelistFile = serde_json::from_str(ARTIST_WHITELIST_JSON)
                .expect("invalid protocol/dictionaries/artist_whitelist.json");
            parsed.protected_names
        })
        .as_slice()
}

fn artist_aliases() -> &'static ArtistAliasTable {
    ALIASES.get_or_init(|| {
        let parsed: ArtistAliasesFile = serde_json::from_str(ARTIST_ALIASES_JSON)
            .expect("invalid protocol/dictionaries/artist_aliases.json");
        let mut canonical_aliases = HashMap::with_capacity(parsed.canonical_aliases.len());
        for (k, v) in parsed.canonical_aliases {
            let norm_k = normalize_punctuation_and_hygiene(&k).to_lowercase();
            let norm_v = normalize_punctuation_and_hygiene(&v);
            if !norm_k.is_empty() && !norm_v.is_empty() {
                canonical_aliases.insert(norm_k, norm_v);
            }
        }
        let mut multi_value_aliases = HashMap::with_capacity(parsed.multi_value_aliases.len());
        for (k, vals) in parsed.multi_value_aliases {
            let norm_k = normalize_punctuation_and_hygiene(&k).to_lowercase();
            let norm_vals: Vec<String> = vals
                .into_iter()
                .map(|v| normalize_punctuation_and_hygiene(&v))
                .filter(|v| !v.is_empty())
                .collect();
            if !norm_k.is_empty() && !norm_vals.is_empty() {
                multi_value_aliases.insert(norm_k, norm_vals);
            }
        }
        let mut composer_short_aliases =
            HashMap::with_capacity(parsed.composer_short_aliases.len());
        for (k, v) in parsed.composer_short_aliases {
            let norm_k = normalize_punctuation_and_hygiene(&k).to_lowercase();
            let norm_v = normalize_punctuation_and_hygiene(&v);
            if !norm_k.is_empty() && !norm_v.is_empty() {
                composer_short_aliases.insert(norm_k, norm_v);
            }
        }
        ArtistAliasTable {
            canonical_aliases,
            multi_value_aliases,
            composer_short_aliases,
        }
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DjVocals {
    Vocal,
    Instrumental,
    Unknown,
}

impl DjVocals {
    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "vocal" => Some(Self::Vocal),
            "instrumental" => Some(Self::Instrumental),
            "unknown" => Some(Self::Unknown),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Vocal => "vocal",
            Self::Instrumental => "instrumental",
            Self::Unknown => "unknown",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DjEnergy {
    Low,
    Medium,
    High,
    Unknown,
}

impl DjEnergy {
    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "low" => Some(Self::Low),
            "medium" => Some(Self::Medium),
            "high" => Some(Self::High),
            "unknown" => Some(Self::Unknown),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Low => "low",
            Self::Medium => "medium",
            Self::High => "high",
            Self::Unknown => "unknown",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DjScene {
    Focus,
    Coding,
    Relax,
    Energy,
    Pop,
}

impl DjScene {
    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "focus" => Some(Self::Focus),
            "coding" => Some(Self::Coding),
            "relax" => Some(Self::Relax),
            "energy" => Some(Self::Energy),
            "pop" => Some(Self::Pop),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Focus => "focus",
            Self::Coding => "coding",
            Self::Relax => "relax",
            Self::Energy => "energy",
            Self::Pop => "pop",
        }
    }
}

/// Parses and deduplicates multi-value `DJ_SCENE` tags against the 5 canonical scenes:
/// `focus`, `coding`, `relax`, `energy`, `pop`.
pub fn normalize_dj_scenes(raw: &str) -> Vec<String> {
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for part in raw.split([';', ',', '/', '\0']) {
        if let Some(scene) = DjScene::parse(part) {
            let canonical = scene.as_str();
            if seen.insert(canonical) {
                out.push(canonical.to_string());
            }
        }
    }
    out
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TempoBucket {
    #[serde(rename = "Very Slow")]
    VerySlow,
    Slow,
    Moderate,
    Fast,
    #[serde(rename = "Very Fast")]
    VeryFast,
}

pub fn bpm_to_tempo_bucket(bpm: f32) -> Option<TempoBucket> {
    if !bpm.is_finite() || bpm <= 0.0 {
        return None;
    }
    Some(match bpm {
        x if x < 65.0 => TempoBucket::VerySlow,
        x if x < 90.0 => TempoBucket::Slow,
        x if x < 120.0 => TempoBucket::Moderate,
        x if x < 150.0 => TempoBucket::Fast,
        _ => TempoBucket::VeryFast,
    })
}

pub fn split_multi_value(raw: &str) -> Vec<String> {
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for part in raw.split([';', '\0']) {
        let trimmed = part.trim();
        if !trimmed.is_empty() {
            let key = trimmed.to_lowercase();
            if seen.insert(key) {
                out.push(trimmed.to_string());
            }
        }
    }
    out
}

fn find_case_insensitive_span(haystack: &str, needle: &str) -> Option<(usize, usize)> {
    let needle_lower: Vec<char> = needle.chars().flat_map(char::to_lowercase).collect();
    if needle_lower.is_empty() {
        return None;
    }
    for (start_byte, _) in haystack.char_indices() {
        let mut matched = 0usize;
        let mut failed = false;
        for (rel_byte, h_ch) in haystack[start_byte..].char_indices() {
            for lc in h_ch.to_lowercase() {
                if matched < needle_lower.len() && needle_lower[matched] == lc {
                    matched += 1;
                } else {
                    failed = true;
                    break;
                }
            }
            if failed {
                break;
            }
            if matched == needle_lower.len() {
                let end_byte = start_byte + rel_byte + h_ch.len_utf8();
                return Some((start_byte, end_byte));
            }
        }
    }
    None
}

fn normalize_punctuation_and_whitespace(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    let mut prev_space = true;
    for ch in raw.chars() {
        match ch {
            '\u{200B}' | '\u{FEFF}' => {}
            '\u{2010}' | '\u{2011}' | '\u{2012}' | '\u{2013}' | '\u{2212}' | '\u{FE63}'
            | '\u{FF0D}' => {
                out.push('-');
                prev_space = false;
            }
            '\u{2018}' | '\u{2019}' | '\u{201B}' | '\u{02BC}' | '\u{FF07}' => {
                out.push('\'');
                prev_space = false;
            }
            c if c.is_whitespace() => {
                if !prev_space {
                    out.push(' ');
                    prev_space = true;
                }
            }
            c => {
                out.push(c);
                prev_space = false;
            }
        }
    }
    if out.ends_with(' ') {
        out.pop();
    }
    out
}

/// Normalizes Unicode hyphens/apostrophes, strips zero-width characters, collapses whitespace,
/// and removes trailing `(writer)` role suffixes (case-insensitive).
pub fn normalize_punctuation_and_hygiene(raw: &str) -> String {
    let mut s = normalize_punctuation_and_whitespace(raw);
    loop {
        let lower = s.to_ascii_lowercase();
        if lower.ends_with("(writer)") {
            let new_len = s.len() - "(writer)".len();
            s.truncate(new_len);
            s = s.trim_end().to_string();
        } else {
            break;
        }
    }
    s.trim().to_string()
}

pub fn normalize_performer_name(raw: &str) -> String {
    let clean = normalize_punctuation_and_hygiene(raw);
    if clean.is_empty() {
        return String::new();
    }
    let table = artist_aliases();
    let key = clean.to_lowercase();
    if let Some(canonical) = table.canonical_aliases.get(&key) {
        return canonical.clone();
    }
    clean
}

pub fn normalize_composer_name(raw: &str) -> String {
    let clean = normalize_punctuation_and_hygiene(raw);
    if clean.is_empty() {
        return String::new();
    }
    let table = artist_aliases();
    let key = clean.to_lowercase();
    if let Some(expanded) = table.composer_short_aliases.get(&key) {
        let expanded_key = expanded.to_lowercase();
        if let Some(canonical) = table.canonical_aliases.get(&expanded_key) {
            return canonical.clone();
        }
        return expanded.clone();
    }
    if let Some(canonical) = table.canonical_aliases.get(&key) {
        return canonical.clone();
    }
    clean
}

fn protect_whitelist_and_jrsr(working: &mut String) -> Vec<(String, String)> {
    let mut replacements: Vec<(String, String)> = Vec::new();
    let mut seq = 0usize;
    for (idx, protected) in artist_whitelist().iter().enumerate() {
        while let Some((start, end)) = find_case_insensitive_span(working, protected) {
            let token = format!("__TW_PROTECTED_{idx}_{seq}__");
            seq += 1;
            let original_slice = working[start..end].to_string();
            working.replace_range(start..end, &token);
            replacements.push((token, original_slice));
        }
    }

    loop {
        let mut found: Option<(usize, usize)> = None;
        for (comma_idx, ch) in working.char_indices() {
            if ch != ',' {
                continue;
            }
            let after_comma = comma_idx + 1;
            let rest_after_comma = &working[after_comma..];
            let ws_bytes = rest_after_comma
                .char_indices()
                .find(|(_, c)| !c.is_whitespace())
                .map(|(i, _)| i)
                .unwrap_or(rest_after_comma.len());
            let word_start = after_comma + ws_bytes;
            let tail = &working[word_start..];
            let matched_len = ["jr.", "sr.", "jr", "sr"]
                .into_iter()
                .find(|pat| {
                    tail.len() >= pat.len()
                        && tail.is_char_boundary(pat.len())
                        && tail[..pat.len()].eq_ignore_ascii_case(pat)
                })
                .map(|pat| pat.len());
            if let Some(mlen) = matched_len {
                let end = word_start + mlen;
                let boundary_ok = working[end..]
                    .chars()
                    .next()
                    .is_none_or(|c| c.is_whitespace() || matches!(c, ',' | ';' | '/' | '\0'));
                let next_non_ws_ok = working[end..]
                    .trim_start()
                    .chars()
                    .next()
                    .is_none_or(|c| matches!(c, ',' | ';' | '/' | '\0' | '&'));
                if boundary_ok && next_non_ws_ok {
                    found = Some((comma_idx, end));
                    break;
                }
            }
        }
        if let Some((start, end)) = found {
            let token = format!("__TW_JRSR_{seq}__");
            seq += 1;
            let original_slice = working[start..end].to_string();
            working.replace_range(start..end, &token);
            replacements.push((token, original_slice));
        } else {
            break;
        }
    }

    replacements
}

fn restore_placeholders(s: &str, replacements: &[(String, String)]) -> String {
    let mut res = s.trim().to_string();
    for (token, original) in replacements.iter().rev() {
        if res.contains(token.as_str()) {
            res = res.replace(token.as_str(), original);
        }
    }
    res.trim().to_string()
}

pub fn normalize_album_artist(raw: &str) -> String {
    let clean = normalize_punctuation_and_hygiene(raw);
    if clean.is_empty() {
        return String::new();
    }
    let table = artist_aliases();
    let full_key = clean.to_lowercase();
    if let Some(multi) = table.multi_value_aliases.get(&full_key) {
        return multi.join(", ");
    }
    if let Some(canonical) = table.canonical_aliases.get(&full_key) {
        return canonical.clone();
    }

    let mut working = clean;
    let replacements = protect_whitelist_and_jrsr(&mut working);

    let delimiters = ["; ", ", ", " & ", ";", ","];
    let mut out = String::new();
    let mut pos = 0usize;
    while pos <= working.len() {
        let mut best: Option<(usize, &str)> = None;
        for &delim in &delimiters {
            if let Some(rel) = working[pos..].find(delim) {
                let abs = pos + rel;
                match best {
                    None => best = Some((abs, delim)),
                    Some((b_abs, b_delim)) => {
                        if abs < b_abs || (abs == b_abs && delim.len() > b_delim.len()) {
                            best = Some((abs, delim));
                        }
                    }
                }
            }
        }

        let (seg_end, next_pos, matched_delim) = match best {
            Some((abs, delim)) => (abs, abs + delim.len(), Some(delim)),
            None => (working.len(), working.len() + 1, None),
        };

        let restored = restore_placeholders(&working[pos..seg_end], &replacements);
        let seg_clean = normalize_punctuation_and_hygiene(&restored);
        if !seg_clean.is_empty() {
            let seg_key = seg_clean.to_lowercase();
            if let Some(multi) = table.multi_value_aliases.get(&seg_key) {
                out.push_str(&multi.join(", "));
            } else {
                out.push_str(&normalize_performer_name(&seg_clean));
            }
        }

        if let Some(delim) = matched_delim {
            out.push_str(delim);
        }
        pos = next_pos;
    }

    out.trim().to_string()
}

fn split_entities_internal(raw: &str, composer_mode: bool) -> Vec<String> {
    let mut working = normalize_punctuation_and_whitespace(raw);
    if working.is_empty() {
        return Vec::new();
    }

    let replacements = protect_whitelist_and_jrsr(&mut working);
    let table = artist_aliases();
    let mut result = Vec::new();
    let mut seen = HashSet::new();

    for chunk in working.split([';', '/', '\0']) {
        let restored = restore_placeholders(chunk, &replacements);
        let cleaned = normalize_punctuation_and_hygiene(&restored);
        if cleaned.is_empty() {
            continue;
        }
        let key = cleaned.to_lowercase();
        if let Some(expanded_list) = table.multi_value_aliases.get(&key) {
            for item in expanded_list {
                let normalized = if composer_mode {
                    normalize_composer_name(item)
                } else {
                    normalize_performer_name(item)
                };
                if !normalized.is_empty() && seen.insert(normalized.to_lowercase()) {
                    result.push(normalized);
                }
            }
        } else {
            let normalized = if composer_mode {
                normalize_composer_name(&cleaned)
            } else {
                normalize_performer_name(&cleaned)
            };
            if !normalized.is_empty() && seen.insert(normalized.to_lowercase()) {
                result.push(normalized);
            }
        }
    }
    result
}

/// Splits artist tags on `/` and `;` while protecting known multi-word/delimited band names
/// from `protocol/dictionaries/artist_whitelist.json` (e.g. `Above & Beyond`, `AC/DC`),
/// normalizing punctuation/hygiene and canonical aliases.
pub fn split_artists_preserving_whitelist(raw: &str) -> Vec<String> {
    split_entities_internal(raw, false)
}

/// Splits composer tags on `/` and `;` while protecting whitelisted names, normalizing punctuation,
/// expanding composer-only surname abbreviations and canonical aliases.
pub fn split_composers_preserving_whitelist(raw: &str) -> Vec<String> {
    split_entities_internal(raw, true)
}

/// Extracts a 4-digit year (1800..=2100) from raw date/year strings such as `"2015"` or `"2019-08-15"`.
pub fn normalize_year(raw: &str) -> Option<u16> {
    let trimmed = raw.trim();
    if trimmed.len() < 4 {
        return None;
    }
    let bytes = trimmed.as_bytes();
    for win in bytes.windows(4) {
        if win.iter().all(|b| b.is_ascii_digit()) {
            let s = std::str::from_utf8(win).ok()?;
            if let Ok(year) = s.parse::<u16>()
                && (1800..=2100).contains(&year)
            {
                return Some(year);
            }
        }
    }
    None
}

#[derive(Debug, Clone, Deserialize)]
struct GenreAliasesFile {
    #[serde(default)]
    canonical_aliases: HashMap<String, String>,
}

static GENRE_ALIASES: OnceLock<HashMap<String, String>> = OnceLock::new();

fn genre_aliases() -> &'static HashMap<String, String> {
    GENRE_ALIASES.get_or_init(|| {
        let parsed: GenreAliasesFile = serde_json::from_str(GENRE_ALIASES_JSON)
            .expect("invalid protocol/dictionaries/genre_aliases.json");
        let mut map = HashMap::with_capacity(parsed.canonical_aliases.len());
        for (k, v) in parsed.canonical_aliases {
            let norm_k = k.trim().to_lowercase();
            let norm_v = v.trim().to_string();
            if !norm_k.is_empty() && !norm_v.is_empty() {
                map.insert(norm_k, norm_v);
            }
        }
        map
    })
}

pub fn normalize_genre(raw: &str) -> String {
    let trimmed_all = raw.trim();
    if trimmed_all.is_empty() {
        return String::new();
    }

    let aliases = genre_aliases();
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for part in trimmed_all.split([';', ',', '|', '、']) {
        let trimmed = part.trim();
        if trimmed.is_empty() {
            continue;
        }
        let canonical = aliases
            .get(&trimmed.to_lowercase())
            .cloned()
            .unwrap_or_else(|| trimmed.to_string());
        if seen.insert(canonical.to_lowercase()) {
            out.push(canonical);
        }
    }
    out.join("; ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn whitelist_protects_delimited_artist_names() {
        assert_eq!(
            split_artists_preserving_whitelist("Above & Beyond"),
            vec!["Above & Beyond"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("AC/DC / Slash"),
            vec!["AC/DC", "Slash"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("卢家宏 / 李宗盛"),
            vec!["卢家宏", "李宗盛"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("İbrahim / AC/DC"),
            vec!["İbrahim", "AC/DC"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("AC/DC / AC/DC"),
            vec!["AC/DC"]
        );
    }

    #[test]
    fn normalizes_year_and_multi_scene() {
        assert_eq!(normalize_year("2015"), Some(2015));
        assert_eq!(normalize_year("2019-..."), Some(2019));
        assert_eq!(normalize_year("2000-04-12T00:00:00Z"), Some(2000));
        assert_eq!(normalize_year("unknown"), None);
        assert_eq!(
            split_multi_value("relax; focus ;RELAX"),
            vec!["relax", "focus"]
        );
        assert_eq!(bpm_to_tempo_bucket(58.0), Some(TempoBucket::VerySlow));
        assert_eq!(bpm_to_tempo_bucket(108.0), Some(TempoBucket::Moderate));
    }

    #[test]
    fn validates_canonical_dj_enums_and_scenes() {
        assert_eq!(DjVocals::parse("vocal"), Some(DjVocals::Vocal));
        assert_eq!(DjVocals::parse("Instrumental"), Some(DjVocals::Instrumental));
        assert_eq!(DjVocals::parse(" UNKNOWN "), Some(DjVocals::Unknown));
        assert_eq!(DjVocals::parse("choir"), None);

        assert_eq!(DjEnergy::parse("low"), Some(DjEnergy::Low));
        assert_eq!(DjEnergy::parse("medium"), Some(DjEnergy::Medium));
        assert_eq!(DjEnergy::parse("HIGH"), Some(DjEnergy::High));
        assert_eq!(DjEnergy::parse("unknown"), Some(DjEnergy::Unknown));
        assert_eq!(DjEnergy::parse("extreme"), None);

        assert_eq!(
            normalize_dj_scenes("Focus; coding, relax / invalid; ENERGY; pop; focus"),
            vec!["focus", "coding", "relax", "energy", "pop"]
        );
    }

    #[test]
    fn normalizes_punctuation_hygiene_and_aliases() {
        assert_eq!(
            normalize_punctuation_and_hygiene("Camille Saint\u{2010}Sa\u{00EB}ns"),
            "Camille Saint-Saëns"
        );
        assert_eq!(
            normalize_punctuation_and_hygiene("Destiny\u{2019}s Child"),
            "Destiny's Child"
        );
        assert_eq!(
            normalize_punctuation_and_hygiene("  Fritz\u{00A0}  Reiner  "),
            "Fritz Reiner"
        );
        assert_eq!(
            normalize_punctuation_and_hygiene("Beyonc\u{00E9} (Writer)"),
            "Beyoncé"
        );
        assert_eq!(
            normalize_punctuation_and_hygiene("Royce da 5\u{2032}9\u{2033}"),
            "Royce da 5\u{2032}9\u{2033}"
        );

        assert_eq!(
            split_artists_preserving_whitelist(
                "Дмитрий Дмитриевич Шостакович; Berliner Philharmoniker; Николaй Кaпустин"
            ),
            vec![
                "Dmitri Shostakovich",
                "Berlin Philharmonic",
                "Nikolai Kapustin"
            ]
        );
        assert_eq!(
            split_artists_preserving_whitelist("Արամ Խաչատրյան / 小澤征爾"),
            vec!["Aram Khachaturian", "Seiji Ozawa"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("Модест Петрович Мусоргский-Maurice Ravel"),
            vec!["Modest Mussorgsky", "Maurice Ravel"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("Tugan Sokhiev: Vienna Philharmonic Orchestra"),
            vec!["Tugan Sokhiev", "Vienna Philharmonic"]
        );
        assert_eq!(
            split_artists_preserving_whitelist("Monk"),
            vec!["Monk"]
        );
        assert_eq!(
            split_composers_preserving_whitelist("Monk; Bach; P. Lawrence; Beyoncé (writer)"),
            vec![
                "Thelonious Monk",
                "Johann Sebastian Bach",
                "Philip Lawrence",
                "Beyoncé"
            ]
        );
        assert_eq!(
            normalize_album_artist(
                "Ludwig van Beethoven; Bayerisches Staatsorchester, Carlos Kleiber"
            ),
            "Ludwig van Beethoven; Bavarian State Orchestra, Carlos Kleiber"
        );
        assert_eq!(
            normalize_album_artist("Johann Strauss, Jr."),
            "Johann Strauss II"
        );
        assert_eq!(
            normalize_album_artist("Above & Beyond, Wiener Philharmoniker"),
            "Above & Beyond, Vienna Philharmonic"
        );
    }

    #[test]
    fn normalizes_genres_across_scripts_cases_and_subgenres() {
        assert_eq!(normalize_genre("  ポップ "), "Pop");
        assert_eq!(normalize_genre("ワールド"), "World Music");
        assert_eq!(normalize_genre("Europe"), "World Music");
        assert_eq!(normalize_genre("クラシック"), "Classical");
        assert_eq!(normalize_genre("Cantopop/HK-Pop"), "Cantopop");
        assert_eq!(normalize_genre("C-Pop"), "Mandopop");
        assert_eq!(normalize_genre("中国风"), "Mandopop");
        assert_eq!(normalize_genre("reggaetón"), "Latin");
        assert_eq!(normalize_genre("R&B/Soul"), "R&B");
        assert_eq!(normalize_genre("ヒップホップ／ラップ"), "Hip-Hop");
        assert_eq!(normalize_genre("Singer/Songwriter; pop; 流行"), "Folk; Pop");
        assert_eq!(normalize_genre("   "), "");
    }
}
