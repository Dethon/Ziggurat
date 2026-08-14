//! The Lemonade transcription client, in Rust.
//!
//! `docs/adr/0026-the-speech-typist-talks-to-lemonade-directly.md` records why this contract
//! exists twice. What is duplicated is the multipart shape, `verbose_json`, and reading the two
//! quality signals back out; what is deliberately not duplicated is container sniffing, Opus
//! decoding and media-type negotiation, because the speech typist captures its own audio and
//! sends WAV and nothing else.

use std::time::Duration;

use serde_json::Value;

use crate::host::{TranscribeError, Transcript, TranscriptionRequest};

pub struct LemonadeClient {
    http: reqwest::Client,
    endpoint: String,
    model: String,
    timeout: Duration,
}

impl LemonadeClient {
    pub fn new(base_url: &str, model: &str, timeout: Duration) -> Self {
        Self {
            http: reqwest::Client::new(),
            endpoint: format!("{}/audio/transcriptions", base_url.trim_end_matches('/')),
            model: model.to_string(),
            timeout,
        }
    }

    pub async fn transcribe(
        &self,
        request: &TranscriptionRequest,
    ) -> Result<Transcript, TranscribeError> {
        let boundary = "speech-typist-boundary";
        let body = multipart_body(boundary, &self.model, request);

        let response = self
            .http
            .post(&self.endpoint)
            .header("content-type", format!("multipart/form-data; boundary={boundary}"))
            .timeout(self.timeout)
            .body(body)
            .send()
            .await
            .map_err(to_error)?;

        let status = response.status().as_u16();
        if !(200..300).contains(&status) {
            return Err(TranscribeError::Status(status));
        }
        let text = response.text().await.map_err(to_error)?;
        parse_verbose_json(&text)
    }
}

/// Connect failures are asked about first: reqwest reports a refused connection as a timeout as
/// well, and "could not reach Lemonade" is the truer thing to say about a host that is down.
fn to_error(error: reqwest::Error) -> TranscribeError {
    if error.is_connect() {
        TranscribeError::Transport(error.to_string())
    } else if error.is_timeout() {
        TranscribeError::Timeout
    } else {
        TranscribeError::Transport(error.to_string())
    }
}

/// The multipart body, byte for byte. The audio part is named with a `.wav` extension because
/// whisper-server picks its decoder from the bytes but still refuses a part it cannot name.
pub fn multipart_body(boundary: &str, model: &str, request: &TranscriptionRequest) -> Vec<u8> {
    let mut body = Vec::new();
    let mut field = |name: &str, value: &str| {
        body.extend_from_slice(format!("--{boundary}\r\n").as_bytes());
        body.extend_from_slice(
            format!("Content-Disposition: form-data; name=\"{name}\"\r\n\r\n").as_bytes(),
        );
        body.extend_from_slice(value.as_bytes());
        body.extend_from_slice(b"\r\n");
    };

    field("model", model);
    field("response_format", "verbose_json");
    field("language", &request.language);
    if let Some(prompt) = &request.prompt {
        field("prompt", prompt);
    }

    body.extend_from_slice(format!("--{boundary}\r\n").as_bytes());
    body.extend_from_slice(
        b"Content-Disposition: form-data; name=\"file\"; filename=\"dictation.wav\"\r\n",
    );
    body.extend_from_slice(b"Content-Type: audio/wav\r\n\r\n");
    body.extend_from_slice(&request.wav);
    body.extend_from_slice(b"\r\n");
    body.extend_from_slice(format!("--{boundary}--\r\n").as_bytes());
    body
}

/// Reads back the text and the duration-weighted quality signals. A body carrying no segments —
/// the plain `json` shape — degrades to no signals rather than an error, and every gate on them
/// then fails open.
pub fn parse_verbose_json(body: &str) -> Result<Transcript, TranscribeError> {
    let json: Value = serde_json::from_str(body)
        .map_err(|e| TranscribeError::Malformed(format!("not JSON: {e}")))?;
    let text = json
        .get("text")
        .and_then(Value::as_str)
        .ok_or_else(|| TranscribeError::Malformed("no text field".into()))?;

    let segments: Vec<&Value> =
        json.get("segments").and_then(Value::as_array).map(|s| s.iter().collect()).unwrap_or_default();

    Ok(Transcript {
        text: text.to_string(),
        avg_logprob: weighted_mean(&segments, "avg_logprob"),
        no_speech_prob: weighted_mean(&segments, "no_speech_prob"),
    })
}

/// Segments differ in length, so a plain mean would let a short noise segment outvote long clean
/// speech. Weight by duration; a segment without the value abstains, which is what makes the gate
/// fail open rather than treating a missing signal as a bad one.
fn weighted_mean(segments: &[&Value], key: &str) -> Option<f64> {
    let pairs: Vec<(f64, f64)> = segments
        .iter()
        .filter_map(|segment| {
            let value = read_number(segment, key)?;
            let start = read_number(segment, "start").unwrap_or(0.0);
            let end = read_number(segment, "end").unwrap_or(0.0);
            Some(((end - start).max(1e-9), value))
        })
        .collect();
    if pairs.is_empty() {
        return None;
    }
    let total: f64 = pairs.iter().map(|(w, _)| w).sum();
    Some(pairs.iter().map(|(w, v)| w * v).sum::<f64>() / total)
}

