use lofty::config::WriteOptions;
use lofty::file::{AudioFile, TaggedFileExt};
use lofty::probe::Probe;
use lofty::tag::{Accessor, ItemKey, Tag};
use rayon::prelude::*;
use rusqlite::Connection;
use serde::Serialize;
use std::collections::HashSet;
use std::fs;
use std::io::BufReader;
use std::path::{Path, PathBuf};
use std::time::{Instant, SystemTime, UNIX_EPOCH};
use tonewise_dsp_core::{AudioAnalysisResult, analyze_audio_file};
use tonewise_metadata::{
    DjEnergy, DjVocals, TempoBucket, normalize_dj_scenes, normalize_year,
    split_artists_preserving_whitelist, split_multi_value,
};
use tonewise_rules::MoodDictionary;
use tonewise_sync::{
    AudioFrameFingerprint, WaveformArchive, compute_audio_frame_fingerprint, normalize_m3u8_path,
};
use walkdir::WalkDir;

const MANIFEST_SCHEMA_SQL: &str = include_str!("../../../../protocol/schemas/manifest.sql");

const AUDIO_EXTENSIONS: &[&str] = &["flac", "mp3", "m4a", "mp4", "alac", "aac", "ogg", "opus", "wav"];

#[derive(Debug, Clone)]
pub struct ScannedTrackRecord {
    pub track_uid: String,
    pub audio_frame_blake3: [u8; 32],
    pub header_blake3: [u8; 32],
    pub header_size: u64,
    pub padding_bytes: u64,
    pub rel_path: String,
    pub file_size: u64,
    pub mtime_ms: u64,
    pub analysis: AudioAnalysisResult,
    pub title: String,
    pub album: Option<String>,
    pub album_artist: Option<String>,
    pub artists: Vec<String>,
    pub composers: Vec<String>,
    pub conductors: Vec<String>,
    pub work_name: Option<String>,
    pub movement_name: Option<String>,
    pub movement_number: Option<u32>,
    pub genre: Option<String>,
    pub year: Option<u16>,
    pub original_date: Option<String>,
    pub release_type: Option<String>,
    pub replaygain_track_db: Option<f32>,
    pub replaygain_album_db: Option<f32>,
    pub replaygain_peak: Option<f32>,
    pub dj_scene: Vec<String>,
    pub dj_vocals: Option<DjVocals>,
    pub dj_energy: Option<DjEnergy>,
    pub tempo_bpm: Option<f32>,
    pub tempo_bucket: Option<TempoBucket>,
    pub moods: Vec<String>,
    pub tempo_backfilled: bool,
    pub replaygain_backfilled: bool,
}

#[derive(Debug, Serialize)]
pub struct ScanSummary {
    pub scanned_tracks: usize,
    pub tempo_backfilled: usize,
    pub replaygain_backfilled: usize,
    pub waveforms_bytes: usize,
    pub elapsed_ms: u128,
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() <= 1 {
        print_banner();
        return;
    }

    match args[1].as_str() {
        "--version" | "-V" => println!("tonewise-cli {}", env!("CARGO_PKG_VERSION")),
        "scan" => {
            if let Err(e) = cmd_scan(&args[2..]) {
                eprintln!("scan error: {e}");
                std::process::exit(1);
            }
        }
        "export-usb" => {
            if let Err(e) = cmd_export_usb(&args[2..]) {
                eprintln!("export-usb error: {e}");
                std::process::exit(1);
            }
        }
        "--help" | "-h" | "help" => print_usage(),
        other => {
            eprintln!("Unknown command: {other}");
            print_usage();
            std::process::exit(2);
        }
    }
}

fn print_banner() {
    println!(
        "tonewise-cli {} | mood_clusters={} | artist_whitelist={}",
        env!("CARGO_PKG_VERSION"),
        MoodDictionary::global().cluster_count(),
        tonewise_metadata::artist_whitelist().len(),
    );
}

