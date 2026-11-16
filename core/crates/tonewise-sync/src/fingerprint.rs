use serde::{Deserialize, Serialize};
use std::io::{self, Read, Seek, SeekFrom};

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
pub fn compute_audio_frame_fingerprint<R: Read + Seek>(
    reader: &mut R,
) -> io::Result<AudioFrameFingerprint> {
    let file_size = reader.seek(SeekFrom::End(0))?;
    reader.seek(SeekFrom::Start(0))?;

    let span = detect_audio_span(reader, file_size)?;

    let mut header_hasher = blake3::Hasher::new();
    if span.start > 0 {
        reader.seek(SeekFrom::Start(0))?;
        hash_exact_len(reader, &mut header_hasher, span.start)?;
    }
    if span.end < file_size {
        reader.seek(SeekFrom::Start(span.end))?;
        hash_exact_len(reader, &mut header_hasher, file_size - span.end)?;
    }

    let mut audio_hasher = blake3::Hasher::new();
    if span.end > span.start {
        reader.seek(SeekFrom::Start(span.start))?;
        hash_exact_len(reader, &mut audio_hasher, span.end - span.start)?;
    }

    Ok(AudioFrameFingerprint {
        audio_frame_blake3_hex: audio_hasher.finalize().to_hex().to_string(),
        header_blake3_hex: header_hasher.finalize().to_hex().to_string(),
        header_size: span.start,
        file_size,
    })
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

    // Check MP4/M4A top-level `ftyp` + `mdat` atom span or RIFF WAVE `data` chunk when starting at 0
    if offset == 0 && file_size >= 12 {
        if let Some(wave_span) = find_riff_wave_data_span(reader, file_size)? {
            return Ok(wave_span);
        }
        if let Some(mdat_span) = find_mp4_mdat_span(reader, file_size)? {
            return Ok(mdat_span);
        }
    }

    offset = offset.min(file_size);
    Ok(AudioSpan {
        start: offset,
        end: file_size,
    })
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
    // Skip Identification (page 0) and Comment/Setup header pages (page 1)
    for _ in 0..2 {
        if pos + 27 > file_size {
            break;
        }
        reader.seek(SeekFrom::Start(pos))?;
        let mut page_hdr = [0u8; 27];
        reader.read_exact(&mut page_hdr)?;
        if &page_hdr[0..4] != b"OggS" {
            break;
        }
        let seg_count = page_hdr[26] as usize;
        if pos + 27 + seg_count as u64 > file_size {
            break;
        }
        let mut seg_table = vec![0u8; seg_count];
        reader.read_exact(&mut seg_table)?;
        let body_len: u64 = seg_table.iter().map(|&b| b as u64).sum();
        pos = (pos + 27 + seg_count as u64 + body_len).min(file_size);
    }
    Ok(pos)
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
        let end = (pos + total_size).min(file_size);
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
}
