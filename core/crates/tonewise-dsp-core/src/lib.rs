pub mod analysis;

pub use analysis::{
    AudioAnalysisError, AudioAnalysisResult, ENVELOPE_POINTS, analyze_audio_file, estimate_bpm,
    estimate_track_replaygain_db, extract_150_rms_envelope,
};

use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FilterKind {
    Peaking,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BiquadFilter {
    pub kind: FilterKind,
    pub freq_hz: f32,
    pub gain_db: f32,
    pub q: f32,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct DspProfile {
    pub preamp_db: f32,
    pub filters: Vec<BiquadFilter>,
}

#[derive(Debug, Error)]
pub enum DspParseError {
    #[error("invalid numeric value on line {line}: {detail}")]
    InvalidNumber { line: usize, detail: String },
}

pub fn parse_equalizer_apo(text: &str) -> Result<DspProfile, DspParseError> {
    let mut profile = DspProfile::default();
    for (idx, raw_line) in text.lines().enumerate() {
        let line_no = idx + 1;
        let line = raw_line.trim().trim_start_matches('\u{feff}');
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(rest) = line.strip_prefix("Preamp:") {
            let db_str = rest.trim().trim_end_matches("dB").trim();
            profile.preamp_db = db_str.parse::<f32>().map_err(|e| DspParseError::InvalidNumber {
                line: line_no,
                detail: e.to_string(),
            })?;
        } else if let Some(rest) = line.strip_prefix("Filter:") {
            let tokens: Vec<&str> = rest.split_whitespace().collect();
            if tokens.len() < 4 || !tokens[0].eq_ignore_ascii_case("ON") {
                continue;
            }
            let kind = match tokens[1].to_uppercase().as_str() {
                "PK" => FilterKind::Peaking,
                "LSC" | "LS" => FilterKind::LowShelf,
                "HSC" | "HS" => FilterKind::HighShelf,
                "LP" | "LPQ" => FilterKind::LowPass,
                "HP" | "HPQ" => FilterKind::HighPass,
                _ => continue,
            };
            let mut freq_hz = None;
            let mut gain_db = 0.0f32;
            let mut q = 0.707f32;
            let mut i = 2;
            while i + 1 < tokens.len() {
                match tokens[i] {
                    "Fc" => {
                        freq_hz = Some(tokens[i + 1].parse::<f32>().map_err(|e| {
                            DspParseError::InvalidNumber {
                                line: line_no,
                                detail: e.to_string(),
                            }
                        })?);
                        i += 2;
                    }
                    "Gain" => {
                        gain_db = tokens[i + 1].parse::<f32>().map_err(|e| {
                            DspParseError::InvalidNumber {
                                line: line_no,
                                detail: e.to_string(),
                            }
                        })?;
                        i += 2;
                    }
                    "Q" => {
                        q = tokens[i + 1].parse::<f32>().map_err(|e| {
                            DspParseError::InvalidNumber {
                                line: line_no,
                                detail: e.to_string(),
                            }
                        })?;
                        i += 2;
                    }
                    _ => i += 1,
                }
            }
            if let Some(fc) = freq_hz {
                profile.filters.push(BiquadFilter {
                    kind,
                    freq_hz: fc,
                    gain_db,
                    q,
                });
            }
        }
    }
    Ok(profile)
}

/// Computes clipping-safe total preamp gain in dB:
/// `G_total = min(replay_gain_db - max(0, max_positive_eq_db), explicit_preamp_db)`.
pub fn effective_preamp_db(profile: &DspProfile, replay_gain_db: f32) -> f32 {
    let max_boost = profile
        .filters
        .iter()
        .map(|f| f.gain_db.max(0.0))
        .fold(0.0f32, f32::max);
    let rg_envelope = replay_gain_db - max_boost;
    rg_envelope.min(profile.preamp_db + replay_gain_db)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_equalizer_apo_and_guards_clipping_preamp() {
        let apo = "\
Preamp: -4.5 dB
Filter: ON LSC Fc 85 Hz Gain 4.5 dB Q 0.71
Filter: ON HSC Fc 6000 Hz Gain -2.5 dB Q 0.71
";
        let profile = parse_equalizer_apo(apo).expect("valid EqualizerAPO");
        assert_eq!(profile.preamp_db, -4.5);
        assert_eq!(profile.filters.len(), 2);
        assert_eq!(profile.filters[0].kind, FilterKind::LowShelf);
        assert!((effective_preamp_db(&profile, -3.0) - (-7.5)).abs() < 1e-5);
    }
}