fn print_usage() {
    println!(
        "Tonewise Offline Curation & Sync CLI ({})

USAGE:
  tonewise-cli scan <MUSIC_DIR> [--out <TONEWISE_DIR>] [--threads <N>] [--write-tags]
  tonewise-cli export-usb <PLAYLIST_OR_DIR> <DEST_DIR> [--music-root <SRC_ROOT>]",
        env!("CARGO_PKG_VERSION")
    );
}

fn cmd_scan(args: &[String]) -> Result<(), String> {
    let mut music_dir: Option<PathBuf> = None;
    let mut out_dir: Option<PathBuf> = None;
    let mut threads: Option<usize> = None;
    let mut write_tags = false;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--out" => {
                i += 1;
                out_dir = Some(PathBuf::from(
                    args.get(i).ok_or("--out requires a directory path")?,
                ));
            }
            "--threads" => {
                i += 1;
                let n: usize = args
                    .get(i)
                    .ok_or("--threads requires an integer")?
                    .parse()
                    .map_err(|e| format!("invalid --threads value: {e}"))?;
                threads = Some(n);
            }
            "--write-tags" => write_tags = true,
            arg if !arg.starts_with('-') && music_dir.is_none() => {
                music_dir = Some(PathBuf::from(arg));
            }
            unknown => return Err(format!("unexpected argument: {unknown}")),
        }
        i += 1;
    }

    let music_root = music_dir.ok_or("missing <MUSIC_DIR>")?;
    let tonewise_dir = out_dir.unwrap_or_else(|| music_root.join(".tonewise"));

    if let Some(n) = threads {
        let _ = rayon::ThreadPoolBuilder::new()
            .num_threads(n.max(1))
            .build_global();
    }

    let summary = scan_library(&music_root, &tonewise_dir, write_tags)?;
    println!("{}", serde_json::to_string_pretty(&summary).unwrap());
    Ok(())
}

pub fn scan_library(
    music_root: &Path,
    tonewise_dir: &Path,
    write_tags: bool,
) -> Result<ScanSummary, String> {
    if !music_root.is_dir() {
        return Err(format!("music directory not found: {}", music_root.display()));
    }
    let start = Instant::now();

    let mut audio_files: Vec<PathBuf> = WalkDir::new(music_root)
        .follow_links(false)
        .into_iter()
        .filter_entry(|e| {
            let name = e.file_name().to_string_lossy();
            !(e.file_type().is_dir() && name.starts_with('.'))
        })
        .filter_map(Result::ok)
        .filter(|e| e.file_type().is_file() && is_audio_file(e.path()))
        .map(|e| e.into_path())
        .collect();
    audio_files.sort();

    let results: Vec<Result<ScannedTrackRecord, String>> = audio_files
        .par_iter()
        .map(|path| process_single_track(music_root, path, write_tags))
        .collect();

    let mut tracks = Vec::with_capacity(results.len());
    for res in results {
        match res {
            Ok(rec) => tracks.push(rec),
            Err(e) => eprintln!("warning: skipping file: {e}"),
        }
    }

    fs::create_dir_all(tonewise_dir)
        .map_err(|e| format!("failed to create {}: {e}", tonewise_dir.display()))?;

    let version_json = serde_json::json!({
        "schema_version": 1,
        "min_core_version": env!("CARGO_PKG_VERSION"),
    });
    fs::write(
        tonewise_dir.join("protocol_version.json"),
        serde_json::to_string_pretty(&version_json).unwrap() + "\n",
    )
    .map_err(|e| format!("failed writing protocol_version.json: {e}"))?;

    let waveform_entries: Vec<([u8; 32], [u8; 150])> = tracks
        .iter()
        .map(|t| {
            let mut pts = [0u8; 150];
            let len = t.analysis.waveform_150.len().min(150);
            pts[..len].copy_from_slice(&t.analysis.waveform_150[..len]);
            (t.audio_frame_blake3, pts)
        })
        .collect();
    let waveforms_bin = WaveformArchive::encode(&waveform_entries);
    let waveforms_bytes = waveforms_bin.len();
    fs::write(tonewise_dir.join("waveforms.bin"), &waveforms_bin)
        .map_err(|e| format!("failed writing waveforms.bin: {e}"))?;

    write_manifest_db(&tonewise_dir.join("manifest.db"), &tracks)?;

    let tempo_backfilled = tracks.iter().filter(|t| t.tempo_backfilled).count();
    let replaygain_backfilled = tracks.iter().filter(|t| t.replaygain_backfilled).count();

    Ok(ScanSummary {
        scanned_tracks: tracks.len(),
        tempo_backfilled,
        replaygain_backfilled,
        waveforms_bytes,
        elapsed_ms: start.elapsed().as_millis(),
    })
}

