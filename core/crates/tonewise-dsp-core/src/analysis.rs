use serde::{Deserialize, Serialize};
use std::fs::File;
use std::path::Path;
use symphonia::core::audio::SampleBuffer;
use symphonia::core::codecs::DecoderOptions;
use symphonia::core::errors::Error as SymphoniaError;
use symphonia::core::formats::FormatOptions;
use symphonia::core::io::MediaSourceStream;
use symphonia::core::meta::MetadataOptions;
use symphonia::core::probe::Hint;
use thiserror::Error;
use tonewise_metadata::{TempoBucket, bpm_to_tempo_bucket};

pub const ENVELOPE_POINTS: usize = 150;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AudioAnalysisResult {
    pub codec: String,
    pub duration_ms: u64,
    pub sample_rate_hz: u32,
    pub bit_depth: Option<u32>,
    pub bitrate_kbps: Option<u32>,
    pub waveform_150: Vec<u8>,
    pub estimated_bpm: Option<f32>,
    pub tempo_bucket: Option<TempoBucket>,
    pub replaygain_track_db: Option<f32>,
    pub peak_linear: f32,
}

#[derive(Debug, Error)]
pub enum AudioAnalysisError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("audio probe/decode error: {0}")]
    Decode(String),
    #[error("no supported audio track found")]
    NoAudioTrack,
}

/// Extracts a 150-point normalized RMS envelope (`0..=255`) from mono `f32` samples.
pub fn extract_150_rms_envelope(mono_samples: &[f32]) -> [u8; ENVELOPE_POINTS] {
    let mut rms = [0.0f32; ENVELOPE_POINTS];
    if mono_samples.is_empty() {
        return [0u8; ENVELOPE_POINTS];
    }

    let len = mono_samples.len();
    for (bucket, slot) in rms.iter_mut().enumerate() {
        let start = bucket * len / ENVELOPE_POINTS;
        let end = ((bucket + 1) * len / ENVELOPE_POINTS).max(start + 1).min(len);
        let slice = &mono_samples[start..end];
        let sum_sq: f64 = slice.iter().map(|&s| (s as f64) * (s as f64)).sum();
        *slot = (sum_sq / slice.len() as f64).sqrt() as f32;
    }

    normalize_rms_buckets(&rms)
}

fn normalize_rms_buckets(rms: &[f32; ENVELOPE_POINTS]) -> [u8; ENVELOPE_POINTS] {
    let max_rms = rms.iter().copied().fold(0.0f32, f32::max);
    let mut out = [0u8; ENVELOPE_POINTS];
    if max_rms <= 1e-7 {
        return out;
    }
    for (i, &val) in rms.iter().enumerate() {
        let norm = (val / max_rms).clamp(0.0, 1.0);
        // Mild perceptual curve so quiet classical passages remain visible without saturating peaks.
        out[i] = (norm.powf(0.8) * 255.0).round().clamp(0.0, 255.0) as u8;
    }
    out
}

