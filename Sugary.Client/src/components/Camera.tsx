import { useEffect, useRef, useState, useSyncExternalStore, type ChangeEvent, type ReactNode } from "react";
import {
  canUseCamera,
  describeTrack,
  isDenied,
  openBackCamera,
  openOtherCamera,
  release,
  type CameraSize,
  type OpenCamera,
} from "../camera";
import {
  CLIP_BITS_PER_SECOND,
  CLIP_LIMIT_MS,
  CLIP_SIZE,
  formatElapsed,
  recordingType,
  sameOrientation,
  startTake,
  type Take,
} from "../recording";
import { useSettings } from "../settings";
import { Icon, type IconName } from "./Icon";
import "./Camera.css";

// Photos and clips are taken in the app rather than handed to the phone's camera app, because
// many camera apps (Samsung's among them) ignore the request for the back camera and reopen
// whichever was used last. When the app cannot use the camera, the phone's camera app is the way
// in, as it was before.

export type CameraMode = "photo" | "video";
type Fallback = "denied" | "failed" | "unsupported";

// Asked of the camera when a photo is taken from the live picture rather than by the camera itself:
// 4:3 as the sensor is, and more than the upload keeps (1568) or the barcode reader wants (2000).
const PHOTO_SIZE: CameraSize = { width: 4096, height: 3072 };
// Some phones take long to take a full photo, or never answer; the picture on screen is used then.
const PHOTO_PATIENCE_MS = 4000;

// Once the camera has failed in the app it is not tried again until the app is reopened: every
// camera button opens the phone's camera app instead, and a note says why.
let failure: Fallback | null = null;
const listeners = new Set<() => void>();

function fallBack(reason: Fallback) {
  failure = reason;
  listeners.forEach((listener) => listener());
}

function supported(mode: CameraMode): boolean {
  return canUseCamera() && (mode === "photo" || typeof MediaRecorder === "function");
}

function useFailure(): Fallback | null {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => failure,
  );
}

function fallbackFor(modes: CameraMode[], failed: Fallback | null): Fallback | null {
  return failed ?? (modes.every(supported) ? null : "unsupported");
}

// Said once beside the camera buttons, when they open the phone's camera app instead.
export function CameraFallbackNote({ modes }: { modes: CameraMode[] }) {
  const { t } = useSettings();
  const reason = fallbackFor(modes, useFailure());
  if (!reason) {
    return null;
  }
  return (
    <p className="notice camera-note" role="status">
      <Icon name="info" size={18} />{" "}
      {t(reason === "denied" ? "cameraDenied" : reason === "failed" ? "cameraFailed" : "cameraUnsupported")}
    </p>
  );
}

// One camera button. In the app it opens the camera screen; where the app cannot use the camera
// it is the phone's own camera, as a file input, so she is never stuck.
export function CameraButton({
  mode,
  icon,
  className,
  disabled,
  children,
  onPhoto,
  onClip,
}: {
  mode: CameraMode;
  icon: IconName;
  className: string;
  disabled?: boolean;
  children: ReactNode;
  onPhoto?: (photo: Blob) => void;
  onClip?: (clip: File) => void;
}) {
  const fallback = fallbackFor([mode], useFailure());
  const [open, setOpen] = useState(false);

  if (fallback) {
    const picked = (event: ChangeEvent<HTMLInputElement>) => {
      const file = event.target.files?.[0];
      event.target.value = "";
      if (file) {
        (mode === "photo" ? onPhoto : onClip)?.(file);
      }
    };
    return (
      <label className={[className, disabled && "disabled"].filter(Boolean).join(" ")}>
        <Icon name={icon} />
        {children}
        <input
          type="file"
          accept={mode === "photo" ? "image/*" : "video/*"}
          capture="environment"
          hidden
          disabled={disabled}
          onChange={picked}
        />
      </label>
    );
  }

  return (
    <>
      <button type="button" className={className} disabled={disabled} onClick={() => setOpen(true)}>
        <Icon name={icon} />
        {children}
      </button>
      {open && (
        <CameraView
          mode={mode}
          onClose={() => setOpen(false)}
          onProblem={(problem) => {
            setOpen(false);
            fallBack(problem);
          }}
          onPhoto={(photo) => {
            setOpen(false);
            onPhoto?.(photo);
          }}
          onClip={(clip) => {
            setOpen(false);
            onClip?.(clip);
          }}
        />
      )}
    </>
  );
}