fn process_single_track(
    music_root: &Path,
    path: &Path,
    write_tags: bool,
) -> Result<ScannedTrackRecord, String> {
    let rel_path = path
        .strip_prefix(music_root)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/");

    let analysis = analyze_audio_file(path)
        .map_err(|e| format!("{}: analysis failed: {e}", path.display()))?;

    let mut tagged = Probe::open(path)
        .map_err(|e| format!("{}: lofty open failed: {e}", path.display()))?
        .read()
        .map_err(|e| format!("{}: lofty read failed: {e}", path.display()))?;

    let primary_tag = tagged.primary_tag().or_else(|| tagged.first_tag());

    let fallback_title = path
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "Unknown".to_string());
    let title = primary_tag
        .and_then(|t| t.title().map(|s| s.to_string()))
        .unwrap_or(fallback_title);
    let album = primary_tag.and_then(|t| t.album().map(|s| s.to_string()));
    let genre = primary_tag.and_then(|t| t.genre().map(|s| s.to_string()));

    let mut album_artist = None;
    let mut raw_artists = Vec::new();
    let mut composers = Vec::new();
    let mut conductors = Vec::new();
    let mut work_name = None;
    let mut movement_name = None;
    let mut movement_number = None;
    let mut year = primary_tag.and_then(|t| t.year()).map(|y| y as u16);
    let mut original_date = None;
    let mut release_type = None;
    let mut rg_track = None;
    let mut rg_album = None;
    let mut rg_peak = None;
    let mut dj_scene = Vec::new();
    let mut dj_vocals = None;
    let mut dj_energy = None;
    let mut tempo_bpm = None;
    let mut moods = Vec::new();

    if let Some(tag) = primary_tag {
        album_artist = tag.get_string(&ItemKey::AlbumArtist).map(str::to_string);
        for item in tag.get_strings(&ItemKey::TrackArtists) {
            raw_artists.extend(split_artists_preserving_whitelist(item));
        }
        if raw_artists.is_empty()
            && let Some(a) = tag.artist()
        {
            raw_artists = split_artists_preserving_whitelist(&a);
        }

        for item in tag.get_strings(&ItemKey::Composer) {
            composers.extend(split_artists_preserving_whitelist(item));
        }
        for item in tag.get_strings(&ItemKey::Conductor) {
            conductors.extend(split_artists_preserving_whitelist(item));
        }
        work_name = tag.get_string(&ItemKey::Work).map(str::to_string);
        movement_name = tag.get_string(&ItemKey::Movement).map(str::to_string);
        movement_number = tag
            .get_string(&ItemKey::MovementNumber)
            .and_then(|s| s.trim().parse::<u32>().ok());

        original_date = tag
            .get_string(&ItemKey::OriginalReleaseDate)
            .or_else(|| tag.get_string(&ItemKey::RecordingDate))
            .map(str::to_string);
        if year.is_none()
            && let Some(ref d) = original_date
        {
            year = normalize_year(d);
        }
        release_type = tag
            .get_string(&ItemKey::MusicBrainzReleaseGroupId)
            .map(str::to_string);

        rg_track = tag
            .get_string(&ItemKey::ReplayGainTrackGain)
            .and_then(parse_gain_db);
        rg_album = tag
            .get_string(&ItemKey::ReplayGainAlbumGain)
            .and_then(parse_gain_db);
        rg_peak = tag
            .get_string(&ItemKey::ReplayGainTrackPeak)
            .and_then(|s| s.trim().parse::<f32>().ok());

        tempo_bpm = tag
            .get_string(&ItemKey::Bpm)
            .or_else(|| tag.get_string(&ItemKey::IntegerBpm))
            .and_then(|s| s.trim().parse::<f32>().ok());
        for m in tag.get_strings(&ItemKey::Mood) {
            moods.extend(split_multi_value(m));
        }

        // Unknown/custom TXXX or Vorbis keys (DJ_SCENE / DJ_VOCALS / DJ_ENERGY / TEMPO)
        for item in tag.items() {
            let desc = match item.key() {
                ItemKey::Unknown(k) => k.to_ascii_uppercase(),
                _ => continue,
            };
            let Some(val) = item.value().text() else {
                continue;
            };
            match desc.as_str() {
                "DJ_SCENE" => dj_scene.extend(normalize_dj_scenes(val)),
                "DJ_VOCALS" => {
                    if let Some(parsed) = DjVocals::parse(val) {
                        dj_vocals = Some(parsed);
                    }
                }
                "DJ_ENERGY" => {
                    if let Some(parsed) = DjEnergy::parse(val) {
                        dj_energy = Some(parsed);
                    }
                }
                "TEMPO" if tempo_bpm.is_none() => {
                    if let Ok(b) = val.trim().parse::<f32>() {
                        tempo_bpm = Some(b);
                    }
                }
                _ => {}
            }
        }
    }

    let mut tempo_backfilled = false;
    if tempo_bpm.is_none()
        && let Some(est) = analysis.estimated_bpm
    {
        tempo_bpm = Some(est);
        tempo_backfilled = true;
    }
    let tempo_bucket = tempo_bpm.and_then(tonewise_metadata::bpm_to_tempo_bucket);

    let mut replaygain_backfilled = false;
    if rg_track.is_none()
        && let Some(est_rg) = analysis.replaygain_track_db
    {
        rg_track = Some(est_rg);
        rg_peak = Some(analysis.peak_linear);
        replaygain_backfilled = true;
    }

    if write_tags && (tempo_backfilled || replaygain_backfilled) {
        let tag_type = tagged.primary_tag_type();
        if tagged.primary_tag_mut().is_none() {
            tagged.insert_tag(Tag::new(tag_type));
        }
        if let Some(tag) = tagged.primary_tag_mut() {
            if tempo_backfilled && let Some(bpm) = tempo_bpm {
                tag.insert_text(ItemKey::Bpm, format!("{bpm:.1}"));
            }
            if replaygain_backfilled && let Some(gain) = rg_track {
                tag.insert_text(ItemKey::ReplayGainTrackGain, format!("{gain:+.2} dB"));
                if let Some(pk) = rg_peak {
                    tag.insert_text(ItemKey::ReplayGainTrackPeak, format!("{pk:.6}"));
                }
            }
        }
        let write_opts = WriteOptions::default();
        tagged
            .save_to_path(path, write_opts)
            .map_err(|e| format!("{}: tag writeback failed: {e}", path.display()))?;
    }

    let fp = compute_fingerprint_from_path(path)?;
    let audio_frame_blake3 = hex_to_32(&fp.audio_frame_blake3_hex)
        .ok_or_else(|| "invalid blake3 hex".to_string())?;
    let header_blake3 =
        hex_to_32(&fp.header_blake3_hex).ok_or_else(|| "invalid header blake3 hex".to_string())?;
    let padding_bytes = detect_flac_padding(path).unwrap_or(0);

    let meta = fs::metadata(path).map_err(|e| format!("{}: metadata failed: {e}", path.display()))?;
    let mtime_ms = meta
        .modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);

    dedup_preserve_order(&mut dj_scene);
    dedup_preserve_order(&mut moods);

    Ok(ScannedTrackRecord {
        track_uid: fp.audio_frame_blake3_hex.clone(),
        audio_frame_blake3,
        header_blake3,
        header_size: fp.header_size,
        padding_bytes,
        rel_path,
        file_size: fp.file_size,
        mtime_ms,
        analysis,
        title,
        album,
        album_artist,
        artists: raw_artists,
        composers,
        conductors,
        work_name,
        movement_name,
        movement_number,
        genre,
        year,
        original_date,
        release_type,
        replaygain_track_db: rg_track,
        replaygain_album_db: rg_album,
        replaygain_peak: rg_peak,
        dj_scene,
        dj_vocals,
        dj_energy,
        tempo_bpm,
        tempo_bucket,
        moods,
        tempo_backfilled,
        replaygain_backfilled,
    })
}

