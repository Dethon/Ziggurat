//! The microphone: prepared while idle, started on key-down, stopped on key-up.
//!
//! Setting a WASAPI stream up costs about 350 ms on a wireless headset, every time, and that was
//! the whole delay between pressing the key and hearing the cue. Starting a stream that is already
//! set up costs under 20 ms. So a stream is prepared at startup and kept after each dictation —
//! set up but stopped, which delivers no audio and does not light Windows' microphone indicator —
//! and a key-down only starts it. [`crate::audio::how_to_start`] decides when it has to be set up
//! again instead.
//!
//! Opening answers once audio is actually flowing, not when the call returns, because the cue
//! that follows means "speak now": a remote desktop session's microphone, for one, delivers
//! nothing for half a second after it starts.
//!
//! cpal's stream is not `Send` on WASAPI, so it lives on a thread of its own that takes commands.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::Arc;
use std::time::Duration;

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use tokio::sync::mpsc::Sender as EventSender;

use crate::audio::{choose_device, how_to_start, Prepared, Start};
use crate::host::{CaptureFormat, HostError, HostEvent};

/// How long a started stream may take to deliver its first audio before it is given up on. A
/// remote desktop microphone takes about 500 ms on its first start; a stream that is silent for
/// longer than this is treated as dead and set up again.
const FIRST_AUDIO: Duration = Duration::from_millis(1_000);

enum Command {
    Open(Sender<Result<CaptureFormat, HostError>>),
    Close,
}

/// The handle the host holds. Dropping it stops the capture thread.
pub struct Capture {
    commands: Sender<Command>,
}

impl Capture {
    pub fn start(device_name: String, events: EventSender<HostEvent>) -> Self {
        let (commands, orders) = channel();
        std::thread::spawn(move || run(orders, device_name, events));
        Self { commands }
    }

    pub fn open(&self) -> Result<CaptureFormat, HostError> {
        let (reply, answer) = channel();
        self.commands
            .send(Command::Open(reply))
            .map_err(|_| HostError("the capture thread is gone".into()))?;
        answer.recv().map_err(|_| HostError("the capture thread did not answer".into()))?
    }

    pub fn close(&self) {
        let _ = self.commands.send(Command::Close);
    }
}

/// A stream that is set up, running or stopped. Holding it is what keeps it set up; dropping it
/// releases the device.
struct Held {
    stream: cpal::Stream,
    device: String,
    format: CaptureFormat,
    /// Set by the stream's error callback — the device going away under it, typically.
    failed: Arc<AtomicBool>,
    /// Armed before each start; the audio callback disarms it and says so on `flowing`.
    armed: Arc<AtomicBool>,
    flowing: Receiver<()>,
}

impl Held {
    /// Starts it, and answers whether audio arrived within [`FIRST_AUDIO`].
    fn start(&self) -> bool {
        while self.flowing.try_recv().is_ok() {}
        self.armed.store(true, Ordering::SeqCst);
        if self.stream.play().is_err() {
            return false;
        }
        self.flowing.recv_timeout(FIRST_AUDIO).is_ok()
    }
}

fn run(orders: Receiver<Command>, device_name: String, events: EventSender<HostEvent>) {
    // Prepared at startup, so the first dictation is as quick as every other one. A failure here
    // is not reported: the key-down that needs the microphone tries again, and says why.
    let mut held = resolve(&cpal::default_host(), &device_name)
        .and_then(|device| build(device, events.clone()))
        .ok();
    while let Ok(command) = orders.recv() {
        match command {
            Command::Open(reply) => {
                let _ = reply.send(open(&mut held, &device_name, &events));
            }
            // Stopped rather than dropped, so the next key-down only has to start it. A stream
            // that cannot even be stopped is let go; the next key-down sets one up again.
            Command::Close => {
                if held.as_ref().is_some_and(|held| held.stream.pause().is_err()) {
                    held = None;
                }
            }
        }
    }
}

fn open(
    held: &mut Option<Held>,
    device_name: &str,
    events: &EventSender<HostEvent>,
) -> Result<CaptureFormat, HostError> {
    let device = resolve(&cpal::default_host(), device_name)?;
    let wanted = device.name().unwrap_or_default();
    let prepared = held.as_ref().map(|held| Prepared {
        device: &held.device,
        failed: held.failed.load(Ordering::SeqCst),
    });
    if how_to_start(prepared, &wanted) == Start::Resume {
        let started = held.as_ref().expect("resumed only when one is held");
        if started.start() {
            return Ok(started.format);
        }
    }

    // Released before the new one is set up, so the device is never asked for twice.
    drop(held.take());
    let fresh = build(device, events.clone())?;
    // Silent past the wait is not refused: the device is open, and refusing would only turn a
    // slow microphone into a failed dictation.
    if !fresh.start() && fresh.failed.load(Ordering::SeqCst) {
        return Err(HostError("the microphone stopped as soon as it started".into()));
    }
    let format = fresh.format;
    *held = Some(fresh);
    Ok(format)
}

