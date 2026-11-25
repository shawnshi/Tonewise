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
    DjEnergy, DjVocals, TempoBucket, normalize_album_artist, normalize_dj_scenes, normalize_genre,
    normalize_year, split_artists_preserving_whitelist, split_composers_preserving_whitelist,
    split_multi_value,
};
use tonewise_rules::MoodDictionary;
use tonewise_sync::{
    AudioFrameFingerprint, WaveformArchive, compute_audio_frame_fingerprint, normalize_m3u8_path,
};
use walkdir::WalkDir;

const MANIFEST_SCHEMA_SQL: &str = include_str!("../../../../protocol/schemas/manifest.sql");

const AUDIO_EXTENSIONS: &[&str] = &[
    "flac", "mp3", "m4a", "mp4", "alac", "aac", "ogg", "opus", "wav", "aiff", "aif", "dsf", "dff",
];

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
    pub disc_number: u32,
    pub track_number: u32,
    pub publisher: Option<String>,
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
  tonewise-cli scan <MUSIC_DIR> [--out <TONEWISE_DIR>] [--threads <N>] [--write-tags] [--fast]
  tonewise-cli export-usb <PLAYLIST_OR_DIR> <DEST_DIR> [--music-root <SRC_ROOT>]",
        env!("CARGO_PKG_VERSION")
    );
}

fn cmd_scan(args: &[String]) -> Result<(), String> {
    let mut music_dir: Option<PathBuf> = None;
    let mut out_dir: Option<PathBuf> = None;
    let mut threads: Option<usize> = None;
    let mut write_tags = false;
    let mut fast_mode = false;

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
            "--fast" => fast_mode = true,
            arg if !arg.starts_with('-') && music_dir.is_none() => {
                music_dir = Some(PathBuf::from(arg));
            }
            unknown => return Err(format!("unexpected argument: {unknown}")),
        }
        i += 1;
    }

    let music_root = music_dir.ok_or("missing <MUSIC_DIR>")?;
    let tonewise_dir = out_dir.unwrap_or_else(|| music_root.join(".tonewise"));

    let default_threads = std::thread::available_parallelism()
        .map(|n| n.get() / 2)
        .unwrap_or(4)
        .clamp(2, 8);
    let target_threads = threads.map(|n| n.max(1)).unwrap_or(default_threads);
    let _ = rayon::ThreadPoolBuilder::new()
        .num_threads(target_threads)
        .build_global();

    let summary = scan_library_with_options(&music_root, &tonewise_dir, write_tags, fast_mode)?;
    println!("{}", serde_json::to_string_pretty(&summary).unwrap());
    Ok(())
}

pub fn scan_library(
    music_root: &Path,
    tonewise_dir: &Path,
    write_tags: bool,
) -> Result<ScanSummary, String> {
    scan_library_with_options(music_root, tonewise_dir, write_tags, false)
}