fn write_manifest_db(db_path: &Path, tracks: &[ScannedTrackRecord]) -> Result<(), String> {
    let mut conn =
        Connection::open(db_path).map_err(|e| format!("sqlite open failed: {e}"))?;
    conn.execute_batch(MANIFEST_SCHEMA_SQL)
        .map_err(|e| format!("sqlite schema init failed: {e}"))?;

    let now_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0);

    let tx = conn
        .transaction()
        .map_err(|e| format!("sqlite tx begin failed: {e}"))?;

    tx.execute("DELETE FROM tracks", [])
        .map_err(|e| format!("sqlite clear failed: {e}"))?;

    {
        let mut stmt = tx
            .prepare(
                "INSERT INTO tracks (
                    track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                    rel_path, file_size, mtime_ms, codec, sample_rate_hz, bit_depth, bitrate_kbps, duration_ms,
                    title, album, album_artist, artists_json, composers_json, conductors_json,
                    work_name, movement_name, movement_number, genre, year, original_date, release_type,
                    replaygain_track_db, replaygain_album_db, replaygain_peak,
                    dj_scene_json, dj_scene_ts, dj_vocals, dj_vocals_ts, dj_energy, dj_energy_ts,
                    tempo_bpm, tempo_bucket, tempo_ts, mood_json, mood_ts,
                    waveform_offset
                ) VALUES (
                    ?1, ?2, ?3, ?4, ?5,
                    ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13,
                    ?14, ?15, ?16, ?17, ?18, ?19,
                    ?20, ?21, ?22, ?23, ?24, ?25, ?26,
                    ?27, ?28, ?29,
                    ?30, ?31, ?32, ?33, ?34, ?35,
                    ?36, ?37, ?38, ?39, ?40,
                    ?41
                )",
            )
            .map_err(|e| format!("sqlite prepare failed: {e}"))?;

        for (idx, t) in tracks.iter().enumerate() {
            let dj_vocals_str = t.dj_vocals.map(DjVocals::as_str);
            let dj_energy_str = t.dj_energy.map(DjEnergy::as_str);
            let tempo_bucket_str = t.tempo_bucket.map(|b| match b {
                TempoBucket::VerySlow => "Very Slow",
                TempoBucket::Slow => "Slow",
                TempoBucket::Moderate => "Moderate",
                TempoBucket::Fast => "Fast",
                TempoBucket::VeryFast => "Very Fast",
            });

            stmt.execute(rusqlite::params![
                t.track_uid,
                t.audio_frame_blake3.as_slice(),
                t.header_size as i64,
                t.header_blake3.as_slice(),
                t.padding_bytes as i64,
                t.rel_path,
                t.file_size as i64,
                t.mtime_ms as i64,
                t.analysis.codec,
                t.analysis.sample_rate_hz as i64,
                t.analysis.bit_depth.map(|v| v as i64),
                t.analysis.bitrate_kbps.map(|v| v as i64),
                t.analysis.duration_ms as i64,
                t.title,
                t.album,
                t.album_artist,
                serde_json::to_string(&t.artists).unwrap(),
                serde_json::to_string(&t.composers).unwrap(),
                serde_json::to_string(&t.conductors).unwrap(),
                t.work_name,
                t.movement_name,
                t.movement_number.map(|v| v as i64),
                t.genre,
                t.year.map(|v| v as i64),
                t.original_date,
                t.release_type,
                t.replaygain_track_db,
                t.replaygain_album_db,
                t.replaygain_peak,
                serde_json::to_string(&t.dj_scene).unwrap(),
                if t.dj_scene.is_empty() { 0i64 } else { now_ms },
                dj_vocals_str,
                if dj_vocals_str.is_some() { now_ms } else { 0i64 },
                dj_energy_str,
                if dj_energy_str.is_some() { now_ms } else { 0i64 },
                t.tempo_bpm,
                tempo_bucket_str,
                if t.tempo_bpm.is_some() { now_ms } else { 0i64 },
                serde_json::to_string(&t.moods).unwrap(),
                if t.moods.is_empty() { 0i64 } else { now_ms },
                (idx * 150) as i64,
            ])
            .map_err(|e| format!("sqlite insert failed for {}: {e}", t.rel_path))?;
        }
    }

    tx.commit()
        .map_err(|e| format!("sqlite commit failed: {e}"))?;
    Ok(())
}