/// Estimates track BPM in `[40.0, 240.0]` using energy onset detection + autocorrelation.
pub fn estimate_bpm(mono_samples: &[f32], sample_rate_hz: u32) -> Option<f32> {
    if sample_rate_hz == 0 || mono_samples.len() < sample_rate_hz as usize * 3 {
        return None;
    }

    // 10 ms frame step (100 Hz onset envelope resolution)
    let hop = (sample_rate_hz as usize / 100).max(1);
    let env_rate = sample_rate_hz as f32 / hop as f32;
    let max_samples = mono_samples.len().min(sample_rate_hz as usize * 90);
    let mut frame_energies = Vec::with_capacity(max_samples / hop);

    for chunk in mono_samples[..max_samples].chunks_exact(hop) {
        let e: f32 = chunk.iter().map(|&s| s * s).sum::<f32>() / chunk.len() as f32;
        frame_energies.push(e.sqrt());
    }
    if frame_energies.len() < 300 {
        return None;
    }

    // Positive half-wave first difference onset curve
    let mut onset = Vec::with_capacity(frame_energies.len() - 1);
    for w in frame_energies.windows(2) {
        onset.push((w[1] - w[0]).max(0.0));
    }
    let mean_onset = onset.iter().sum::<f32>() / onset.len() as f32;
    if mean_onset <= 1e-6 {
        return None;
    }
    for v in &mut onset {
        *v = (*v - mean_onset).max(0.0);
    }

    let min_lag = ((60.0 / 240.0) * env_rate).round().max(1.0) as usize;
    let max_lag = ((60.0 / 40.0) * env_rate).round() as usize;
    if max_lag >= onset.len() / 2 {
        return None;
    }

    let mut best_lag = 0usize;
    let mut best_score = 0.0f64;

    for lag in min_lag..=max_lag {
        let mut corr = 0.0f64;
        for i in 0..(onset.len() - lag) {
            corr += (onset[i] as f64) * (onset[i + lag] as f64);
        }
        // Mild tempo prior favoring musical 75..155 BPM to avoid sub-harmonic / double-time drift
        let candidate_bpm = 60.0 * env_rate / lag as f32;
        let prior = if (75.0..=155.0).contains(&candidate_bpm) {
            1.15
        } else {
            1.0
        };
        let weighted = corr * prior;
        if weighted > best_score {
            best_score = weighted;
            best_lag = lag;
        }
    }

    if best_lag == 0 || best_score <= 0.0 {
        return None;
    }

    let mut bpm = 60.0 * env_rate / best_lag as f32;
    while bpm < 60.0 {
        bpm *= 2.0;
    }
    while bpm > 180.0 {
        bpm *= 0.5;
    }
    Some((bpm * 10.0).round() / 10.0)
}

/// Estimates ReplayGain 2.0 track gain in dB referenced to `-18.0 dBFS` RMS.
pub fn estimate_track_replaygain_db(mono_samples: &[f32]) -> Option<f32> {
    if mono_samples.is_empty() {
        return None;
    }
    let sum_sq: f64 = mono_samples.iter().map(|&s| (s as f64) * (s as f64)).sum();
    let rms = (sum_sq / mono_samples.len() as f64).sqrt();
    if rms <= 1e-8 {
        return None;
    }
    let dbfs = 20.0 * rms.log10();
    let gain = (-18.0 - dbfs).clamp(-24.0, 24.0);
    Some(((gain * 100.0).round() / 100.0) as f32)
}

