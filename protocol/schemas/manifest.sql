-- Tonewise /Music/.tonewise/manifest.db SQLite Schema (v2)
-- Single-Writer Contract: PC desktop / CLI is the canonical writer of manifest.db.
-- Mobile clients consume manifest.db read-only and emit delta mutations via mutations.<device_id>.jsonl.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('schema_version', '2');

CREATE TABLE IF NOT EXISTS tracks (
    track_uid            TEXT PRIMARY KEY,              -- Canonical UID (hex blake3 audio-frame fingerprint or MBID-anchored UID)
    audio_frame_blake3   BLOB NOT NULL UNIQUE,          -- 32-byte Blake3 digest over pure audio frames (tag headers excluded)
    header_size          INTEGER NOT NULL DEFAULT 0,    -- Metadata header byte span [0, header_size)
    header_blake3        BLOB NOT NULL,                 -- 32-byte Blake3 digest over metadata header block for delta patch detection
    padding_bytes        INTEGER NOT NULL DEFAULT 0,    -- Available FLAC/container padding bytes for in-place mobile header patching

    rel_path             TEXT NOT NULL,                 -- Normalized POSIX relative path under /Music/ (no leading slash)
    file_size            INTEGER NOT NULL,
    mtime_ms             INTEGER NOT NULL,
    codec                TEXT NOT NULL,
    sample_rate_hz       INTEGER NOT NULL,
    bit_depth            INTEGER,
    bitrate_kbps         INTEGER,
    duration_ms          INTEGER NOT NULL,

    title                TEXT NOT NULL,
    album                TEXT,
    album_artist         TEXT,
    artists_json         TEXT NOT NULL DEFAULT '[]',    -- JSON array of split artists (whitelist-protected)
    composers_json       TEXT NOT NULL DEFAULT '[]',    -- JSON array of composers (TCOM / COMPOSER / ©wrt)
    conductors_json      TEXT NOT NULL DEFAULT '[]',    -- JSON array of conductors (TPE3 / CONDUCTOR)
    work_name            TEXT,                          -- Classical work title (TIT1 / WORK)
    movement_name        TEXT,                          -- Classical movement title
    movement_number      INTEGER,
    genre                TEXT,
    year                 INTEGER,                       -- Normalized 4-digit year
    original_date        TEXT,                          -- Preserved ISO date from TDOR / ORIGINALDATE
    release_type         TEXT,

    replaygain_track_db  REAL,
    replaygain_album_db  REAL,
    replaygain_peak      REAL,

    -- Five-dimensional curation metadata with per-tag LWW vector timestamps (UTC epoch ms)
    dj_scene_json        TEXT NOT NULL DEFAULT '[]',    -- Multi-value JSON array e.g. ["focus","coding","relax","energy","pop"]
    dj_scene_ts          INTEGER NOT NULL DEFAULT 0,
    dj_vocals            TEXT CHECK (dj_vocals IN ('vocal', 'instrumental', 'unknown') OR dj_vocals IS NULL),
    dj_vocals_ts         INTEGER NOT NULL DEFAULT 0,
    dj_energy            TEXT CHECK (dj_energy IN ('low', 'medium', 'high', 'unknown') OR dj_energy IS NULL),
    dj_energy_ts         INTEGER NOT NULL DEFAULT 0,
    tempo_bpm            REAL,
    tempo_bucket         TEXT CHECK (tempo_bucket IN ('Very Slow', 'Slow', 'Moderate', 'Fast', 'Very Fast') OR tempo_bucket IS NULL),
    tempo_ts             INTEGER NOT NULL DEFAULT 0,
    mood_json            TEXT NOT NULL DEFAULT '[]',    -- Raw mood tags normalized against mood_clusters.json
    mood_ts              INTEGER NOT NULL DEFAULT 0,

    rating                       INTEGER CHECK (rating BETWEEN 0 AND 5) DEFAULT 0,
    rating_ts                    INTEGER NOT NULL DEFAULT 0,
    play_count_total             INTEGER NOT NULL DEFAULT 0,
    last_played_ts               INTEGER NOT NULL DEFAULT 0,
    waveform_offset              INTEGER,                       -- Byte offset into waveforms.bin payload (NULL if pending)
    disc_number                  INTEGER NOT NULL DEFAULT 0,
    track_number                 INTEGER NOT NULL DEFAULT 0,
    publisher                    TEXT,
    legacy_play_count            INTEGER NOT NULL DEFAULT 0,
    lastfm_pulled_count          INTEGER NOT NULL DEFAULT 0,
    local_unsynced_expired_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_tracks_rel_path ON tracks (rel_path);
CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks (album);
CREATE INDEX IF NOT EXISTS idx_tracks_genre ON tracks (genre);
CREATE INDEX IF NOT EXISTS idx_tracks_energy ON tracks (dj_energy);
CREATE INDEX IF NOT EXISTS idx_tracks_vocals ON tracks (dj_vocals);
CREATE INDEX IF NOT EXISTS idx_tracks_work ON tracks (album_artist, work_name);
CREATE INDEX IF NOT EXISTS idx_tracks_work_name ON tracks (work_name);
CREATE INDEX IF NOT EXISTS idx_tracks_last_played ON tracks (last_played_ts);
