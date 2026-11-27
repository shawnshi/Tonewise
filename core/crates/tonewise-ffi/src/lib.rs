use jni::JNIEnv;
use jni::objects::{JByteArray, JClass, JString};
use jni::sys::{jint, jstring};
use serde::Deserialize;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char};
use std::fs::{File, OpenOptions};
use tonewise_dsp_core::{
    AutoEqPolicy, DspProfile, ResolvedAutoEq, TrackAutoEqInput, compose_dsp_profiles,
    default_auto_eq_catalog, resolve_auto_eq,
};
use tonewise_rules::{RuleTrack, SceneCard, evaluate_scene};
use tonewise_sync::{
    LwwTrackState, SessionSlot, TagMutationEvent, WaveformArchive, apply_header_delta_patch,
    apply_lww_mutations, compute_audio_frame_fingerprint, decode_hex_prefix16,
    merge_m3u8_three_way, merge_session_slots,
};

pub const TONEWISE_CORE_ABI_VERSION: u32 = 1;

#[derive(Deserialize)]
struct M3u8MergeRequest {
    base: Vec<String>,
    ours: Vec<String>,
    theirs: Vec<String>,
}

#[derive(Deserialize)]
struct LwwApplyRequest {
    state: LwwTrackState,
    mutations: Vec<TagMutationEvent>,
}

#[derive(Deserialize)]
struct SessionMergeRequest {
    local: Vec<SessionSlot>,
    remote: Vec<SessionSlot>,
}

#[derive(Deserialize)]
struct TagWriteRequest {
    path: String,
    updates: HashMap<String, Option<String>>,
}

#[derive(Deserialize)]
struct AutoEqResolveRequest {
    #[serde(default)]
    policy: AutoEqPolicy,
    track: TrackAutoEqInput,
    #[serde(default)]
    previous_work_key: Option<String>,
    #[serde(default)]
    previous_resolved: Option<ResolvedAutoEq>,
}

#[derive(Deserialize)]
struct ComposeDspRequest {
    #[serde(default)]
    device_profile: DspProfile,
    #[serde(default)]
    content_profile: DspProfile,
}

