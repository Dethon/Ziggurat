import { describe, expect, it } from "vitest";
import { hashOf, tabOf, visibleTabs } from "./tabs";

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
