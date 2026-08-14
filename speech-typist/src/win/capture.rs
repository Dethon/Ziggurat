//! The microphone, opened on key-down and closed on key-up.
//!
//! There is deliberately no pre-roll ring, unlike the satellite: this trades the first 50-200 ms
//! of a dictation for not holding the device and not showing Windows' permanent microphone
//! indicator. That is the choice most likely to want revisiting after real use.
//!
//! cpal's stream is not `Send` on WASAPI, so it lives on a thread of its own that takes commands.

use std::sync::mpsc::{channel, Receiver, Sender};

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use tokio::sync::mpsc::Sender as EventSender;

use crate::audio::choose_device;
use crate::host::{CaptureFormat, HostError, HostEvent};

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

fn run(orders: Receiver<Command>, device_name: String, events: EventSender<HostEvent>) {
    // Holding the stream is what keeps the device open; dropping it is what closes it. There is
    // nothing else to read it for.
    let mut held: Option<cpal::Stream> = None;
    while let Ok(command) = orders.recv() {
        match command {
            Command::Open(reply) => {
                drop(held.take());
                let answer = match build(&device_name, events.clone()) {
                    Ok((stream, format)) => {
                        held = Some(stream);
                        Ok(format)
                    }
                    Err(error) => Err(error),
                };
                let _ = reply.send(answer);
            }
            Command::Close => drop(held.take()),
        }
    }
}

fn build(
    device_name: &str,
    events: EventSender<HostEvent>,
) -> Result<(cpal::Stream, CaptureFormat), HostError> {
    let host = cpal::default_host();
    let device = resolve(&host, device_name)?;
    let config = device
        .default_input_config()
        .map_err(|e| HostError(format!("the microphone has no usable input format: {e}")))?;
    let format = CaptureFormat { sample_rate: config.sample_rate().0 };
    let channels = config.channels() as usize;

    let on_error = |error| tracing::warn!(%error, "capture stream error");
    let stream = match config.sample_format() {
        cpal::SampleFormat::I16 => device.build_input_stream(
            &config.into(),
            move |data: &[i16], _: &_| forward(&events, downmix_i16(data, channels)),
            on_error,
            None,
        ),
        cpal::SampleFormat::F32 => device.build_input_stream(
            &config.into(),
            move |data: &[f32], _: &_| forward(&events, downmix_f32(data, channels)),
            on_error,
            None,
        ),
        other => return Err(HostError(format!("unsupported sample format {other:?}"))),
    }
    .map_err(|e| HostError(format!("could not open the microphone: {e}")))?;

    stream.play().map_err(|e| HostError(format!("could not start the microphone: {e}")))?;
    Ok((stream, format))
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