fn build(device: cpal::Device, events: EventSender<HostEvent>) -> Result<Held, HostError> {
    let name = device.name().unwrap_or_default();
    let config = device
        .default_input_config()
        .map_err(|e| HostError(format!("the microphone has no usable input format: {e}")))?;
    let format = CaptureFormat { sample_rate: config.sample_rate().0 };
    let channels = config.channels() as usize;

    let failed = Arc::new(AtomicBool::new(false));
    let armed = Arc::new(AtomicBool::new(false));
    let (signal, flowing) = channel();
    // The callback is on a real-time audio thread with no route to the tray, so an error is only
    // remembered: the next key-down sees it and sets up a stream again.
    let on_error = {
        let failed = failed.clone();
        move |_| failed.store(true, Ordering::SeqCst)
    };
    let first = {
        let armed = armed.clone();
        move || {
            if armed.swap(false, Ordering::SeqCst) {
                let _ = signal.send(());
            }
        }
    };
    let stream = match config.sample_format() {
        cpal::SampleFormat::I16 => device.build_input_stream(
            &config.into(),
            move |data: &[i16], _: &_| {
                first();
                forward(&events, downmix_i16(data, channels))
            },
            on_error,
            None,
        ),
        cpal::SampleFormat::F32 => device.build_input_stream(
            &config.into(),
            move |data: &[f32], _: &_| {
                first();
                forward(&events, downmix_f32(data, channels))
            },
            on_error,
            None,
        ),
        other => return Err(HostError(format!("unsupported sample format {other:?}"))),
    }
    .map_err(|e| HostError(format!("could not open the microphone: {e}")))?;

    Ok(Held { stream, device: name, format, failed, armed, flowing })
}

fn resolve(host: &cpal::Host, device_name: &str) -> Result<cpal::Device, HostError> {
    if device_name.trim().is_empty() {
        return host
            .default_input_device()
            .ok_or_else(|| HostError("this machine has no default microphone".into()));
    }
    let devices: Vec<cpal::Device> = host
        .input_devices()
        .map_err(|e| HostError(format!("could not list the microphones: {e}")))?
        .collect();
    let names: Vec<String> =
        devices.iter().map(|d| d.name().unwrap_or_else(|_| "<unnamed>".into())).collect();
    let chosen = choose_device(&names, device_name).map_err(HostError)?;
    match chosen {
        Some(index) => Ok(devices.into_iter().nth(index).expect("the index came from this list")),
        None => host
            .default_input_device()
            .ok_or_else(|| HostError("this machine has no default microphone".into())),
    }
}

/// The names Windows knows the capture devices by, for the tray's device list — which is how a
/// person finds the fragment the config wants without guessing.
pub fn device_names() -> Vec<String> {
    cpal::default_host()
        .input_devices()
        .map(|devices| devices.filter_map(|d| d.name().ok()).collect())
        .unwrap_or_default()
}

/// Dropped rather than blocked: the audio callback runs on a real-time thread, and a full channel
/// means the core is already behind by more than a dictation's worth of frames.
fn forward(events: &EventSender<HostEvent>, frame: Vec<i16>) {
    let _ = events.try_send(HostEvent::Frame(frame));
}

fn downmix_i16(data: &[i16], channels: usize) -> Vec<i16> {
    if channels <= 1 {
        return data.to_vec();
    }
    data.chunks_exact(channels)
        .map(|frame| (frame.iter().map(|&s| s as i32).sum::<i32>() / channels as i32) as i16)
        .collect()
}

fn downmix_f32(data: &[f32], channels: usize) -> Vec<i16> {
    let to_i16 = |sample: f32| (sample.clamp(-1.0, 1.0) * i16::MAX as f32) as i16;
    if channels <= 1 {
        return data.iter().copied().map(to_i16).collect();
    }
    data.chunks_exact(channels)
        .map(|frame| to_i16(frame.iter().sum::<f32>() / channels as f32))
        .collect()
}