fn resolve_auto_eq_json_impl(request_json: &str) -> String {
    let req: AutoEqResolveRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid auto eq request json: {e}")),
    };
    let resolved = resolve_auto_eq(
        &req.policy,
        &req.track,
        req.previous_work_key.as_deref(),
        req.previous_resolved.as_ref(),
    );
    serde_json::to_string(&resolved).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn compose_dsp_json_impl(request_json: &str) -> String {
    let req: ComposeDspRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid compose dsp request json: {e}")),
    };
    let composed = compose_dsp_profiles(&req.device_profile, &req.content_profile);
    serde_json::to_string(&composed).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn default_auto_eq_catalog_json_impl() -> String {
    let catalog = default_auto_eq_catalog();
    serde_json::to_string(&catalog).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn split_artists_json_impl(raw: &str) -> String {
    let out = tonewise_metadata::split_artists_preserving_whitelist(raw);
    serde_json::to_string(&out).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn evaluate_scene_json_impl(scene_json: &str, tracks_json: &str) -> String {
    let scene: SceneCard = match serde_json::from_str(scene_json) {
        Ok(s) => s,
        Err(e) => return json_error(&format!("invalid scene json: {e}")),
    };
    let tracks: Vec<RuleTrack> = match serde_json::from_str(tracks_json) {
        Ok(t) => t,
        Err(e) => return json_error(&format!("invalid tracks json: {e}")),
    };
    let matched = evaluate_scene(&scene, &tracks);
    serde_json::to_string(&matched).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn merge_m3u8_json_impl(request_json: &str) -> String {
    let req: M3u8MergeRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid merge request json: {e}")),
    };
    let result = merge_m3u8_three_way(&req.base, &req.ours, &req.theirs);
    serde_json::to_string(&result).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn fingerprint_path_impl(path: &str) -> String {
    let mut file = match File::open(path) {
        Ok(f) => f,
        Err(e) => return json_error(&format!("open failed: {e}")),
    };
    match compute_audio_frame_fingerprint(&mut file) {
        Ok(fp) => serde_json::to_string(&fp).unwrap_or_else(|e| json_error(&e.to_string())),
        Err(e) => json_error(&format!("fingerprint failed: {e}")),
    }
}

#[cfg(unix)]
fn fingerprint_fd_impl(fd: i32) -> String {
    use std::mem::ManuallyDrop;
    use std::os::fd::FromRawFd;

    if fd < 0 {
        return json_error("invalid file descriptor");
    }
    // Safety: caller owns `fd` (e.g. Android ParcelFileDescriptor); ManuallyDrop ensures
    // we never close the original fd while `try_clone()` duplicates it safely.
    let borrowed = ManuallyDrop::new(unsafe { File::from_raw_fd(fd) });
    let mut cloned = match borrowed.try_clone() {
        Ok(f) => f,
        Err(e) => return json_error(&format!("dup fd failed: {e}")),
    };
    match compute_audio_frame_fingerprint(&mut cloned) {
        Ok(fp) => serde_json::to_string(&fp).unwrap_or_else(|e| json_error(&e.to_string())),
        Err(e) => json_error(&format!("fingerprint failed: {e}")),
    }
}

#[cfg(not(unix))]
fn fingerprint_fd_impl(_fd: i32) -> String {
    json_error("raw fd fingerprinting is only supported on Unix/Android targets")
}

fn lookup_waveform_path_impl(archive_path: &str, blake3_hex: &str) -> String {
    let Some(prefix16) = decode_hex_prefix16(blake3_hex) else {
        return json_error("invalid blake3 hex");
    };
    let mut file = match File::open(archive_path) {
        Ok(f) => f,
        Err(e) => return json_error(&format!("open failed: {e}")),
    };
    match WaveformArchive::lookup_reader(&mut file, &prefix16) {
        Ok(Some(points)) => serde_json::to_string(&points.to_vec())
            .unwrap_or_else(|e| json_error(&e.to_string())),
        Ok(None) => "null".to_string(),
        Err(e) => json_error(&format!("lookup failed: {e}")),
    }
}

#[cfg(unix)]
fn lookup_waveform_fd_impl(fd: i32, blake3_hex: &str) -> String {
    use std::mem::ManuallyDrop;
    use std::os::fd::FromRawFd;

    let Some(prefix16) = decode_hex_prefix16(blake3_hex) else {
        return json_error("invalid blake3 hex");
    };
    if fd < 0 {
        return json_error("invalid file descriptor");
    }
    let borrowed = ManuallyDrop::new(unsafe { File::from_raw_fd(fd) });
    let mut cloned = match borrowed.try_clone() {
        Ok(f) => f,
        Err(e) => return json_error(&format!("dup fd failed: {e}")),
    };
    match WaveformArchive::lookup_reader(&mut cloned, &prefix16) {
        Ok(Some(points)) => serde_json::to_string(&points.to_vec())
            .unwrap_or_else(|e| json_error(&e.to_string())),
        Ok(None) => "null".to_string(),
        Err(e) => json_error(&format!("lookup failed: {e}")),
    }
}

#[cfg(not(unix))]
fn lookup_waveform_fd_impl(_fd: i32, _blake3_hex: &str) -> String {
    json_error("raw fd waveform lookup is only supported on Unix/Android targets")
}

fn apply_lww_mutations_json_impl(request_json: &str) -> String {
    let mut req: LwwApplyRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid lww request json: {e}")),
    };
    let applied = apply_lww_mutations(&mut req.state, &req.mutations);
    serde_json::json!({ "applied": applied, "state": req.state }).to_string()
}

fn merge_sessions_json_impl(request_json: &str) -> String {
    let req: SessionMergeRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid session merge json: {e}")),
    };
    let merged = merge_session_slots(&req.local, &req.remote);
    serde_json::to_string(&merged).unwrap_or_else(|e| json_error(&e.to_string()))
}

fn sync_file_best_effort(file: &File) -> std::io::Result<()> {
    match file.sync_data() {
        Ok(()) => Ok(()),
        #[cfg(unix)]
        Err(e) if e.raw_os_error() == Some(22) => Ok(()), // EINVAL on non-syncable pipes/fds
        Err(e) => Err(e),
    }
}

fn apply_header_patch_file_impl(file: &mut File, expected_old_hex: &str, new_header: &[u8]) -> String {
    use std::io::{Read, Seek, SeekFrom, Write};

    let backup_hdr = match compute_audio_frame_fingerprint(file) {
        Ok(fp) if fp.header_size <= 64 * 1024 * 1024 => {
            let mut buf = vec![0u8; fp.header_size as usize];
            if file.seek(SeekFrom::Start(0)).is_ok() && file.read_exact(&mut buf).is_ok() {
                Some(buf)
            } else {
                None
            }
        }
        _ => None,
    };

    match apply_header_delta_patch(file, expected_old_hex, new_header) {
        Ok(outcome) => {
            if matches!(outcome, tonewise_sync::HeaderPatchOutcome::Applied { .. })
                && let Err(e) = sync_file_best_effort(file)
            {
                return json_error(&format!("sync failed: {e}"));
            }
            serde_json::to_string(&outcome).unwrap_or_else(|e| json_error(&e.to_string()))
        }
        Err(e) => {
            if let Some(orig) = backup_hdr {
                let _ = file.seek(SeekFrom::Start(0));
                let _ = file.write_all(&orig);
                let _ = sync_file_best_effort(file);
            }
            json_error(&format!("patch failed: {e}"))
        }
    }
}

fn apply_header_patch_path_impl(path: &str, expected_old_hex: &str, new_header: &[u8]) -> String {
    let mut file = match OpenOptions::new().read(true).write(true).open(path) {
        Ok(f) => f,
        Err(e) => return json_error(&format!("open rw failed: {e}")),
    };
    apply_header_patch_file_impl(&mut file, expected_old_hex, new_header)
}

#[cfg(unix)]
fn apply_header_patch_fd_impl(fd: i32, expected_old_hex: &str, new_header: &[u8]) -> String {
    use std::mem::ManuallyDrop;
    use std::os::fd::FromRawFd;

    if fd < 0 {
        return json_error("invalid file descriptor");
    }
    let borrowed = ManuallyDrop::new(unsafe { File::from_raw_fd(fd) });
    let mut cloned = match borrowed.try_clone() {
        Ok(f) => f,
        Err(e) => return json_error(&format!("dup fd failed: {e}")),
    };
    apply_header_patch_file_impl(&mut cloned, expected_old_hex, new_header)
}

#[cfg(not(unix))]
fn apply_header_patch_fd_impl(_fd: i32, _expected_old_hex: &str, _new_header: &[u8]) -> String {
    json_error("raw fd patching is only supported on Unix/Android targets")
}

fn json_error(msg: &str) -> String {
    serde_json::json!({ "error": msg }).to_string()
}

#[cfg(not(target_os = "android"))]
fn analyze_audio_path_impl(path: &str) -> String {
    match tonewise_dsp_core::analyze_audio_file(std::path::Path::new(path)) {
        Ok(res) => serde_json::to_string(&res).unwrap_or_else(|e| json_error(&e.to_string())),
        Err(e) => json_error(&format!("analyze failed: {e}")),
    }
}

#[cfg(target_os = "android")]
fn analyze_audio_path_impl(_path: &str) -> String {
    json_error("analyze_audio_path is not enabled in Android slim build")
}

#[cfg(not(target_os = "android"))]
fn parse_tag_list_value(v: &str) -> Vec<String> {
    if v.starts_with('[')
        && let Ok(arr) = serde_json::from_str::<Vec<String>>(v)
    {
        return arr
            .into_iter()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
            .collect();
    }
    v.split([';', ','])
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .collect()
}

#[cfg(not(target_os = "android"))]
fn save_primary_tag_or_file(
    target: &std::path::Path,
    tagged: &lofty::file::TaggedFile,
) -> lofty::error::Result<()> {
    use lofty::config::WriteOptions;
    use lofty::file::{AudioFile, TaggedFileExt};
    use lofty::tag::TagExt;

    if let Some(primary) = tagged.primary_tag() {
        primary.save_to_path(target, WriteOptions::default())
    } else {
        tagged.save_to_path(target, WriteOptions::default())
    }
}

#[cfg(not(target_os = "android"))]
fn strip_id3v2_txxx_work_on_mpeg(path: &std::path::Path) -> Result<(), String> {
    use lofty::config::{ParseOptions, ParsingMode, WriteOptions};
    use lofty::file::AudioFile;
    use lofty::mpeg::MpegFile;
    use lofty::tag::TagExt;

    let mut file = std::fs::File::open(path).map_err(|e| format!("open mpeg failed: {e}"))?;
    let mut mpeg = MpegFile::read_from(
        &mut file,
        ParseOptions::new()
            .read_properties(false)
            .parsing_mode(ParsingMode::Relaxed),
    )
    .map_err(|e| format!("read mpeg failed: {e}"))?;
    drop(file);

    if let Some(id3v2) = mpeg.id3v2_mut() {
        let mut removed = false;
        for desc in ["WORK", "work", "Work"] {
            while id3v2.remove_user_text(desc).is_some() {
                removed = true;
            }
        }
        if removed {
            id3v2
                .save_to_path(path, WriteOptions::default())
                .map_err(|e| format!("id3v2 save failed: {e}"))?;
        }
    }
    Ok(())
}

#[cfg(all(test, not(target_os = "android")))]
fn save_tagged_file_atomically(
    path: &std::path::Path,
    tagged: &lofty::file::TaggedFile,
) -> Result<(), String> {
    save_tagged_file_atomically_ex(path, tagged, false)
}

#[cfg(not(target_os = "android"))]
fn find_flac_marker_after_id3v2(file: &mut std::fs::File) -> Option<u64> {
    use std::io::{Read, Seek, SeekFrom};

    let mut offset = 0u64;
    for _ in 0..8 {
        file.seek(SeekFrom::Start(offset)).ok()?;
        let mut hdr = [0u8; 10];
        let n = file.read(&mut hdr).ok()?;
        if n < 4 {
            return None;
        }
        if &hdr[..4] == b"fLaC" {
            return Some(offset);
        }
        if n < 10 || &hdr[..3] != b"ID3" {
            break;
        }
        if (hdr[6] & 0x80) != 0 || (hdr[7] & 0x80) != 0 || (hdr[8] & 0x80) != 0 || (hdr[9] & 0x80) != 0 {
            return None;
        }
        let size = ((hdr[6] as u64) << 21)
            | ((hdr[7] as u64) << 14)
            | ((hdr[8] as u64) << 7)
            | (hdr[9] as u64);
        let has_footer = (hdr[5] & 0x10) != 0;
        let step = 10u64
            .checked_add(size)?
            .checked_add(if has_footer { 10 } else { 0 })?;
        offset = offset.checked_add(step)?;
    }

    file.seek(SeekFrom::Start(offset)).ok()?;
    let mut probe_buf = vec![0u8; 4096];
    let n = file.read(&mut probe_buf).ok()?;
    if n < 4 {
        return None;
    }
    let nul_skip = probe_buf[..n].iter().position(|&b| b != 0)?;
    if n - nul_skip >= 4 && &probe_buf[nul_skip..nul_skip + 4] == b"fLaC" {
        return offset.checked_add(nul_skip as u64);
    }
    None
}

#[cfg(not(target_os = "android"))]
fn strip_leading_id3v2_from_flac(tmp_path: &std::path::Path) -> Result<bool, String> {
    use std::io::{BufReader, BufWriter, Seek, SeekFrom, Write};

    let mut src = std::fs::File::open(tmp_path).map_err(|e| format!("open flac temp failed: {e}"))?;
    let Some(marker_offset) = find_flac_marker_after_id3v2(&mut src) else {
        return Ok(false);
    };
    if marker_offset == 0 {
        return Ok(false);
    }

    src.seek(SeekFrom::Start(marker_offset))
        .map_err(|e| format!("seek flac marker failed: {e}"))?;

    let stripped_path = tmp_path.with_extension("twstrip");
    {
        let mut reader = BufReader::with_capacity(64 * 1024, src);
        let dst = std::fs::File::create(&stripped_path)
            .map_err(|e| format!("create stripped flac temp failed: {e}"))?;
        let mut writer = BufWriter::with_capacity(64 * 1024, dst);
        if let Err(e) = std::io::copy(&mut reader, &mut writer) {
            let _ = std::fs::remove_file(&stripped_path);
            return Err(format!("copy stripped flac failed: {e}"));
        }
        if let Err(e) = writer.flush() {
            let _ = std::fs::remove_file(&stripped_path);
            return Err(format!("flush stripped flac failed: {e}"));
        }
    }

    if let Err(e) = std::fs::remove_file(tmp_path) {
        let _ = std::fs::remove_file(&stripped_path);
        return Err(format!("remove prefixed flac temp failed: {e}"));
    }
    if let Err(e) = std::fs::rename(&stripped_path, tmp_path) {
        let _ = std::fs::remove_file(&stripped_path);
        return Err(format!("rename stripped flac temp failed: {e}"));
    }
    Ok(true)
}

#[cfg(not(target_os = "android"))]
fn backfill_flac_vorbis_from_id3v2(primary: &mut lofty::tag::Tag, id3: &lofty::tag::Tag) {
    use lofty::tag::{Accessor, ItemKey, ItemValue, TagItem};

    if primary.title().as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none()
        && let Some(v) = id3.title().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    {
        primary.set_title(v);
    }
    if primary.artist().as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none()
        && let Some(v) = id3.artist().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    {
        primary.set_artist(v);
    }
    if primary.album().as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none()
        && let Some(v) = id3.album().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    {
        primary.set_album(v);
    }
    if primary.genre().as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none()
        && let Some(v) = id3.genre().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    {
        primary.set_genre(v);
    }
    if primary.track().is_none()
        && let Some(t) = id3.track()
    {
        primary.set_track(t);
    }
    if primary.disk().is_none()
        && let Some(d) = id3.disk()
    {
        primary.set_disk(d);
    }
    if primary.year().is_none()
        && let Some(y) = id3.year()
    {
        primary.set_year(y);
    }
    if primary.comment().as_deref().map(str::trim).filter(|s| !s.is_empty()).is_none()
        && let Some(v) = id3.comment().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
    {
        primary.set_comment(v);
    }

    for key in [
        ItemKey::TrackArtists,
        ItemKey::AlbumArtist,
        ItemKey::Composer,
        ItemKey::Conductor,
        ItemKey::Work,
        ItemKey::ContentGroup,
        ItemKey::Movement,
        ItemKey::MovementNumber,
        ItemKey::RecordingDate,
        ItemKey::OriginalReleaseDate,
        ItemKey::Publisher,
        ItemKey::Label,
        ItemKey::Mood,
        ItemKey::Bpm,
        ItemKey::IntegerBpm,
        ItemKey::FlagCompilation,
        ItemKey::ReplayGainTrackGain,
        ItemKey::ReplayGainAlbumGain,
        ItemKey::ReplayGainTrackPeak,
    ] {
        if primary.get_strings(&key).next().is_none() {
            for val in id3.get_strings(&key) {
                let trimmed = val.trim();
                if !trimmed.is_empty() {
                    primary.push(TagItem::new(
                        key.clone(),
                        ItemValue::Text(trimmed.to_string()),
                    ));
                }
            }
        }
    }
    if primary.get_strings(&ItemKey::Work).next().is_none()
        && let Some(cg) = id3.get_string(&ItemKey::ContentGroup)
    {
        let trimmed = cg.trim();
        if !trimmed.is_empty() {
            primary.insert_text(ItemKey::Work, trimmed.to_string());
        }
    }
}

#[cfg(not(target_os = "android"))]
fn save_tagged_file_atomically_ex(
    path: &std::path::Path,
    tagged: &lofty::file::TaggedFile,
    strip_id3v2_txxx_work: bool,
) -> Result<(), String> {
    use lofty::file::TaggedFileExt;
    use std::sync::atomic::{AtomicU64, Ordering};

    static TEMP_SEQ: AtomicU64 = AtomicU64::new(0);
    let seq = TEMP_SEQ.fetch_add(1, Ordering::Relaxed);
    let pid = std::process::id();
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or_else(|| std::path::Path::new("."));
    let stem = path
        .file_stem()
        .map(|n| n.to_string_lossy().to_string())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "audio".to_string());
    let tmp_name = match path.extension().and_then(|e| e.to_str()).filter(|s| !s.is_empty()) {
        Some(ext) => format!("{stem}.twtmp.{pid}.{seq}.{ext}"),
        None => format!("{stem}.twtmp.{pid}.{seq}"),
    };
    let tmp_path = parent.join(tmp_name);

    std::fs::copy(path, &tmp_path).map_err(|e| format!("temp copy failed: {e}"))?;
    if tagged.file_type() == lofty::file::FileType::Flac
        && let Err(e) = strip_leading_id3v2_from_flac(&tmp_path)
    {
        let _ = std::fs::remove_file(&tmp_path);
        return Err(e);
    }
    if let Err(e) = save_primary_tag_or_file(&tmp_path, tagged) {
        let _ = std::fs::remove_file(&tmp_path);
        return Err(format!("lofty save failed: {e}"));
    }
    if strip_id3v2_txxx_work && let Err(e) = strip_id3v2_txxx_work_on_mpeg(&tmp_path) {
        let _ = std::fs::remove_file(&tmp_path);
        return Err(e);
    }
    let f = std::fs::OpenOptions::new()
        .write(true)
        .open(&tmp_path)
        .map_err(|e| {
            let _ = std::fs::remove_file(&tmp_path);
            format!("open temp for sync failed: {e}")
        })?;
    f.sync_all().map_err(|e| {
        let _ = std::fs::remove_file(&tmp_path);
        format!("sync temp failed: {e}")
    })?;
    drop(f);

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

        // Fall back to explicit backup swap if MoveFileExW rejects replacement under external lock/ACL
        let bak_path = parent.join(format!("{stem}.twbak.{pid}.{seq}"));
        let _ = std::fs::remove_file(&bak_path);
        if let Err(e) = std::fs::rename(path, &bak_path) {
            let _ = std::fs::remove_file(&tmp_path);
            return Err(format!("backup rename failed: {e}"));
        }
        if let Err(e) = std::fs::rename(&tmp_path, path) {
            let _ = std::fs::rename(&bak_path, path);
            let _ = std::fs::remove_file(&tmp_path);
            return Err(format!("atomic replace failed: {e}"));
        }
        let _ = std::fs::remove_file(&bak_path);
        Ok(())
    }

    #[cfg(not(windows))]
    {
        if let Err(e) = std::fs::rename(&tmp_path, path) {
            let _ = std::fs::remove_file(&tmp_path);
            return Err(format!("atomic rename failed: {e}"));
        }
        Ok(())
    }
}