pub fn scan_library_with_options(
    music_root: &Path,
    tonewise_dir: &Path,
    write_tags: bool,
    fast_mode: bool,
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
        .map(|path| process_single_track(music_root, path, write_tags, fast_mode))
        .collect();

    let mut tracks = Vec::with_capacity(results.len());
    for res in results {
        match res {
            Ok(rec) => tracks.push(rec),
            Err(e) => eprintln!("warning: skipping file: {e}"),
        }
    }

    let mut seen_uids = HashSet::with_capacity(tracks.len());
    tracks.retain(|t| seen_uids.insert(t.audio_frame_blake3));

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

    let wf_path = tonewise_dir.join("waveforms.bin");
    let waveforms_bytes = if fast_mode
        && fs::metadata(&wf_path).map(|m| m.len() > 12).unwrap_or(false)
    {
        fs::metadata(&wf_path).map(|m| m.len() as usize).unwrap_or(0)
    } else {
        let waveform_entries: Vec<([u8; 32], [u8; 150])> = tracks
            .iter()
            .filter(|t| !t.analysis.waveform_150.is_empty())
            .map(|t| {
                let mut pts = [0u8; 150];
                let len = t.analysis.waveform_150.len().min(150);
                pts[..len].copy_from_slice(&t.analysis.waveform_150[..len]);
                (t.audio_frame_blake3, pts)
            })
            .collect();
        let waveforms_bin = WaveformArchive::encode(&waveform_entries);
        let len = waveforms_bin.len();
        fs::write(&wf_path, &waveforms_bin)
            .map_err(|e| format!("failed writing waveforms.bin: {e}"))?;
        len
    };

    write_manifest_db(&tonewise_dir.join("manifest.db"), music_root, &tracks, true)?;

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
    fast_mode: bool,
) -> Result<ScannedTrackRecord, String> {
    let rel_path = path
        .strip_prefix(music_root)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/");

    let ext_lower = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_ascii_lowercase())
        .unwrap_or_default();
    let is_dsd = matches!(ext_lower.as_str(), "dsf" | "dff");

    let mut tagged = match Probe::open(path).and_then(|p| p.read()) {
        Ok(t) => Some(t),
        Err(_) if is_dsd => None,
        Err(e) => return Err(format!("{}: lofty read failed: {e}", path.display())),
    };

    let dsd_fallback_analysis = |codec: &str| AudioAnalysisResult {
        codec: codec.to_string(),
        duration_ms: 0,
        sample_rate_hz: 2_822_400,
        bit_depth: Some(1),
        bitrate_kbps: None,
        waveform_150: Vec::new(),
        estimated_bpm: None,
        tempo_bucket: None,
        replaygain_track_db: None,
        peak_linear: 0.0,
    };

    let analysis = if fast_mode {
        if let Some(ref t) = tagged {
            let props = t.properties();
            AudioAnalysisResult {
                codec: format!("{:?}", t.file_type()),
                duration_ms: props.duration().as_millis() as u64,
                sample_rate_hz: props.sample_rate().unwrap_or(44100),
                bit_depth: props.bit_depth().map(|b| b as u32),
                bitrate_kbps: props.audio_bitrate().or_else(|| props.overall_bitrate()),
                waveform_150: Vec::new(),
                estimated_bpm: None,
                tempo_bucket: None,
                replaygain_track_db: None,
                peak_linear: 0.0,
            }
        } else {
            dsd_fallback_analysis(&ext_lower)
        }
    } else {
        match analyze_audio_file(path) {
            Ok(res) => res,
            Err(_) if is_dsd => dsd_fallback_analysis(&ext_lower),
            Err(e) => return Err(format!("{}: analysis failed: {e}", path.display())),
        }
    };

    let primary_tag = tagged
        .as_ref()
        .and_then(|t| t.primary_tag().or_else(|| t.first_tag()));

    let fallback_title = path
        .file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_else(|| "Unknown".to_string());
    let title = primary_tag
        .and_then(|t| t.title().map(|s| s.to_string()))
        .unwrap_or(fallback_title);
    let album = primary_tag.and_then(|t| t.album().map(|s| s.to_string()));
    let genre = primary_tag
        .and_then(|t| t.genre())
        .map(|s| normalize_genre(&s))
        .filter(|s| !s.is_empty());

    let mut album_artist = None;
    let mut raw_artists = Vec::new();
    let mut composers = Vec::new();
    let mut conductors = Vec::new();
    let mut work_name = None;
    let mut movement_name = None;
    let mut movement_number = None;
    let disc_number = primary_tag.and_then(|t| t.disk()).unwrap_or(0);
    let track_number = primary_tag.and_then(|t| t.track()).unwrap_or(0);
    let mut publisher = primary_tag.and_then(|t| {
        t.get_string(&ItemKey::Publisher)
            .or_else(|| t.get_string(&ItemKey::Label))
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string)
    });
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
        album_artist = tag
            .get_string(&ItemKey::AlbumArtist)
            .map(normalize_album_artist)
            .filter(|s| !s.is_empty());
        for item in tag.get_strings(&ItemKey::TrackArtists) {
            raw_artists.extend(split_artists_preserving_whitelist(item));
        }
        if raw_artists.is_empty()
            && let Some(a) = tag.artist()
        {
            raw_artists = split_artists_preserving_whitelist(&a);
        }

        for item in tag.get_strings(&ItemKey::Composer) {
            composers.extend(split_composers_preserving_whitelist(item));
        }
        for item in tag.get_strings(&ItemKey::Conductor) {
            conductors.extend(split_artists_preserving_whitelist(item));
        }
        work_name = tag
            .get_string(&ItemKey::Work)
            .or_else(|| tag.get_string(&ItemKey::ContentGroup))
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string);
        movement_name = tag
            .get_string(&ItemKey::Movement)
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_string);
        movement_number = tag
            .get_string(&ItemKey::MovementNumber)
            .and_then(parse_movement_number);

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
            .get_string(&ItemKey::FlagCompilation)
            .filter(|v| v.trim() == "1")
            .map(|_| "compilation".to_string());

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
                "WORK" | "GROUPING" | "CONTENTGROUP" if work_name.is_none() => {
                    let trimmed = val.trim();
                    if !trimmed.is_empty() {
                        work_name = Some(trimmed.to_string());
                    }
                }
                "MOVEMENT" | "MOVEMENTNAME" if movement_name.is_none() => {
                    let trimmed = val.trim();
                    if !trimmed.is_empty() {
                        movement_name = Some(trimmed.to_string());
                    }
                }
                "MOVEMENTNUMBER" if movement_number.is_none() => {
                    movement_number = parse_movement_number(val);
                }
                "COMPOSER" if composers.is_empty() => {
                    composers.extend(split_composers_preserving_whitelist(val));
                }
                "CONDUCTOR" if conductors.is_empty() => {
                    conductors.extend(split_artists_preserving_whitelist(val));
                }
                "REPLAYGAIN_ALBUM_GAIN" if rg_album.is_none() => {
                    rg_album = parse_gain_db(val);
                }
                "REPLAYGAIN_TRACK_GAIN" if rg_track.is_none() => {
                    rg_track = parse_gain_db(val);
                }
                "PUBLISHER" | "LABEL" | "ORGANIZATION" if publisher.is_none() => {
                    let trimmed = val.trim();
                    if !trimmed.is_empty() {
                        publisher = Some(trimmed.to_string());
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

    if write_tags
        && (tempo_backfilled || replaygain_backfilled)
        && let Some(ref mut tf) = tagged
    {
        let tag_type = tf.primary_tag_type();
        if tf.primary_tag_mut().is_none() {
            tf.insert_tag(Tag::new(tag_type));
        }
        if let Some(tag) = tf.primary_tag_mut() {
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
        save_tagged_file_atomically(path, tf)?;
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
        disc_number,
        track_number,
        publisher,
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

fn save_tagged_file_atomically(
    path: &Path,
    tagged: &lofty::file::TaggedFile,
) -> Result<(), String> {
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEMP_SEQ: AtomicU64 = AtomicU64::new(0);
    let seq = TEMP_SEQ.fetch_add(1, Ordering::Relaxed);
    let pid = std::process::id();
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let base_name = path
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_else(|| "audio".to_string());
    let tmp_path = parent.join(format!("{base_name}.twtmp.{pid}.{seq}"));

    use lofty::tag::TagExt;

    fs::copy(path, &tmp_path)
        .map_err(|e| format!("{}: temp copy failed: {e}", path.display()))?;
    let save_res = if let Some(primary) = tagged.primary_tag() {
        primary.save_to_path(&tmp_path, WriteOptions::default())
    } else {
        tagged.save_to_path(&tmp_path, WriteOptions::default())
    };
    if let Err(e) = save_res {
        let _ = fs::remove_file(&tmp_path);
        return Err(format!("{}: tag writeback failed: {e}", path.display()));
    }
    if let Ok(f) = fs::File::open(&tmp_path) {
        let _ = f.sync_data();
    }

    #[cfg(windows)]
    {
        use std::ffi::OsStr;
        use std::os::windows::ffi::OsStrExt;
        unsafe extern "system" {
            fn MoveFileExW(
                lp_existing_file_name: *const u16,
                lp_new_file_name: *const u16,
                dw_flags: u32,
            ) -> i32;
        }
        const MOVEFILE_REPLACE_EXISTING: u32 = 0x0000_0001;
        const MOVEFILE_WRITE_THROUGH: u32 = 0x0000_0008;

        let src_w: Vec<u16> = OsStr::new(&tmp_path).encode_wide().chain(std::iter::once(0)).collect();
        let dst_w: Vec<u16> = OsStr::new(path).encode_wide().chain(std::iter::once(0)).collect();
        let moved = unsafe {
            MoveFileExW(
                src_w.as_ptr(),
                dst_w.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        };
        if moved != 0 {
            return Ok(());
        }

        let bak_path = parent.join(format!("{base_name}.twbak.{pid}.{seq}"));
        let _ = fs::remove_file(&bak_path);
        if let Err(e) = fs::rename(path, &bak_path) {
            let _ = fs::remove_file(&tmp_path);
            return Err(format!("{}: backup rename failed: {e}", path.display()));
        }
        if let Err(e) = fs::rename(&tmp_path, path) {
            let _ = fs::rename(&bak_path, path);
            let _ = fs::remove_file(&tmp_path);
            return Err(format!("{}: atomic replace failed: {e}", path.display()));
        }
        let _ = fs::remove_file(&bak_path);
        Ok(())
    }

    #[cfg(not(windows))]
    {
        if let Err(e) = fs::rename(&tmp_path, path) {
            let _ = fs::remove_file(&tmp_path);
            return Err(format!("{}: atomic rename failed: {e}", path.display()));
        }
        Ok(())
    }
}

fn ensure_manifest_v2_schema(conn: &Connection) -> Result<(), String> {
    let mut existing = HashSet::new();
    {
        let mut stmt = conn
            .prepare("SELECT name FROM pragma_table_info('tracks')")
            .map_err(|e| format!("pragma_table_info failed: {e}"))?;
        let rows = stmt
            .query_map([], |r| r.get::<_, String>(0))
            .map_err(|e| format!("pragma query failed: {e}"))?;
        for col in rows.flatten() {
            existing.insert(col.to_ascii_lowercase());
        }
    }

    if existing.is_empty() {
        return Ok(());
    }

    let v2_cols: &[(&str, &str)] = &[
        ("disc_number", "ALTER TABLE tracks ADD COLUMN disc_number INTEGER NOT NULL DEFAULT 0"),
        ("track_number", "ALTER TABLE tracks ADD COLUMN track_number INTEGER NOT NULL DEFAULT 0"),
        ("publisher", "ALTER TABLE tracks ADD COLUMN publisher TEXT"),
        ("legacy_play_count", "ALTER TABLE tracks ADD COLUMN legacy_play_count INTEGER NOT NULL DEFAULT 0"),
        ("lastfm_pulled_count", "ALTER TABLE tracks ADD COLUMN lastfm_pulled_count INTEGER NOT NULL DEFAULT 0"),
        ("local_unsynced_expired_count", "ALTER TABLE tracks ADD COLUMN local_unsynced_expired_count INTEGER NOT NULL DEFAULT 0"),
    ];
    for (col, sql) in v2_cols {
        if !existing.contains(*col) {
            conn.execute_batch(sql)
                .map_err(|e| format!("manifest v2 alter column {col} failed: {e}"))?;
        }
    }

    conn.execute_batch(
        "CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks (album);
         CREATE INDEX IF NOT EXISTS idx_tracks_genre ON tracks (genre);
         CREATE INDEX IF NOT EXISTS idx_tracks_work_name ON tracks (work_name);
         INSERT INTO schema_meta (key, value) VALUES ('schema_version', '2')
         ON CONFLICT(key) DO UPDATE SET value = '2';",
    )
    .map_err(|e| format!("manifest v2 schema stamp failed: {e}"))?;
    Ok(())
}

fn write_manifest_db(
    db_path: &Path,
    music_root: &Path,
    tracks: &[ScannedTrackRecord],
    prune_deleted: bool,
) -> Result<(), String> {
    let mut conn =
        Connection::open(db_path).map_err(|e| format!("sqlite open failed: {e}"))?;
    conn.busy_timeout(std::time::Duration::from_millis(5000))
        .map_err(|e| format!("sqlite busy_timeout failed: {e}"))?;
    conn.execute_batch(
        "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;",
    )
    .map_err(|e| format!("sqlite pragma setup failed: {e}"))?;
    conn.execute_batch(MANIFEST_SCHEMA_SQL)
        .map_err(|e| format!("sqlite schema init failed: {e}"))?;
    ensure_manifest_v2_schema(&conn)?;

    let now_ms = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0);

    let mut wf_prefixes: Vec<[u8; 16]> = tracks
        .iter()
        .filter(|t| !t.analysis.waveform_150.is_empty())
        .map(|t| {
            let mut p = [0u8; 16];
            p.copy_from_slice(&t.audio_frame_blake3[..16]);
            p
        })
        .collect();
    wf_prefixes.sort();
    wf_prefixes.dedup();
    let wf_payload_base = 12usize + wf_prefixes.len() * 20usize;
    let wf_offset_by_prefix: std::collections::HashMap<[u8; 16], i64> = wf_prefixes
        .iter()
        .enumerate()
        .map(|(sorted_idx, prefix)| (*prefix, (wf_payload_base + sorted_idx * 150) as i64))
        .collect();

    let tx = conn
        .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)
        .map_err(|e| format!("sqlite tx begin failed: {e}"))?;

    {
        let mut dedup_af_stmt = tx
            .prepare("DELETE FROM tracks WHERE audio_frame_blake3 = ?1 AND track_uid <> ?2")
            .map_err(|e| format!("sqlite prepare dedup failed: {e}"))?;
        let mut stmt = tx
            .prepare(
                "INSERT INTO tracks (
                    track_uid, audio_frame_blake3, header_size, header_blake3, padding_bytes,
                    rel_path, file_size, mtime_ms, codec, sample_rate_hz, bit_depth, bitrate_kbps, duration_ms,
                    title, album, album_artist, artists_json, composers_json, conductors_json,
                    work_name, movement_name, movement_number, disc_number, track_number, publisher,
                    genre, year, original_date, release_type,
                    replaygain_track_db, replaygain_album_db, replaygain_peak,
                    dj_scene_json, dj_scene_ts, dj_vocals, dj_vocals_ts, dj_energy, dj_energy_ts,
                    tempo_bpm, tempo_bucket, tempo_ts, mood_json, mood_ts,
                    waveform_offset
                ) VALUES (
                    ?1, ?2, ?3, ?4, ?5,
                    ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13,
                    ?14, ?15, ?16, ?17, ?18, ?19,
                    ?20, ?21, ?22, ?23, ?24, ?25,
                    ?26, ?27, ?28, ?29,
                    ?30, ?31, ?32,
                    ?33, ?34, ?35, ?36, ?37, ?38,
                    ?39, ?40, ?41, ?42, ?43,
                    ?44
                )
                ON CONFLICT(track_uid) DO UPDATE SET
                    header_size = excluded.header_size,
                    header_blake3 = excluded.header_blake3,
                    padding_bytes = excluded.padding_bytes,
                    rel_path = excluded.rel_path,
                    file_size = excluded.file_size,
                    mtime_ms = excluded.mtime_ms,
                    codec = excluded.codec,
                    sample_rate_hz = excluded.sample_rate_hz,
                    bit_depth = excluded.bit_depth,
                    bitrate_kbps = excluded.bitrate_kbps,
                    duration_ms = excluded.duration_ms,
                    title = excluded.title,
                    album = excluded.album,
                    album_artist = excluded.album_artist,
                    artists_json = excluded.artists_json,
                    composers_json = excluded.composers_json,
                    conductors_json = excluded.conductors_json,
                    work_name = COALESCE(NULLIF(excluded.work_name, ''), tracks.work_name),
                    movement_name = COALESCE(NULLIF(excluded.movement_name, ''), tracks.movement_name),
                    movement_number = COALESCE(excluded.movement_number, tracks.movement_number),
                    disc_number = CASE WHEN excluded.disc_number > 0 THEN excluded.disc_number ELSE tracks.disc_number END,
                    track_number = CASE WHEN excluded.track_number > 0 THEN excluded.track_number ELSE tracks.track_number END,
                    publisher = COALESCE(NULLIF(excluded.publisher, ''), tracks.publisher),
                    genre = excluded.genre,
                    year = excluded.year,
                    original_date = excluded.original_date,
                    release_type = COALESCE(excluded.release_type, tracks.release_type),
                    replaygain_track_db = excluded.replaygain_track_db,
                    replaygain_album_db = excluded.replaygain_album_db,
                    replaygain_peak = excluded.replaygain_peak,
                    dj_scene_json = CASE WHEN tracks.dj_scene_ts = 0 THEN excluded.dj_scene_json ELSE tracks.dj_scene_json END,
                    dj_scene_ts = CASE WHEN tracks.dj_scene_ts = 0 THEN excluded.dj_scene_ts ELSE tracks.dj_scene_ts END,
                    dj_vocals = CASE WHEN tracks.dj_vocals_ts = 0 THEN excluded.dj_vocals ELSE tracks.dj_vocals END,
                    dj_vocals_ts = CASE WHEN tracks.dj_vocals_ts = 0 THEN excluded.dj_vocals_ts ELSE tracks.dj_vocals_ts END,
                    dj_energy = CASE WHEN tracks.dj_energy_ts = 0 THEN excluded.dj_energy ELSE tracks.dj_energy END,
                    dj_energy_ts = CASE WHEN tracks.dj_energy_ts = 0 THEN excluded.dj_energy_ts ELSE tracks.dj_energy_ts END,
                    tempo_bpm = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_bpm ELSE tracks.tempo_bpm END,
                    tempo_bucket = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_bucket ELSE tracks.tempo_bucket END,
                    tempo_ts = CASE WHEN tracks.tempo_ts = 0 THEN excluded.tempo_ts ELSE tracks.tempo_ts END,
                    mood_json = CASE WHEN tracks.mood_ts = 0 THEN excluded.mood_json ELSE tracks.mood_json END,
                    mood_ts = CASE WHEN tracks.mood_ts = 0 THEN excluded.mood_ts ELSE tracks.mood_ts END,
                    waveform_offset = COALESCE(excluded.waveform_offset, tracks.waveform_offset)",
            )
            .map_err(|e| format!("sqlite prepare failed: {e}"))?;

        for t in tracks.iter() {
            dedup_af_stmt
                .execute(rusqlite::params![t.audio_frame_blake3.as_slice(), t.track_uid])
                .map_err(|e| format!("sqlite audio_frame_blake3 dedup failed: {e}"))?;
            let dj_vocals_str = t.dj_vocals.map(DjVocals::as_str);
            let dj_energy_str = t.dj_energy.map(DjEnergy::as_str);
            let tempo_bucket_str = t.tempo_bucket.map(|b| match b {
                TempoBucket::VerySlow => "Very Slow",
                TempoBucket::Slow => "Slow",
                TempoBucket::Moderate => "Moderate",
                TempoBucket::Fast => "Fast",
                TempoBucket::VeryFast => "Very Fast",
            });
            let wf_offset = if t.analysis.waveform_150.is_empty() {
                None::<i64>
            } else {
                let mut prefix = [0u8; 16];
                prefix.copy_from_slice(&t.audio_frame_blake3[..16]);
                wf_offset_by_prefix.get(&prefix).copied()
            };

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
                t.disc_number as i64,
                t.track_number as i64,
                t.publisher,
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
                wf_offset,
            ])
            .map_err(|e| format!("sqlite insert failed for {}: {e}", t.rel_path))?;
        }
    }

    if prune_deleted {
        let active_uids: HashSet<&str> = tracks.iter().map(|t| t.track_uid.as_str()).collect();
        let mut to_delete: Vec<String> = Vec::new();
        {
            let mut q = tx
                .prepare("SELECT track_uid, rel_path FROM tracks")
                .map_err(|e| format!("sqlite select for prune failed: {e}"))?;
            let rows = q
                .query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))
                .map_err(|e| format!("sqlite prune row read failed: {e}"))?;
            for (uid, rel) in rows.flatten() {
                if !active_uids.contains(uid.as_str()) {
                    let abs_path = music_root.join(rel.replace('/', std::path::MAIN_SEPARATOR_STR));
                    if !abs_path.exists() {
                        to_delete.push(uid);
                    }
                }
            }
        }
        if !to_delete.is_empty() {
            let mut del_stmt = tx
                .prepare("DELETE FROM tracks WHERE track_uid = ?1")
                .map_err(|e| format!("sqlite prepare delete failed: {e}"))?;
            for uid in to_delete {
                del_stmt
                    .execute(rusqlite::params![uid])
                    .map_err(|e| format!("sqlite delete pruned track failed: {e}"))?;
            }
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
        assert_eq!(wf_off, 12 + 20);
        assert_eq!(fp_blob.len(), 32);

        // Verify waveforms.bin lookup matches
        let wf_bytes = fs::read(tonewise_dir.join("waveforms.bin")).unwrap();
        let mut prefix = [0u8; 16];
        prefix.copy_from_slice(&fp_blob[..16]);
        let envelope = WaveformArchive::lookup_bytes(&wf_bytes, &prefix).expect("waveform found");
        assert_eq!(envelope.len(), 150);
        assert_eq!(&wf_bytes[wf_off as usize..wf_off as usize + 150], &envelope);
        assert!(envelope.iter().any(|&v| v > 200));

        drop(conn);
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn cli_scan_preserves_existing_ledger_and_writes_real_waveform_offsets_for_multiple_tracks() {
        let base = std::env::temp_dir().join("tw_cli_preserve_ledger_test");
        let _ = fs::remove_dir_all(&base);
        let music = base.join("Music");
        let album = music.join("Suite");
        fs::create_dir_all(&album).unwrap();
        let wav1 = album.join("01_Slow.wav");
        let wav2 = album.join("02_Fast.wav");
        write_test_wav(&wav1, 44100, 2);
        write_test_wav(&wav2, 48000, 6);

        let tonewise_dir = music.join(".tonewise");
        scan_library(&music, &tonewise_dir, false).expect("initial scan");

        let conn = Connection::open(tonewise_dir.join("manifest.db")).unwrap();
        conn.execute(
            "UPDATE tracks SET rating = 5, play_count_total = 42 WHERE rel_path = 'Suite/01_Slow.wav'",
            [],
        )
        .unwrap();
        drop(conn);

        // Rescan: must preserve rating=5 and play_count_total=42, and write accurate waveforms.bin offsets
        scan_library(&music, &tonewise_dir, false).expect("rescan preserves ledger");

        let conn = Connection::open(tonewise_dir.join("manifest.db")).unwrap();
        let wf_bytes = fs::read(tonewise_dir.join("waveforms.bin")).unwrap();
        let mut stmt = conn
            .prepare("SELECT rel_path, rating, play_count_total, waveform_offset, audio_frame_blake3 FROM tracks")
            .unwrap();
        let rows: Vec<(String, i64, i64, i64, Vec<u8>)> = stmt
            .query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?)))
            .unwrap()
            .map(|r| r.unwrap())
            .collect();
        assert_eq!(rows.len(), 2);
        for (rel, rating, plays, off, fp_blob) in &rows {
            if rel == "Suite/01_Slow.wav" {
                assert_eq!(*rating, 5);
                assert_eq!(*plays, 42);
            }
            let mut prefix = [0u8; 16];
            prefix.copy_from_slice(&fp_blob[..16]);
            let envelope = WaveformArchive::lookup_bytes(&wf_bytes, &prefix).unwrap();
            assert_eq!(&wf_bytes[*off as usize..*off as usize + 150], &envelope);
        }

        drop(stmt);
        drop(conn);
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

    #[test]
    fn cli_scan_deduplicates_identical_audio_files_and_supports_fast_mode() {
        let base = std::env::temp_dir().join("tw_cli_dedup_fast_test");
        let _ = fs::remove_dir_all(&base);
        let music = base.join("Music");
        let album_a = music.join("AlbumA");
        let album_b = music.join("AlbumB");
        fs::create_dir_all(&album_a).unwrap();
        fs::create_dir_all(&album_b).unwrap();

        let wav_a = album_a.join("01_Dup.wav");
        let wav_b = album_b.join("01_DupCopy.wav");
        write_test_wav(&wav_a, 44100, 4);
        fs::copy(&wav_a, &wav_b).unwrap();

        let tonewise_dir = music.join(".tonewise");
        let full_summary =
            scan_library_with_options(&music, &tonewise_dir, false, false).expect("full scan dedups");
        assert_eq!(full_summary.scanned_tracks, 1);

        let conn = Connection::open(tonewise_dir.join("manifest.db")).unwrap();
        let count: i64 = conn
            .query_row("SELECT COUNT(*) FROM tracks", [], |r| r.get(0))
            .unwrap();
        assert_eq!(count, 1);

        let fast_summary =
            scan_library_with_options(&music, &tonewise_dir, false, true).expect("fast scan dedups");
        assert_eq!(fast_summary.scanned_tracks, 1);
        assert_eq!(fast_summary.tempo_backfilled, 0);

        let (fast_count, wf_off): (i64, Option<i64>) = conn
            .query_row(
                "SELECT (SELECT COUNT(*) FROM tracks), waveform_offset FROM tracks LIMIT 1",
                [],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .unwrap();
        assert_eq!(fast_count, 1);
        assert_eq!(wf_off, Some(32));

        // Fresh directory scanned directly in fast mode leaves waveform_offset NULL
        let fresh_tonewise_dir = base.join("FreshTonewise");
        scan_library_with_options(&music, &fresh_tonewise_dir, false, true).expect("fresh fast scan");
        let fresh_conn = Connection::open(fresh_tonewise_dir.join("manifest.db")).unwrap();
        let fresh_wf_off: Option<i64> = fresh_conn
            .query_row("SELECT waveform_offset FROM tracks LIMIT 1", [], |r| r.get(0))
            .unwrap();
        assert_eq!(fresh_wf_off, None);

        drop(fresh_conn);
        drop(conn);
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn parse_movement_number_handles_slash_and_text_formats() {
        assert_eq!(parse_movement_number("1/4"), Some(1));
        assert_eq!(parse_movement_number("03 of 4"), Some(3));
        assert_eq!(parse_movement_number(" 2 "), Some(2));
        assert_eq!(parse_movement_number("0"), None);
        assert_eq!(parse_movement_number("0/4"), None);
        assert_eq!(parse_movement_number("abc"), None);
    }

    #[test]
    fn cli_scan_upgrades_v1_manifest_prunes_deleted_files_and_guards_blake3_conflict() {
        let base = std::env::temp_dir().join("tw_cli_v2_prune_test");
        let _ = fs::remove_dir_all(&base);
        let music = base.join("Music");
        let disc2 = music.join("AlbumCD2");
        fs::create_dir_all(&disc2).unwrap();

        let wav_keep = disc2.join("01_Keep.wav");
        let wav_del = disc2.join("02_DeleteMe.wav");
        write_test_wav(&wav_keep, 44100, 3);
        write_test_wav(&wav_del, 48000, 3);

        let tonewise_dir = music.join(".tonewise");
        fs::create_dir_all(&tonewise_dir).unwrap();
        let db_path = tonewise_dir.join("manifest.db");

        // Seed synthetic v1 manifest without v2 columns (disc_number, track_number, publisher, ledger cols)
        {
            let conn = Connection::open(&db_path).unwrap();
            conn.execute_batch(
                "CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                 INSERT INTO schema_meta (key, value) VALUES ('schema_version', '1');
                 CREATE TABLE tracks (
                     track_uid TEXT PRIMARY KEY,
                     audio_frame_blake3 BLOB NOT NULL UNIQUE,
                     header_size INTEGER NOT NULL DEFAULT 0,
                     header_blake3 BLOB NOT NULL,
                     padding_bytes INTEGER NOT NULL DEFAULT 0,
                     rel_path TEXT NOT NULL,
                     file_size INTEGER NOT NULL,
                     mtime_ms INTEGER NOT NULL,
                     codec TEXT NOT NULL,
                     sample_rate_hz INTEGER NOT NULL,
                     bit_depth INTEGER,
                     bitrate_kbps INTEGER,
                     duration_ms INTEGER NOT NULL,
                     title TEXT NOT NULL,
                     album TEXT,
                     album_artist TEXT,
                     artists_json TEXT NOT NULL DEFAULT '[]',
                     composers_json TEXT NOT NULL DEFAULT '[]',
                     conductors_json TEXT NOT NULL DEFAULT '[]',
                     work_name TEXT,
                     movement_name TEXT,
                     movement_number INTEGER,
                     genre TEXT,
                     year INTEGER,
                     original_date TEXT,
                     release_type TEXT,
                     replaygain_track_db REAL,
                     replaygain_album_db REAL,
                     replaygain_peak REAL,
                     dj_scene_json TEXT NOT NULL DEFAULT '[]',
                     dj_scene_ts INTEGER NOT NULL DEFAULT 0,
                     dj_vocals TEXT,
                     dj_vocals_ts INTEGER NOT NULL DEFAULT 0,
                     dj_energy TEXT,
                     dj_energy_ts INTEGER NOT NULL DEFAULT 0,
                     tempo_bpm REAL,
                     tempo_bucket TEXT,
                     tempo_ts INTEGER NOT NULL DEFAULT 0,
                     mood_json TEXT NOT NULL DEFAULT '[]',
                     mood_ts INTEGER NOT NULL DEFAULT 0,
                     rating INTEGER DEFAULT 0,
                     rating_ts INTEGER NOT NULL DEFAULT 0,
                     play_count_total INTEGER NOT NULL DEFAULT 0,
                     last_played_ts INTEGER NOT NULL DEFAULT 0,
                     waveform_offset INTEGER
                 );",
            )
            .unwrap();

            // Simulate legacy track_uid != canonical hex for same audio_frame_blake3 of wav_keep
            let fp_keep = compute_fingerprint_from_path(&wav_keep).unwrap();
            let fp_bytes = hex_to_32(&fp_keep.audio_frame_blake3_hex).unwrap();
            let hdr_bytes = hex_to_32(&fp_keep.header_blake3_hex).unwrap();
            conn.execute(
                "INSERT INTO tracks (track_uid, audio_frame_blake3, header_size, header_blake3, rel_path, file_size, mtime_ms, codec, sample_rate_hz, duration_ms, title, play_count_total)
                 VALUES ('legacy-uid-keep', ?1, 0, ?2, 'AlbumCD2/01_Keep.wav', 100, 1, 'wav', 44100, 3000, 'Keep Old', 7)",
                rusqlite::params![fp_bytes.as_slice(), hdr_bytes.as_slice()],
            ).unwrap();
        }

        // First scan must upgrade schema_version to '2', replace conflicting audio_frame_blake3 UID without UNIQUE error, and record both tracks
        let s1 = scan_library(&music, &tonewise_dir, false).expect("scan upgrades v1 cleanly");
        assert_eq!(s1.scanned_tracks, 2);

        // Delete wav_del on disk and rescan -> deleted track must be pruned from tracks table
        fs::remove_file(&wav_del).unwrap();
        let s2 = scan_library(&music, &tonewise_dir, false).expect("rescan prunes deleted file");
        assert_eq!(s2.scanned_tracks, 1);

        let conn = Connection::open(&db_path).unwrap();
        let schema_ver: String = conn
            .query_row("SELECT value FROM schema_meta WHERE key = 'schema_version'", [], |r| r.get(0))
            .unwrap();
        assert_eq!(schema_ver, "2");

        let count: i64 = conn
            .query_row("SELECT COUNT(*) FROM tracks", [], |r| r.get(0))
            .unwrap();
        assert_eq!(count, 1);

        let remaining_rel: String = conn
            .query_row("SELECT rel_path FROM tracks LIMIT 1", [], |r| r.get(0))
            .unwrap();
        assert_eq!(remaining_rel, "AlbumCD2/01_Keep.wav");

        drop(conn);
        let _ = fs::remove_dir_all(&base);
    }
}

fn parse_movement_number(raw: &str) -> Option<u32> {
    let digits: String = raw
        .trim()
        .chars()
        .skip_while(|c| !c.is_ascii_digit())
        .take_while(|c| c.is_ascii_digit())
        .collect();
    if digits.is_empty() {
        return None;
    }
    digits.parse::<u32>().ok().filter(|&n| n > 0)
}