fn cmd_export_usb(args: &[String]) -> Result<(), String> {
    let mut source: Option<PathBuf> = None;
    let mut dest_dir: Option<PathBuf> = None;
    let mut music_root: Option<PathBuf> = None;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--music-root" => {
                i += 1;
                music_root = Some(PathBuf::from(
                    args.get(i).ok_or("--music-root requires a path")?,
                ));
            }
            arg if !arg.starts_with('-') => {
                if source.is_none() {
                    source = Some(PathBuf::from(arg));
                } else if dest_dir.is_none() {
                    dest_dir = Some(PathBuf::from(arg));
                } else {
                    return Err(format!("unexpected positional argument: {arg}"));
                }
            }
            unknown => return Err(format!("unexpected argument: {unknown}")),
        }
        i += 1;
    }

    let src = source.ok_or("missing <PLAYLIST_OR_DIR>")?;
    let dst = dest_dir.ok_or("missing <DEST_DIR>")?;
    let count = export_usb_playlists(&src, &dst, music_root.as_deref())?;
    println!("Exported {count} UTF-8 .m3u8 playlist(s) to {}", dst.display());
    Ok(())
}

pub fn export_usb_playlists(
    source: &Path,
    dest_dir: &Path,
    music_root: Option<&Path>,
) -> Result<usize, String> {
    let files: Vec<PathBuf> = if source.is_file() {
        vec![source.to_path_buf()]
    } else if source.is_dir() {
        WalkDir::new(source)
            .into_iter()
            .filter_map(Result::ok)
            .filter(|e| {
                e.file_type().is_file()
                    && e.path()
                        .extension()
                        .and_then(|x| x.to_str())
                        .is_some_and(|x| x.eq_ignore_ascii_case("m3u") || x.eq_ignore_ascii_case("m3u8"))
            })
            .map(|e| e.into_path())
            .collect()
    } else {
        return Err(format!("playlist source not found: {}", source.display()));
    };

    fs::create_dir_all(dest_dir)
        .map_err(|e| format!("failed to create {}: {e}", dest_dir.display()))?;

    let root_norm = music_root.map(|r| normalize_m3u8_path(&r.to_string_lossy()));
    let mut exported = 0usize;

    for file in files {
        let content = fs::read_to_string(&file)
            .map_err(|e| format!("failed reading {}: {e}", file.display()))?;
        let mut lines_out = Vec::new();
        for raw_line in content.lines() {
            let line = raw_line.trim().trim_start_matches('\u{feff}');
            if line.is_empty() {
                continue;
            }
            if line.starts_with('#') {
                lines_out.push(line.to_string());
                continue;
            }
            let mut norm = normalize_m3u8_path(line);
            if let Some(ref root) = root_norm {
                let prefix = if root.ends_with('/') {
                    root.clone()
                } else {
                    format!("{root}/")
                };
                if norm.to_ascii_lowercase().starts_with(&prefix.to_ascii_lowercase()) {
                    norm = norm[prefix.len()..].to_string();
                }
            } else if norm.len() >= 3 && norm.as_bytes()[1] == b':' && norm.as_bytes()[2] == b'/' {
                // Strip Windows drive letter prefix when exporting without explicit --music-root
                norm = norm[3..].to_string();
            }
            lines_out.push(norm);
        }

        let stem = file
            .file_stem()
            .map(|s| s.to_string_lossy().to_string())
            .unwrap_or_else(|| "playlist".to_string());
        let out_path = dest_dir.join(format!("{stem}.m3u8"));
        // Strict UTF-8 without BOM and LF line endings for car head units
        let body = lines_out.join("\n") + "\n";
        fs::write(&out_path, body.as_bytes())
            .map_err(|e| format!("failed writing {}: {e}", out_path.display()))?;
        exported += 1;
    }

    Ok(exported)
}

