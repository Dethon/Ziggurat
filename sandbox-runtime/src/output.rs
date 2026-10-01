//! A stream's output, capped the way the in-process runner caps it: at most `cap` bytes of UTF-8,
//! never splitting a character, with a flag saying whether anything was dropped. The rest of the
//! stream is still drained, so a chatty command cannot block on a full pipe.

pub struct CappedOutput {
    cap: usize,
    bytes: Vec<u8>,
    truncated: bool,
}

impl CappedOutput {
    pub fn new(cap: usize) -> Self {
        Self { cap, bytes: Vec::new(), truncated: false }
    }

    pub fn push(&mut self, chunk: &[u8]) {
        let room = self.cap.saturating_sub(self.bytes.len());
        if chunk.len() > room {
            self.truncated = true;
        }
        self.bytes.extend_from_slice(&chunk[..chunk.len().min(room)]);
    }

    pub fn truncated(&self) -> bool {
        self.truncated
    }

    /// The text, with a character the cap cut in half dropped rather than mangled. Invalid UTF-8
    /// the command itself wrote is replaced, as reading it into a .NET string would.
    pub fn into_text(self) -> String {
        let end = if self.truncated { boundary(&self.bytes) } else { self.bytes.len() };
        String::from_utf8_lossy(&self.bytes[..end]).into_owned()
    }
}

// The longest prefix that does not end inside a multi-byte character.
fn boundary(bytes: &[u8]) -> usize {
    let tail_start = bytes.len().saturating_sub(3);
    (tail_start..bytes.len())
        .rev()
        .find(|&i| bytes[i] & 0b1100_0000 != 0b1000_0000)
        .filter(|&lead| {
            let width = match bytes[lead] {
                b if b < 0x80 => 1,
                b if b >= 0xF0 => 4,
                b if b >= 0xE0 => 3,
                b if b >= 0xC0 => 2,
                _ => 1,
            };
            lead + width > bytes.len()
        })
        .unwrap_or(bytes.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn under_the_cap_everything_is_kept() {
        let mut out = CappedOutput::new(16);
        out.push(b"hello\n");

        assert!(!out.truncated());
        assert_eq!(out.into_text(), "hello\n");
    }

    #[test]
    fn over_the_cap_the_rest_is_dropped_and_flagged() {
        let mut out = CappedOutput::new(4);
        out.push(b"hel");
        out.push(b"lo world");

        assert!(out.truncated());
        assert_eq!(out.into_text(), "hell");
    }

    #[test]
    fn a_character_the_cap_cuts_in_half_is_dropped_whole() {
        let mut out = CappedOutput::new(4);
        out.push("abñ".as_bytes()); // 4 bytes: fits exactly
        out.push("é".as_bytes());

        assert!(out.truncated());
        assert_eq!(out.into_text(), "abñ");

        let mut cut = CappedOutput::new(3);
        cut.push("abñ".as_bytes()); // ñ is two bytes, only one fits

        assert!(cut.truncated());
        assert_eq!(cut.into_text(), "ab");
    }

    #[test]
    fn exactly_at_the_cap_is_not_truncated() {
        let mut out = CappedOutput::new(5);
        out.push(b"hello");

        assert!(!out.truncated());
        assert_eq!(out.into_text(), "hello");
    }
}
