use std::io::{self, Read, Seek, SeekFrom};

pub const WAVEFORM_MAGIC: [u8; 4] = *b"TWWF";
pub const WAVEFORM_VERSION: u16 = 1;
pub const WAVEFORM_POINTS: usize = 150;
const HEADER_SIZE: usize = 12;
const INDEX_ENTRY_SIZE: usize = 20;

pub struct WaveformArchive;

impl WaveformArchive {
    /// Encodes `(audio_frame_blake3_32, [u8; 150])` pairs into compact `TWWF` binary format
    /// sorted by 16-byte Blake3 prefix for O(log N) binary-search lookup.
    pub fn encode(entries: &[([u8; 32], [u8; WAVEFORM_POINTS])]) -> Vec<u8> {
        let mut items: Vec<([u8; 16], [u8; WAVEFORM_POINTS])> = entries
            .iter()
            .map(|(hash, points)| {
                let mut prefix = [0u8; 16];
                prefix.copy_from_slice(&hash[..16]);
                (prefix, *points)
            })
            .collect();
        items.sort_by_key(|a| a.0);
        items.dedup_by(|a, b| a.0 == b.0);

        let count = items.len();
        let mut out =
            Vec::with_capacity(HEADER_SIZE + count * (INDEX_ENTRY_SIZE + WAVEFORM_POINTS));

        out.extend_from_slice(&WAVEFORM_MAGIC);
        out.extend_from_slice(&WAVEFORM_VERSION.to_le_bytes());
        out.extend_from_slice(&(WAVEFORM_POINTS as u16).to_le_bytes());
        out.extend_from_slice(&(count as u32).to_le_bytes());

        for (idx, (prefix, _)) in items.iter().enumerate() {
            out.extend_from_slice(prefix);
            out.extend_from_slice(&(idx as u32).to_le_bytes());
        }
        for (_, points) in &items {
            out.extend_from_slice(points);
        }
        out
    }

    pub fn lookup_bytes(archive: &[u8], prefix16: &[u8; 16]) -> Option<[u8; WAVEFORM_POINTS]> {
        if archive.len() < HEADER_SIZE || archive[0..4] != WAVEFORM_MAGIC {
            return None;
        }
        let version = u16::from_le_bytes([archive[4], archive[5]]);
        let points = u16::from_le_bytes([archive[6], archive[7]]) as usize;
        let count = u32::from_le_bytes([archive[8], archive[9], archive[10], archive[11]]) as usize;
        if version != WAVEFORM_VERSION || points != WAVEFORM_POINTS {
            return None;
        }
        let payload_base = HEADER_SIZE + count * INDEX_ENTRY_SIZE;
        if archive.len() < payload_base + count * WAVEFORM_POINTS {
            return None;
        }

        let mut lo = 0usize;
        let mut hi = count;
        while lo < hi {
            let mid = lo + (hi - lo) / 2;
            let entry_off = HEADER_SIZE + mid * INDEX_ENTRY_SIZE;
            let mid_prefix = &archive[entry_off..entry_off + 16];
            match mid_prefix.cmp(prefix16.as_slice()) {
                std::cmp::Ordering::Less => lo = mid + 1,
                std::cmp::Ordering::Greater => hi = mid,
                std::cmp::Ordering::Equal => {
                    let idx = u32::from_le_bytes([
                        archive[entry_off + 16],
                        archive[entry_off + 17],
                        archive[entry_off + 18],
                        archive[entry_off + 19],
                    ]) as usize;
                    let p_off = payload_base + idx * WAVEFORM_POINTS;
                    let mut out = [0u8; WAVEFORM_POINTS];
                    out.copy_from_slice(&archive[p_off..p_off + WAVEFORM_POINTS]);
                    return Some(out);
                }
            }
        }
        None
    }

    pub fn lookup_reader<R: Read + Seek>(
        reader: &mut R,
        prefix16: &[u8; 16],
    ) -> io::Result<Option<[u8; WAVEFORM_POINTS]>> {
        reader.seek(SeekFrom::Start(0))?;
        let mut hdr = [0u8; HEADER_SIZE];
        if reader.read_exact(&mut hdr).is_err() || hdr[0..4] != WAVEFORM_MAGIC {
            return Ok(None);
        }
        let version = u16::from_le_bytes([hdr[4], hdr[5]]);
        let points = u16::from_le_bytes([hdr[6], hdr[7]]) as usize;
        let count = u32::from_le_bytes([hdr[8], hdr[9], hdr[10], hdr[11]]) as usize;
        if version != WAVEFORM_VERSION || points != WAVEFORM_POINTS || count == 0 {
            return Ok(None);
        }

        let payload_base = (HEADER_SIZE + count * INDEX_ENTRY_SIZE) as u64;
        let mut lo = 0usize;
        let mut hi = count;
        let mut entry = [0u8; INDEX_ENTRY_SIZE];

        while lo < hi {
            let mid = lo + (hi - lo) / 2;
            reader.seek(SeekFrom::Start((HEADER_SIZE + mid * INDEX_ENTRY_SIZE) as u64))?;
            reader.read_exact(&mut entry)?;
            match entry[0..16].cmp(prefix16.as_slice()) {
                std::cmp::Ordering::Less => lo = mid + 1,
                std::cmp::Ordering::Greater => hi = mid,
                std::cmp::Ordering::Equal => {
                    let idx = u32::from_le_bytes([entry[16], entry[17], entry[18], entry[19]])
                        as u64;
                    reader.seek(SeekFrom::Start(payload_base + idx * WAVEFORM_POINTS as u64))?;
                    let mut out = [0u8; WAVEFORM_POINTS];
                    reader.read_exact(&mut out)?;
                    return Ok(Some(out));
                }
            }
        }
        Ok(None)
    }
}

pub fn decode_hex_prefix16(hex: &str) -> Option<[u8; 16]> {
    let trimmed = hex.trim();
    if trimmed.len() < 32 {
        return None;
    }
    let mut out = [0u8; 16];
    for (i, slot) in out.iter_mut().enumerate() {
        *slot = u8::from_str_radix(&trimmed[i * 2..i * 2 + 2], 16).ok()?;
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn waveform_archive_encodes_and_binary_searches() {
        let mut h1 = [0u8; 32];
        h1[0] = 0xAA;
        let mut h2 = [0u8; 32];
        h2[0] = 0x11;
        let mut p1 = [0u8; 150];
        p1[0] = 42;
        p1[149] = 250;
        let mut p2 = [0u8; 150];
        p2[75] = 180;

        let encoded = WaveformArchive::encode(&[(h1, p1), (h2, p2)]);
        assert_eq!(encoded.len(), 12 + 2 * (20 + 150));

        let mut pref1 = [0u8; 16];
        pref1.copy_from_slice(&h1[..16]);
        let mut pref2 = [0u8; 16];
        pref2.copy_from_slice(&h2[..16]);
        let missing = [0xFFu8; 16];

        assert_eq!(WaveformArchive::lookup_bytes(&encoded, &pref1), Some(p1));
        assert_eq!(WaveformArchive::lookup_bytes(&encoded, &pref2), Some(p2));
        assert_eq!(WaveformArchive::lookup_bytes(&encoded, &missing), None);

        let mut cursor = Cursor::new(&encoded);
        assert_eq!(WaveformArchive::lookup_reader(&mut cursor, &pref1).unwrap(), Some(p1));
        assert_eq!(WaveformArchive::lookup_reader(&mut cursor, &pref2).unwrap(), Some(p2));
        assert_eq!(WaveformArchive::lookup_reader(&mut cursor, &missing).unwrap(), None);
    }
}
