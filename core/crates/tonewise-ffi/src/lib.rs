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
fn save_tagged_file_atomically(
    path: &std::path::Path,
    tagged: &lofty::file::TaggedFile,
) -> Result<(), String> {
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
    if let Err(e) = save_primary_tag_or_file(&tmp_path, tagged) {
        let _ = std::fs::remove_file(&tmp_path);
        return Err(format!("lofty save failed: {e}"));
    }
    if let Ok(f) = std::fs::File::open(&tmp_path) {
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
fn write_audio_tags_json_impl(request_json: &str) -> String {
    use lofty::config::ParseOptions;
    use lofty::file::TaggedFileExt;
    use lofty::probe::Probe;
    use lofty::tag::{Accessor, ItemKey, ItemValue, Tag, TagItem};

    let req: TagWriteRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid tag write request json: {e}")),
    };

    let path = std::path::Path::new(&req.path);
    let mut tagged = match Probe::open(path).and_then(|p| {
        p.options(ParseOptions::new().read_properties(false)).read()
    }) {
        Ok(t) => t,
        Err(e) => return json_error(&format!("lofty read failed: {e}")),
    };

    let tag_type = tagged.primary_tag_type();
    if tagged.primary_tag_mut().is_none() {
        tagged.insert_tag(Tag::new(tag_type));
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
                    let key = if matches!(tag_type, lofty::tag::TagType::Id3v2) {
                        ItemKey::ContentGroup
                    } else {
                        ItemKey::Work
                    };
                    match clean_val {
                        Some(v) => {
                            tag.insert_text(key, v.to_string());
                        }
                        None => {
                            tag.remove_key(&ItemKey::Work);
                            tag.remove_key(&ItemKey::ContentGroup);
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

    if let Err(e) = save_tagged_file_atomically(path, &tagged) {
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
}
