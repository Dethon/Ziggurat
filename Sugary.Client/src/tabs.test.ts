import { describe, expect, it } from "vitest";
import { fuses, hashOf, openedTab, railTab, railTabs, tabOf, visibleTabs } from "./tabs";

describe("the tab in the address", () => {
  it("reads the tab the page was on", () => {
    expect(tabOf("#glucose")).toBe("glucose");
    expect(tabOf("#settings")).toBe("settings");
  });

  it("opens the meal with no tab or one it does not know", () => {
    expect(tabOf("")).toBe("meal");
    expect(tabOf("#")).toBe("meal");
    expect(tabOf("#nonsense")).toBe("meal");
    expect(tabOf("#toString")).toBe("meal");
  });

  it("writes every tab back as one it reads", () => {
    for (const tab of ["meal", "log", "glucose", "cycle", "foods", "settings"] as const) {
      expect(tabOf(hashOf(tab))).toBe(tab);
    }
  });
});

describe("the tabs a person is shown", () => {
  it("leaves out the Cycle tab when it is not hers", () => {
    expect(visibleTabs(true)).toContain("cycle");
    expect(visibleTabs(false)).not.toContain("cycle");
  });

  it("opens the meal for a tab she is not shown", () => {
    expect(tabOf("#cycle", visibleTabs(false))).toBe("meal");
    expect(tabOf("#cycle", visibleTabs(true))).toBe("cycle");
  });
});

describe("a window wide enough for the meal beside the log", () => {
  it("sets the meal she is building beside her log", () => {
    expect(fuses(visibleTabs(true), true)).toBe(true);
    expect(fuses(visibleTabs(true), false)).toBe(false);
  });

  it("fuses nothing in somebody else's log, which has no meal", () => {
    expect(fuses(visibleTabs(true, true), true)).toBe(false);
  });

  it("names the two in one rail entry, the log's, which keeps its badge", () => {
    expect(railTabs(visibleTabs(true), true)).toEqual(["log", "glucose", "cycle", "foods", "settings"]);
    expect(railTabs(visibleTabs(true), false)).toEqual(visibleTabs(true));
  });

  it("lights that entry for the meal as for the log", () => {
    expect(railTab("meal", true)).toBe("log");
    expect(railTab("log", true)).toBe("log");
    expect(railTab("foods", true)).toBe("foods");
    expect(railTab("meal", false)).toBe("meal");
  });
});

describe("the tab a tapped reminder opens", () => {
  it("is the one its address names, when she is shown it", () => {
    expect(openedTab("/#cycle", visibleTabs(true))).toBe("cycle");
  });

  // A pen's reminder names no tab: an open app stays where she is.
  it("is none for an address naming no tab, or one she is not shown", () => {
    expect(openedTab("/", visibleTabs(true))).toBeNull();
    expect(openedTab("/#nonsense", visibleTabs(true))).toBeNull();
    expect(openedTab("/#cycle", visibleTabs(false))).toBeNull();
  });
});
