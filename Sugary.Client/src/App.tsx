import { useCallback, useEffect, useState } from "react";
import { api, UNAUTHORIZED_EVENT } from "./api";
import { Icon, type IconName } from "./components/Icon";
import type { StringKey } from "./i18n";
import { LogScreen } from "./screens/LogScreen";
import { MealScreen } from "./screens/MealScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { SignIn } from "./screens/SignIn";
import { useSettings } from "./settings";
import type { SessionState } from "./types";

type Tab = "meal" | "log" | "settings";

const TABS: { id: Tab; label: StringKey; icon: IconName }[] = [
  { id: "meal", label: "tabMeal", icon: "plate" },
  { id: "log", label: "tabLog", icon: "list" },
  { id: "settings", label: "tabSettings", icon: "sliders" },
];

const TOAST_MS = 2500;

export function App() {
  const { t } = useSettings();
  const [session, setSession] = useState<SessionState | null>(null);
  const [tab, setTab] = useState<Tab>("meal");
  const [toast, setToast] = useState<string | null>(null);

  const refreshSession = useCallback(
    () =>
      api
        .session()
        .then(setSession)
        // Offline at launch: show the app rather than a dead screen; calls will report it.
        .catch(() => setSession({ required: false, authenticated: false })),
    [],
  );

  useEffect(() => {
    void refreshSession();
    const onUnauthorized = () => setSession({ required: true, authenticated: false });
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, [refreshSession]);

  useEffect(() => {
    if (!toast) {
      return;
    }
    const timer = window.setTimeout(() => setToast(null), TOAST_MS);
    return () => window.clearTimeout(timer);
  }, [toast]);

  if (session === null) {
    return null;
  }
  if (session.required && !session.authenticated) {
    return <SignIn onSignedIn={refreshSession} />;
  }

  return (
    <>
      <main>
        {/* The meal screen stays mounted so an estimate in progress survives a look at the log. */}
        <div hidden={tab !== "meal"}>
          <MealScreen
            onSaved={() => {
              setToast(t("saved"));
              setTab("log");
            }}
          />
        </div>
        {tab === "log" && <LogScreen />}
        {tab === "settings" && (
          <SettingsScreen onSignOut={session.required ? () => api.signOut().then(refreshSession) : null} />
        )}
      </main>

      {toast && (
        <p className="toast" role="status">
          <Icon name="check" size={18} /> {toast}
        </p>
      )}

      <nav className="tabs">
        {TABS.map(({ id, label, icon }) => (
          <button
            key={id}
            type="button"
            className={tab === id ? "tab tab-on" : "tab"}
            aria-current={tab === id ? "page" : undefined}
            onClick={() => setTab(id)}
          >
            <Icon name={icon} />
            {t(label)}
          </button>
        ))}
      </nav>
    </>
  );
}