#[cfg(not(target_os = "android"))]
#[derive(Debug, serde::Serialize)]
struct ProbeTrackResult {
    audio_frame_blake3_hex: String,
    header_blake3_hex: String,
    header_size: u64,
    footer_size: u64,
    file_size: u64,
    padding_bytes: u64,
    codec: String,
    sample_rate_hz: u32,
    bit_depth: Option<u32>,
    bitrate_kbps: Option<u32>,
    duration_ms: u64,
    title: Option<String>,
    album: Option<String>,
    album_artist: Option<String>,
    artists: Vec<String>,
    composers: Vec<String>,
    conductors: Vec<String>,
    work_name: Option<String>,
    movement_name: Option<String>,
    movement_number: Option<u32>,
    disc_number: u32,
    track_number: u32,
    publisher: Option<String>,
    genre: Option<String>,
    year: Option<u16>,
    original_date: Option<String>,
    release_type: Option<String>,
    replaygain_track_db: Option<f32>,
    replaygain_album_db: Option<f32>,
    replaygain_peak: Option<f32>,
    dj_scene: Vec<String>,
    dj_vocals: Option<String>,
    dj_energy: Option<String>,
    tempo_bpm: Option<f32>,
    tempo_bucket: Option<String>,
    moods: Vec<String>,
}

#[cfg(not(target_os = "android"))]
fn canonical_codec_name(file_type: Option<lofty::file::FileType>, ext_lower: &str) -> String {
    use lofty::file::FileType;
    match file_type {
        Some(FileType::Flac) => "flac".to_string(),
        Some(FileType::Mpeg) => "mp3".to_string(),
        Some(FileType::Vorbis) => "ogg".to_string(),
        Some(FileType::Opus) => "opus".to_string(),
        Some(FileType::Wav) => "wav".to_string(),
        Some(FileType::Aiff) => "aiff".to_string(),
        Some(FileType::Aac) => "aac".to_string(),
        Some(FileType::Ape) => "ape".to_string(),
        Some(FileType::WavPack) => "wv".to_string(),
        Some(FileType::Mp4) => {
            if matches!(ext_lower, "m4a" | "alac" | "aac") {
                ext_lower.to_string()
            } else {
                "mp4".to_string()
            }
        }
        Some(other) => {
            if !ext_lower.is_empty() {
                ext_lower.to_string()
            } else {
                format!("{other:?}").to_ascii_lowercase()
            }
        }
        None => ext_lower.to_string(),
    }
}

#[cfg(not(target_os = "android"))]
fn detect_flac_padding_bytes(path: &std::path::Path) -> Option<u64> {
    use std::io::{Read, Seek, SeekFrom};
    let mut f = File::open(path).ok()?;
    let marker_offset = find_flac_marker_after_id3v2(&mut f)?;
    f.seek(SeekFrom::Start(marker_offset + 4)).ok()?;
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
        if f.seek(SeekFrom::Current(len as i64)).is_err() {
            break;
        }
        if is_last {
            break;
        }
    }
    Some(total_padding)
}

#[cfg(not(target_os = "android"))]
fn parse_probe_gain_db(s: &str) -> Option<f32> {
    s.trim()
        .trim_end_matches("dB")
        .trim_end_matches("db")
        .trim()
        .parse::<f32>()
        .ok()
        .filter(|v| v.is_finite())
}

#[cfg(not(target_os = "android"))]
fn parse_probe_movement_number(raw: &str) -> Option<u32> {
    let trimmed = raw.trim();
    if let Some(roman) = tonewise_metadata::parse_roman_numeral(trimmed.trim_end_matches('.')) {
        return Some(roman);
    }
    let digits: String = trimmed
        .chars()
        .skip_while(|c| !c.is_ascii_digit())
        .take_while(|c| c.is_ascii_digit())
        .collect();
    if digits.is_empty() {
        return None;
    }
    digits.parse::<u32>().ok().filter(|&n| n > 0)
}

#[cfg(not(target_os = "android"))]
fn dedup_strings_ci(items: &mut Vec<String>) {
    let mut seen = std::collections::HashSet::new();
    items.retain(|s| seen.insert(s.to_ascii_lowercase()));
}

