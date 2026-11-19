import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, setViewing, withPerson } from "./api";
import { errorMessage } from "./errors";
import { translator } from "./i18n";
import { firstViewed, hasNoLog, offered, sessionOf, showsPicker } from "./viewing";
import { tabOf, visibleTabs } from "./tabs";
import type { SessionState } from "./types";

const ana = { userId: "ana", displayName: "Ana" };
const bea = { userId: "bea", displayName: "Bea" };
const reader = (usesSugary: boolean): SessionState =>
  sessionOf({ estimates: true, labels: true, person: "ana", usesSugary, people: usesSugary ? [ana, bea] : [bea] });

describe("the session an older server answers", () => {
  it("reads as somebody using Sugary with nobody else to look at", () => {
    const session = sessionOf({ estimates: true, labels: false, person: "ana" });

    expect(session.usesSugary).toBe(true);
    expect(session.people).toEqual([]);
    expect(showsPicker(session)).toBe(false);
    expect(firstViewed(session)).toBeNull();
  });
});

describe("the person picker", () => {
  it("is offered only when somebody else's log can be looked at", () => {
    expect(showsPicker(sessionOf({ estimates: true, labels: true, person: "ana", usesSugary: true, people: [ana] }))).toBe(false);
    expect(showsPicker(reader(true))).toBe(true);
  });

  it("offers a reader who uses Sugary her own log too, and opens on it", () => {
    expect(offered(reader(true)).map((p) => p.userId)).toEqual(["ana", "bea"]);
    expect(firstViewed(reader(true))).toBeNull();
  });

  it("opens a reader with no log of her own on the first person, and never offers her own", () => {
    const session = sessionOf({ estimates: true, labels: true, person: "ana", usesSugary: false, people: [ana, bea] });

    expect(offered(session).map((p) => p.userId)).toEqual(["bea"]);
    expect(firstViewed(session)).toBe("bea");
    expect(hasNoLog(session)).toBe(false);
  });

  it("finds no log at all for somebody who neither uses Sugary nor has anyone to look at", () => {
    expect(hasNoLog(sessionOf({ estimates: false, labels: false, person: "ana", usesSugary: false, people: [] }))).toBe(true);
  });
});

describe("the tabs while looking at somebody else's log", () => {
  it("are the Log, Glucose and, where hers, Cycle alone", () => {
    expect(visibleTabs(true, true)).toEqual(["log", "glucose", "cycle"]);
    expect(visibleTabs(false, true)).toEqual(["log", "glucose"]);
  });

  it("open on the Log for a tab not shown", () => {
    expect(tabOf("#meal", visibleTabs(true, true))).toBe("log");
    expect(tabOf("#settings", visibleTabs(true, true))).toBe("log");
    expect(tabOf("#glucose", visibleTabs(true, true))).toBe("glucose");
  });
});

describe("whose log a request reads", () => {
  afterEach(() => {
    setViewing(null);
    vi.unstubAllGlobals();
  });

  it("names nobody while she looks at her own", () => {
    expect(withPerson("/api/meals?skip=0")).toBe("/api/meals?skip=0");
    expect(api.photoUrl("m1")).toBe("/api/meals/m1/photo");
  });

  it("names the person looked at on every address, the photo's and the export's too", () => {
    setViewing("bea");

    expect(withPerson("/api/meals?skip=0&take=50")).toBe("/api/meals?skip=0&take=50&person=bea");
    expect(withPerson("/api/periods")).toBe("/api/periods?person=bea");
    expect(api.photoUrl("m1")).toBe("/api/meals/m1/photo?person=bea");
    expect(api.exportUrl("meals")).toMatch(/^\/api\/export\/meals\.csv\?tz=[^&]+&person=bea$/);
  });

  it("names the person looked at on a call through the app's own request", async () => {
    const asked: string[] = [];
    vi.stubGlobal("fetch", async (url: string) => {
      asked.push(url);
      return new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } });
    });
    setViewing("bea person");

    await api.periods();
    await api.mealPhoto("m1").catch(() => {});

    expect(asked).toEqual(["/api/periods?person=bea%20person", "/api/meals/m1/photo?person=bea%20person"]);
  });
});

describe("a refusal of looking", () => {
  const en = translator("en");

  it("is said in her language for each code", () => {
    expect(errorMessage(new ApiError(403, "", "viewing-read-only"), en)).toBe(en("errorViewingReadOnly"));
    expect(errorMessage(new ApiError(403, "", "not-granted"), en)).toBe(en("errorNotGranted"));
    expect(errorMessage(new ApiError(403, "", "sugary-not-yours"), en)).toBe(en("errorSugaryNotYours"));
  });
});
