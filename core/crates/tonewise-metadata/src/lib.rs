use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::sync::OnceLock;

const ARTIST_WHITELIST_JSON: &str =
    include_str!("../../../../protocol/dictionaries/artist_whitelist.json");

#[derive(Debug, Clone, Deserialize)]
struct ArtistWhitelistFile {
    protected_names: Vec<String>,
}

static WHITELIST: OnceLock<Vec<String>> = OnceLock::new();

pub fn artist_whitelist() -> &'static [String] {
    WHITELIST
        .get_or_init(|| {
            let parsed: ArtistWhitelistFile = serde_json::from_str(ARTIST_WHITELIST_JSON)
                .expect("invalid protocol/dictionaries/artist_whitelist.json");
            parsed.protected_names
        })
        .as_slice()
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

/// Splits artist tags on `/` and `;` while protecting known multi-word/delimited band names
/// from `protocol/dictionaries/artist_whitelist.json` (e.g. `Above & Beyond`, `AC/DC`).
pub fn split_artists_preserving_whitelist(raw: &str) -> Vec<String> {
    let mut working = raw.trim().to_string();
    if working.is_empty() {
        return Vec::new();
    }

    let mut replacements: Vec<(String, String)> = Vec::new();
    for (idx, protected) in artist_whitelist().iter().enumerate() {
        if let Some(pos) = working.to_lowercase().find(&protected.to_lowercase()) {
            let token = format!("__TW_PROTECTED_{idx}__");
            let end = pos + protected.len();
            let original_slice = working[pos..end].to_string();
            working.replace_range(pos..end, &token);
            replacements.push((token, original_slice));
        }
    }

    let mut result = Vec::new();
    let mut seen = HashSet::new();
    for chunk in working.split([';', '/', '\0']) {
        let mut restored = chunk.trim().to_string();
        for (token, original) in &replacements {
            if restored.contains(token) {
                restored = restored.replace(token, original);
            }
        }
        let cleaned = restored.trim();
        if !cleaned.is_empty() {
            let key = cleaned.to_lowercase();
            if seen.insert(key) {
                result.push(cleaned.to_string());
            }
        }
    }
    result
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
}
