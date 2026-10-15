import { describe, expect, it } from "vitest";
import { afterCameraApp, afterProblem, fallbackFor, NO_FALLBACK } from "./fallback";

const everywhere = () => true;

describe("falling back to the phone's camera app", () => {
  it("does not while the app's camera works", () => {
    expect(fallbackFor(NO_FALLBACK, ["photo", "video"], everywhere)).toBeNull();
  });

  it("does for good once the camera is refused", () => {
    let state = afterProblem(NO_FALLBACK, "photo", "denied");
    state = afterCameraApp(state, "photo");

    expect(fallbackFor(state, ["photo"], everywhere)).toBe("denied");
    expect(fallbackFor(state, ["video"], everywhere)).toBe("denied");
  });

  // A camera held by another app a moment ago must not send her to the front camera for the rest
  // of the day.
  it("does for one tap after a failure, then tries the app's camera again", () => {
    const failed = afterProblem(NO_FALLBACK, "video", "failed");

    expect(fallbackFor(failed, ["video"], everywhere)).toBe("failed");
    expect(fallbackFor(failed, ["photo"], everywhere)).toBeNull();
    expect(fallbackFor(afterCameraApp(failed, "video"), ["video"], everywhere)).toBeNull();
  });

  it("does where the browser cannot film or use the camera at all", () => {
    const noRecorder = (mode: string) => mode === "photo";

    expect(fallbackFor(NO_FALLBACK, ["photo"], noRecorder)).toBeNull();
    expect(fallbackFor(NO_FALLBACK, ["video"], noRecorder)).toBe("unsupported");
    expect(fallbackFor(NO_FALLBACK, ["photo", "video"], noRecorder)).toBe("unsupported");
  });
});
