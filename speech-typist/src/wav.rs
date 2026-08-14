//! The RIFF header whisper-server needs, wrapped around mono 16-bit PCM.
//!
//! Only WAV is ever sent. The speech typist captures its own audio, so there is no
//! sender-supplied media type to distrust, nothing to sniff and no Opus to decode — the three
//! problems the .NET side's `AudioContainer` exists to solve are problems this does not have.

pub const HEADER_BYTES: usize = 44;

/// Mono, 16-bit, at whatever rate the device opened. No resampler, matching what
/// `WavAudio.FromPcm` on the .NET side already does by taking whatever rate it was handed.
pub fn from_pcm(samples: &[i16], sample_rate: u32) -> Vec<u8> {
    let channels: u16 = 1;
    let bits: u16 = 16;
    let data_len = samples.len() * 2;
    let block_align = channels * bits / 8;
    let byte_rate = sample_rate * block_align as u32;

    let mut wav = Vec::with_capacity(HEADER_BYTES + data_len);
    wav.extend_from_slice(b"RIFF");
    wav.extend_from_slice(&((36 + data_len) as u32).to_le_bytes());
    wav.extend_from_slice(b"WAVE");
    wav.extend_from_slice(b"fmt ");
    wav.extend_from_slice(&16u32.to_le_bytes());
    wav.extend_from_slice(&1u16.to_le_bytes()); // PCM
    wav.extend_from_slice(&channels.to_le_bytes());
    wav.extend_from_slice(&sample_rate.to_le_bytes());
    wav.extend_from_slice(&byte_rate.to_le_bytes());
    wav.extend_from_slice(&block_align.to_le_bytes());
    wav.extend_from_slice(&bits.to_le_bytes());
    wav.extend_from_slice(b"data");
    wav.extend_from_slice(&(data_len as u32).to_le_bytes());
    wav.extend(samples.iter().flat_map(|s| s.to_le_bytes()));
    wav
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn declares_the_rate_the_device_opened_at_rather_than_resampling() {
        let wav = from_pcm(&[1, -1, 2], 44_100);

        assert_eq!(&wav[0..4], b"RIFF");
        assert_eq!(&wav[8..12], b"WAVE");
        assert_eq!(u32::from_le_bytes(wav[24..28].try_into().unwrap()), 44_100);
        assert_eq!(u16::from_le_bytes(wav[22..24].try_into().unwrap()), 1, "mono");
        assert_eq!(u16::from_le_bytes(wav[34..36].try_into().unwrap()), 16);
        assert_eq!(u32::from_le_bytes(wav[40..44].try_into().unwrap()), 6);
        assert_eq!(wav.len(), HEADER_BYTES + 6);
    }

    #[test]
    fn riff_size_counts_everything_after_the_first_eight_bytes() {
        let wav = from_pcm(&[0; 10], 16_000);

        let declared = u32::from_le_bytes(wav[4..8].try_into().unwrap()) as usize;
        assert_eq!(declared, wav.len() - 8);
    }
}