/// Streams and decodes any Symphonia-supported audio file in one pass to extract duration,
/// 150-point RMS waveform, transient BPM estimate, and ReplayGain estimate.
pub fn analyze_audio_file(path: &Path) -> Result<AudioAnalysisResult, AudioAnalysisError> {
    let file = File::open(path)?;
    let mss = MediaSourceStream::new(Box::new(file), Default::default());

    let mut hint = Hint::new();
    if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
        hint.with_extension(ext);
    }

    let probed = symphonia::default::get_probe()
        .format(
            &hint,
            mss,
            &FormatOptions::default(),
            &MetadataOptions::default(),
        )
        .map_err(|e| AudioAnalysisError::Decode(e.to_string()))?;

    let mut format = probed.format;
    let track = format
        .default_track()
        .ok_or(AudioAnalysisError::NoAudioTrack)?;
    let track_id = track.id;
    let codec_params = track.codec_params.clone();

    let sample_rate_hz = codec_params.sample_rate.unwrap_or(44100);
    let bit_depth = codec_params.bits_per_sample;
    let total_frames_hint = codec_params.n_frames;

    let codec_name = format!("{:?}", codec_params.codec);

    let mut decoder = symphonia::default::get_codecs()
        .make(&codec_params, &DecoderOptions::default())
        .map_err(|e| AudioAnalysisError::Decode(e.to_string()))?;

    let mut sample_buf: Option<SampleBuffer<f32>> = None;
    let mut peak_linear = 0.0f32;
    let mut total_frames: u64 = 0;

    // Accumulate up to 90s of mono audio for BPM detection; keep streaming RMS buckets bounded
    let bpm_cap = sample_rate_hz as usize * 90;
    let mut bpm_mono = Vec::with_capacity(bpm_cap.min(sample_rate_hz as usize * 30));

    // Also keep per-block RMS history (~20ms blocks) that re-bins cleanly to 150 points
    // without holding full multi-hour PCM buffers in memory.
    let block_frames = (sample_rate_hz as usize / 50).max(256);
    let mut block_rms_sq: Vec<f64> = Vec::with_capacity(
        total_frames_hint
            .map(|n| (n as usize / block_frames).max(150))
            .unwrap_or(4096),
    );
    let mut cur_block_sum_sq = 0.0f64;
    let mut cur_block_count = 0usize;

    let mut global_sum_sq = 0.0f64;

    loop {
        let packet = match format.next_packet() {
            Ok(p) => p,
            Err(SymphoniaError::IoError(ref err))
                if err.kind() == std::io::ErrorKind::UnexpectedEof =>
            {
                break;
            }
            Err(SymphoniaError::ResetRequired) => break,
            Err(e) => return Err(AudioAnalysisError::Decode(e.to_string())),
        };

        if packet.track_id() != track_id {
            continue;
        }

        let decoded = match decoder.decode(&packet) {
            Ok(d) => d,
            Err(SymphoniaError::DecodeError(_)) => continue,
            Err(e) => return Err(AudioAnalysisError::Decode(e.to_string())),
        };

        let spec = *decoded.spec();
        let ch_count = spec.channels.count().max(1);
        let frames = decoded.frames();
        if frames == 0 {
            continue;
        }

        let buf = sample_buf.get_or_insert_with(|| {
            SampleBuffer::<f32>::new(decoded.capacity() as u64, spec)
        });
        if buf.capacity() < frames * ch_count {
            *buf = SampleBuffer::<f32>::new(decoded.capacity() as u64, spec);
        }
        buf.copy_interleaved_ref(decoded);
        let interleaved = buf.samples();

        for frame_slice in interleaved.chunks_exact(ch_count) {
            let mut mono = 0.0f32;
            for &s in frame_slice {
                let a = s.abs();
                if a > peak_linear {
                    peak_linear = a;
                }
                mono += s;
            }
            mono /= ch_count as f32;

            let sq = (mono as f64) * (mono as f64);
            global_sum_sq += sq;
            cur_block_sum_sq += sq;
            cur_block_count += 1;
            if cur_block_count >= block_frames {
                block_rms_sq.push(cur_block_sum_sq / cur_block_count as f64);
                cur_block_sum_sq = 0.0;
                cur_block_count = 0;
            }

            if bpm_mono.len() < bpm_cap {
                bpm_mono.push(mono);
            }
            total_frames += 1;
        }
    }

    if cur_block_count > 0 {
        block_rms_sq.push(cur_block_sum_sq / cur_block_count as f64);
    }

    let mut rms_150 = [0.0f32; ENVELOPE_POINTS];
    if !block_rms_sq.is_empty() {
        let n = block_rms_sq.len();
        for (i, slot) in rms_150.iter_mut().enumerate() {
            let s = i * n / ENVELOPE_POINTS;
            let e = ((i + 1) * n / ENVELOPE_POINTS).max(s + 1).min(n);
            let avg_sq: f64 = block_rms_sq[s..e].iter().sum::<f64>() / (e - s) as f64;
            *slot = avg_sq.sqrt() as f32;
        }
    }
    let waveform = normalize_rms_buckets(&rms_150);

    let duration_ms = if total_frames > 0 {
        total_frames * 1000 / sample_rate_hz as u64
    } else if let Some(n_frames) = total_frames_hint {
        n_frames * 1000 / sample_rate_hz as u64
    } else {
        0
    };

    let file_len_bytes = std::fs::metadata(path).map(|m| m.len()).unwrap_or(0);
    let bitrate_kbps = (file_len_bytes * 8)
        .checked_div(duration_ms)
        .map(|v| v as u32);

    let estimated_bpm = estimate_bpm(&bpm_mono, sample_rate_hz);
    let tempo_bucket = estimated_bpm.and_then(bpm_to_tempo_bucket);

    let replaygain_track_db = if total_frames > 0 {
        let rms = (global_sum_sq / total_frames as f64).sqrt();
        if rms > 1e-8 {
            let dbfs = 20.0 * rms.log10();
            let gain = (-18.0 - dbfs).clamp(-24.0, 24.0);
            Some(((gain * 100.0).round() / 100.0) as f32)
        } else {
            None
        }
    } else {
        None
    };

    Ok(AudioAnalysisResult {
        codec: codec_name,
        duration_ms,
        sample_rate_hz,
        bit_depth,
        bitrate_kbps,
        waveform_150: waveform.to_vec(),
        estimated_bpm,
        tempo_bucket,
        replaygain_track_db,
        peak_linear,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn extracts_150_point_envelope_and_estimates_120_bpm_from_synthetic_wav() {
        let sample_rate = 44_100u32;
        let duration_secs = 12u32;
        let total_samples = (sample_rate * duration_secs) as usize;
        let beat_interval = (sample_rate / 2) as usize; // 120 BPM = 2 beats/sec

        let mut mono = vec![0.0f32; total_samples];
        for (i, s) in mono.iter_mut().enumerate() {
            let pos_in_beat = i % beat_interval;
            if pos_in_beat < 1200 {
                // Decaying transient click at 440 Hz
                let env = (-(pos_in_beat as f32) / 300.0).exp();
                let tone = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / sample_rate as f32).sin();
                *s = 0.7 * env * tone;
            }
        }

        let bpm = estimate_bpm(&mono, sample_rate).expect("should detect BPM");
        assert!((bpm - 120.0).abs() <= 2.5, "expected ~120 BPM, got {bpm}");

        let env = extract_150_rms_envelope(&mono);
        assert_eq!(env.len(), 150);
        assert!(env.iter().any(|&v| v > 200));

        let rg = estimate_track_replaygain_db(&mono).expect("should estimate ReplayGain");
        assert!(rg.is_finite());

        // Write real PCM 16-bit WAV and test full Symphonia streaming analysis
        let tmp = std::env::temp_dir().join("tw_test_120bpm.wav");
        {
            let mut f = File::create(&tmp).unwrap();
            let data_bytes = (total_samples * 2) as u32;
            f.write_all(b"RIFF").unwrap();
            f.write_all(&(36 + data_bytes).to_le_bytes()).unwrap();
            f.write_all(b"WAVEfmt ").unwrap();
            f.write_all(&16u32.to_le_bytes()).unwrap();
            f.write_all(&1u16.to_le_bytes()).unwrap(); // PCM
            f.write_all(&1u16.to_le_bytes()).unwrap(); // Mono
            f.write_all(&sample_rate.to_le_bytes()).unwrap();
            f.write_all(&(sample_rate * 2).to_le_bytes()).unwrap();
            f.write_all(&2u16.to_le_bytes()).unwrap();
            f.write_all(&16u16.to_le_bytes()).unwrap();
            f.write_all(b"data").unwrap();
            f.write_all(&data_bytes.to_le_bytes()).unwrap();
            for &sample in &mono {
                let i16_val = (sample * 32767.0).clamp(-32768.0, 32767.0) as i16;
                f.write_all(&i16_val.to_le_bytes()).unwrap();
            }
        }

        let res = analyze_audio_file(&tmp).expect("Symphonia WAV analysis succeeded");
        let _ = std::fs::remove_file(&tmp);

        assert_eq!(res.sample_rate_hz, 44100);
        assert_eq!(res.waveform_150.len(), 150);
        assert!(res.duration_ms >= 11_900 && res.duration_ms <= 12_100);
        let decoded_bpm = res.estimated_bpm.expect("decoded BPM present");
        assert!((decoded_bpm - 120.0).abs() <= 2.5, "got {decoded_bpm}");
        assert_eq!(res.tempo_bucket, Some(TempoBucket::Fast));
    }
}
