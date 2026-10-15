import { useEffect, useRef, useState } from "react";
import { openFrameReader } from "../barcode";
import { canUseCamera, isDenied, openBackCamera, release } from "../camera";
import { coverCrop } from "../scanner";
import { useSettings } from "../settings";
import "./BarcodeScanner.css";

export type ScanProblem = "denied" | "unavailable";

// A camera can only be opened from a page served over HTTPS (or from localhost).
export const canScanLive = canUseCamera;

const SLOW_AFTER_MS = 7000;
const CONFIRMATION_MS = 350;
// Pauses between looks keep the page responsive while the decoder shares its thread.
const BREATH_MS = 30;

const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

// Resolves when the camera has a new picture, or shortly after if the browser cannot say.
function nextPicture(video: HTMLVideoElement): Promise<void> {
  if (typeof video.requestVideoFrameCallback !== "function") {
    return pause(80);
  }
  return Promise.race([new Promise<void>((resolve) => video.requestVideoFrameCallback(() => resolve())), pause(250)]);
}

// The light and the focus mode are capabilities of phone cameras that the standard types do
// not list yet, and Firefox has no getCapabilities at all.
type PhoneCapabilities = MediaTrackCapabilities & { torch?: boolean; focusMode?: string[] };

function capabilitiesOf(track: MediaStreamTrack): PhoneCapabilities | undefined {
  return (track as { getCapabilities?: () => PhoneCapabilities }).getCapabilities?.();
}

function switchLight(track: MediaStreamTrack, on: boolean): Promise<void> {
  return track.applyConstraints({ advanced: [{ torch: on } as MediaTrackConstraintSet] });
}

export function BarcodeScanner({
  onDetected,
  onProblem,
  onStop,
}: {
  onDetected: (code: string) => void;
  onProblem: (problem: ScanProblem) => void;
  onStop: () => void;
}) {
  const { t } = useSettings();
  const frame = useRef<HTMLDivElement>(null);
  const video = useRef<HTMLVideoElement>(null);
  const track = useRef<MediaStreamTrack | null>(null);
  const [phase, setPhase] = useState<"starting" | "scanning" | "found">("starting");
  const [light, setLightOn] = useState<boolean | null>(null);
  const [slow, setSlow] = useState(false);
  const [awake, setAwake] = useState(() => !document.hidden);
  // The camera must not restart because the parent re-rendered with new callbacks.
  const handlers = useRef({ onDetected, onProblem });
  handlers.current = { onDetected, onProblem };

  // A hidden tab has no use for the camera, and keeping it would leave its light on.
  useEffect(() => {
    const update = () => setAwake(!document.hidden);
    document.addEventListener("visibilitychange", update);
    return () => document.removeEventListener("visibilitychange", update);
  }, []);

  useEffect(() => {
    if (!awake) {
      return;
    }
    const element = video.current!;
    let stopped = false;
    let stream: MediaStream | null = null;

    async function scan() {
      // The decoder loads while the camera opens, so neither waits for the other.
      const opening = openFrameReader();
      opening.catch(() => undefined);
      try {
        // The back camera, insisted on: asked only to prefer it, some phones open the front one.
        stream = await openBackCamera(navigator.mediaDevices, { width: 1920, height: 1080 });
      } catch (failure) {
        if (!stopped) {
          handlers.current.onProblem(isDenied(failure) ? "denied" : "unavailable");
        }
        return;
      }
      if (stopped) {
        release(stream);
        return;
      }

      element.srcObject = stream;
      await element.play().catch(() => undefined);
      const [camera] = stream.getVideoTracks();
      track.current = camera;
      const capabilities = capabilitiesOf(camera);
      setLightOn(capabilities?.torch ? false : null);
      if (capabilities?.focusMode?.includes("continuous")) {
        const refocusing = { focusMode: "continuous" } as MediaTrackConstraintSet;
        void camera.applyConstraints({ advanced: [refocusing] }).catch(() => undefined);
      }

      const reader = await opening;
      if (stopped) {
        return;
      }
      setPhase("scanning");
      const began = performance.now();
      while (!stopped) {
        await nextPicture(element);
        if (stopped) {
          return;
        }
        if (element.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || element.videoWidth === 0) {
          continue;
        }
        // Everything the person can see is read, not just the marked box: a barcode held
        // sideways or a little off still counts.
        const { width, height } = frame.current!.getBoundingClientRect();
        const shown = coverCrop(
          { width: element.videoWidth, height: element.videoHeight },
          { width, height },
          { x: 0, y: 0, width, height },
        );
        const now = performance.now();
        const code = await reader.look(element, shown, now);
        if (stopped) {
          return;
        }
        if (code) {
          setPhase("found");
          navigator.vibrate?.(80);
          // Long enough to see that it was read, short enough not to be waited for.
          await pause(CONFIRMATION_MS);
          if (!stopped) {
            handlers.current.onDetected(code);
          }
          return;
        }
        setSlow(now - began > SLOW_AFTER_MS);
        await pause(BREATH_MS);
      }
    }

    scan().catch(() => {
      if (!stopped) {
        handlers.current.onProblem("unavailable");
      }
    });

    return () => {
      stopped = true;
      release(stream);
      track.current = null;
      element.srcObject = null;
      setPhase("starting");
      setSlow(false);
      setLightOn(null);
    };
  }, [awake]);

  function toggleLight() {
    const next = !light;
    if (track.current) {
      switchLight(track.current, next).then(
        () => setLightOn(next),
        () => setLightOn(null),
      );
    }
  }

  return (
    <div className="scanner" role="group" aria-label={t("scanBarcode")}>
      <div ref={frame} className={`scanner-frame scanner-${phase}`}>
        <video ref={video} muted playsInline autoPlay aria-hidden="true" />
        <div className="scanner-guide">
          {phase === "found" && (
            <svg className="scanner-check" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M5 12.5l4.5 4.5L19 7.5" />
            </svg>
          )}
        </div>
        {light !== null && (
          <button
            type="button"
            className={light ? "scanner-light scanner-light-on" : "scanner-light"}
            aria-pressed={light}
            aria-label={t(light ? "scanLightOff" : "scanLightOn")}
            onClick={toggleLight}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M8 3h8l-1 5 2 3v2h-4v8h-2v-8H7v-2l2-3-1-5Z" />
            </svg>
          </button>
        )}
      </div>
      <p className="scanner-status" role="status">
        {phase === "found" ? t("scanFound") : phase === "starting" ? t("scanStarting") : t(slow ? "scanSlow" : "scanHint")}
      </p>
      {/* The button that opened the scanner is gone, so the keyboard is given somewhere to be. */}
      <button type="button" className="button button-quiet" autoFocus onClick={onStop}>
        {t("scanStop")}
      </button>
    </div>
  );
}
