export type Tab = "meal" | "log" | "glucose" | "cycle" | "foods" | "settings";

const TABS: readonly Tab[] = ["meal", "log", "glucose", "cycle", "foods", "settings"];

// The tab she is on lives in the address's fragment, so a reload (a pull-down to refresh) and a
// return from signing in land on it again, where before every reload opened the meal. Anything else
// there opens the meal, as the app's own start does.
export function tabOf(hash: string): Tab {
  const name = hash.replace(/^#/, "");
  return TABS.find((tab) => tab === name) ?? "meal";
}

export function hashOf(tab: Tab): string {
  return `#${tab}`;
}