#[cfg(not(target_os = "android"))]
fn probe_track_path_impl(path_str: &str) -> String {
    use lofty::config::{ParseOptions, ParsingMode};
    use lofty::file::{AudioFile, TaggedFileExt};
    use lofty::probe::Probe;
    use lofty::tag::{Accessor, ItemKey};
    use tonewise_metadata::{
        DjEnergy, DjVocals, TempoBucket, bpm_to_tempo_bucket, is_classical_track,
        normalize_album_artist, normalize_dj_scenes, normalize_genre, normalize_year,
        split_artists_preserving_whitelist, split_composers_preserving_whitelist,
        split_multi_value, split_work_and_movement,
    };

    let path = std::path::Path::new(path_str);
    let fp = match File::open(path)
        .map_err(|e| format!("open failed: {e}"))
        .and_then(|f| {
            let mut reader = std::io::BufReader::new(f);
            compute_audio_frame_fingerprint(&mut reader)
                .map_err(|e| format!("fingerprint failed: {e}"))
        }) {
        Ok(fp) => fp,
        Err(e) => return json_error(&e),
    };
    if fp.file_size == 0 {
        return json_error("empty audio file");
    }

    let ext_lower = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_ascii_lowercase())
        .unwrap_or_default();
    let is_dsd = matches!(ext_lower.as_str(), "dsf" | "dff");

    let read_opts = |read_props: bool| {
        ParseOptions::new()
            .read_properties(read_props)
            .parsing_mode(ParsingMode::Relaxed)
    };
    let tagged = match Probe::open(path).and_then(|p| p.options(read_opts(true)).read()) {
        Ok(t) => Some(t),
        Err(_) => Probe::open(path)
            .and_then(|p| p.options(read_opts(false)).read())
            .ok(),
    };

    let (codec, sample_rate_hz, bit_depth, bitrate_kbps, duration_ms) = if let Some(ref t) = tagged
    {
        let props = t.properties();
        (
            canonical_codec_name(Some(t.file_type()), &ext_lower),
            props.sample_rate().unwrap_or(if is_dsd { 2_822_400 } else { 0 }),
            props
                .bit_depth()
                .map(|b| b as u32)
                .or_else(|| if is_dsd { Some(1) } else { None }),
            props.audio_bitrate().or_else(|| props.overall_bitrate()),
            props.duration().as_millis() as u64,
        )
    } else {
        (
            canonical_codec_name(None, &ext_lower),
            if is_dsd { 2_822_400 } else { 0 },
            if is_dsd { Some(1) } else { None },
            None,
            0,
        )
    };

    let primary_tag = tagged
        .as_ref()
        .and_then(|t| t.primary_tag().or_else(|| t.first_tag()));

    let title = primary_tag
        .and_then(|t| t.title().map(|s| s.trim().to_string()))
        .filter(|s| !s.is_empty());
    let album = primary_tag
        .and_then(|t| t.album().map(|s| s.trim().to_string()))
        .filter(|s| !s.is_empty());

    let raw_genres: Vec<&str> = primary_tag
        .map(|t| t.get_strings(&ItemKey::Genre).collect())
        .unwrap_or_default();
    let genre = if !raw_genres.is_empty() {
        let joined = raw_genres.join("; ");
        let norm = normalize_genre(&joined);
        if norm.is_empty() { None } else { Some(norm) }
    } else {
        primary_tag
            .and_then(|t| t.genre())
            .map(|s| normalize_genre(&s))
            .filter(|s| !s.is_empty())
    };

    let mut album_artist = None;
    let mut artists = Vec::new();
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
    let mut replaygain_track_db = None;
    let mut replaygain_album_db = None;
    let mut replaygain_peak = None;
    let mut dj_scene = Vec::new();
    let mut dj_vocals = None;
    let mut dj_energy = None;
    let mut tempo_bpm = None;
    let mut moods = Vec::new();

    if let Some(tag) = primary_tag {
        album_artist = tag
            .get_string(&ItemKey::AlbumArtist)
            .or_else(|| {
                tag.items().find_map(|it| match it.key() {
                    ItemKey::Unknown(k) if k.eq_ignore_ascii_case("ALBUM ARTIST") => {
                        it.value().text()
                    }
                    _ => None,
                })
            })
            .map(normalize_album_artist)
            .filter(|s| !s.is_empty());

        for item in tag.get_strings(&ItemKey::TrackArtists) {
            artists.extend(split_artists_preserving_whitelist(item));
        }
        if artists.is_empty()
            && let Some(a) = tag.artist()
        {
            artists = split_artists_preserving_whitelist(&a);
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
            .and_then(parse_probe_movement_number);

        original_date = tag
            .get_string(&ItemKey::OriginalReleaseDate)
            .or_else(|| tag.get_string(&ItemKey::RecordingDate))
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty());
        if year.is_none()
            && let Some(ref d) = original_date
        {
            year = normalize_year(d);
        }
        release_type = tag
            .get_string(&ItemKey::FlagCompilation)
            .filter(|v| v.trim() == "1")
            .map(|_| "compilation".to_string());

        replaygain_track_db = tag
            .get_string(&ItemKey::ReplayGainTrackGain)
            .and_then(parse_probe_gain_db);
        replaygain_album_db = tag
            .get_string(&ItemKey::ReplayGainAlbumGain)
            .and_then(parse_probe_gain_db);
        replaygain_peak = tag
            .get_string(&ItemKey::ReplayGainTrackPeak)
            .and_then(|s| s.trim().parse::<f32>().ok())
            .filter(|v| v.is_finite());

        tempo_bpm = tag
            .get_string(&ItemKey::Bpm)
            .or_else(|| tag.get_string(&ItemKey::IntegerBpm))
            .and_then(|s| s.trim().parse::<f32>().ok())
            .filter(|v| v.is_finite() && *v > 0.0);
        for m in tag.get_strings(&ItemKey::Mood) {
            moods.extend(split_multi_value(m));
        }

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
                "DJ_VOCALS" if dj_vocals.is_none() => {
                    dj_vocals = DjVocals::parse(val).map(|v| v.as_str().to_string());
                }
                "DJ_ENERGY" if dj_energy.is_none() => {
                    dj_energy = DjEnergy::parse(val).map(|v| v.as_str().to_string());
                }
                "TEMPO" if tempo_bpm.is_none() => {
                    if let Ok(b) = val.trim().parse::<f32>()
                        && b.is_finite()
                        && b > 0.0
                    {
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
                    movement_number = parse_probe_movement_number(val);
                }
                "COMPOSER" if composers.is_empty() => {
                    composers.extend(split_composers_preserving_whitelist(val));
                }
                "CONDUCTOR" if conductors.is_empty() => {
                    conductors.extend(split_artists_preserving_whitelist(val));
                }
                "REPLAYGAIN_ALBUM_GAIN" if replaygain_album_db.is_none() => {
                    replaygain_album_db = parse_probe_gain_db(val);
                }
                "REPLAYGAIN_TRACK_GAIN" if replaygain_track_db.is_none() => {
                    replaygain_track_db = parse_probe_gain_db(val);
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

    let tempo_bucket = tempo_bpm
        .and_then(bpm_to_tempo_bucket)
        .map(|b| match b {
            TempoBucket::VerySlow => "Very Slow",
            TempoBucket::Slow => "Slow",
            TempoBucket::Moderate => "Moderate",
            TempoBucket::Fast => "Fast",
            TempoBucket::VeryFast => "Very Fast",
        })
        .map(str::to_string);

    let title_for_classical = title.as_deref().unwrap_or("");
    let genre_str = genre.as_deref().unwrap_or("");
    let primary_composer = composers.first().map(String::as_str);
    let primary_conductor = conductors.first().map(String::as_str);
    if (!title_for_classical.is_empty() || work_name.is_some() || movement_name.is_some())
        && is_classical_track(
            genre_str,
            work_name.as_deref(),
            primary_composer,
            primary_conductor,
            title_for_classical,
        )
        && (work_name.is_none() || movement_name.is_none() || movement_number.is_none())
    {
        let parsed = split_work_and_movement(
            title_for_classical,
            work_name.as_deref(),
            movement_name.as_deref(),
            movement_number,
        );
        if work_name.is_none() && !parsed.work.is_empty() {
            work_name = Some(parsed.work);
        }
        if movement_name.is_none() && !parsed.movement.is_empty() {
            movement_name = Some(parsed.movement);
        }
        if movement_number.is_none() {
            movement_number = parsed.movement_number;
        }
    }

    dedup_strings_ci(&mut artists);
    dedup_strings_ci(&mut composers);
    dedup_strings_ci(&mut conductors);
    dedup_strings_ci(&mut dj_scene);
    dedup_strings_ci(&mut moods);

    let padding_bytes = detect_flac_padding_bytes(path).unwrap_or(0);
    let result = ProbeTrackResult {
        audio_frame_blake3_hex: fp.audio_frame_blake3_hex,
        header_blake3_hex: fp.header_blake3_hex,
        header_size: fp.header_size,
        footer_size: fp.footer_size,
        file_size: fp.file_size,
        padding_bytes,
        codec,
        sample_rate_hz,
        bit_depth,
        bitrate_kbps,
        duration_ms,
        title,
        album,
        album_artist,
        artists,
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
        replaygain_track_db,
        replaygain_album_db,
        replaygain_peak,
        dj_scene,
        dj_vocals,
        dj_energy,
        tempo_bpm,
        tempo_bucket,
        moods,
    };

    serde_json::to_string(&result).unwrap_or_else(|e| json_error(&e.to_string()))
}

#[cfg(target_os = "android")]
fn probe_track_path_impl(_path_str: &str) -> String {
    json_error("probe_track_path is not enabled in Android slim build")
}

#[cfg(not(target_os = "android"))]
fn write_audio_tags_json_impl(request_json: &str) -> String {
    use lofty::config::{ParseOptions, ParsingMode};
    use lofty::file::TaggedFileExt;
    use lofty::probe::Probe;
    use lofty::tag::{Accessor, ItemKey, ItemValue, Tag, TagItem};

    let req: TagWriteRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid tag write request json: {e}")),
    };

    let path = std::path::Path::new(&req.path);
    let mut tagged = match Probe::open(path).and_then(|p| {
        p.options(
            ParseOptions::new()
                .read_properties(false)
                .parsing_mode(ParsingMode::Relaxed),
        )
        .read()
    }) {
        Ok(t) => t,
        Err(e) => return json_error(&format!("lofty read failed: {e}")),
    };

    let touched_work = req.updates.keys().any(|k| {
        let f = k.trim().to_ascii_lowercase();
        f == "work" || f == "work_name"
    });
    let strip_id3v2_txxx_work =
        touched_work && tagged.file_type() == lofty::file::FileType::Mpeg;

    let tag_type = tagged.primary_tag_type();
    let flac_id3v2_backup = if tagged.file_type() == lofty::file::FileType::Flac {
        tagged.tag(lofty::tag::TagType::Id3v2).cloned()
    } else {
        None
    };
    if tagged.primary_tag_mut().is_none() {
        tagged.insert_tag(Tag::new(tag_type));
    }
    if let (Some(id3), Some(primary)) = (flac_id3v2_backup.as_ref(), tagged.primary_tag_mut()) {
        backfill_flac_vorbis_from_id3v2(primary, id3);
    }

    if let Some(tag) = tagged.primary_tag_mut() {
        for (raw_field, raw_opt) in &req.updates {
            let field = raw_field.trim().to_ascii_lowercase();
            let clean_val = raw_opt
                .as_deref()
                .map(str::trim)
                .filter(|v| !v.is_empty() && *v != "[]");

            match field.as_str() {
                "title" => match clean_val {
                    Some(v) => tag.set_title(v.to_string()),
                    None => tag.remove_title(),
                },
                "artist" | "artists" | "artists_json" => {
                    tag.remove_key(&ItemKey::TrackArtists);
                    let display = clean_val.map(|v| {
                        if v.starts_with('[')
                            && let Ok(arr) = serde_json::from_str::<Vec<String>>(v)
                        {
                            arr.into_iter()
                                .filter(|s| !s.trim().is_empty())
                                .collect::<Vec<_>>()
                                .join(" / ")
                        } else {
                            v.to_string()
                        }
                    });
                    match display.as_deref().filter(|s| !s.trim().is_empty()) {
                        Some(v) => tag.set_artist(v.to_string()),
                        None => tag.remove_artist(),
                    }
                }
                "album_artist" | "albumartist" => {
                    tag.remove_key(&ItemKey::AlbumArtist);
                    tag.retain(|it| {
                        !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("ALBUM ARTIST"))
                    });
                    if let Some(v) = clean_val {
                        tag.insert_text(ItemKey::AlbumArtist, v.to_string());
                    }
                }
                "composer" | "composers" | "composers_json" => {
                    tag.remove_key(&ItemKey::Composer);
                    if let Some(v) = clean_val {
                        for c in parse_tag_list_value(v) {
                            tag.push(TagItem::new(ItemKey::Composer, ItemValue::Text(c)));
                        }
                    }
                }
                "conductor" | "conductors" | "conductors_json" => {
                    tag.remove_key(&ItemKey::Conductor);
                    if let Some(v) = clean_val {
                        for c in parse_tag_list_value(v) {
                            tag.push(TagItem::new(ItemKey::Conductor, ItemValue::Text(c)));
                        }
                    }
                }
                "album" => match clean_val {
                    Some(v) => tag.set_album(v.to_string()),
                    None => tag.remove_album(),
                },
                "genre" => match clean_val {
                    Some(v) => tag.set_genre(v.to_string()),
                    None => tag.remove_genre(),
                },
                "year" => match clean_val.and_then(|v| v.parse::<u32>().ok()) {
                    Some(y) => {
                        tag.set_year(y);
                        tag.insert_text(ItemKey::RecordingDate, y.to_string());
                    }
                    None => {
                        tag.remove_year();
                        tag.remove_key(&ItemKey::RecordingDate);
                    }
                },
                "publisher" => match clean_val {
                    Some(v) => {
                        tag.insert_text(ItemKey::Publisher, v.to_string());
                    }
                    None => {
                        tag.remove_key(&ItemKey::Publisher);
                        tag.remove_key(&ItemKey::Label);
                    }
                },
                "work" | "work_name" => {
                    if matches!(tag_type, lofty::tag::TagType::Id3v2) {
                        // Clear any stale TXXX:WORK (ItemKey::Work / Unknown("WORK")) so rescans never read outdated work values over TIT1.
                        tag.remove_key(&ItemKey::Work);
                        tag.retain(|it| {
                            !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("WORK"))
                        });
                        match clean_val {
                            Some(v) => {
                                tag.insert_text(ItemKey::ContentGroup, v.to_string());
                            }
                            None => {
                                tag.remove_key(&ItemKey::ContentGroup);
                            }
                        }
                    } else {
                        match clean_val {
                            Some(v) => {
                                tag.insert_text(ItemKey::Work, v.to_string());
                            }
                            None => {
                                tag.remove_key(&ItemKey::Work);
                                tag.remove_key(&ItemKey::ContentGroup);
                            }
                        }
                    }
                }
                "movement" | "movement_name" => match clean_val {
                    Some(v) => {
                        tag.insert_text(ItemKey::Movement, v.to_string());
                    }
                    None => {
                        tag.remove_key(&ItemKey::Movement);
                    }
                },
                "tempo_bpm" => match clean_val.and_then(|v| v.parse::<f32>().ok()) {
                    Some(bpm) if bpm.is_finite() && bpm > 0.0 => {
                        tag.insert_text(ItemKey::Bpm, format!("{bpm:.1}"));
                        tag.insert_text(ItemKey::IntegerBpm, format!("{}", bpm.round() as u32));
                    }
                    _ => {
                        tag.remove_key(&ItemKey::Bpm);
                        tag.remove_key(&ItemKey::IntegerBpm);
                    }
                },
                "mood" | "mood_json" => {
                    tag.remove_key(&ItemKey::Mood);
                    if let Some(v) = clean_val {
                        for m in parse_tag_list_value(v) {
                            tag.push(TagItem::new(ItemKey::Mood, ItemValue::Text(m)));
                        }
                    }
                }
                "dj_scene" | "dj_scene_json" => {
                    tag.retain(|it| !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("DJ_SCENE")));
                    if let Some(v) = clean_val {
                        let items = parse_tag_list_value(v);
                        if !items.is_empty() {
                            tag.push_unchecked(TagItem::new(
                                ItemKey::Unknown("DJ_SCENE".to_string()),
                                ItemValue::Text(items.join("; ")),
                            ));
                        }
                    }
                }
                "dj_vocals" => {
                    tag.retain(|it| !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("DJ_VOCALS")));
                    if let Some(v) = clean_val {
                        tag.push_unchecked(TagItem::new(
                            ItemKey::Unknown("DJ_VOCALS".to_string()),
                            ItemValue::Text(v.to_string()),
                        ));
                    }
                }
                "dj_energy" => {
                    tag.retain(|it| !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("DJ_ENERGY")));
                    if let Some(v) = clean_val {
                        tag.push_unchecked(TagItem::new(
                            ItemKey::Unknown("DJ_ENERGY".to_string()),
                            ItemValue::Text(v.to_string()),
                        ));
                    }
                }
                _ => {}
            }
        }
    }

    if let Err(e) = save_tagged_file_atomically_ex(path, &tagged, strip_id3v2_txxx_work) {
        return json_error(&e);
    }

    fingerprint_path_impl(&req.path)
}

#[cfg(target_os = "android")]
fn write_audio_tags_json_impl(_request_json: &str) -> String {
    json_error("write_audio_tags is not enabled in Android slim build")
}

// -----------------------------------------------------------------------------
// C-FFI exports for Windows Desktop (.NET P/Invoke via tonewise_core.dll)
// -----------------------------------------------------------------------------