fn is_audio_file(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .is_some_and(|ext| {
            let lower = ext.to_ascii_lowercase();
            AUDIO_EXTENSIONS.contains(&lower.as_str())
        })
}

fn parse_gain_db(s: &str) -> Option<f32> {
    s.trim()
        .trim_end_matches("dB")
        .trim_end_matches("db")
        .trim()
        .parse::<f32>()
        .ok()
}

fn compute_fingerprint_from_path(path: &Path) -> Result<AudioFrameFingerprint, String> {
    let f = fs::File::open(path).map_err(|e| format!("open failed: {e}"))?;
    let mut reader = BufReader::new(f);
    compute_audio_frame_fingerprint(&mut reader).map_err(|e| format!("blake3 failed: {e}"))
}

fn hex_to_32(hex: &str) -> Option<[u8; 32]> {
    if hex.len() != 64 {
        return None;
    }
    let mut out = [0u8; 32];
    for (i, slot) in out.iter_mut().enumerate() {
        *slot = u8::from_str_radix(&hex[i * 2..i * 2 + 2], 16).ok()?;
    }
    Some(out)
}

fn detect_flac_padding(path: &Path) -> Option<u64> {
    use std::io::Read;
    let mut f = fs::File::open(path).ok()?;
    let mut magic = [0u8; 4];
    f.read_exact(&mut magic).ok()?;
    if &magic != b"fLaC" {
        return None;
    }
    let mut total_padding = 0u64;
    loop {
        let mut hdr = [0u8; 4];
        if f.read_exact(&mut hdr).is_err() {
            break;
        }
        let is_last = (hdr[0] & 0x80) != 0;
        let block_type = hdr[0] & 0x7F;
        let len = ((hdr[1] as u64) << 16) | ((hdr[2] as u64) << 8) | (hdr[3] as u64);
        if block_type == 1 {
            total_padding += len;
        }
        if std::io::Seek::seek(&mut f, std::io::SeekFrom::Current(len as i64)).is_err() {
            break;
        }
        if is_last {
            break;
        }
    }
    Some(total_padding)
}

