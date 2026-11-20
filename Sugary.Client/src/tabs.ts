export type Tab = "meal" | "log" | "glucose" | "cycle" | "foods" | "settings";

const TABS: readonly Tab[] = ["meal", "log", "glucose", "cycle", "foods", "settings"];

// The tab she is on lives in the address's fragment, so a reload (a pull-down to refresh) and a
// return from signing in land on it again, where before every reload opened the meal. Anything else
// there, or a tab she is not shown, opens the first she is shown: the meal, as the app's own start
// does, or the Log of somebody else's.
export function tabOf(hash: string, shown: readonly Tab[] = TABS): Tab {
  const name = hash.replace(/^#/, "");
  return shown.find((tab) => tab === name) ?? shown[0] ?? "meal";
}

// What somebody else's log shows her: all but the meal she is building, read only but for the pantry,
// which is the household's whoever's log is open; the settings show that person's, read only.
const LOOKING: readonly Tab[] = ["log", "glucose", "cycle", "foods", "settings"];

// The Cycle tab is a woman's: a man who said so is not shown it, and his periods, if any, stay logged.
export function visibleTabs(cycle: boolean, someoneElses = false): Tab[] {
  return (someoneElses ? LOOKING : TABS).filter((tab) => cycle || tab !== "cycle");
}

export function hashOf(tab: Tab): string {
  return `#${tab}`;
}

// A window wide enough (styles.css, "A wide window") sets the meal she is building beside her log, so
// what she eats and what she has eaten are one place: her own log only, as somebody else's has no meal.
export function fuses(shown: readonly Tab[], wide: boolean): boolean {
  return wide && shown.includes("meal") && shown.includes("log");
}

// The two fused are one entry in the rail, the log's, which keeps the badge of meals awaiting her.
export function railTabs(shown: readonly Tab[], fused: boolean): Tab[] {
  return shown.filter((tab) => !fused || tab !== "meal");
}

// The rail entry lit for the tab she is on: the log's for the meal too, once the two are one.
export function railTab(tab: Tab, fused: boolean): Tab {
  return fused && tab === "meal" ? "log" : tab;
}
