#ifndef TONEWISE_CORE_H
#define TONEWISE_CORE_H

#include <stdint.h>

uint32_t tonewise_core_abi_version(void);

void tonewise_free_string(char* ptr);

char* tonewise_split_artists_json(const char* raw_utf8);

char* tonewise_evaluate_scene_json(const char* scene_json, const char* tracks_json);

char* tonewise_merge_m3u8_json(const char* request_json);

char* tonewise_audio_fingerprint_path(const char* path_utf8);

char* tonewise_lookup_waveform_json(const char* archive_path_utf8, const char* blake3_hex_utf8);

char* tonewise_apply_lww_mutations_json(const char* request_json);

char* tonewise_merge_sessions_json(const char* request_json);

char* tonewise_apply_header_patch_path(
    const char* path_utf8,
    const char* expected_old_hex_utf8,
    const uint8_t* new_header_ptr,
    uintptr_t new_header_len);

#endif /* TONEWISE_CORE_H */