fn dedup_preserve_order(items: &mut Vec<String>) {
    let mut seen = HashSet::new();
    items.retain(|s| seen.insert(s.to_ascii_lowercase()));
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn write_test_wav(path: &Path, sample_rate: u32, secs: u32) {
        let total = (sample_rate * secs) as usize;
        let beat_interval = (sample_rate / 2) as usize; // 120 BPM
        let mut f = fs::File::create(path).unwrap();
        let data_bytes = (total * 2) as u32;
        f.write_all(b"RIFF").unwrap();
        f.write_all(&(36 + data_bytes).to_le_bytes()).unwrap();
        f.write_all(b"WAVEfmt ").unwrap();
        f.write_all(&16u32.to_le_bytes()).unwrap();
        f.write_all(&1u16.to_le_bytes()).unwrap();
        f.write_all(&1u16.to_le_bytes()).unwrap();
        f.write_all(&sample_rate.to_le_bytes()).unwrap();
        f.write_all(&(sample_rate * 2).to_le_bytes()).unwrap();
        f.write_all(&2u16.to_le_bytes()).unwrap();
        f.write_all(&16u16.to_le_bytes()).unwrap();
        f.write_all(b"data").unwrap();
        f.write_all(&data_bytes.to_le_bytes()).unwrap();
        for i in 0..total {
            let pos = i % beat_interval;
            let sample = if pos < 1000 {
                let env = (-(pos as f32) / 250.0).exp();
                (0.6 * env * (2.0 * std::f32::consts::PI * 440.0 * i as f32 / sample_rate as f32).sin()
                    * 32767.0) as i16
            } else {
                0
            };
            f.write_all(&sample.to_le_bytes()).unwrap();
        }
    }

    #[test]
    fn cli_scan_builds_manifest_db_and_waveforms_bin() {
        let base = std::env::temp_dir().join("tw_cli_scan_test");
        let _ = fs::remove_dir_all(&base);
        let music = base.join("Music");
        let album = music.join("Bach");
        fs::create_dir_all(&album).unwrap();
        let wav_path = album.join("01_Prelude.wav");
        write_test_wav(&wav_path, 44100, 6);

        let tonewise_dir = music.join(".tonewise");
        let summary = scan_library(&music, &tonewise_dir, false).expect("scan_library succeeds");
        assert_eq!(summary.scanned_tracks, 1);
        assert_eq!(summary.tempo_backfilled, 1);
        assert_eq!(summary.replaygain_backfilled, 1);

        // Verify manifest.db row
        let conn = Connection::open(tonewise_dir.join("manifest.db")).unwrap();
        let (rel_path, tempo_bucket, wf_off, fp_blob): (String, Option<String>, i64, Vec<u8>) = conn
            .query_row(
                "SELECT rel_path, tempo_bucket, waveform_offset, audio_frame_blake3 FROM tracks",
                [],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
            )
            .unwrap();
        assert_eq!(rel_path, "Bach/01_Prelude.wav");
        assert_eq!(tempo_bucket.as_deref(), Some("Fast"));
        assert_eq!(wf_off, 0);
        assert_eq!(fp_blob.len(), 32);

        // Verify waveforms.bin lookup matches
        let wf_bytes = fs::read(tonewise_dir.join("waveforms.bin")).unwrap();
        let mut prefix = [0u8; 16];
        prefix.copy_from_slice(&fp_blob[..16]);
        let envelope = WaveformArchive::lookup_bytes(&wf_bytes, &prefix).expect("waveform found");
        assert_eq!(envelope.len(), 150);
        assert!(envelope.iter().any(|&v| v > 200));

        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn cli_export_usb_writes_utf8_no_bom_posix_m3u8() {
        let base = std::env::temp_dir().join("tw_cli_usb_test");
        let _ = fs::remove_dir_all(&base);
        fs::create_dir_all(&base).unwrap();
        let src = base.join("Focus.m3u");
        fs::write(
            &src,
            "\u{FEFF}#EXTM3U\r\nD:\\Music\\Bach\\01.flac\r\nD:\\Music\\Jazz\\02.flac\r\n",
        )
        .unwrap();

        let dest = base.join("usb_playlists");
        let count = export_usb_playlists(&src, &dest, Some(Path::new("D:\\Music"))).unwrap();
        assert_eq!(count, 1);

        let exported_bytes = fs::read(dest.join("Focus.m3u8")).unwrap();
        assert!(!exported_bytes.starts_with(&[0xEF, 0xBB, 0xBF]));
        let text = String::from_utf8(exported_bytes).unwrap();
        assert_eq!(text, "#EXTM3U\nBach/01.flac\nJazz/02.flac\n");

        let _ = fs::remove_dir_all(&base);
    }
}