fn into_c_string(s: String) -> *mut c_char {
    CString::new(s)
        .unwrap_or_else(|_| CString::new("{\"error\":\"interior nul byte\"}").expect("valid json"))
        .into_raw()
}

fn ffi_catch<F: FnOnce() -> String>(f: F) -> *mut c_char {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(out) => into_c_string(out),
        Err(payload) => {
            let msg = if let Some(s) = payload.downcast_ref::<&str>() {
                (*s).to_string()
            } else if let Some(s) = payload.downcast_ref::<String>() {
                s.clone()
            } else {
                "unknown panic in tonewise_core".to_string()
            };
            into_c_string(json_error(&format!("native panic caught: {msg}")))
        }
    }
}

unsafe fn read_c_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().ok()
}

#[unsafe(no_mangle)]
pub extern "C" fn tonewise_core_abi_version() -> u32 {
    TONEWISE_CORE_ABI_VERSION
}

/// Frees a UTF-8 string previously returned by `tonewise_*` C-FFI functions.
///
/// # Safety
/// `ptr` must be null or a pointer previously allocated by this library and not yet freed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        drop(unsafe { CString::from_raw(ptr) });
    }
}

/// # Safety
/// `raw_utf8` must be a valid NUL-terminated UTF-8 string or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_split_artists_json(raw_utf8: *const c_char) -> *mut c_char {
    let Some(raw) = (unsafe { read_c_str(raw_utf8) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| split_artists_json_impl(raw))
}

/// # Safety
/// `scene_json` and `tracks_json` must be valid NUL-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_evaluate_scene_json(
    scene_json: *const c_char,
    tracks_json: *const c_char,
) -> *mut c_char {
    let (Some(scene), Some(tracks)) =
        (unsafe { read_c_str(scene_json) }, unsafe { read_c_str(tracks_json) })
    else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| evaluate_scene_json_impl(scene, tracks))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_merge_m3u8_json(request_json: *const c_char) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| merge_m3u8_json_impl(req))
}

/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_audio_fingerprint_path(path_utf8: *const c_char) -> *mut c_char {
    let Some(path) = (unsafe { read_c_str(path_utf8) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| fingerprint_path_impl(path))
}

/// # Safety
/// `archive_path_utf8` and `blake3_hex_utf8` must be valid NUL-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_lookup_waveform_json(
    archive_path_utf8: *const c_char,
    blake3_hex_utf8: *const c_char,
) -> *mut c_char {
    let (Some(archive_path), Some(hex)) = (
        unsafe { read_c_str(archive_path_utf8) },
        unsafe { read_c_str(blake3_hex_utf8) },
    ) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| lookup_waveform_path_impl(archive_path, hex))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_apply_lww_mutations_json(
    request_json: *const c_char,
) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| apply_lww_mutations_json_impl(req))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_merge_sessions_json(request_json: *const c_char) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| merge_sessions_json_impl(req))
}

/// # Safety
/// `path_utf8`, `expected_old_hex_utf8`, and `new_header_ptr` (length `new_header_len`) must be valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_apply_header_patch_path(
    path_utf8: *const c_char,
    expected_old_hex_utf8: *const c_char,
    new_header_ptr: *const u8,
    new_header_len: usize,
) -> *mut c_char {
    let (Some(path), Some(old_hex)) = (
        unsafe { read_c_str(path_utf8) },
        unsafe { read_c_str(expected_old_hex_utf8) },
    ) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    if new_header_ptr.is_null() && new_header_len > 0 {
        return into_c_string(json_error("null header buffer"));
    }
    let bytes = if new_header_len == 0 {
        &[][..]
    } else {
        unsafe { std::slice::from_raw_parts(new_header_ptr, new_header_len) }
    };
    ffi_catch(|| apply_header_patch_path_impl(path, old_hex, bytes))
}

/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_analyze_audio_path(path_utf8: *const c_char) -> *mut c_char {
    let Some(path) = (unsafe { read_c_str(path_utf8) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| analyze_audio_path_impl(path))
}

/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_probe_track_path(path_utf8: *const c_char) -> *mut c_char {
    let Some(path) = (unsafe { read_c_str(path_utf8) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| probe_track_path_impl(path))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_write_audio_tags_json(request_json: *const c_char) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| write_audio_tags_json_impl(req))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_resolve_auto_eq_json(
    request_json: *const c_char,
) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| resolve_auto_eq_json_impl(req))
}

/// # Safety
/// `request_json` must be a valid NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tonewise_compose_dsp_json(request_json: *const c_char) -> *mut c_char {
    let Some(req) = (unsafe { read_c_str(request_json) }) else {
        return into_c_string(json_error("null or invalid utf-8 input"));
    };
    ffi_catch(|| compose_dsp_json_impl(req))
}

#[unsafe(no_mangle)]
pub extern "C" fn tonewise_default_auto_eq_catalog_json() -> *mut c_char {
    ffi_catch(default_auto_eq_catalog_json_impl)
}

// -----------------------------------------------------------------------------
// JNI exports for Android (:musikr -> org.oxycblt.musikr.core.TonewiseCoreJNI)
// -----------------------------------------------------------------------------

fn format_panic_payload(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "unknown panic in tonewise_core".to_string()
    }
}

fn jni_catch<F: FnOnce() -> String>(f: F) -> String {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(out) => out,
        Err(payload) => json_error(&format!("native panic caught: {}", format_panic_payload(payload))),
    }
}

fn clear_pending_jni_exception(env: &mut JNIEnv) -> bool {
    if env.exception_check().unwrap_or(false) {
        let _ = env.exception_clear();
        true
    } else {
        false
    }
}

fn jstring_to_rust(env: &mut JNIEnv, input: &JString) -> Result<String, String> {
    if clear_pending_jni_exception(env) {
        return Err("jni pending exception cleared".to_string());
    }
    if input.is_null() {
        return Err("null jstring".to_string());
    }
    match env.get_string(input) {
        Ok(s) => Ok(s.into()),
        Err(e) => {
            clear_pending_jni_exception(env);
            Err(format!("jni get_string failed: {e}"))
        }
    }
}

fn jbytes_to_rust(env: &mut JNIEnv, input: &JByteArray) -> Result<Vec<u8>, String> {
    if clear_pending_jni_exception(env) {
        return Err("jni pending exception cleared".to_string());
    }
    if input.is_null() {
        return Err("null jbyteArray".to_string());
    }
    match env.convert_byte_array(input) {
        Ok(b) => Ok(b),
        Err(e) => {
            clear_pending_jni_exception(env);
            Err(format!("convert byte array failed: {e}"))
        }
    }
}

