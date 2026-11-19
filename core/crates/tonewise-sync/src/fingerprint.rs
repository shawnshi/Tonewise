use serde::{Deserialize, Serialize};
use std::io::{self, Read, Seek, SeekFrom};

const FAST_HASH_THRESHOLD: u64 = 256 * 1024;
const FAST_WINDOW_BYTES: u64 = 64 * 1024;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AudioFrameFingerprint {
    pub audio_frame_blake3_hex: String,
    pub header_blake3_hex: String,
    pub header_size: u64,
    pub file_size: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct AudioSpan {
    start: u64,
    end: u64,
}

/// Computes `audio_frame_blake3` over pure audio frames while skipping metadata headers
/// (ID3v2, FLAC metadata/padding blocks, MP4 non-`mdat` atoms, Ogg init/comment pages).
/// Spans larger than 256 KB hash length + head/mid/tail 64 KB windows to bound I/O on lossless files.
pub fn compute_audio_frame_fingerprint<R: Read + Seek>(
    reader: &mut R,
) -> io::Result<AudioFrameFingerprint> {
    let file_size = reader.seek(SeekFrom::End(0))?;
    reader.seek(SeekFrom::Start(0))?;

    let span = detect_audio_span(reader, file_size)?;

    let mut header_hasher = blake3::Hasher::new();
    if span.start > 0 {
        hash_span_bounded(reader, &mut header_hasher, 0, span.start)?;
    }
    if span.end < file_size {
        hash_span_bounded(reader, &mut header_hasher, span.end, file_size)?;
    }

    let mut audio_hasher = blake3::Hasher::new();
    if span.end > span.start {
        hash_span_bounded(reader, &mut audio_hasher, span.start, span.end)?;
    }

    Ok(AudioFrameFingerprint {
        audio_frame_blake3_hex: audio_hasher.finalize().to_hex().to_string(),
        header_blake3_hex: header_hasher.finalize().to_hex().to_string(),
        header_size: span.start,
        file_size,
    })
}

fn hash_span_bounded<R: Read + Seek>(
    reader: &mut R,
    hasher: &mut blake3::Hasher,
    start: u64,
    end: u64,
) -> io::Result<()> {
    let span_len = end.saturating_sub(start);
    if span_len == 0 {
        return Ok(());
    }
    if span_len <= FAST_HASH_THRESHOLD {
        reader.seek(SeekFrom::Start(start))?;
        return hash_exact_len(reader, hasher, span_len);
    }

    hasher.update(&span_len.to_le_bytes());
    let mid_start = start + (span_len / 2) - (FAST_WINDOW_BYTES / 2);
    let tail_start = end - FAST_WINDOW_BYTES;
    for offset in [start, mid_start, tail_start] {
        reader.seek(SeekFrom::Start(offset))?;
        hash_exact_len(reader, hasher, FAST_WINDOW_BYTES)?;
    }
    Ok(())
}

fn hash_exact_len<R: Read>(
    reader: &mut R,
    hasher: &mut blake3::Hasher,
    mut remaining: u64,
) -> io::Result<()> {
    let mut buf = [0u8; 64 * 1024];
    while remaining > 0 {
        let to_read = remaining.min(buf.len() as u64) as usize;
        let n = reader.read(&mut buf[..to_read])?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        remaining -= n as u64;
    }
    Ok(())
}

fn detect_audio_span<R: Read + Seek>(reader: &mut R, file_size: u64) -> io::Result<AudioSpan> {
    if file_size < 4 {
        return Ok(AudioSpan {
            start: 0,
            end: file_size,
        });
    }

    let mut offset = skip_id3v2_if_present(reader, file_size)?;

    if offset + 4 <= file_size {
        reader.seek(SeekFrom::Start(offset))?;
        let mut magic = [0u8; 4];
        reader.read_exact(&mut magic)?;
        if &magic == b"fLaC" {
            let flac_audio_start = skip_flac_metadata_blocks(reader, offset + 4, file_size)?;
            return Ok(AudioSpan {
                start: flac_audio_start,
                end: file_size,
            });
        }
        if &magic == b"OggS" {
            let ogg_audio_start = skip_ogg_header_pages(reader, offset, file_size)?;
            return Ok(AudioSpan {
                start: ogg_audio_start,
                end: file_size,
            });
        }
    }

    // Check MP4/M4A top-level `ftyp` + `mdat` atom, RIFF WAVE `data`, DSF `data`, or AIFF/AIFC `SSND`
    if offset == 0 && file_size >= 12 {
        if let Some(wave_span) = find_riff_wave_data_span(reader, file_size)? {
            return Ok(wave_span);
        }
        if let Some(mdat_span) = find_mp4_mdat_span(reader, file_size)? {
            return Ok(mdat_span);
        }
        if let Some(dsf_span) = find_dsf_data_span(reader, file_size)? {
            return Ok(dsf_span);
        }
        if let Some(aiff_span) = find_aiff_ssnd_span(reader, file_size)? {
            return Ok(aiff_span);
        }
    }

    offset = offset.min(file_size);
    let end = strip_trailing_id3v1_and_apev2(reader, offset, file_size)?;
    Ok(AudioSpan { start: offset, end })
}

fn skip_id3v2_if_present<R: Read + Seek>(reader: &mut R, file_size: u64) -> io::Result<u64> {
    if file_size < 10 {
        return Ok(0);
    }
    reader.seek(SeekFrom::Start(0))?;
    let mut header = [0u8; 10];
    reader.read_exact(&mut header)?;
    if &header[0..3] != b"ID3" {
        return Ok(0);
    }
    let flags = header[5];
    let size = (((header[6] & 0x7F) as u64) << 21)
        | (((header[7] & 0x7F) as u64) << 14)
        | (((header[8] & 0x7F) as u64) << 7)
        | ((header[9] & 0x7F) as u64);
    let footer = if (flags & 0x10) != 0 { 10 } else { 0 };
    Ok((10 + size + footer).min(file_size))
}

fn skip_flac_metadata_blocks<R: Read + Seek>(
    reader: &mut R,
    mut pos: u64,
    file_size: u64,
) -> io::Result<u64> {
    while pos + 4 <= file_size {
        reader.seek(SeekFrom::Start(pos))?;
        let mut block_hdr = [0u8; 4];
        reader.read_exact(&mut block_hdr)?;
        let is_last = (block_hdr[0] & 0x80) != 0;
        let len = ((block_hdr[1] as u64) << 16)
            | ((block_hdr[2] as u64) << 8)
            | (block_hdr[3] as u64);
        pos = (pos + 4 + len).min(file_size);
        if is_last {
            break;
        }
    }
    Ok(pos)
}

fn skip_ogg_header_pages<R: Read + Seek>(
    reader: &mut R,
    mut pos: u64,
    file_size: u64,
) -> io::Result<u64> {
    // Vorbis has 3 header packets (1=ident, 3=comment, 5=setup); Opus has 2 (OpusHead, OpusTags).
    // Header pages can span multiple OggS continuation pages (e.g., large album comments or pictures).
    let mut target_header_packets: Option<usize> = None;
    let mut completed_packets = 0usize;
    let mut page_idx = 0usize;

    while page_idx < 128 && pos + 27 <= file_size {
        reader.seek(SeekFrom::Start(pos))?;
        let mut page_hdr = [0u8; 27];
        reader.read_exact(&mut page_hdr)?;
        if &page_hdr[0..4] != b"OggS" {
            break;
        }

        let granule_pos = u64::from_le_bytes(page_hdr[6..14].try_into().unwrap());
        if page_idx > 0 && granule_pos != 0 && granule_pos != u64::MAX {
            // Audio packets have begun on this page; stop before consuming it.
            break;
        }

        let seg_count = page_hdr[26] as usize;
        if pos + 27 + seg_count as u64 > file_size {
            break;
        }
        let mut seg_table = vec![0u8; seg_count];
        reader.read_exact(&mut seg_table)?;

        let payload_pos = pos + 27 + seg_count as u64;
        if page_idx == 0 {
            let probe_len = (file_size - payload_pos).min(8) as usize;
            let mut probe = [0u8; 8];
            if probe_len > 0 {
                reader.seek(SeekFrom::Start(payload_pos))?;
                reader.read_exact(&mut probe[..probe_len])?;
            }
            if probe_len >= 7 && &probe[..7] == b"\x01vorbis" {
                target_header_packets = Some(3);
            } else if probe_len >= 8 && &probe[..8] == b"OpusHead" {
                target_header_packets = Some(2);
            }
        }

        let body_len: u64 = seg_table.iter().map(|&b| b as u64).sum();
        let next_pos = payload_pos.saturating_add(body_len).min(file_size);
        if next_pos <= pos {
            break;
        }
        pos = next_pos;
        page_idx += 1;
        completed_packets += seg_table.iter().filter(|&&b| b < 255).count();

        match target_header_packets {
            Some(target) if completed_packets >= target => break,
            None if page_idx >= 2 => break,
            _ => {}
        }
    }
    Ok(pos)
}

fn strip_trailing_id3v1_and_apev2<R: Read + Seek>(
    reader: &mut R,
    start: u64,
    end: u64,
) -> io::Result<u64> {
    let mut cur_end = end.max(start);
    for _ in 0..2 {
        let mut trimmed = false;

        // 1. Trailing ID3v1 / ID3v1.1 (128 bytes starting with "TAG")
        if cur_end >= start + 128 {
            reader.seek(SeekFrom::Start(cur_end - 128))?;
            let mut id3v1_magic = [0u8; 3];
            reader.read_exact(&mut id3v1_magic)?;
            if &id3v1_magic == b"TAG" {
                cur_end -= 128;
                trimmed = true;
            }
        }

        // 2. Trailing APEv1 / APEv2 tag footer (32 bytes ending at cur_end)
        if cur_end >= start + 32 {
            reader.seek(SeekFrom::Start(cur_end - 32))?;
            let mut footer = [0u8; 32];
            reader.read_exact(&mut footer)?;
            if &footer[0..8] == b"APETAGEX" {
                let version = u32::from_le_bytes(footer[8..12].try_into().unwrap());
                let tag_size = u32::from_le_bytes(footer[12..16].try_into().unwrap()) as u64;
                let flags = u32::from_le_bytes(footer[20..24].try_into().unwrap());
                let is_footer = (flags & 0x2000_0000) == 0;
                if (version == 1000 || version == 2000) && is_footer && tag_size >= 32 {
                    let has_header = (flags & 0x8000_0000) != 0;
                    let total_ape = tag_size.saturating_add(if has_header { 32 } else { 0 });
                    if cur_end >= start + total_ape {
                        let header_ok = if has_header {
                            reader.seek(SeekFrom::Start(cur_end - total_ape))?;
                            let mut hdr_magic = [0u8; 8];
                            reader.read_exact(&mut hdr_magic)?;
                            &hdr_magic == b"APETAGEX"
                        } else {
                            true
                        };
                        if header_ok {
                            cur_end -= total_ape;
                            trimmed = true;
                        }
                    }
                }
            }
        }

        if !trimmed {
            break;
        }
    }
    Ok(cur_end)
}

fn find_mp4_mdat_span<R: Read + Seek>(
    reader: &mut R,
    file_size: u64,
) -> io::Result<Option<AudioSpan>> {
    reader.seek(SeekFrom::Start(0))?;
    let mut first = [0u8; 8];
    if reader.read_exact(&mut first).is_err() || &first[4..8] != b"ftyp" {
        return Ok(None);
    }

    let mut pos = 0u64;
    while pos + 8 <= file_size {
        reader.seek(SeekFrom::Start(pos))?;
        let mut hdr = [0u8; 8];
        reader.read_exact(&mut hdr)?;
        let size32 = u32::from_be_bytes([hdr[0], hdr[1], hdr[2], hdr[3]]) as u64;
        let box_type = &hdr[4..8];

        let (header_bytes, total_size) = if size32 == 1 {
            if pos + 16 > file_size {
                break;
            }
            let mut ext = [0u8; 8];
            reader.read_exact(&mut ext)?;
            (16u64, u64::from_be_bytes(ext))
        } else if size32 == 0 {
            (8u64, file_size - pos)
        } else {
            (8u64, size32)
        };

        if total_size < header_bytes {
            break;
        }
        let end = pos
            .checked_add(total_size)
            .map(|e| e.min(file_size))
            .unwrap_or(file_size);
        if end <= pos {
            break;
        }
        if box_type == b"mdat" {
            return Ok(Some(AudioSpan {
                start: pos + header_bytes,
                end,
            }));
        }
        pos = end;
    }
    Ok(None)
}

fn find_riff_wave_data_span<R: Read + Seek>(
    reader: &mut R,
    file_size: u64,
) -> io::Result<Option<AudioSpan>> {
    reader.seek(SeekFrom::Start(0))?;
    let mut riff = [0u8; 12];
    if reader.read_exact(&mut riff).is_err() || &riff[0..4] != b"RIFF" || &riff[8..12] != b"WAVE" {
        return Ok(None);
    }

    let mut pos = 12u64;
    while pos + 8 <= file_size {
        reader.seek(SeekFrom::Start(pos))?;
        let mut hdr = [0u8; 8];
        reader.read_exact(&mut hdr)?;
        let chunk_len = u32::from_le_bytes([hdr[4], hdr[5], hdr[6], hdr[7]]) as u64;
        let data_start = pos + 8;
        let data_end = (data_start + chunk_len).min(file_size);
        if &hdr[0..4] == b"data" {
            return Ok(Some(AudioSpan {
                start: data_start,
                end: data_end,
            }));
        }
        let padded = chunk_len + (chunk_len & 1);
        pos = data_start.saturating_add(padded);
    }
    Ok(None)
}

fn find_dsf_data_span<R: Read + Seek>(
    reader: &mut R,
    file_size: u64,
) -> io::Result<Option<AudioSpan>> {
    if file_size < 28 {
        return Ok(None);
    }
    reader.seek(SeekFrom::Start(0))?;
    let mut dsd_hdr = [0u8; 28];
    if reader.read_exact(&mut dsd_hdr).is_err() || &dsd_hdr[0..4] != b"DSD " {
        return Ok(None);
    }
    let chunk0_size = u64::from_le_bytes(dsd_hdr[4..12].try_into().unwrap());
    if chunk0_size < 28 || chunk0_size > file_size {
        return Ok(None);
    }
    let metadata_offset = u64::from_le_bytes(dsd_hdr[20..28].try_into().unwrap());
    let audio_limit = if metadata_offset >= chunk0_size && metadata_offset <= file_size {
        metadata_offset
    } else {
        file_size
    };

    let mut pos = chunk0_size;
    while pos + 12 <= audio_limit {
        reader.seek(SeekFrom::Start(pos))?;
        let mut chunk_hdr = [0u8; 12];
        reader.read_exact(&mut chunk_hdr)?;
        let chunk_size = u64::from_le_bytes(chunk_hdr[4..12].try_into().unwrap());
        if chunk_size < 12 {
            break;
        }
        let end = pos
            .checked_add(chunk_size)
            .map(|e| e.min(audio_limit))
            .unwrap_or(audio_limit);
        if end <= pos {
            break;
        }
        if &chunk_hdr[0..4] == b"data" {
            return Ok(Some(AudioSpan {
                start: pos + 12,
                end,
            }));
        }
        pos = end;
    }
    Ok(None)
}

fn find_aiff_ssnd_span<R: Read + Seek>(
    reader: &mut R,
    file_size: u64,
) -> io::Result<Option<AudioSpan>> {
    if file_size < 12 {
        return Ok(None);
    }
    reader.seek(SeekFrom::Start(0))?;
    let mut form = [0u8; 12];
    if reader.read_exact(&mut form).is_err()
        || &form[0..4] != b"FORM"
        || (&form[8..12] != b"AIFF" && &form[8..12] != b"AIFC")
    {
        return Ok(None);
    }

    let mut pos = 12u64;
    while pos + 8 <= file_size {
        reader.seek(SeekFrom::Start(pos))?;
        let mut hdr = [0u8; 8];
        reader.read_exact(&mut hdr)?;
        let chunk_len = u32::from_be_bytes([hdr[4], hdr[5], hdr[6], hdr[7]]) as u64;
        let body_start = pos + 8;
        let body_end = body_start.saturating_add(chunk_len).min(file_size);
        if &hdr[0..4] == b"SSND" {
            // Skip 8-byte SSND offset + blockSize fields when present
            let ssnd_start = if chunk_len >= 8 && body_start + 8 <= body_end {
                body_start + 8
            } else {
                body_start
            };
            return Ok(Some(AudioSpan {
                start: ssnd_start,
                end: body_end,
            }));
        }
        let padded = chunk_len + (chunk_len & 1);
        let next_pos = body_start.saturating_add(padded);
        if next_pos <= pos {
            break;
        }
        pos = next_pos;
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn build_synthetic_flac(comment_payload: &[u8], padding_len: usize, audio: &[u8]) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(b"fLaC");
        // STREAMINFO (34 bytes), not last
        out.push(0x00);
        out.extend_from_slice(&(34u32.to_be_bytes())[1..4]);
        out.extend_from_slice(&[0u8; 34]);

        // VORBIS_COMMENT (type 4), not last
        out.push(0x04);
        out.extend_from_slice(&((comment_payload.len() as u32).to_be_bytes())[1..4]);
        out.extend_from_slice(comment_payload);

        // PADDING (type 1), last metadata block (0x81)
        out.push(0x81);
        out.extend_from_slice(&((padding_len as u32).to_be_bytes())[1..4]);
        out.extend_from_slice(&vec![0u8; padding_len]);

        out.extend_from_slice(audio);
        out
    }

    #[test]
    fn flac_audio_frame_blake3_is_invariant_to_tag_and_padding_edits() {
        let audio = b"FAKE_FLAC_AUDIO_FRAME_DATA_0123456789ABCDEF";
        let v1 = build_synthetic_flac(b"DJ_SCENE=relax", 8192, audio);
        let v2 = build_synthetic_flac(b"DJ_SCENE=relax;focus\0TEMPO=104", 8172, audio);

        let fp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&v1)).unwrap();
        let fp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&v2)).unwrap();

        assert_eq!(fp1.audio_frame_blake3_hex, fp2.audio_frame_blake3_hex);
        assert_ne!(fp1.header_blake3_hex, fp2.header_blake3_hex);
        assert!(fp1.header_size > 8192);
    }

    #[test]
    fn id3v2_audio_frame_blake3_skips_tag_header() {
        let audio = b"MP3_FRAME_SYNC_PAYLOAD";
        let make_mp3 = |tag_bytes: &[u8]| {
            let mut out = Vec::new();
            out.extend_from_slice(b"ID3\x04\x00\x00");
            let sz = tag_bytes.len() as u32;
            out.push(((sz >> 21) & 0x7F) as u8);
            out.push(((sz >> 14) & 0x7F) as u8);
            out.push(((sz >> 7) & 0x7F) as u8);
            out.push((sz & 0x7F) as u8);
            out.extend_from_slice(tag_bytes);
            out.extend_from_slice(audio);
            out
        };

        let fp1 = compute_audio_frame_fingerprint(&mut Cursor::new(make_mp3(b"TIT2=A"))).unwrap();
        let fp2 =
            compute_audio_frame_fingerprint(&mut Cursor::new(make_mp3(b"TIT2=LongerTitleB")))
                .unwrap();

        assert_eq!(fp1.audio_frame_blake3_hex, fp2.audio_frame_blake3_hex);
        assert_ne!(fp1.header_blake3_hex, fp2.header_blake3_hex);
        assert_eq!(fp1.header_size, 10 + 6);
        assert_eq!(fp2.header_size, 10 + 17);
    }

    #[test]
    fn large_audio_span_fingerprint_is_bounded_and_tag_invariant() {
        let mut audio = vec![0x5Au8; 400 * 1024];
        audio[0] = 0x11;
        audio[200 * 1024] = 0x22;
        audio[400 * 1024 - 1] = 0x33;

        let v1 = build_synthetic_flac(b"ARTIST=Bach", 4096, &audio);
        let v2 = build_synthetic_flac(b"ARTIST=Johann Sebastian Bach\0BPM=96", 2048, &audio);

        let mut modified_audio = audio.clone();
        modified_audio[200 * 1024] = 0x99;
        let v3 = build_synthetic_flac(b"ARTIST=Bach", 4096, &modified_audio);

        let fp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&v1)).unwrap();
        let fp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&v2)).unwrap();
        let fp3 = compute_audio_frame_fingerprint(&mut Cursor::new(&v3)).unwrap();

        assert_eq!(fp1.audio_frame_blake3_hex, fp2.audio_frame_blake3_hex);
        assert_ne!(fp1.header_blake3_hex, fp2.header_blake3_hex);
        assert_ne!(fp1.audio_frame_blake3_hex, fp3.audio_frame_blake3_hex);
    }

    #[test]
    fn mp4_malformed_largesize_overflow_terminates_cleanly() {
        let mut data = Vec::new();
        // Valid 8-byte ftyp atom at offset 0 so next atom starts at pos = 8
        data.extend_from_slice(&8u32.to_be_bytes());
        data.extend_from_slice(b"ftyp");
        // Malformed 64-bit largesize box (size32 == 1) with total_size = u64::MAX - 4
        data.extend_from_slice(&1u32.to_be_bytes());
        data.extend_from_slice(b"free");
        data.extend_from_slice(&(u64::MAX - 4).to_be_bytes());
        data.extend_from_slice(b"trailing_payload_bytes");

        let fp = compute_audio_frame_fingerprint(&mut Cursor::new(&data)).unwrap();
        assert_eq!(fp.file_size, data.len() as u64);
    }

    fn build_ogg_page(granule_pos: u64, payload_segments: &[&[u8]], continues_last: bool) -> Vec<u8> {
        let mut page = Vec::new();
        page.extend_from_slice(b"OggS");
        page.push(0); // stream_structure_version
        page.push(0); // header_type_flag
        page.extend_from_slice(&granule_pos.to_le_bytes());
        page.extend_from_slice(&1u32.to_le_bytes()); // serial
        page.extend_from_slice(&0u32.to_le_bytes()); // page seq
        page.extend_from_slice(&0u32.to_le_bytes()); // crc

        let mut laces = Vec::new();
        let mut body = Vec::new();
        for (idx, seg) in payload_segments.iter().enumerate() {
            let mut rem = seg.len();
            while rem >= 255 {
                laces.push(255u8);
                rem -= 255;
            }
            if !continues_last || idx < payload_segments.len() - 1 {
                laces.push(rem as u8);
            }
            body.extend_from_slice(seg);
        }
        page.push(laces.len() as u8);
        page.extend_from_slice(&laces);
        page.extend_from_slice(&body);
        page
    }

    #[test]
    fn ogg_multipage_vorbis_and_opus_headers_are_tag_invariant() {
        let vorbis_id = b"\x01vorbis_id_header";
        let vorbis_setup = b"\x05vorbis_setup_header";
        let vorbis_audio = b"VORBIS_AUDIO_FRAME_BYTES_XYZ";
        let opus_id = b"OpusHead_id_header";
        let opus_audio = b"OPUS_AUDIO_FRAME_BYTES_XYZ";

        // Case A: Small Vorbis comment fits on page 1 alongside setup packet; Case B: multi-page comment spans page 1+2 before page 3 setup
        let mut vorbis_v1 = Vec::new();
        vorbis_v1.extend_from_slice(&build_ogg_page(0, &[vorbis_id], false));
        vorbis_v1.extend_from_slice(&build_ogg_page(0, &[b"\x03vorbisSMALL", vorbis_setup], false));
        vorbis_v1.extend_from_slice(&build_ogg_page(1024, &[vorbis_audio], false));

        let big_comment = vec![b'A'; 510]; // two 255-byte continuation segments
        let mut vorbis_v2 = Vec::new();
        vorbis_v2.extend_from_slice(&build_ogg_page(0, &[vorbis_id], false));
        vorbis_v2.extend_from_slice(&build_ogg_page(u64::MAX, &[&big_comment], true));
        vorbis_v2.extend_from_slice(&build_ogg_page(0, &[b"_END", vorbis_setup], false));
        vorbis_v2.extend_from_slice(&build_ogg_page(1024, &[vorbis_audio], false));

        let vfp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&vorbis_v1)).unwrap();
        let vfp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&vorbis_v2)).unwrap();
        assert_eq!(vfp1.audio_frame_blake3_hex, vfp2.audio_frame_blake3_hex);
        assert_ne!(vfp1.header_blake3_hex, vfp2.header_blake3_hex);

        // Opus: Case A 2 pages vs Case B 3 pages (multi-page OpusTags)
        let mut opus_v1 = Vec::new();
        opus_v1.extend_from_slice(&build_ogg_page(0, &[opus_id], false));
        opus_v1.extend_from_slice(&build_ogg_page(0, &[b"OpusTags_small"], false));
        opus_v1.extend_from_slice(&build_ogg_page(960, &[opus_audio], false));

        let mut opus_v2 = Vec::new();
        opus_v2.extend_from_slice(&build_ogg_page(0, &[opus_id], false));
        opus_v2.extend_from_slice(&build_ogg_page(u64::MAX, &[&big_comment], true));
        opus_v2.extend_from_slice(&build_ogg_page(0, &[b"OpusTags_tail"], false));
        opus_v2.extend_from_slice(&build_ogg_page(960, &[opus_audio], false));

        let ofp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&opus_v1)).unwrap();
        let ofp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&opus_v2)).unwrap();
        assert_eq!(ofp1.audio_frame_blake3_hex, ofp2.audio_frame_blake3_hex);
        assert_ne!(ofp1.header_blake3_hex, ofp2.header_blake3_hex);
    }

    #[test]
    fn mp3_trailing_id3v1_and_apev2_tags_are_stripped_from_audio_fingerprint() {
        let audio = b"RAW_MP3_FRAMES_0123456789ABCDEF_PAYLOAD";

        let mut ape_footer = [0u8; 32];
        ape_footer[0..8].copy_from_slice(b"APETAGEX");
        ape_footer[8..12].copy_from_slice(&2000u32.to_le_bytes());
        let ape_item = b"\x05\x00\x00\x00\x00\x00\x00\x00TITLE\x00Hello";
        let ape_size = (32 + ape_item.len()) as u32;
        ape_footer[12..16].copy_from_slice(&ape_size.to_le_bytes());
        ape_footer[16..20].copy_from_slice(&1u32.to_le_bytes());
        ape_footer[20..24].copy_from_slice(&0x8000_0000u32.to_le_bytes()); // has header, this is footer

        let mut ape_header = ape_footer;
        ape_header[20..24].copy_from_slice(&0xA000_0000u32.to_le_bytes()); // header flag

        let mut id3v1 = [0u8; 128];
        id3v1[0..3].copy_from_slice(b"TAG");
        id3v1[3..8].copy_from_slice(b"Track");

        let mut tagged = Vec::new();
        tagged.extend_from_slice(audio);
        tagged.extend_from_slice(&ape_header);
        tagged.extend_from_slice(ape_item);
        tagged.extend_from_slice(&ape_footer);
        tagged.extend_from_slice(&id3v1);

        let fp_plain = compute_audio_frame_fingerprint(&mut Cursor::new(audio)).unwrap();
        let fp_tagged = compute_audio_frame_fingerprint(&mut Cursor::new(&tagged)).unwrap();
        assert_eq!(fp_plain.audio_frame_blake3_hex, fp_tagged.audio_frame_blake3_hex);
        assert_ne!(fp_plain.header_blake3_hex, fp_tagged.header_blake3_hex);
    }

    #[test]
    fn dsf_and_aiff_audio_fingerprints_are_tag_invariant() {
        // Synthetic DSF with and without trailing ID3v2 pointed to by metadata_offset
        let dsd_samples = b"DSD_AUDIO_STREAM_BYTES_0123456789";
        let make_dsf = |id3_tail: &[u8]| {
            let fmt_chunk_len = 52u64;
            let data_chunk_len = 12u64 + dsd_samples.len() as u64;
            let meta_offset = if id3_tail.is_empty() { 0u64 } else { 28 + fmt_chunk_len + data_chunk_len };
            let total_size = 28 + fmt_chunk_len + data_chunk_len + id3_tail.len() as u64;
            let mut out = Vec::new();
            out.extend_from_slice(b"DSD ");
            out.extend_from_slice(&28u64.to_le_bytes());
            out.extend_from_slice(&total_size.to_le_bytes());
            out.extend_from_slice(&meta_offset.to_le_bytes());
            out.extend_from_slice(b"fmt ");
            out.extend_from_slice(&fmt_chunk_len.to_le_bytes());
            out.extend_from_slice(&[0u8; 40]);
            out.extend_from_slice(b"data");
            out.extend_from_slice(&data_chunk_len.to_le_bytes());
            out.extend_from_slice(dsd_samples);
            out.extend_from_slice(id3_tail);
            out
        };
        let dsf1 = make_dsf(b"");
        let dsf2 = make_dsf(b"ID3_TAIL_METADATA_V2");
        let dfp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&dsf1)).unwrap();
        let dfp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&dsf2)).unwrap();
        assert_eq!(dfp1.audio_frame_blake3_hex, dfp2.audio_frame_blake3_hex);
        assert_ne!(dfp1.header_blake3_hex, dfp2.header_blake3_hex);

        // Synthetic AIFF with NAME + trailing ID3 chunks
        let ssnd_samples = b"AIFF_PCM_SAMPLE_FRAMES";
        let make_aiff = |name_bytes: &[u8], id3_bytes: &[u8]| {
            let mut body = Vec::new();
            body.extend_from_slice(b"COMM");
            body.extend_from_slice(&18u32.to_be_bytes());
            body.extend_from_slice(&[0u8; 18]);

            body.extend_from_slice(b"NAME");
            body.extend_from_slice(&(name_bytes.len() as u32).to_be_bytes());
            body.extend_from_slice(name_bytes);
            if name_bytes.len() % 2 == 1 {
                body.push(0);
            }

            let ssnd_len = (8 + ssnd_samples.len()) as u32;
            body.extend_from_slice(b"SSND");
            body.extend_from_slice(&ssnd_len.to_be_bytes());
            body.extend_from_slice(&[0u8; 8]); // offset + blockSize
            body.extend_from_slice(ssnd_samples);
            if ssnd_samples.len() % 2 == 1 {
                body.push(0);
            }

            if !id3_bytes.is_empty() {
                body.extend_from_slice(b"ID3 ");
                body.extend_from_slice(&(id3_bytes.len() as u32).to_be_bytes());
                body.extend_from_slice(id3_bytes);
                if id3_bytes.len() % 2 == 1 {
                    body.push(0);
                }
            }

            let mut out = Vec::new();
            out.extend_from_slice(b"FORM");
            out.extend_from_slice(&((4 + body.len()) as u32).to_be_bytes());
            out.extend_from_slice(b"AIFF");
            out.extend_from_slice(&body);
            out
        };
        let aiff1 = make_aiff(b"A1", b"");
        let aiff2 = make_aiff(b"RenamedTitle", b"ID3_TAG_V2");
        let afp1 = compute_audio_frame_fingerprint(&mut Cursor::new(&aiff1)).unwrap();
        let afp2 = compute_audio_frame_fingerprint(&mut Cursor::new(&aiff2)).unwrap();
        assert_eq!(afp1.audio_frame_blake3_hex, afp2.audio_frame_blake3_hex);
        assert_ne!(afp1.header_blake3_hex, afp2.header_blake3_hex);
    }
}
