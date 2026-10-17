import { describe, expect, it } from "vitest";
import { canStopNow, duration, elapsedMinutes, running, sessionMinutes, startNow } from "./exercise";
import { translator } from "./i18n";
import type { Exercise } from "./types";

const now = new Date("2026-10-17T10:00:00Z");
const minutesAgo = (n: number) => new Date(now.getTime() - n * 60_000).toISOString();
const session = (id: string, started: number, ended: number | null, kind: Exercise["kind"] = "walk"): Exercise => ({
  id,
  kind,
  name: null,
  startedAt: minutesAgo(started),
  endedAt: ended === null ? null : minutesAgo(ended),
  note: null,
});

describe("startNow", () => {
  it("starts the kind she tapped at this moment, with nothing else asked", () => {
    expect(startNow("gym", now)).toEqual({
      kind: "gym",
      name: null,
      startedAt: "2026-10-17T10:00:00.000Z",
      endedAt: null,
      note: null,
    });
  });
});

describe("running", () => {
  it("is the session with no end", () => {
    expect(running([session("done", 90, 60), session("now", 20, null)])?.id).toBe("now");
  });

  it("is none when every session has ended", () => {
    expect(running([session("done", 90, 60)])).toBeNull();
  });
});

describe("elapsedMinutes", () => {
  it("counts the whole minutes since it started", () => {
    expect(elapsedMinutes(session("now", 23.5, null), now)).toBe(23);
  });

  it("is never below zero for a phone whose clock is behind the start", () => {
    expect(elapsedMinutes(session("now", -2, null), now)).toBe(0);
  });
});

describe("sessionMinutes", () => {
  it("rounds a finished session to the minute, as the tables do", () => {
    expect(sessionMinutes({ ...session("walk", 0, null), startedAt: "2026-10-17T09:00:00Z", endedAt: "2026-10-17T09:44:31Z" })).toBe(45);
  });

  it("is null while the session runs", () => {
    expect(sessionMinutes(session("now", 10, null))).toBeNull();
  });
});

describe("canStopNow", () => {
  it("stops a session of up to twelve hours at this moment", () => {
    expect(canStopNow(session("long", 12 * 60, null), now)).toBe(true);
  });

  it("asks when a session left running past twelve hours really ended", () => {
    expect(canStopNow(session("forgotten", 12 * 60 + 1, null), now)).toBe(false);
  });
});

describe("duration", () => {
  const t = translator("es");

  it("says minutes under an hour", () => {
    expect(duration(45, t)).toBe("45 min");
  });

  it("says hours and minutes past an hour, and hours alone on the hour", () => {
    expect(duration(80, t)).toBe("1 h 20 min");
    expect(duration(120, t)).toBe("2 h");
  });
});
