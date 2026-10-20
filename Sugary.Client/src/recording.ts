// Filming in the app: what to record in, when to stop, and the file the server is sent. Kept
// apart from the camera screen so it can be tested.

import type { CameraSize } from "./camera";

// The server refuses a clip longer than this (with half a second's grace), so a take stops itself.
export const CLIP_LIMIT_MS = 20_000;

// Enough for sharp stills: the server takes them from the clip as sent, at up to 1568 pixels, and
// shrinks the clip itself to 540 lines. At about 5 Mbit/s, 20 seconds is some 12 MB of her data.
export const CLIP_SIZE: CameraSize = { width: 1920, height: 1080 };
export const CLIP_BITS_PER_SECOND = 5_000_000;

// H.264 in MP4 first, which Safari and newer Chrome record and the server reads as phones film;
// a bare "video/mp4" may hold another codec, so it comes second. Then WebM, which other browsers record.
const TYPES = ["video/mp4;codecs=avc1", "video/mp4", "video/webm;codecs=vp9", "video/webm;codecs=vp8", "video/webm"];

export function recordingType(isTypeSupported: (type: string) => boolean): string | null {
  return TYPES.find((type) => isTypeSupported(type)) ?? null;
}

const EXTENSIONS: Record<string, string> = {
  "video/mp4": "mp4",
  "video/webm": "webm",
  "video/quicktime": "mov",
  "video/x-matroska": "mkv",
};

// The codecs are left off the part's type: the server reads the clip whatever it is labelled,
// and a list of codecs has commas in it.
export function clipFile(chunks: BlobPart[], recordedType: string): File {
  const type = recordedType.split(";")[0].trim().toLowerCase() || "video/mp4";
  return new File(chunks, `meal.${EXTENSIONS[type] ?? "mp4"}`, { type });
}

export function formatElapsed(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

// Whether a photo is upright the way the picture on screen was; a square fits either.
export function sameOrientation(photo: CameraSize, shown: CameraSize): boolean {
  if (photo.width === photo.height || shown.width === shown.height) {
    return true;
  }
  return photo.width > photo.height === shown.width > shown.height;
}

export type Recorder = Pick<MediaRecorder, "state" | "mimeType" | "start" | "stop"> &
  Pick<EventTarget, "addEventListener">;

export interface Take {
  // Ends the take and keeps it.
  stop(): void;
  // Ends the take and throws it away.
  cancel(): void;
  // The clip, or null when it was abandoned or nothing was recorded.
  result: Promise<File | null>;
}

const TICK_MS = 250;

export function startTake(
  recorder: Recorder,
  { onTick, limitMs = CLIP_LIMIT_MS }: { onTick: (elapsedMs: number) => void; limitMs?: number },
): Take {
  const chunks: Blob[] = [];
  let kept = true;
  const began = Date.now();

  let ticking: ReturnType<typeof setInterval> | undefined;
  let limit: ReturnType<typeof setTimeout> | undefined;

  const result = new Promise<File | null>((resolve) => {
    recorder.addEventListener("dataavailable", (event) => {
      const data = (event as BlobEvent).data;
      if (data && data.size > 0) {
        chunks.push(data);
      }
    });
    // The recorder hands over its last slice before it says it has stopped.
    recorder.addEventListener("stop", () => {
      clearInterval(ticking);
      clearTimeout(limit);
      resolve(kept && chunks.length > 0 ? clipFile(chunks, recorder.mimeType) : null);
    });
    recorder.addEventListener("error", () => {
      kept = false;
      end(false);
      resolve(null);
    });
  });

  function end(keep: boolean) {
    kept &&= keep;
    clearInterval(ticking);
    clearTimeout(limit);
    if (recorder.state !== "inactive") {
      recorder.stop();
    }
  }

  // Slices of a second, so the recording is not held in one piece until the end. A recorder that
  // will not start throws here, before any timer is set.
  recorder.start(1000);
  ticking = setInterval(() => onTick(Date.now() - began), TICK_MS);
  limit = setTimeout(() => end(true), limitMs);
  onTick(0);

  return { stop: () => end(true), cancel: () => end(false), result };
}