// The ImageCapture API takes a photo with the camera itself, at its full resolution. Chrome has
// it; the standard types do not list it yet.
interface PhotoTaker {
  takePhoto(): Promise<Blob>;
}
type PhotoTakerClass = new (track: MediaStreamTrack) => PhotoTaker;

function frameOf(video: HTMLVideoElement): HTMLCanvasElement {
  const canvas = document.createElement("canvas");
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  canvas.getContext("2d")!.drawImage(video, 0, 0, canvas.width, canvas.height);
  return canvas;
}

const encoded = (canvas: HTMLCanvasElement) =>
  new Promise<Blob>((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error("encode failed"))), "image/jpeg", 0.92),
  );

async function sizeOf(photo: Blob): Promise<CameraSize> {
  const bitmap = await createImageBitmap(photo, { imageOrientation: "from-image" });
  const size = { width: bitmap.width, height: bitmap.height };
  bitmap.close();
  return size;
}

// The picture on screen is kept the moment the shutter is pressed, then the camera is asked for a
// full photo. Its photo is used unless it fails, takes too long or comes out turned on its side.
async function takePhoto(track: MediaStreamTrack, video: HTMLVideoElement): Promise<Blob> {
  const frame = frameOf(video);
  const Taker = (window as { ImageCapture?: PhotoTakerClass }).ImageCapture;
  if (Taker) {
    try {
      const photo = await Promise.race([
        new Taker(track).takePhoto(),
        new Promise<never>((_, reject) => setTimeout(() => reject(new Error("too slow")), PHOTO_PATIENCE_MS)),
      ]);
      if (sameOrientation(await sizeOf(photo), { width: frame.width, height: frame.height })) {
        return photo;
      }
    } catch {
      // The frame below is the photo.
    }
  }
  return encoded(frame);
}

type Phase = "starting" | "ready" | "recording" | "busy";

