import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { translator, type Language, type Translate } from "./i18n";

export type RationSize = 10 | 15;

export interface Settings {
  rationSize: RationSize;
  language: Language;
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
});

const Context = createContext<SettingsContext | null>(null);

export function SettingsProvider({ children }: { children: ReactNode }) {
  const [settings, setSettings] = useState<Settings>(() => load(STORAGE_KEY, defaults()));

  useEffect(() => {
    store(STORAGE_KEY, settings);
    document.documentElement.lang = settings.language;
  }, [settings]);

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
