//! The Windows implementation of the host port, and the message loop it needs.
//!
//! Everything in here is verified by hand on a real machine. That is what the port exists for:
//! these implementations stay thin enough to read, and there is nothing in them worth faking a
//! Windows API to test. What can be wrong quietly — the key decision table, device resolution,
//! prompt composition, segmenting — lives outside this module and is tested in WSL.

mod autostart;
mod capture;
mod cue;
mod hook;
mod inject;
mod tray;

use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use windows::core::w;
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetForegroundWindow, GetMessageW,
    KillTimer, PostQuitMessage, RegisterClassW, SetTimer, TranslateMessage, UnhookWindowsHookEx,
    HMENU, HWND_MESSAGE, MSG, WINDOW_EX_STYLE, WINDOW_STYLE, WM_COMMAND, WM_DESTROY, WM_RBUTTONUP,
    WM_TIMER, WNDCLASSW,
};

use crate::config::Config;
use crate::core::Session;
use crate::host::{
    CaptureFormat, Cue, Host, HostError, HostEvent, Injection, KeyCode, TranscribeError,
    Transcript, TranscriptionRequest, TrayState, WindowId,
};
use crate::lemonade::LemonadeClient;

/// How often the message loop stamps the clock the watchdog reads, and drains what the core asked
/// the tray to do. 100 ms is far below any deadline here and costs nothing while idle.
const TICK_MS: u32 = 100;
const TIMER_ID: usize = 1;
/// Posted to the loop when the core has something for the tray, so a notification does not wait
/// out the tick.
const WM_UI: u32 = windows::Win32::UI::WindowsAndMessaging::WM_APP + 2;

/// What the core asked the tray to do. The tray icon belongs to the thread that created it, so
/// these cross by queue rather than by call.
enum UiCommand {
    Tray(TrayState),
    Notify(String),
}

#[derive(Default)]
struct Outbox {
    pending: Mutex<Vec<UiCommand>>,
    hwnd: Mutex<isize>,
}

impl Outbox {
    fn push(&self, command: UiCommand) {
        self.pending.lock().unwrap().push(command);
        let hwnd = *self.hwnd.lock().unwrap();
        if hwnd != 0 {
            unsafe {
                let _ = windows::Win32::UI::WindowsAndMessaging::PostMessageW(
                    HWND(hwnd as *mut std::ffi::c_void),
                    WM_UI,
                    WPARAM(0),
                    LPARAM(0),
                );
            }
        }
    }

    fn drain(&self) -> Vec<UiCommand> {
        std::mem::take(&mut *self.pending.lock().unwrap())
    }
}

struct WindowsHost {
    capture: capture::Capture,
    cues: cue::Cues,
    cues_enabled: bool,
    lemonade: LemonadeClient,
    outbox: Arc<Outbox>,
}

#[async_trait]
impl Host for WindowsHost {
    fn open_capture(&self) -> Result<CaptureFormat, HostError> {
        self.capture.open()
    }

    fn close_capture(&self) {
        self.capture.close();
    }

    fn foreground_window(&self) -> WindowId {
        WindowId(unsafe { GetForegroundWindow() }.0 as u64)
    }

    fn inject(&self, injection: Injection<'_>) -> Result<(), HostError> {
        inject::inject(injection)
    }

    fn set_bindings(&self, keys: &[KeyCode]) {
        hook::set_bindings(keys);
    }

    fn set_tray(&self, state: TrayState) {
        self.outbox.push(UiCommand::Tray(state));
    }

    fn play_cue(&self, cue: Cue) {
        if self.cues_enabled {
            self.cues.play(cue);
        }
    }

    fn notify(&self, message: &str) {
        self.outbox.push(UiCommand::Notify(message.to_string()));
    }

    async fn transcribe(
        &self,
        request: TranscriptionRequest,
    ) -> Result<Transcript, TranscribeError> {
        self.lemonade.transcribe(&request).await
    }
}

/// What the window procedure needs, on the thread that owns it.
struct Ui {
    tray: tray::Tray,
    languages: Vec<String>,
    devices: Vec<String>,
    events: tokio::sync::mpsc::Sender<HostEvent>,
    outbox: Arc<Outbox>,
    started: std::time::Instant,
}

// Thread-local rather than a static: the tray icon and the window belong to the thread that
// created them, and the window procedure is the only thing that ever touches this.
thread_local! {
    static UI: std::cell::RefCell<Option<Ui>> = const { std::cell::RefCell::new(None) };
}

