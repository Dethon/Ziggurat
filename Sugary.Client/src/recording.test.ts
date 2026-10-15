import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CLIP_LIMIT_MS, clipFile, formatElapsed, recordingType, sameOrientation, startTake, type Recorder } from "./recording";

describe("recording type", () => {
  it("prefers H.264 in MP4, which Safari and newer Chrome record", () => {
    expect(recordingType(() => true)).toBe("video/mp4;codecs=avc1");
    expect(recordingType((type) => type === "video/mp4" || type.startsWith("video/webm"))).toBe("video/mp4");
  });

  it("falls back to WebM, VP9 before VP8", () => {
    expect(recordingType((type) => type.startsWith("video/webm"))).toBe("video/webm;codecs=vp9");
    expect(recordingType((type) => type === "video/webm;codecs=vp8" || type === "video/webm")).toBe("video/webm;codecs=vp8");
    expect(recordingType((type) => type === "video/webm")).toBe("video/webm");
  });

  it("leaves the choice to the browser when it can say nothing", () => {
    expect(recordingType(() => false)).toBeNull();
  });
});

describe("the clip's file", () => {
  it("is named for its container, with the codecs left off its type", () => {
    const webm = clipFile([new Blob(["a"])], "video/webm;codecs=vp9");
    const mp4 = clipFile([new Blob(["a"])], "video/mp4;codecs=avc1,opus");

    expect([webm.name, webm.type]).toEqual(["meal.webm", "video/webm"]);
    expect([mp4.name, mp4.type]).toEqual(["meal.mp4", "video/mp4"]);
    expect(clipFile([], "video/quicktime").name).toBe("meal.mov");
  });

  it("is taken for MP4 when the recorder does not say", () => {
    const file = clipFile([new Blob(["a"])], "");

    expect([file.name, file.type]).toEqual(["meal.mp4", "video/mp4"]);
  });

  it("holds everything recorded, in order", async () => {
    const file = clipFile([new Blob(["ab"]), new Blob(["cd"])], "video/webm");

    expect(await file.text()).toBe("abcd");
  });
});

describe("elapsed time", () => {
  it("reads as minutes and seconds", () => {
    expect(formatElapsed(0)).toBe("0:00");
    expect(formatElapsed(7400)).toBe("0:07");
    expect(formatElapsed(20_000)).toBe("0:20");
  });
});

describe("orientation", () => {
  it("compares portrait and landscape, a square fitting either", () => {
    expect(sameOrientation({ width: 3000, height: 4000 }, { width: 1080, height: 1920 })).toBe(true);
    expect(sameOrientation({ width: 4000, height: 3000 }, { width: 1080, height: 1920 })).toBe(false);
    expect(sameOrientation({ width: 1000, height: 1000 }, { width: 1080, height: 1920 })).toBe(true);
  });
});

// A recorder as the browser's behaves: data comes in slices, and stopping hands over the last
// slice before saying it has stopped.
class FakeRecorder extends EventTarget implements Recorder {
  state: RecordingState = "inactive";
  mimeType = "video/webm;codecs=vp9";
  start = vi.fn(() => {
    this.state = "recording";
  });
  stop = vi.fn(() => {
    if (this.state === "inactive") {
      throw new Error("InvalidStateError");
    }
    this.state = "inactive";
    this.slice("last");
    this.dispatchEvent(new Event("stop"));
  });

  slice(text: string) {
    this.dispatchEvent(Object.assign(new Event("dataavailable"), { data: new Blob([text]) }));
  }
}

describe("a take", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("stops by itself at 20 seconds, so a clip is never too long for the server", async () => {
    const recorder = new FakeRecorder();
    const take = startTake(recorder, { onTick: () => undefined });

    vi.advanceTimersByTime(CLIP_LIMIT_MS - 1);
    expect(recorder.stop).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);

    expect(CLIP_LIMIT_MS).toBe(20_000);
    expect(recorder.stop).toHaveBeenCalledOnce();
    const file = await take.result;
    expect(file?.name).toBe("meal.webm");
  });

  it("keeps everything recorded when she stops it", async () => {
    const recorder = new FakeRecorder();
    const take = startTake(recorder, { onTick: () => undefined });
    recorder.slice("first ");

    vi.advanceTimersByTime(5000);
    take.stop();

    expect(await (await take.result)!.text()).toBe("first last");
    vi.advanceTimersByTime(CLIP_LIMIT_MS);
    expect(recorder.stop).toHaveBeenCalledOnce();
  });

  it("ticks the elapsed time while recording, and no longer after", () => {
    const recorder = new FakeRecorder();
    const ticks: number[] = [];
    const take = startTake(recorder, { onTick: (ms) => ticks.push(ms) });

    vi.advanceTimersByTime(1000);
    expect(ticks.at(-1)).toBe(1000);
    take.stop();
    const seen = ticks.length;
    vi.advanceTimersByTime(3000);

    expect(ticks).toHaveLength(seen);
  });

  it("gives nothing when abandoned", async () => {
    const recorder = new FakeRecorder();
    const take = startTake(recorder, { onTick: () => undefined });
    recorder.slice("some");

    take.cancel();

    expect(await take.result).toBeNull();
    expect(recorder.stop).toHaveBeenCalledOnce();
  });

  it("gives nothing when nothing was recorded", async () => {
    const recorder = new FakeRecorder();
    recorder.stop.mockImplementation(() => {
      recorder.state = "inactive";
      recorder.dispatchEvent(new Event("stop"));
    });
    const take = startTake(recorder, { onTick: () => undefined });

    take.stop();

    expect(await take.result).toBeNull();
  });

  it("asks the recorder for slices, so a long take is not held in one piece", () => {
    const recorder = new FakeRecorder();

    startTake(recorder, { onTick: () => undefined });

    expect(recorder.start).toHaveBeenCalledWith(1000);
  });
});
