use jni::JNIEnv;
use jni::objects::{JByteArray, JClass, JString};
use jni::sys::{jint, jstring};
use serde::Deserialize;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char};
use std::fs::{File, OpenOptions};
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

fn apply_header_patch_path_impl(path: &str, expected_old_hex: &str, new_header: &[u8]) -> String {
    let mut file = match OpenOptions::new().read(true).write(true).open(path) {
        Ok(f) => f,
        Err(e) => return json_error(&format!("open rw failed: {e}")),
    };
    match apply_header_delta_patch(&mut file, expected_old_hex, new_header) {
        Ok(outcome) => serde_json::to_string(&outcome).unwrap_or_else(|e| json_error(&e.to_string())),
        Err(e) => json_error(&format!("patch failed: {e}")),
    }
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
    match apply_header_delta_patch(&mut cloned, expected_old_hex, new_header) {
        Ok(outcome) => serde_json::to_string(&outcome).unwrap_or_else(|e| json_error(&e.to_string())),
        Err(e) => json_error(&format!("patch failed: {e}")),
    }
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
fn write_audio_tags_json_impl(request_json: &str) -> String {
    use lofty::config::WriteOptions;
    use lofty::file::{AudioFile, TaggedFileExt};
    use lofty::probe::Probe;
    use lofty::tag::{Accessor, ItemKey, ItemValue, Tag, TagItem};

    let req: TagWriteRequest = match serde_json::from_str(request_json) {
        Ok(r) => r,
        Err(e) => return json_error(&format!("invalid tag write request json: {e}")),
    };

    let path = std::path::Path::new(&req.path);
    let mut tagged = match Probe::open(path).and_then(|p| p.read()) {
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
                    let display = clean_val.map(|v| {
                        if v.starts_with('[') {
                            serde_json::from_str::<Vec<String>>(v)
                                .unwrap_or_default()
                                .into_iter()
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
                "composer" | "composers" | "composers_json" => {
                    tag.remove_key(&ItemKey::Composer);
                    if let Some(v) = clean_val {
                        let items: Vec<String> = if v.starts_with('[') {
                            serde_json::from_str::<Vec<String>>(v).unwrap_or_default()
                        } else {
                            v.split([';', ','])
                                .map(|s| s.trim().to_string())
                                .filter(|s| !s.is_empty())
                                .collect()
                        };
                        for c in items {
                            tag.push(TagItem::new(ItemKey::Composer, ItemValue::Text(c)));
                        }
                    }
                }
                "conductor" | "conductors" | "conductors_json" => {
                    tag.remove_key(&ItemKey::Conductor);
                    if let Some(v) = clean_val {
                        let items: Vec<String> = if v.starts_with('[') {
                            serde_json::from_str::<Vec<String>>(v).unwrap_or_default()
                        } else {
                            v.split([';', ','])
                                .map(|s| s.trim().to_string())
                                .filter(|s| !s.is_empty())
                                .collect()
                        };
                        for c in items {
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
                        let items: Vec<String> = if v.starts_with('[') {
                            serde_json::from_str::<Vec<String>>(v).unwrap_or_default()
                        } else {
                            v.split([';', ','])
                                .map(|s| s.trim().to_string())
                                .filter(|s| !s.is_empty())
                                .collect()
                        };
                        for m in items {
                            tag.push(TagItem::new(ItemKey::Mood, ItemValue::Text(m)));
                        }
                    }
                }
                "dj_scene" | "dj_scene_json" => {
                    tag.retain(|it| !matches!(it.key(), ItemKey::Unknown(k) if k.eq_ignore_ascii_case("DJ_SCENE")));
                    if let Some(v) = clean_val {
                        let items: Vec<String> = if v.starts_with('[') {
                            serde_json::from_str::<Vec<String>>(v).unwrap_or_default()
                        } else {
                            v.split([';', ','])
                                .map(|s| s.trim().to_string())
                                .filter(|s| !s.is_empty())
                                .collect()
                        };
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

    if let Err(e) = tagged.save_to_path(path, WriteOptions::default()) {
        return json_error(&format!("lofty save failed: {e}"));
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

// -----------------------------------------------------------------------------
// JNI exports for Android (:musikr -> org.oxycblt.musikr.core.TonewiseCoreJNI)
// -----------------------------------------------------------------------------

fn jstring_to_rust(env: &mut JNIEnv, input: &JString) -> Result<String, String> {
    env.get_string(input)
        .map(|s| s.into())
        .map_err(|e| format!("jni get_string failed: {e}"))
}

fn rust_to_jstring(env: &mut JNIEnv, value: &str) -> jstring {
    env.new_string(value)
        .map(|s| s.into_raw())
        .unwrap_or(std::ptr::null_mut())
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
        Ok(s) => split_artists_json_impl(&s),
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
    let out = match (
        jstring_to_rust(&mut env, &scene_json),
        jstring_to_rust(&mut env, &tracks_json),
    ) {
        (Ok(s), Ok(t)) => evaluate_scene_json_impl(&s, &t),
        (Err(e), _) | (_, Err(e)) => json_error(&e),
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
        Ok(r) => merge_m3u8_json_impl(&r),
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
    let out = fingerprint_fd_impl(fd);
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
        Ok(p) => fingerprint_path_impl(&p),
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
    let out = match (
        jstring_to_rust(&mut env, &archive_path),
        jstring_to_rust(&mut env, &blake3_hex),
    ) {
        (Ok(p), Ok(h)) => lookup_waveform_path_impl(&p, &h),
        (Err(e), _) | (_, Err(e)) => json_error(&e),
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
        Ok(h) => lookup_waveform_fd_impl(fd, &h),
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
        Ok(r) => apply_lww_mutations_json_impl(&r),
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
        Ok(r) => merge_sessions_json_impl(&r),
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
    let out = match (
        jstring_to_rust(&mut env, &expected_old_hex),
        env.convert_byte_array(&new_header)
            .map_err(|e| format!("convert byte array failed: {e}")),
    ) {
        (Ok(hex), Ok(bytes)) => apply_header_patch_fd_impl(fd, &hex, &bytes),
        (Err(e), _) | (_, Err(e)) => json_error(&e),
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
    let out = match (
        jstring_to_rust(&mut env, &path),
        jstring_to_rust(&mut env, &expected_old_hex),
        env.convert_byte_array(&new_header)
            .map_err(|e| format!("convert byte array failed: {e}")),
    ) {
        (Ok(p), Ok(hex), Ok(bytes)) => apply_header_patch_path_impl(&p, &hex, &bytes),
        (Err(e), _, _) | (_, Err(e), _) | (_, _, Err(e)) => json_error(&e),
    };
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
}
