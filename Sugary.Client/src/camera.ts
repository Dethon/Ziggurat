// Which camera to open. Phones are asked for the back camera in more than one way, because many
// answer a polite request with whichever camera was used last, often the front one. Kept apart
// from the screens, with the browser's camera calls passed in, so it can be tested.

export type Cameras = Pick<MediaDevices, "getUserMedia" | "enumerateDevices">;
export type Facing = "back" | "front" | "unknown";

export interface CameraSize {
  width: number;
  height: number;
}

export interface OpenCamera {
  deviceId: string;
  facing: Facing;
}

// A camera can only be opened from a page served over HTTPS (or from localhost).
export function canUseCamera(): boolean {
  return window.isSecureContext && typeof navigator.mediaDevices?.getUserMedia === "function";
}

// How phones and browsers name their cameras: Chrome on Android says "camera2 0, facing back",
// Firefox "Facing back", Safari "Back Camera" or, in Spanish, "Cámara trasera".
const BACK = /\b(back|rear|environment|world)\b|trasera|posterior/i;
const FRONT = /\b(front|user|selfie|facetime)\b|delantera|frontal/i;
// On a phone with several back cameras the plain one is the main camera; the others crop or
// stretch the plate.
const LESSER = /ultra|wide|tele|macro|depth|dual|triple/i;

export function facingOf(label: string): Facing {
  return BACK.test(label) ? "back" : FRONT.test(label) ? "front" : "unknown";
}

export function describeTrack(track: MediaStreamTrack): OpenCamera {
  const settings = track.getSettings();
  const said = settings.facingMode === "environment" ? "back" : settings.facingMode === "user" ? "front" : "unknown";
  return { deviceId: settings.deviceId ?? "", facing: said !== "unknown" ? said : facingOf(track.label) };
}

export function release(stream: MediaStream | null | undefined) {
  stream?.getTracks().forEach((track) => track.stop());
}

export function isDenied(failure: unknown): boolean {
  const name = (failure as { name?: string } | null)?.name;
  return name === "NotAllowedError" || name === "SecurityError";
}

// The back camera that worked, so the next photo opens it directly. Kept for as long as the app
// is open: a phone renames its cameras now and then.
let rememberedBack: string | null = null;

export function forgetCamera() {
  rememberedBack = null;
}

async function videoInputs(cameras: Cameras): Promise<MediaDeviceInfo[]> {
  try {
    return (await cameras.enumerateDevices()).filter((device) => device.kind === "videoinput");
  } catch {
    return [];
  }
}

function mainBackCamera(devices: MediaDeviceInfo[]): string | null {
  const backs = devices.filter((device) => device.deviceId && facingOf(device.label) === "back");
  return (backs.find((device) => !LESSER.test(device.label)) ?? backs[0])?.deviceId ?? null;
}

function open(cameras: Cameras, choice: MediaTrackConstraints, size: CameraSize): Promise<MediaStream> {
  return cameras.getUserMedia({
    audio: false,
    video: { ...choice, width: { ideal: size.width }, height: { ideal: size.height } },
  });
}

function trackOf(stream: MediaStream): MediaStreamTrack {
  return stream.getVideoTracks()[0];
}

// Opens the back camera, asking in turn: the one found last time; the back camera, insisted on;
// a camera labelled back, once labels can be read; the back camera as a wish, then swapped for
// one labelled back if the wish was not granted; and last any camera at all, as a computer has.
// A refusal stops at once, since asking again only asks her again.
export async function openBackCamera(cameras: Cameras, size: CameraSize): Promise<MediaStream> {
  let last: unknown = null;
  const attempt = async (choice: MediaTrackConstraints): Promise<MediaStream | null> => {
    try {
      return await open(cameras, choice, size);
    } catch (failure) {
      if (isDenied(failure)) {
        throw failure;
      }
      last = failure;
      return null;
    }
  };
  const keep = (stream: MediaStream) => {
    const camera = describeTrack(trackOf(stream));
    if (camera.facing !== "front" && camera.deviceId) {
      rememberedBack = camera.deviceId;
    }
    return stream;
  };

  if (rememberedBack) {
    const stream = await attempt({ deviceId: { exact: rememberedBack } });
    if (stream) {
      return stream;
    }
    rememberedBack = null;
  }

  const insisted = await attempt({ facingMode: { exact: "environment" } });
  if (insisted) {
    return keep(insisted);
  }

  // Labels are blank until a camera has been allowed, so on a first visit this finds nothing.
  const labelled = mainBackCamera(await videoInputs(cameras));
  if (labelled) {
    const stream = await attempt({ deviceId: { exact: labelled } });
    if (stream) {
      return keep(stream);
    }
  }

  const wished = await attempt({ facingMode: { ideal: "environment" } });
  if (wished) {
    if (describeTrack(trackOf(wished)).facing === "back") {
      return keep(wished);
    }
    // The wish may have opened the front camera. Now that a camera is allowed the labels can be
    // read, and one of them may say back. Phones open one camera at a time, so this one closes first.
    const back = mainBackCamera(await videoInputs(cameras));
    if (back && back !== describeTrack(trackOf(wished)).deviceId) {
      release(wished);
      return keep(
        (await attempt({ deviceId: { exact: back } })) ??
          (await open(cameras, { facingMode: { ideal: "environment" } }, size)),
      );
    }
    return keep(wished);
  }

  const any = await attempt({});
  if (any) {
    return keep(any);
  }
  throw last;
}

// The other camera from the one showing, which the caller has already closed. Never remembered:
// the next photo starts on the back camera again.
export async function openOtherCamera(cameras: Cameras, from: OpenCamera, size: CameraSize): Promise<MediaStream> {
  if (from.facing === "front") {
    return openBackCamera(cameras, size);
  }
  const devices = (await videoInputs(cameras)).filter((device) => device.deviceId);
  if (from.facing === "back") {
    const front = devices.find((device) => facingOf(device.label) === "front");
    return open(cameras, front ? { deviceId: { exact: front.deviceId } } : { facingMode: { exact: "user" } }, size);
  }
  // A computer's webcams say nothing of where they face, so the next one in the list is taken.
  const here = devices.findIndex((device) => device.deviceId === from.deviceId);
  const next = devices[(here + 1) % Math.max(devices.length, 1)];
  return open(cameras, next ? { deviceId: { exact: next.deviceId } } : {}, size);
}
