// When the camera buttons open the phone's camera app instead of the app's own camera. Kept apart
// from the buttons so it can be tested.

export type CameraMode = "photo" | "video";
export type Fallback = "denied" | "failed" | "unsupported";

export interface FallbackState {
  // The camera was refused: asking again would only be refused again, so it is not asked until
  // the app is reopened.
  denied: boolean;
  // The camera failed once for these buttons (held by another app, a recorder that would not
  // start). Only the next tap goes to the phone's camera app; the one after tries the app's again.
  failed: readonly CameraMode[];
}

export const NO_FALLBACK: FallbackState = { denied: false, failed: [] };

export function afterProblem(state: FallbackState, mode: CameraMode, problem: "denied" | "failed"): FallbackState {
  if (problem === "denied") {
    return { ...state, denied: true };
  }
  return state.failed.includes(mode) ? state : { ...state, failed: [...state.failed, mode] };
}

// The phone's camera app was opened (and used or cancelled) after a failure.
export function afterCameraApp(state: FallbackState, mode: CameraMode): FallbackState {
  return state.failed.includes(mode) ? { ...state, failed: state.failed.filter((other) => other !== mode) } : state;
}

export function fallbackFor(
  state: FallbackState,
  modes: readonly CameraMode[],
  supported: (mode: CameraMode) => boolean,
): Fallback | null {
  if (state.denied) {
    return "denied";
  }
  if (modes.some((mode) => state.failed.includes(mode))) {
    return "failed";
  }
  return modes.every(supported) ? null : "unsupported";
}