/// Absent, malformed or non-finite means "no signal", never an error: this body comes from a
/// peer, so read it tolerantly.
fn read_number(json: &Value, key: &str) -> Option<f64> {
    let value = json.get(key)?.as_f64()?;
    value.is_finite().then_some(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;

    /// Stands in for Lemonade on loopback so the real client is exercised end to end. This is the
    /// test that protects the duplication `docs/adr/0026` knowingly accepted, so it asserts on the
    /// bytes actually sent rather than on an intermediate structure.
    struct FakeLemonade {
        base_url: String,
        captured: tokio::sync::oneshot::Receiver<Vec<u8>>,
    }

    enum Reply {
        Body(&'static str),
        Status(u16),
        Never,
    }

    impl FakeLemonade {
        async fn answering(reply: Reply) -> Self {
            let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            let (tx, captured) = tokio::sync::oneshot::channel();

            tokio::spawn(async move {
                let (mut socket, _) = listener.accept().await.unwrap();
                let mut request = Vec::new();
                let mut buffer = [0u8; 8192];
                loop {
                    let read = socket.read(&mut buffer).await.unwrap_or(0);
                    if read == 0 {
                        break;
                    }
                    request.extend_from_slice(&buffer[..read]);
                    if is_complete(&request) {
                        break;
                    }
                }
                let _ = tx.send(request);
                match reply {
                    Reply::Body(body) => {
                        let response = format!(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}",
                            body.len()
                        );
                        let _ = socket.write_all(response.as_bytes()).await;
                    }
                    Reply::Status(code) => {
                        let response =
                            format!("HTTP/1.1 {code} Nope\r\nContent-Length: 0\r\n\r\n");
                        let _ = socket.write_all(response.as_bytes()).await;
                    }
                    // Held open and silent: what a wedged whisper looks like from here.
                    Reply::Never => tokio::time::sleep(Duration::from_secs(30)).await,
                }
                let _ = socket.shutdown().await;
            });

            Self { base_url: format!("http://127.0.0.1:{port}/api/v1"), captured }
        }

        fn client(&self) -> LemonadeClient {
            LemonadeClient::new(&self.base_url, "Whisper-Base", Duration::from_secs(2))
        }

        async fn request_bytes(self) -> Vec<u8> {
            self.captured.await.unwrap()
        }

        async fn request(self) -> String {
            String::from_utf8_lossy(&self.request_bytes().await).into_owned()
        }
    }

    fn is_complete(request: &[u8]) -> bool {
        let text = String::from_utf8_lossy(request);
        let Some(head_end) = text.find("\r\n\r\n") else {
            return false;
        };
        let length: usize = text[..head_end]
            .lines()
            .find_map(|line| line.strip_prefix("content-length: ").or(line.strip_prefix("Content-Length: ")))
            .and_then(|value| value.trim().parse().ok())
            .unwrap_or(0);
        request.len() >= head_end + 4 + length
    }

    fn a_request() -> TranscriptionRequest {
        TranscriptionRequest {
            wav: crate::wav::from_pcm(&[1, 2, 3], 16_000),
            language: "es".into(),
            prompt: Some("Ziggurat, Lemonade".into()),
        }
    }

    const ONE_SEGMENT: &str = r#"{"text":" hola","segments":[
        {"start":0.0,"end":2.0,"avg_logprob":-0.3,"no_speech_prob":0.05}]}"#;

    #[tokio::test]
    async fn posts_at_the_openai_compatible_transcriptions_route() {
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;
        lemonade.client().transcribe(&a_request()).await.unwrap();

        let sent = lemonade.request().await;
        assert!(sent.starts_with("POST /api/v1/audio/transcriptions HTTP/1.1"), "{sent}");
    }

    #[tokio::test]
    async fn names_the_audio_part_with_a_wav_extension() {
        // whisper-server picks its decoder from the bytes, but it still refuses a part it cannot
        // name, so the extension has to match what the part carries.
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;
        lemonade.client().transcribe(&a_request()).await.unwrap();

        let sent = lemonade.request().await;
        assert!(
            sent.contains("Content-Disposition: form-data; name=\"file\"; filename=\"dictation.wav\""),
            "{sent}"
        );
        assert!(sent.contains("Content-Type: audio/wav"), "{sent}");
    }

    #[tokio::test]
    async fn sends_the_model_language_prompt_and_always_verbose_json() {
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;
        lemonade.client().transcribe(&a_request()).await.unwrap();

        let sent = lemonade.request().await;
        for field in ["model", "response_format", "language", "prompt"] {
            assert!(
                sent.contains(&format!("Content-Disposition: form-data; name=\"{field}\"")),
                "no {field} part in {sent}"
            );
        }
        assert!(sent.contains("multipart/form-data; boundary="), "{sent}");
        assert!(sent.contains("Whisper-Base"), "{sent}");
        assert!(sent.contains("verbose_json"), "{sent}");
        assert!(sent.contains("Ziggurat, Lemonade"), "{sent}");
    }

    #[tokio::test]
    async fn sends_the_audio_bytes_unaltered() {
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;
        let request = a_request();
        lemonade.client().transcribe(&request).await.unwrap();

        let sent = lemonade.request_bytes().await;
        let riff = sent.windows(4).position(|w| w == b"RIFF").expect("no RIFF in the body");
        assert_eq!(&sent[riff..riff + request.wav.len()], request.wav.as_slice());
    }

    #[tokio::test]
    async fn omits_the_prompt_part_when_there_is_no_prompt() {
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;
        let request = TranscriptionRequest { prompt: None, ..a_request() };
        lemonade.client().transcribe(&request).await.unwrap();

        let sent = lemonade.request().await;
        assert!(!sent.contains("name=\"prompt\""), "{sent}");
    }

    #[tokio::test]
    async fn reads_back_the_text_and_the_quality_signals() {
        let lemonade = FakeLemonade::answering(Reply::Body(ONE_SEGMENT)).await;

        let transcript = lemonade.client().transcribe(&a_request()).await.unwrap();

        assert_eq!(transcript.text, " hola");
        assert_eq!(transcript.avg_logprob, Some(-0.3));
        assert_eq!(transcript.no_speech_prob, Some(0.05));
    }

    #[tokio::test]
    async fn a_five_hundred_and_a_timeout_are_different_errors() {
        let refused = FakeLemonade::answering(Reply::Status(500)).await;
        assert_eq!(
            refused.client().transcribe(&a_request()).await.unwrap_err(),
            TranscribeError::Status(500)
        );

        let wedged = FakeLemonade::answering(Reply::Never).await;
        let client = LemonadeClient::new(&wedged.base_url, "Whisper-Base", Duration::from_millis(150));
        assert_eq!(client.transcribe(&a_request()).await.unwrap_err(), TranscribeError::Timeout);
    }

    #[tokio::test]
    async fn a_lemonade_that_cannot_be_reached_at_all_is_a_transport_error() {
        // A host that cannot resolve, rather than a port nothing listens on: a closed port answers
        // differently depending on what sits between here and it, and this failure is the one that
        // means "Lemonade is not there" in every environment.
        let client = LemonadeClient::new(
            "http://speech-typist.invalid:13305",
            "Whisper-Base",
            Duration::from_secs(2),
        );

        let error = client.transcribe(&a_request()).await.unwrap_err();
        assert!(matches!(error, TranscribeError::Transport(_)), "got {error:?}");
    }

    // ── verbose_json parsing, as a pure unit ───────────────────────────────────────────────────

    #[test]
    fn weights_the_signals_by_how_long_each_segment_lasted() {
        // A short noise segment must not outvote long clean speech, which a plain mean would let
        // it do: -0.2 over 9 s against -3.0 over 1 s averages plainly to -1.6 and by duration to
        // -0.48, and only one of those two is the truth about the audio.
        let body = r#"{"text":"x","segments":[
            {"start":0.0,"end":9.0,"avg_logprob":-0.2,"no_speech_prob":0.1},
            {"start":9.0,"end":10.0,"avg_logprob":-3.0,"no_speech_prob":0.9}]}"#;

        let transcript = parse_verbose_json(body).unwrap();

        assert!((transcript.avg_logprob.unwrap() - -0.48).abs() < 1e-9);
        assert!((transcript.no_speech_prob.unwrap() - 0.18).abs() < 1e-9);
    }

    #[test]
    fn a_body_with_no_segments_yields_no_signals_rather_than_an_error() {
        let transcript = parse_verbose_json(r#"{"text":"hola"}"#).unwrap();

        assert_eq!(transcript.text, "hola");
        assert_eq!(transcript.avg_logprob, None);
        assert_eq!(transcript.no_speech_prob, None);
    }

    #[test]
    fn a_segment_missing_a_signal_abstains_instead_of_voting_zero() {
        let body = r#"{"text":"x","segments":[
            {"start":0.0,"end":1.0,"avg_logprob":-0.5},
            {"start":1.0,"end":2.0,"no_speech_prob":0.4}]}"#;

        let transcript = parse_verbose_json(body).unwrap();

        assert_eq!(transcript.avg_logprob, Some(-0.5));
        assert_eq!(transcript.no_speech_prob, Some(0.4));
    }

    #[test]
    fn a_signal_that_is_a_string_or_not_finite_is_no_signal_at_all() {
        let body = r#"{"text":"x","segments":[
            {"start":0.0,"end":1.0,"avg_logprob":"bad","no_speech_prob":null}]}"#;

        let transcript = parse_verbose_json(body).unwrap();

        assert_eq!(transcript.avg_logprob, None);
        assert_eq!(transcript.no_speech_prob, None);
    }

    #[test]
    fn a_body_that_is_not_a_transcription_response_is_malformed() {
        assert!(matches!(parse_verbose_json("not json"), Err(TranscribeError::Malformed(_))));
        assert!(matches!(parse_verbose_json(r#"{"error":"nope"}"#), Err(TranscribeError::Malformed(_))));
    }
}