pub fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "speech_typist=info".into()),
        )
        .init();

    let (beside_exe, in_profile) = crate::config::locations();
    let loaded = crate::config::load(&beside_exe, &in_profile)?;
    tracing::info!(path = %loaded.path.display(), first_run = loaded.written, "config");
    let config = loaded.config.clone();

    // Frames and key events share this queue. It is sized so a transient stall in the core cannot
    // eat a key-up: dropping one would leave the microphone open until the watchdog closed it.
    let (events, inbox) = tokio::sync::mpsc::channel(1_024);
    let outbox = Arc::new(Outbox::default());
    let host = build_host(&config, events.clone(), outbox.clone());

    // The core runs off the UI thread: the message loop must stay answering, because a hook that
    // stops pumping is a hook Windows removes.
    let session = Session {
        config: config.clone(),
        config_path: loaded.path,
        first_run: loaded.written,
    };
    let core = std::thread::Builder::new().name("speech-typist-core".into()).spawn(move || {
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("a tokio runtime");
        runtime.block_on(crate::core::run(host, inbox, session));
    })?;

    run_message_loop(&config, events, outbox)?;
    let _ = core.join();
    Ok(())
}

fn build_host(
    config: &Config,
    events: tokio::sync::mpsc::Sender<HostEvent>,
    outbox: Arc<Outbox>,
) -> Arc<dyn Host> {
    Arc::new(WindowsHost {
        capture: capture::Capture::start(config.audio.device_name.clone(), events),
        cues: cue::Cues::new(),
        cues_enabled: config.audio.cues.enabled,
        lemonade: LemonadeClient::new(
            &config.lemonade.base_url,
            &config.lemonade.model,
            std::time::Duration::from_secs(config.lemonade.request_timeout_secs),
        ),
        outbox,
    })
}

fn run_message_loop(
    config: &Config,
    events: tokio::sync::mpsc::Sender<HostEvent>,
    outbox: Arc<Outbox>,
) -> anyhow::Result<()> {
    let instance = unsafe { GetModuleHandleW(None) }?;
    let class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: HINSTANCE(instance.0),
        lpszClassName: w!("SpeechTypistMessageWindow"),
        ..Default::default()
    };
    unsafe { RegisterClassW(&class) };

    // HWND_MESSAGE: never shown, never painted, cannot be focused. A window that can take focus
    // can break injection into the window underneath, which is why there is no other one.
    let hwnd = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE::default(),
            w!("SpeechTypistMessageWindow"),
            w!("speech typist"),
            WINDOW_STYLE::default(),
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            HMENU::default(),
            HINSTANCE(instance.0),
            None,
        )
    }?;
    *outbox.hwnd.lock().unwrap() = hwnd.0 as isize;

    let tray = tray::Tray::new(hwnd)?;
    let ui = Ui {
        tray,
        languages: config.bindings.iter().map(|b| b.language.clone()).collect(),
        devices: capture::device_names(),
        events: events.clone(),
        outbox,
        started: std::time::Instant::now(),
    };
    UI.with(|slot| *slot.borrow_mut() = Some(ui));

    let hook = hook::install(events.clone())?;
    unsafe { SetTimer(hwnd, TIMER_ID, TICK_MS, None) };

    let mut message = MSG::default();
    while unsafe { GetMessageW(&mut message, HWND::default(), 0, 0) }.as_bool() {
        unsafe {
            let _ = TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    unsafe {
        let _ = KillTimer(hwnd, TIMER_ID);
        let _ = UnhookWindowsHookEx(hook);
    }
    let _ = events.blocking_send(HostEvent::Quit);
    Ok(())
}

extern "system" fn window_proc(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match message {
        WM_TIMER => {
            with_ui(|ui| {
                let at_ms = ui.started.elapsed().as_millis() as u64;
                let _ = ui.events.try_send(HostEvent::Tick { at_ms });
                apply(ui);
            });
            LRESULT(0)
        }
        WM_UI => {
            with_ui(apply);
            LRESULT(0)
        }
        tray::WM_TRAY => {
            if lparam.0 as u32 == WM_RBUTTONUP {
                with_ui(|ui| {
                    let autostart = autostart::is_on();
                    tray::show_menu(hwnd, &ui.languages, &ui.devices, autostart);
                });
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            on_command((wparam.0 & 0xFFFF) as usize);
            LRESULT(0)
        }
        WM_DESTROY => {
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, message, wparam, lparam) },
    }
}

fn on_command(id: usize) {
    match id {
        tray::ID_QUIT => unsafe { PostQuitMessage(0) },
        tray::ID_AUTOSTART => {
            let wanted = !autostart::is_on();
            if let Err(error) = autostart::set(wanted) {
                with_ui(|ui| ui.tray.notify(&format!("Could not change autostart: {error}")));
            }
        }
        id if (tray::ID_LEARN_FIRST..tray::ID_DEVICE_FIRST).contains(&id) => {
            let binding = id - tray::ID_LEARN_FIRST;
            hook::learn(binding);
            with_ui(|ui| ui.tray.notify("Press the key you want for this binding."));
        }
        _ => {}
    }
}

fn apply(ui: &mut Ui) {
    for command in ui.outbox.drain() {
        match command {
            UiCommand::Tray(state) => ui.tray.set_state(state),
            UiCommand::Notify(message) => ui.tray.notify(&message),
        }
    }
}

fn with_ui(action: impl FnOnce(&mut Ui)) {
    UI.with(|slot| {
        if let Ok(mut slot) = slot.try_borrow_mut() {
            if let Some(ui) = slot.as_mut() {
                action(ui);
            }
        }
    });
}