// The camera, full screen, always opening on the back camera. A photo is taken with the shutter;
// a clip is filmed until she stops it or 20 seconds have passed. The camera is closed whenever
// this screen goes, or the app is hidden.
export function CameraView({
  mode,
  onPhoto,
  onClip,
  onProblem,
  onClose,
}: {
  mode: CameraMode;
  onPhoto: (photo: Blob) => void;
  onClip: (clip: File) => void;
  onProblem: (problem: "denied" | "failed") => void;
  onClose: () => void;
}) {
  const { t } = useSettings();
  const dialog = useRef<HTMLDialogElement>(null);
  const video = useRef<HTMLVideoElement>(null);
  const stream = useRef<MediaStream | null>(null);
  const take = useRef<Take | null>(null);
  // The camera the back side opened, from which the other side is found.
  const backCamera = useRef<OpenCamera | null>(null);
  const [side, setSide] = useState<"back" | "other">("back");
  const [facing, setFacing] = useState<OpenCamera["facing"]>("back");
  const [canFlip, setCanFlip] = useState(false);
  const [phase, setPhase] = useState<Phase>("starting");
  const [elapsed, setElapsed] = useState(0);
  const [awake, setAwake] = useState(() => !document.hidden);
  // The camera must not restart because the parent re-rendered with new callbacks.
  const handlers = useRef({ onPhoto, onClip, onProblem });
  handlers.current = { onPhoto, onClip, onProblem };

  useEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => element?.close();
  }, []);

  // A hidden app has no use for the camera, and keeping it would leave its light on.
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
    let opened: MediaStream | null = null;
    const size = mode === "photo" ? PHOTO_SIZE : CLIP_SIZE;

    async function start() {
      const cameras = navigator.mediaDevices;
      try {
        opened =
          side === "other" && backCamera.current
            ? await openOtherCamera(cameras, backCamera.current, size)
            : await openBackCamera(cameras, size);
      } catch (failure) {
        if (stopped) {
          return;
        }
        if (side === "other" && !isDenied(failure)) {
          // There is no other camera to show after all; the back one comes back.
          setCanFlip(false);
          setSide("back");
          return;
        }
        handlers.current.onProblem(isDenied(failure) ? "denied" : "failed");
        return;
      }
      if (stopped) {
        release(opened);
        return;
      }
      const camera = describeTrack(opened.getVideoTracks()[0]);
      if (side === "back") {
        backCamera.current = camera;
      }
      setFacing(camera.facing);
      stream.current = opened;
      element.srcObject = opened;
      await element.play().catch(() => undefined);
      if (stopped) {
        return;
      }
      setPhase("ready");
      const devices = await cameras.enumerateDevices().catch(() => []);
      if (!stopped) {
        setCanFlip(devices.filter((device) => device.kind === "videoinput").length > 1);
      }
    }

    void start();

    return () => {
      stopped = true;
      // A take not finished when the camera goes is thrown away; she films it again.
      take.current?.cancel();
      take.current = null;
      release(opened);
      stream.current = null;
      element.srcObject = null;
      setPhase("starting");
      setElapsed(0);
    };
  }, [awake, side, mode]);

  async function shoot() {
    const camera = stream.current?.getVideoTracks()[0];
    const element = video.current;
    if (!camera || !element || element.videoWidth === 0) {
      return;
    }
    setPhase("busy");
    try {
      handlers.current.onPhoto(await takePhoto(camera, element));
    } catch {
      setPhase("ready");
    }
  }

  function record() {
    const opened = stream.current;
    if (!opened) {
      return;
    }
    const type = recordingType((candidate) => MediaRecorder.isTypeSupported(candidate));
    let recorder: MediaRecorder;
    try {
      recorder = new MediaRecorder(opened, {
        ...(type ? { mimeType: type } : {}),
        videoBitsPerSecond: CLIP_BITS_PER_SECOND,
      });
      const started = startTake(recorder, { onTick: setElapsed });
      take.current = started;
      setPhase("recording");
      void started.result.then((clip) => {
        if (take.current !== started) {
          return;
        }
        take.current = null;
        if (clip) {
          handlers.current.onClip(clip);
        } else {
          setElapsed(0);
          setPhase("ready");
        }
      });
    } catch {
      handlers.current.onProblem("failed");
    }
  }

  function stopRecording() {
    setPhase("busy");
    take.current?.stop();
  }

  const status =
    phase === "starting"
      ? t("scanStarting")
      : mode === "video" && phase !== "ready"
        ? `${formatElapsed(elapsed)} / ${formatElapsed(CLIP_LIMIT_MS)}`
        : mode === "video"
          ? t("cameraFilmHint")
          : "";

  return (
    <dialog ref={dialog} className="camera" aria-label={t("camera")} onCancel={onClose}>
      <div className="camera-view">
        <p className={phase === "recording" ? "camera-status camera-status-recording" : "camera-status"} role="status">
          {status}
        </p>
        <video
          ref={video}
          className={facing === "front" ? "camera-mirrored" : undefined}
          muted
          playsInline
          autoPlay
          aria-hidden="true"
        />
        <div className="camera-bar">
          <button
            type="button"
            className="camera-icon"
            aria-label={t("close")}
            disabled={phase === "recording"}
            onClick={onClose}
          >
            <Icon name="close" size={26} />
          </button>
          {mode === "photo" ? (
            <button
              type="button"
              className="camera-shutter"
              aria-label={t("cameraShutter")}
              disabled={phase !== "ready"}
              onClick={shoot}
            />
          ) : (
            <button
              type="button"
              className={phase === "recording" ? "camera-shutter camera-record camera-recording" : "camera-shutter camera-record"}
              aria-label={t(phase === "recording" ? "cameraStopFilming" : "cameraFilm")}
              disabled={phase !== "ready" && phase !== "recording"}
              onClick={phase === "recording" ? stopRecording : record}
            />
          )}
          <button
            type="button"
            className="camera-icon"
            aria-label={t("cameraFlip")}
            disabled={phase !== "ready"}
            style={{ visibility: canFlip ? "visible" : "hidden" }}
            onClick={() => {
              setPhase("starting");
              setSide((current) => (current === "back" ? "other" : "back"));
            }}
          >
            <Icon name="flip" size={26} />
          </button>
        </div>
      </div>
    </dialog>
  );
}
