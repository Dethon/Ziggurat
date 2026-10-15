import { beforeEach, describe, expect, it, vi } from "vitest";
import { describeTrack, facingOf, forgetCamera, openBackCamera, openOtherCamera, type Cameras } from "./camera";

interface FakeCamera {
  deviceId: string;
  label: string;
  // What getSettings() reports, where the browser reports it.
  facingMode?: string;
}

const PHONE: FakeCamera[] = [
  { deviceId: "front", label: "camera2 1, facing front", facingMode: "user" },
  { deviceId: "back", label: "camera2 0, facing back", facingMode: "environment" },
];

const failure = (name: string) => Object.assign(new Error(name), { name });

function track(camera: FakeCamera) {
  return {
    label: camera.label,
    stop: vi.fn(),
    getSettings: () => ({ deviceId: camera.deviceId, facingMode: camera.facingMode }),
  };
}

function stream(camera: FakeCamera) {
  const only = track(camera);
  return { camera, track: only, getTracks: () => [only], getVideoTracks: () => [only] } as unknown as MediaStream & {
    camera: FakeCamera;
    track: ReturnType<typeof track>;
  };
}

// A phone as the browser shows it. `opens` decides which camera a request gets, or throws; labels
// are blank until a camera has been allowed once, as browsers do.
function phone(
  cameras: FakeCamera[],
  opens: (video: MediaTrackConstraints) => FakeCamera | Error,
): Cameras & { asked: MediaTrackConstraints[]; opened: ReturnType<typeof stream>[] } {
  let allowed = false;
  const asked: MediaTrackConstraints[] = [];
  const opened: ReturnType<typeof stream>[] = [];
  return {
    asked,
    opened,
    async getUserMedia(constraints) {
      const video = constraints?.video as MediaTrackConstraints;
      asked.push(video);
      const answer = opens(video);
      if (answer instanceof Error) {
        throw answer;
      }
      allowed = true;
      const opening = stream(answer);
      opened.push(opening);
      return opening;
    },
    async enumerateDevices() {
      return cameras.map((camera) => ({
        kind: "videoinput",
        deviceId: allowed ? camera.deviceId : "",
        label: allowed ? camera.label : "",
        groupId: "",
        toJSON: () => ({}),
      })) as MediaDeviceInfo[];
    },
  };
}

const exactId = (video: MediaTrackConstraints) => (video.deviceId as ConstrainDOMStringParameters | undefined)?.exact;
const facing = (video: MediaTrackConstraints) => video.facingMode as ConstrainDOMStringParameters | undefined;
const byId = (cameras: FakeCamera[], id: unknown) => cameras.find((camera) => camera.deviceId === id);

const SIZE = { width: 1920, height: 1080 };

beforeEach(() => forgetCamera());

describe("facing from a label", () => {
  it("reads back and front cameras as phones name them, in English and Spanish", () => {
    expect(facingOf("camera2 0, facing back")).toBe("back");
    expect(facingOf("Back Camera")).toBe("back");
    expect(facingOf("Rear camera")).toBe("back");
    expect(facingOf("Cámara trasera")).toBe("back");
    expect(facingOf("Cámara posterior")).toBe("back");
    expect(facingOf("Camera 0, Facing back, Orientation 90")).toBe("back");
    expect(facingOf("camera2 1, facing front")).toBe("front");
    expect(facingOf("Front Camera")).toBe("front");
    expect(facingOf("Cámara delantera")).toBe("front");
    expect(facingOf("Cámara frontal")).toBe("front");
    expect(facingOf("Integrated Webcam (0bda:5532)")).toBe("unknown");
    expect(facingOf("")).toBe("unknown");
  });
});

