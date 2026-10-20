import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { translator, type Language, type Translate } from "./i18n";
import { resolveTheme, type Theme } from "./theme";

export type RationSize = 10 | 15;

export interface Settings {
  rationSize: RationSize;
  language: Language;
  theme: Theme;
}

interface SettingsContext extends Settings {
  t: Translate;
  update: (change: Partial<Settings>) => void;
}

const STORAGE_KEY = "sugary.settings";

export function load<T>(key: string, fallback: T): T {
  try {
    const stored = localStorage.getItem(key);
    return stored ? { ...fallback, ...JSON.parse(stored) } : fallback;
  } catch {
    return fallback;
  }
}

export function store(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Private browsing or a full quota: the app still works, it just forgets on reload.
  }
}

const defaults = (): Settings => ({
  rationSize: 10,
  language: navigator.language.toLowerCase().startsWith("es") ? "es" : "en",
  theme: "system",
});

// index.html sets the same attribute before the bundle loads, so a chosen theme never flashes the other.
function applyTheme(theme: Theme, systemDark: boolean) {
  const root = document.documentElement;
  root.dataset.theme = resolveTheme(theme, systemDark);
  // The status bar takes the page's colour, not the phone's scheme its media queries assume.
  const bg = getComputedStyle(root).getPropertyValue("--color-bg").trim();
  document.querySelectorAll('meta[name="theme-color"]').forEach((meta) => meta.setAttribute("content", bg));
}

const Context = createContext<SettingsContext | null>(null);

export function SettingsProvider({ children }: { children: ReactNode }) {
  const [settings, setSettings] = useState<Settings>(() => load(STORAGE_KEY, defaults()));

  useEffect(() => {
    store(STORAGE_KEY, settings);
    document.documentElement.lang = settings.language;
  }, [settings]);

  useEffect(() => {
    const dark = matchMedia("(prefers-color-scheme: dark)");
    const follow = () => applyTheme(settings.theme, dark.matches);
    follow();
    dark.addEventListener("change", follow);
    return () => dark.removeEventListener("change", follow);
  }, [settings.theme]);

  const update = useCallback((change: Partial<Settings>) => setSettings((current) => ({ ...current, ...change })), []);
  const value = useMemo(
    () => ({ ...settings, t: translator(settings.language), update }),
    [settings, update],
  );

  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export function useSettings(): SettingsContext {
  const context = useContext(Context);
  if (!context) {
    throw new Error("useSettings outside SettingsProvider");
  }
  return context;
}