fn rust_to_jstring(env: &mut JNIEnv, value: &str) -> jstring {
    clear_pending_jni_exception(env);
    match env.new_string(value) {
        Ok(s) => s.into_raw(),
        Err(_) => {
            clear_pending_jni_exception(env);
            env.new_string("{\"error\":\"jni new_string failed\"}")
                .map(|s| s.into_raw())
                .unwrap_or(std::ptr::null_mut())
        }
    }
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeAbiVersion(
    _env: JNIEnv,
    _cls: JClass,
) -> jint {
    TONEWISE_CORE_ABI_VERSION as jint
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeSplitArtistsJson<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    raw: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &raw) {
        Ok(s) => jni_catch(|| split_artists_json_impl(&s)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeEvaluateSceneJson<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    scene_json: JString<'local>,
    tracks_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &scene_json) {
        Ok(s) => match jstring_to_rust(&mut env, &tracks_json) {
            Ok(t) => jni_catch(|| evaluate_scene_json_impl(&s, &t)),
            Err(e) => json_error(&e),
        },
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeMergeM3u8Json<'local>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    request_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &request_json) {
        Ok(r) => jni_catch(|| merge_m3u8_json_impl(&r)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeAudioFingerprintFd(
    mut env: JNIEnv,
    _cls: JClass,
    fd: jint,
) -> jstring {
    let out = jni_catch(|| fingerprint_fd_impl(fd));
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeAudioFingerprintPath<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    path: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &path) {
        Ok(p) => jni_catch(|| fingerprint_path_impl(&p)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeLookupWaveformPath<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    archive_path: JString<'local>,
    blake3_hex: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &archive_path) {
        Ok(p) => match jstring_to_rust(&mut env, &blake3_hex) {
            Ok(h) => jni_catch(|| lookup_waveform_path_impl(&p, &h)),
            Err(e) => json_error(&e),
        },
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeLookupWaveformFd<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    fd: jint,
    blake3_hex: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &blake3_hex) {
        Ok(h) => jni_catch(|| lookup_waveform_fd_impl(fd, &h)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeApplyLwwMutationsJson<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    request_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &request_json) {
        Ok(r) => jni_catch(|| apply_lww_mutations_json_impl(&r)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeMergeSessionsJson<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    request_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &request_json) {
        Ok(r) => jni_catch(|| merge_sessions_json_impl(&r)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeApplyHeaderPatchFd<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    fd: jint,
    expected_old_hex: JString<'local>,
    new_header: JByteArray<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &expected_old_hex) {
        Ok(hex) => match jbytes_to_rust(&mut env, &new_header) {
            Ok(bytes) => jni_catch(|| apply_header_patch_fd_impl(fd, &hex, &bytes)),
            Err(e) => json_error(&e),
        },
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeApplyHeaderPatchPath<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    path: JString<'local>,
    expected_old_hex: JString<'local>,
    new_header: JByteArray<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &path) {
        Ok(p) => match jstring_to_rust(&mut env, &expected_old_hex) {
            Ok(hex) => match jbytes_to_rust(&mut env, &new_header) {
                Ok(bytes) => jni_catch(|| apply_header_patch_path_impl(&p, &hex, &bytes)),
                Err(e) => json_error(&e),
            },
            Err(e) => json_error(&e),
        },
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeResolveAutoEqJson<
    'local,
>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    request_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &request_json) {
        Ok(r) => jni_catch(|| resolve_auto_eq_json_impl(&r)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeComposeDspJson<'local>(
    mut env: JNIEnv<'local>,
    _cls: JClass<'local>,
    request_json: JString<'local>,
) -> jstring {
    let out = match jstring_to_rust(&mut env, &request_json) {
        Ok(r) => jni_catch(|| compose_dsp_json_impl(&r)),
        Err(e) => json_error(&e),
    };
    rust_to_jstring(&mut env, &out)
}

#[unsafe(no_mangle)]
pub extern "system" fn Java_org_oxycblt_musikr_core_TonewiseCoreJNI_nativeDefaultAutoEqCatalogJson(
    mut env: JNIEnv,
    _cls: JClass,
) -> jstring {
    let out = jni_catch(default_auto_eq_catalog_json_impl);
    rust_to_jstring(&mut env, &out)
}

pub fn workspace_banner() -> String {
    format!(
        "tonewise-core abi=v{} mood_clusters={} protected_artists={}",
        TONEWISE_CORE_ABI_VERSION,
        tonewise_rules::MoodDictionary::global().cluster_count(),
        tonewise_metadata::artist_whitelist().len(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ffi_abi_and_c_exports_roundtrip() {
        assert_eq!(tonewise_core_abi_version(), 1);
        assert!(workspace_banner().contains("mood_clusters=5"));

        let input = CString::new("Above & Beyond / Andrew Bayer").unwrap();
        let out_ptr = unsafe { tonewise_split_artists_json(input.as_ptr()) };
        let out_str = unsafe { CStr::from_ptr(out_ptr) }.to_str().unwrap().to_string();
        unsafe { tonewise_free_string(out_ptr) };

        let parsed: Vec<String> = serde_json::from_str(&out_str).unwrap();
        assert_eq!(parsed, vec!["Above & Beyond", "Andrew Bayer"]);
    }

    #[test]
    fn ffi_auto_eq_and_compose_dsp_roundtrip() {
        let req = serde_json::json!({
            "policy": { "enabled": true, "classical_auto_crossfeed": true, "lock_within_classical_work": true },
            "track": { "genres": ["Classical"], "dj_vocals": "instrumental", "work_name": "Symphony No. 5" }
        })
        .to_string();
        let req_c = CString::new(req).unwrap();
        let out_ptr = unsafe { tonewise_resolve_auto_eq_json(req_c.as_ptr()) };
        let out_str = unsafe { CStr::from_ptr(out_ptr) }.to_str().unwrap().to_string();
        unsafe { tonewise_free_string(out_ptr) };
        let resolved: ResolvedAutoEq = serde_json::from_str(&out_str).unwrap();
        assert!(resolved.applied);
        assert!(resolved.enable_crossfeed);
        assert!(!resolved.has_non_zero_eq);

        let cat_ptr = tonewise_default_auto_eq_catalog_json();
        let cat_str = unsafe { CStr::from_ptr(cat_ptr) }.to_str().unwrap().to_string();
        unsafe { tonewise_free_string(cat_ptr) };
        let catalog: serde_json::Value = serde_json::from_str(&cat_str).unwrap();
        assert_eq!(catalog.as_array().unwrap().len(), 24);
    }

    #[test]
    fn ffi_catch_contains_panics_as_json_error() {
        let out_ptr = ffi_catch(|| panic!("synthetic boom"));
        let out_str = unsafe { CStr::from_ptr(out_ptr) }.to_str().unwrap().to_string();
        unsafe { tonewise_free_string(out_ptr) };
        let val: serde_json::Value = serde_json::from_str(&out_str).unwrap();
        assert!(val["error"].as_str().unwrap().contains("synthetic boom"));
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_preserves_bracket_artist_and_removes_track_artists_atomically() {
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::{Accessor, ItemKey};

        let dir = std::env::temp_dir().join(format!("tw_ffi_tags_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let wav_path = dir.join("test_bracket.wav");

        // Minimal 1-second 8kHz mono 8-bit PCM WAV
        let sample_count = 8000u32;
        let data_len = sample_count;
        let mut wav = Vec::with_capacity(44 + data_len as usize);
        wav.extend_from_slice(b"RIFF");
        wav.extend_from_slice(&(36 + data_len).to_le_bytes());
        wav.extend_from_slice(b"WAVEfmt ");
        wav.extend_from_slice(&16u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&8000u32.to_le_bytes());
        wav.extend_from_slice(&8000u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&8u16.to_le_bytes());
        wav.extend_from_slice(b"data");
        wav.extend_from_slice(&data_len.to_le_bytes());
        wav.resize(44 + data_len as usize, 128u8);
        std::fs::write(&wav_path, &wav).unwrap();

        let req = serde_json::json!({
            "path": wav_path.to_string_lossy(),
            "updates": {
                "artist": "[Live] AC/DC",
                "title": "Test Track"
            }
        })
        .to_string();
        let res_json = write_audio_tags_json_impl(&req);
        let parsed: serde_json::Value = serde_json::from_str(&res_json).unwrap();
        assert!(parsed.get("error").is_none(), "unexpected error: {res_json}");

        let reloaded = Probe::open(&wav_path).unwrap().read().unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(tag.artist().as_deref(), Some("[Live] AC/DC"));
        assert!(tag.get_strings(&ItemKey::TrackArtists).next().is_none());
        let leftovers: Vec<_> = std::fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".twtmp."))
            .collect();
        assert!(leftovers.is_empty());

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn apply_header_patch_file_impl_preserves_original_on_mismatch_and_updates_on_match() {
        let dir = std::env::temp_dir().join(format!("tw_ffi_patch_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let flac_path = dir.join("patch_test.flac");

        let mut flac = Vec::new();
        flac.extend_from_slice(b"fLaC");
        // STREAMINFO (34 bytes), last=false
        flac.push(0x00);
        flac.extend_from_slice(&[0, 0, 34]);
        flac.extend_from_slice(&[0u8; 34]);
        // PADDING (16 bytes), last=true
        flac.push(0x81);
        flac.extend_from_slice(&[0, 0, 16]);
        flac.extend_from_slice(&[0u8; 16]);
        let audio = b"AUDIO_FRAMES_12345";
        flac.extend_from_slice(audio);
        std::fs::write(&flac_path, &flac).unwrap();

        let fp_json = fingerprint_path_impl(&flac_path.to_string_lossy());
        let fp_val: serde_json::Value = serde_json::from_str(&fp_json).unwrap();
        let old_hdr_hex = fp_val["header_blake3_hex"].as_str().unwrap().to_string();
        let hdr_size = fp_val["header_size"].as_u64().unwrap() as usize;

        let mut new_hdr = flac[..hdr_size].to_vec();
        new_hdr[hdr_size - 1] = 0xAA;

        // Wrong expected header hash must leave file untouched
        let mismatch_json = apply_header_patch_path_impl(
            &flac_path.to_string_lossy(),
            &"00".repeat(32),
            &new_hdr,
        );
        let mismatch_val: serde_json::Value = serde_json::from_str(&mismatch_json).unwrap();
        assert_eq!(mismatch_val["status"].as_str(), Some("header_hash_mismatch"));
        assert_eq!(std::fs::read(&flac_path).unwrap(), flac);

        // Matching header hash must update header cleanly and preserve audio frames
        let ok_json = apply_header_patch_path_impl(
            &flac_path.to_string_lossy(),
            &old_hdr_hex,
            &new_hdr,
        );
        let ok_val: serde_json::Value = serde_json::from_str(&ok_json).unwrap();
        assert_eq!(ok_val["status"].as_str(), Some("applied"));
        let updated = std::fs::read(&flac_path).unwrap();
        assert_eq!(&updated[..hdr_size], &new_hdr[..]);
        assert_eq!(&updated[hdr_size..], audio);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_writes_classical_work_and_movement_preserving_audio_blake3() {
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::ItemKey;

        let dir = std::env::temp_dir().join(format!("tw_ffi_classical_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let wav_path = dir.join("classical.wav");

        let data_len = 8000u32;
        let mut wav = Vec::with_capacity(44 + data_len as usize);
        wav.extend_from_slice(b"RIFF");
        wav.extend_from_slice(&(36 + data_len).to_le_bytes());
        wav.extend_from_slice(b"WAVEfmt ");
        wav.extend_from_slice(&16u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&8000u32.to_le_bytes());
        wav.extend_from_slice(&8000u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&8u16.to_le_bytes());
        wav.extend_from_slice(b"data");
        wav.extend_from_slice(&data_len.to_le_bytes());
        wav.resize(44 + data_len as usize, 128u8);
        std::fs::write(&wav_path, &wav).unwrap();

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&wav_path.to_string_lossy())).unwrap();

        let req = serde_json::json!({
            "path": wav_path.to_string_lossy(),
            "updates": {
                "work_name": "Symphony No. 5 in C minor, Op. 67",
                "movement_name": "I. Allegro con brio"
            }
        })
        .to_string();
        let after: serde_json::Value = serde_json::from_str(&write_audio_tags_json_impl(&req)).unwrap();
        assert!(after.get("error").is_none(), "unexpected error: {after}");
        assert_eq!(after["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&wav_path).unwrap().read().unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(
            tag.get_string(&ItemKey::ContentGroup)
                .or_else(|| tag.get_string(&ItemKey::Work)),
            Some("Symphony No. 5 in C minor, Op. 67")
        );
        assert_eq!(tag.get_string(&ItemKey::Movement), Some("I. Allegro con brio"));

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_updates_padded_flac_without_corrupting_audio_frames() {
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::Accessor;

        let dir = std::env::temp_dir().join(format!("tw_ffi_flac_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let flac_path = dir.join("padded.flac");

        let mut flac = Vec::new();
        flac.extend_from_slice(b"fLaC");
        // STREAMINFO (34 bytes), last=false
        flac.push(0x00);
        flac.extend_from_slice(&[0, 0, 34]);
        let mut streaminfo = [0u8; 34];
        // Sample rate 44100 Hz (0xAC44), 2 ch, 16 bps
        streaminfo[10] = 0x0A;
        streaminfo[11] = 0xC4;
        streaminfo[12] = 0x42;
        streaminfo[13] = 0xF0;
        flac.extend_from_slice(&streaminfo);
        // PADDING (128 bytes), last=true
        flac.push(0x81);
        flac.extend_from_slice(&[0, 0, 128]);
        flac.extend_from_slice(&[0u8; 128]);
        let audio_payload = vec![0xAAu8; 4096];
        flac.extend_from_slice(&audio_payload);
        std::fs::write(&flac_path, &flac).unwrap();

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&flac_path.to_string_lossy())).unwrap();

        let req = serde_json::json!({
            "path": flac_path.to_string_lossy(),
            "updates": {
                "title": "晴天",
                "artist": "周杰伦"
            }
        })
        .to_string();
        let after: serde_json::Value = serde_json::from_str(&write_audio_tags_json_impl(&req)).unwrap();
        assert!(after.get("error").is_none(), "unexpected error: {after}");
        assert_eq!(after["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&flac_path).unwrap().read().unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(tag.title().as_deref(), Some("晴天"));
        assert_eq!(tag.artist().as_deref(), Some("周杰伦"));

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_updates_and_clears_album_artist_on_flac() {
        use lofty::config::WriteOptions;
        use lofty::file::{AudioFile, TaggedFileExt};
        use lofty::probe::Probe;
        use lofty::tag::{ItemKey, ItemValue, Tag, TagItem};

        let dir = std::env::temp_dir().join(format!("tw_ffi_aa_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let flac_path = dir.join("album_artist.flac");

        let mut flac = Vec::new();
        flac.extend_from_slice(b"fLaC");
        flac.push(0x00);
        flac.extend_from_slice(&[0, 0, 34]);
        let mut streaminfo = [0u8; 34];
        streaminfo[10] = 0x0A;
        streaminfo[11] = 0xC4;
        streaminfo[12] = 0x42;
        streaminfo[13] = 0xF0;
        flac.extend_from_slice(&streaminfo);
        flac.push(0x81);
        flac.extend_from_slice(&[0, 0, 128]);
        flac.extend_from_slice(&[0u8; 128]);
        flac.extend_from_slice(&vec![0xAAu8; 4096]);
        std::fs::write(&flac_path, &flac).unwrap();

        let mut initial = Probe::open(&flac_path).unwrap().read().unwrap();
        let tag_type = initial.primary_tag_type();
        let mut tag = Tag::new(tag_type);
        tag.insert_text(ItemKey::AlbumArtist, "周杰倫".to_string());
        tag.push_unchecked(TagItem::new(
            ItemKey::Unknown("ALBUM ARTIST".to_string()),
            ItemValue::Text("周杰倫".to_string()),
        ));
        initial.insert_tag(tag);
        initial.save_to_path(&flac_path, WriteOptions::default()).unwrap();

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&flac_path.to_string_lossy())).unwrap();

        let req_update = serde_json::json!({
            "path": flac_path.to_string_lossy(),
            "updates": {
                "album_artist": "周杰伦"
            }
        })
        .to_string();
        let after_update: serde_json::Value =
            serde_json::from_str(&write_audio_tags_json_impl(&req_update)).unwrap();
        assert!(after_update.get("error").is_none(), "unexpected error: {after_update}");
        assert_eq!(after_update["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&flac_path).unwrap().read().unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(tag.get_string(&ItemKey::AlbumArtist), Some("周杰伦"));
        assert!(!tag.items().any(|it| matches!(
            it.key(),
            ItemKey::Unknown(k) if k.eq_ignore_ascii_case("ALBUM ARTIST")
        )));

        let req_clear = serde_json::json!({
            "path": flac_path.to_string_lossy(),
            "updates": {
                "album_artist": null
            }
        })
        .to_string();
        let after_clear: serde_json::Value =
            serde_json::from_str(&write_audio_tags_json_impl(&req_clear)).unwrap();
        assert!(after_clear.get("error").is_none(), "unexpected error: {after_clear}");
        assert_eq!(after_clear["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded_clear = Probe::open(&flac_path).unwrap().read().unwrap();
        let tag_clear = reloaded_clear.primary_tag().unwrap();
        assert_eq!(tag_clear.get_string(&ItemKey::AlbumArtist), None);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_updates_mp3_with_latin1_id3v1_byte30_boundary_without_panic() {
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::{Accessor, ItemKey, TagType};

        let dir = std::env::temp_dir().join(format!("tw_ffi_mp3_v1_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let mp3_path = dir.join("traeumerei.mp3");

        let mut mp3 = Vec::new();
        // Minimal empty ID3v2.3 header (10 bytes, zero payload)
        mp3.extend_from_slice(b"ID3\x03\x00\x00\x00\x00\x00\x00");
        // 8 MPEG-1 Layer III frames (128kbps, 44100Hz, stereo => 417 bytes/frame)
        let mut frame = vec![0u8; 417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        for _ in 0..8 {
            mp3.extend_from_slice(&frame);
        }
        // 128-byte ID3v1 footer whose 30-byte Latin-1 title ends in 0xE4 ('ä'), expanding
        // to 31 UTF-8 bytes so byte index 30 falls inside 'ä' (bytes 29..31).
        let mut id3v1 = Vec::with_capacity(128);
        id3v1.extend_from_slice(b"TAG");
        let title_v1 = b"Kinderszenen, Op. 15: VII. Tr\xe4";
        assert_eq!(title_v1.len(), 30);
        id3v1.extend_from_slice(title_v1);
        let mut artist_v1 = [0u8; 30];
        artist_v1[..15].copy_from_slice(b"Robert Schumann");
        id3v1.extend_from_slice(&artist_v1);
        let album_v1 = b"Kinderszenen / Fantasie op. 17";
        assert_eq!(album_v1.len(), 30);
        id3v1.extend_from_slice(album_v1);
        id3v1.extend_from_slice(b"1990");
        id3v1.extend_from_slice(&[0u8; 28]);
        id3v1.push(0); // comment terminator (ID3v1.1)
        id3v1.push(7); // track
        id3v1.push(32); // Classical
        assert_eq!(id3v1.len(), 128);
        mp3.extend_from_slice(&id3v1);
        std::fs::write(&mp3_path, &mp3).unwrap();

        let initial_probe = Probe::open(&mp3_path).unwrap().read().unwrap();
        assert!(
            initial_probe.tag(TagType::Id3v1).is_some(),
            "test fixture must expose legacy ID3v1 tag to lofty"
        );

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&mp3_path.to_string_lossy())).unwrap();

        let req = serde_json::json!({
            "path": mp3_path.to_string_lossy(),
            "updates": {
                "title": "Kinderszenen, Op. 15: VII. Träumerei",
                "artist": "Vladimir Horowitz",
                "album_artist": "Robert Schumann; Vladimir Horowitz",
                "genre": "Classical"
            }
        })
        .to_string();

        // Execute through write_audio_tags_json_impl (and catch_unwind via std::panic) to verify zero panic
        let raw_resp = std::panic::catch_unwind(|| write_audio_tags_json_impl(&req))
            .expect("must not trigger lofty ID3v1 byte-30 UTF-8 slice panic");
        let after: serde_json::Value = serde_json::from_str(&raw_resp).unwrap();
        assert!(after.get("error").is_none(), "unexpected error: {after}");
        assert_eq!(after["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&mp3_path).unwrap().read().unwrap();
        let primary = reloaded.primary_tag().unwrap();
        assert_eq!(primary.title().as_deref(), Some("Kinderszenen, Op. 15: VII. Träumerei"));
        assert_eq!(primary.artist().as_deref(), Some("Vladimir Horowitz"));
        assert_eq!(
            primary.get_string(&ItemKey::AlbumArtist),
            Some("Robert Schumann; Vladimir Horowitz")
        );
        assert_eq!(primary.genre().as_deref(), Some("Classical"));

        let final_bytes = std::fs::read(&mp3_path).unwrap();
        assert_eq!(&final_bytes[final_bytes.len() - 128..], id3v1.as_slice());

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn save_tagged_file_atomically_preserves_original_when_writeback_fails() {
        use lofty::tag::{Accessor, Tag, TagType};
        let dir = std::env::temp_dir().join(format!("tonewise_atomic_guard_{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        let corrupt_flac = dir.join("corrupt.flac");
        let original = b"NOT_A_VALID_FLAC_STREAM_SENTINEL_BYTES_123456";
        std::fs::write(&corrupt_flac, original).unwrap();

        let mut bad_tag = Tag::new(TagType::VorbisComments);
        bad_tag.set_title("Atomic Guard Test".to_string());
        let tagged = lofty::file::TaggedFile::new(
            lofty::file::FileType::Flac,
            lofty::properties::FileProperties::default(),
            vec![bad_tag],
        );
        let res = save_tagged_file_atomically(&corrupt_flac, &tagged);
        assert!(res.is_err(), "expected lofty FLAC writeback error on invalid container");
        assert_eq!(std::fs::read(&corrupt_flac).unwrap(), original);

        let leftover_temps: Vec<_> = std::fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().contains(".twtmp."))
            .collect();
        assert!(leftover_temps.is_empty(), "temp file should be cleaned on failure");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_clears_stale_txxx_work_on_id3v2_update() {
        use lofty::config::ParseOptions;
        use lofty::file::{AudioFile, TaggedFileExt};
        use lofty::mpeg::MpegFile;
        use lofty::probe::Probe;
        use lofty::tag::ItemKey;

        let dir = std::env::temp_dir().join(format!("tw_ffi_txxx_work_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let mp3_path = dir.join("stale_work.mp3");

        let txxx_payload = b"\x03WORK\x00Old Work: I. Allegro";
        let frame_len = txxx_payload.len() as u32;
        let tag_len = 10 + frame_len;
        let to_synchsafe = |v: u32| -> [u8; 4] {
            [
                ((v >> 21) & 0x7F) as u8,
                ((v >> 14) & 0x7F) as u8,
                ((v >> 7) & 0x7F) as u8,
                (v & 0x7F) as u8,
            ]
        };

        let mut mp3 = Vec::new();
        mp3.extend_from_slice(b"ID3\x04\x00\x00");
        mp3.extend_from_slice(&to_synchsafe(tag_len));
        mp3.extend_from_slice(b"TXXX");
        mp3.extend_from_slice(&to_synchsafe(frame_len));
        mp3.extend_from_slice(b"\x00\x00");
        mp3.extend_from_slice(txxx_payload);
        let mut frame = vec![0u8; 417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        for _ in 0..8 {
            mp3.extend_from_slice(&frame);
        }
        std::fs::write(&mp3_path, &mp3).unwrap();

        {
            let mut f = std::fs::File::open(&mp3_path).unwrap();
            let mpeg_before =
                MpegFile::read_from(&mut f, ParseOptions::new().read_properties(false)).unwrap();
            assert_eq!(
                mpeg_before.id3v2().unwrap().get_user_text("WORK"),
                Some("Old Work: I. Allegro")
            );
        }

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&mp3_path.to_string_lossy())).unwrap();

        let req = serde_json::json!({
            "path": mp3_path.to_string_lossy(),
            "updates": {
                "work_name": "Canonical Work",
                "movement_name": "I. Allegro"
            }
        })
        .to_string();

        let raw_resp = write_audio_tags_json_impl(&req);
        let after: serde_json::Value = serde_json::from_str(&raw_resp).unwrap();
        assert!(after.get("error").is_none(), "unexpected error: {after}");
        assert_eq!(after["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&mp3_path).unwrap().read().unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(tag.get_string(&ItemKey::ContentGroup), Some("Canonical Work"));
        assert_eq!(tag.get_string(&ItemKey::Work), None);
        assert_eq!(tag.get_string(&ItemKey::Movement), Some("I. Allegro"));

        let mut f_after = std::fs::File::open(&mp3_path).unwrap();
        let mpeg_after =
            MpegFile::read_from(&mut f_after, ParseOptions::new().read_properties(false)).unwrap();
        assert_eq!(mpeg_after.id3v2().unwrap().get_user_text("WORK"), None);

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_tolerates_malformed_id3v2_timestamp_frame() {
        use lofty::config::{ParseOptions, ParsingMode};
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::ItemKey;

        let dir = std::env::temp_dir().join(format!("tw_ffi_bad_tdrc_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let mp3_path = dir.join("bad_tdrc.mp3");

        let to_synchsafe = |v: u32| -> [u8; 4] {
            [
                ((v >> 21) & 0x7F) as u8,
                ((v >> 14) & 0x7F) as u8,
                ((v >> 7) & 0x7F) as u8,
                (v & 0x7F) as u8,
            ]
        };

        let tdrc_payload = b"\x002016/1/1";
        let tyer_payload = b"\x032016";
        let tag_len = (10 + tdrc_payload.len() + 10 + tyer_payload.len()) as u32;

        let mut mp3 = Vec::new();
        mp3.extend_from_slice(b"ID3\x04\x00\x00");
        mp3.extend_from_slice(&to_synchsafe(tag_len));
        mp3.extend_from_slice(b"TDRC");
        mp3.extend_from_slice(&to_synchsafe(tdrc_payload.len() as u32));
        mp3.extend_from_slice(b"\x00\x00");
        mp3.extend_from_slice(tdrc_payload);
        mp3.extend_from_slice(b"TYER");
        mp3.extend_from_slice(&to_synchsafe(tyer_payload.len() as u32));
        mp3.extend_from_slice(b"\x00\x00");
        mp3.extend_from_slice(tyer_payload);

        let mut frame = vec![0u8; 417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        for _ in 0..8 {
            mp3.extend_from_slice(&frame);
        }
        std::fs::write(&mp3_path, &mp3).unwrap();

        let before: serde_json::Value =
            serde_json::from_str(&fingerprint_path_impl(&mp3_path.to_string_lossy())).unwrap();

        let req = serde_json::json!({
            "path": mp3_path.to_string_lossy(),
            "updates": {
                "work_name": "Piano Quartet No. 3 in C minor, Op. 60",
                "movement_name": "IV. Finale. Allegro comodo"
            }
        })
        .to_string();

        let raw_resp = write_audio_tags_json_impl(&req);
        let after: serde_json::Value = serde_json::from_str(&raw_resp).unwrap();
        assert!(after.get("error").is_none(), "unexpected error: {after}");
        assert_eq!(after["audio_frame_blake3_hex"], before["audio_frame_blake3_hex"]);

        let reloaded = Probe::open(&mp3_path)
            .unwrap()
            .options(ParseOptions::new().parsing_mode(ParsingMode::Relaxed))
            .read()
            .unwrap();
        let tag = reloaded.primary_tag().unwrap();
        assert_eq!(
            tag.get_string(&ItemKey::ContentGroup),
            Some("Piano Quartet No. 3 in C minor, Op. 60")
        );
        assert_eq!(
            tag.get_string(&ItemKey::Movement),
            Some("IV. Finale. Allegro comodo")
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn probe_track_path_extracts_multivalue_flac_tags_padding_and_wav_props() {
        use lofty::config::WriteOptions;
        use lofty::file::TaggedFileExt;
        use lofty::probe::Probe;
        use lofty::tag::{Accessor, ItemKey, ItemValue, Tag, TagExt, TagItem};

        let dir = std::env::temp_dir().join(format!("tw_ffi_probe_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();

        // 1. Multi-tag FLAC with padding, publisher, DJ_SCENE, multiple ARTIST/GENRE/COMPOSER, and classical work splitting
        let flac_path = dir.join("symphony.flac");
        let mut flac = Vec::new();
        flac.extend_from_slice(b"fLaC");
        flac.push(0x00);
        flac.extend_from_slice(&[0, 0, 34]);
        let mut streaminfo = [0u8; 34];
        streaminfo[10] = 0x0A;
        streaminfo[11] = 0xC4;
        streaminfo[12] = 0x42;
        streaminfo[13] = 0xF0;
        // total_samples = 44100 * 3 = 132300 (0x204CC)
        streaminfo[14] = 0x00;
        streaminfo[15] = 0x02;
        streaminfo[16] = 0x04;
        streaminfo[17] = 0xCC;
        flac.extend_from_slice(&streaminfo);
        flac.push(0x81);
        flac.extend_from_slice(&[0, 0, 128]);
        flac.extend_from_slice(&[0u8; 128]);
        flac.extend_from_slice(&vec![0xABu8; 4096]);
        std::fs::write(&flac_path, &flac).unwrap();

        let initial = Probe::open(&flac_path).unwrap().read().unwrap();
        let mut tag = Tag::new(initial.primary_tag_type());
        tag.set_title("Symphony No. 5: I. Allegro".to_string());
        tag.set_album("Beethoven Symphonies".to_string());
        tag.set_track(1);
        tag.set_disk(2);
        tag.set_year(1963);
        tag.push_unchecked(TagItem::new(
            ItemKey::Unknown("ALBUM ARTIST".to_string()),
            ItemValue::Text("Berliner Philharmoniker".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::TrackArtists,
            ItemValue::Text("Herbert von Karajan".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::TrackArtists,
            ItemValue::Text("Berliner Philharmoniker".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::Composer,
            ItemValue::Text("Ludwig van Beethoven".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::Conductor,
            ItemValue::Text("Herbert von Karajan".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::Genre,
            ItemValue::Text("クラシック".to_string()),
        ));
        tag.push(TagItem::new(
            ItemKey::Genre,
            ItemValue::Text("オペラ".to_string()),
        ));
        tag.insert_text(ItemKey::Publisher, "Deutsche Grammophon".to_string());
        tag.push_unchecked(TagItem::new(
            ItemKey::Unknown("DJ_SCENE".to_string()),
            ItemValue::Text("focus; relax".to_string()),
        ));
        tag.push_unchecked(TagItem::new(
            ItemKey::Unknown("DJ_VOCALS".to_string()),
            ItemValue::Text("instrumental".to_string()),
        ));
        tag.push_unchecked(TagItem::new(
            ItemKey::Unknown("TEMPO".to_string()),
            ItemValue::Text("112.5".to_string()),
        ));
        tag.save_to_path(&flac_path, WriteOptions::default()).unwrap();

        let raw_flac_probe = probe_track_path_impl(&flac_path.to_string_lossy());
        let flac_json: serde_json::Value = serde_json::from_str(&raw_flac_probe).unwrap();
        assert!(flac_json.get("error").is_none(), "probe error: {flac_json}");
        assert_eq!(flac_json["codec"], "flac");
        assert_eq!(flac_json["sample_rate_hz"], 44100);
        assert_eq!(flac_json["bit_depth"], 16);
        assert_eq!(flac_json["duration_ms"], 3000);
        assert_eq!(flac_json["album_artist"], "Berlin Philharmonic");
        assert_eq!(
            flac_json["artists"],
            serde_json::json!(["Herbert von Karajan", "Berlin Philharmonic"])
        );
        assert_eq!(flac_json["composers"], serde_json::json!(["Ludwig van Beethoven"]));
        assert_eq!(flac_json["conductors"], serde_json::json!(["Herbert von Karajan"]));
        assert_eq!(flac_json["genre"], "Classical; Opera");
        assert_eq!(flac_json["publisher"], "Deutsche Grammophon");
        assert_eq!(flac_json["work_name"], "Symphony No. 5");
        assert_eq!(flac_json["movement_name"], "Allegro");
        assert_eq!(flac_json["movement_number"], 1);
        assert_eq!(flac_json["disc_number"], 2);
        assert_eq!(flac_json["track_number"], 1);
        assert_eq!(flac_json["year"], 1963);
        assert_eq!(flac_json["dj_scene"], serde_json::json!(["focus", "relax"]));
        assert_eq!(flac_json["dj_vocals"], "instrumental");
        assert_eq!(flac_json["tempo_bpm"], 112.5);
        assert_eq!(flac_json["tempo_bucket"], "Moderate");

        // 2. Minimal WAV verification
        let wav_path = dir.join("tone.wav");
        let mut wav = Vec::new();
        let data_bytes = (44100u32 * 2) as u32;
        wav.extend_from_slice(b"RIFF");
        wav.extend_from_slice(&(36 + data_bytes).to_le_bytes());
        wav.extend_from_slice(b"WAVEfmt ");
        wav.extend_from_slice(&16u32.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&1u16.to_le_bytes());
        wav.extend_from_slice(&44100u32.to_le_bytes());
        wav.extend_from_slice(&(44100u32 * 2).to_le_bytes());
        wav.extend_from_slice(&2u16.to_le_bytes());
        wav.extend_from_slice(&16u16.to_le_bytes());
        wav.extend_from_slice(b"data");
        wav.extend_from_slice(&data_bytes.to_le_bytes());
        wav.extend_from_slice(&vec![0u8; data_bytes as usize]);
        std::fs::write(&wav_path, &wav).unwrap();

        let raw_wav_probe = probe_track_path_impl(&wav_path.to_string_lossy());
        let wav_json: serde_json::Value = serde_json::from_str(&raw_wav_probe).unwrap();
        assert!(wav_json.get("error").is_none(), "wav probe error: {wav_json}");
        assert_eq!(wav_json["codec"], "wav");
        assert_eq!(wav_json["sample_rate_hz"], 44100);
        assert_eq!(wav_json["bit_depth"], 16);
        assert_eq!(wav_json["duration_ms"], 1000);
        assert!(wav_json["title"].is_null());

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[cfg(not(target_os = "android"))]
    #[test]
    fn write_audio_tags_strips_leading_id3v2_on_flac_preserving_audio_hash_and_tags() {
        let dir = std::env::temp_dir().join(format!("tw_ffi_id3_flac_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let flac_path = dir.join("prefixed.flac");

        let title_payload = [b"\x03".as_slice(), b"Legacy ID3 Title"].concat();
        let frame_len = title_payload.len() as u32;
        let tag_len = 10 + frame_len;
        let to_synchsafe = |v: u32| -> [u8; 4] {
            [
                ((v >> 21) & 0x7F) as u8,
                ((v >> 14) & 0x7F) as u8,
                ((v >> 7) & 0x7F) as u8,
                (v & 0x7F) as u8,
            ]
        };

        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"ID3\x03\x00\x00");
        bytes.extend_from_slice(&to_synchsafe(tag_len));
        bytes.extend_from_slice(b"TIT2");
        bytes.extend_from_slice(&(frame_len.to_be_bytes()));
        bytes.extend_from_slice(&[0u8; 2]);
        bytes.extend_from_slice(&title_payload);

        let mut stream_info = [0u8; 34];
        stream_info[10] = 0x0A;
        stream_info[11] = 0xC4;
        stream_info[12] = 0x42;
        stream_info[13] = 0xF0;
        stream_info[14] = 0x00;
        stream_info[15] = 0x02;
        stream_info[16] = 0x04;
        stream_info[17] = 0xCC;
        bytes.extend_from_slice(b"fLaC");
        bytes.push(0x00);
        bytes.extend_from_slice(&[0, 0, 34]);
        bytes.extend_from_slice(&stream_info);
        bytes.push(0x81);
        bytes.extend_from_slice(&[0, 0, 128]);
        bytes.extend_from_slice(&[0u8; 128]);
        bytes.extend_from_slice(&vec![0xABu8; 4096]);
        std::fs::write(&flac_path, &bytes).unwrap();

        let before_fp_raw = fingerprint_path_impl(&flac_path.to_string_lossy());
        let before_fp: serde_json::Value = serde_json::from_str(&before_fp_raw).unwrap();
        let expected_hash = before_fp["audio_frame_blake3_hex"].as_str().unwrap().to_string();

        let req = serde_json::json!({
            "path": flac_path.to_string_lossy(),
            "updates": {
                "composer": "Sergei Prokofiev",
                "album": "Prokofiev Classics"
            }
        });
        let write_raw = write_audio_tags_json_impl(&req.to_string());
        let write_val: serde_json::Value = serde_json::from_str(&write_raw).unwrap();
        assert!(write_val.get("error").is_none(), "write failed: {write_val}");
        assert_eq!(write_val["audio_frame_blake3_hex"], expected_hash);

        let updated_bytes = std::fs::read(&flac_path).unwrap();
        assert!(updated_bytes.starts_with(b"fLaC"), "ID3v2 prefix should be stripped");

        let probe_raw = probe_track_path_impl(&flac_path.to_string_lossy());
        let probe_val: serde_json::Value = serde_json::from_str(&probe_raw).unwrap();
        assert_eq!(probe_val["audio_frame_blake3_hex"], expected_hash);
        assert_eq!(probe_val["title"], "Legacy ID3 Title");
        assert_eq!(probe_val["composers"], serde_json::json!(["Sergei Prokofiev"]));
        assert_eq!(probe_val["album"], "Prokofiev Classics");

        let _ = std::fs::remove_dir_all(&dir);
    }
}