describe("opening the back camera", () => {
  it("insists on the back camera first, and keeps the asked size", async () => {
    const cameras = phone(PHONE, (video) => (facing(video)?.exact === "environment" ? PHONE[1] : PHONE[0]));

    const opened = await openBackCamera(cameras, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("back");
    expect(cameras.asked).toEqual([{ facingMode: { exact: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } }]);
  });

  // The phone she has: asked for the back camera as a wish, it opens the front one.
  it("when insisting is refused and a wish gets the front camera, switches to a camera labelled back", async () => {
    const cameras = phone(PHONE, (video) =>
      facing(video)?.exact ? failure("OverconstrainedError") : (byId(PHONE, exactId(video)) ?? PHONE[0]),
    );

    const opened = await openBackCamera(cameras, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("back");
    expect(cameras.opened[0].track.stop).toHaveBeenCalled();
    expect(exactId(cameras.asked.at(-1)!)).toBe("back");
  });

  it("goes straight to a camera labelled back once labels can be read", async () => {
    const cameras = phone(PHONE, (video) =>
      facing(video)?.exact ? failure("NotFoundError") : (byId(PHONE, exactId(video)) ?? PHONE[0]),
    );
    await openBackCamera(cameras, SIZE);
    forgetCamera();
    cameras.asked.length = 0;

    const opened = await openBackCamera(cameras, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("back");
    expect(cameras.asked.map(exactId)).toEqual([undefined, "back"]);
  });

  it("prefers the main back camera to a wide or telephoto one", async () => {
    const many: FakeCamera[] = [
      { deviceId: "ultra", label: "Back Ultra Wide Camera" },
      { deviceId: "main", label: "Back Camera" },
      { deviceId: "front", label: "Front Camera" },
    ];
    const cameras = phone(many, (video) =>
      facing(video)?.exact ? failure("OverconstrainedError") : (byId(many, exactId(video)) ?? many[2]),
    );

    const opened = await openBackCamera(cameras, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("main");
  });

  it("remembers the back camera it found and opens it directly next time", async () => {
    const cameras = phone(PHONE, (video) =>
      facing(video)?.exact ? failure("OverconstrainedError") : (byId(PHONE, exactId(video)) ?? PHONE[0]),
    );
    await openBackCamera(cameras, SIZE);
    cameras.asked.length = 0;

    await openBackCamera(cameras, SIZE);

    expect(cameras.asked).toEqual([{ deviceId: { exact: "back" }, width: { ideal: 1920 }, height: { ideal: 1080 } }]);
  });

  it("forgets a remembered camera that has gone and looks again", async () => {
    const cameras = phone(PHONE, (video) => (facing(video)?.exact === "environment" ? PHONE[1] : PHONE[0]));
    await openBackCamera(cameras, SIZE);
    const gone = phone([PHONE[0]], (video) =>
      exactId(video) ? failure("OverconstrainedError") : facing(video)?.exact ? failure("OverconstrainedError") : PHONE[0],
    );

    const opened = await openBackCamera(gone, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("front");
  });

  it("does not remember a front camera taken for want of a back one", async () => {
    const selfieOnly = [PHONE[0]];
    const cameras = phone(selfieOnly, (video) => (facing(video)?.exact ? failure("OverconstrainedError") : PHONE[0]));
    await openBackCamera(cameras, SIZE);
    cameras.asked.length = 0;

    await openBackCamera(cameras, SIZE);

    expect(exactId(cameras.asked[0])).toBeUndefined();
  });

  it("takes any camera on a computer whose webcam has no facing", async () => {
    const webcam: FakeCamera = { deviceId: "webcam", label: "Integrated Webcam" };
    const cameras = phone([webcam], (video) =>
      facing(video)?.exact ? failure("OverconstrainedError") : video.facingMode ? failure("NotReadableError") : webcam,
    );

    const opened = await openBackCamera(cameras, SIZE);

    expect(describeTrack(opened.getVideoTracks()[0]).deviceId).toBe("webcam");
    expect(cameras.asked.at(-1)).toEqual({ width: { ideal: 1920 }, height: { ideal: 1080 } });
  });

  it("stops at once when the camera is refused, asking nothing more", async () => {
    const cameras = phone(PHONE, () => failure("NotAllowedError"));

    await expect(openBackCamera(cameras, SIZE)).rejects.toMatchObject({ name: "NotAllowedError" });
    expect(cameras.asked).toHaveLength(1);
  });

  it("gives the last failure when there is no camera at all", async () => {
    const cameras = phone([], () => failure("NotFoundError"));

    await expect(openBackCamera(cameras, SIZE)).rejects.toMatchObject({ name: "NotFoundError" });
  });
});

describe("flipping", () => {
  const cameras = () => phone(PHONE, (video) => byId(PHONE, exactId(video)) ?? (facing(video)?.exact === "user" ? PHONE[0] : PHONE[1]));

  it("from the back camera opens one labelled front", async () => {
    const devices = cameras();
    const back = await openBackCamera(devices, SIZE);

    const other = await openOtherCamera(devices, describeTrack(back.getVideoTracks()[0]), SIZE);

    expect(describeTrack(other.getVideoTracks()[0]).deviceId).toBe("front");
  });

  it("from the front camera returns to the back one", async () => {
    const devices = cameras();
    const front = await openOtherCamera(devices, describeTrack(stream(PHONE[1]).getVideoTracks()[0]), SIZE);

    const back = await openOtherCamera(devices, describeTrack(front.getVideoTracks()[0]), SIZE);

    expect(describeTrack(back.getVideoTracks()[0]).deviceId).toBe("back");
  });

  it("does not make the front camera the one remembered", async () => {
    const devices = cameras();
    const back = await openBackCamera(devices, SIZE);
    await openOtherCamera(devices, describeTrack(back.getVideoTracks()[0]), SIZE);
    devices.asked.length = 0;

    const next = await openBackCamera(devices, SIZE);

    expect(describeTrack(next.getVideoTracks()[0]).deviceId).toBe("back");
  });

  it("on a computer goes to the next webcam", async () => {
    const webcams: FakeCamera[] = [
      { deviceId: "one", label: "Integrated Webcam" },
      { deviceId: "two", label: "USB Camera" },
    ];
    const devices = phone(webcams, (video) => byId(webcams, exactId(video)) ?? webcams[0]);
    const first = await openBackCamera(devices, SIZE);

    const other = await openOtherCamera(devices, describeTrack(first.getVideoTracks()[0]), SIZE);

    expect(describeTrack(other.getVideoTracks()[0]).deviceId).toBe("two");
  });
});
