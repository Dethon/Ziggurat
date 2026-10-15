import { useCallback, useEffect, useState } from "react";
import { api, UNAUTHORIZED_EVENT } from "./api";
import { Icon, type IconName } from "./components/Icon";
import type { StringKey } from "./i18n";
import { FoodsScreen } from "./screens/FoodsScreen";
import { LogScreen } from "./screens/LogScreen";
import { MealScreen } from "./screens/MealScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { SignIn } from "./screens/SignIn";
import { useSettings } from "./settings";
import { ToastProvider, useToast } from "./toast";
import type { SessionState } from "./types";

export type Tab = "meal" | "log" | "foods" | "settings";

const TABS: { id: Tab; label: StringKey; icon: IconName }[] = [
  { id: "meal", label: "tabMeal", icon: "plate" },
  { id: "log", label: "tabLog", icon: "book" },
  { id: "foods", label: "tabFoods", icon: "jar" },
  { id: "settings", label: "tabSettings", icon: "sliders" },
];

export function App() {
  const [session, setSession] = useState<SessionState | null>(null);

  const refreshSession = useCallback(
    () =>
      api
        .session()
        .then(setSession)
        // Offline at launch: show the app rather than a dead screen; calls will report it.
        .catch(() => setSession({ required: false, authenticated: false, estimates: false, labels: false })),
    [],
  );

  useEffect(() => {
    void refreshSession();
    const onUnauthorized = () =>
      setSession((current) => ({ estimates: false, labels: false, ...current, required: true, authenticated: false }));
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, [refreshSession]);

  if (session === null) {
    return null;
  }
  if (session.required && !session.authenticated) {
    return <SignIn onSignedIn={refreshSession} />;
  }
  return (
    <ToastProvider>
      <Shell session={session} onSignOut={session.required ? () => api.signOut().then(refreshSession) : null} />
    </ToastProvider>
  );
}

function Shell({ session, onSignOut }: { session: SessionState; onSignOut: (() => void) | null }) {
  const { t } = useSettings();
  const toast = useToast();
  const [tab, setTab] = useState<Tab>("meal");

  return (
    <>
      <main>
        {/* The meal screen stays mounted so an estimate in progress survives a look at the log. */}
        <div hidden={tab !== "meal"}>
          <MealScreen
            active={tab === "meal"}
            estimates={session.estimates}
            labels={session.labels}
            onSaved={() => {
              toast({ text: t("saved") });
              setTab("log");
            }}
          />
        </div>
        {tab === "log" && <LogScreen />}
        {tab === "foods" && <FoodsScreen />}
        {tab === "settings" && <SettingsScreen onSignOut={onSignOut} />}
      </main>

      <nav className="tabs" aria-label="Sugary">
        {TABS.map(({ id, label, icon }) => (
          <button
            key={id}
            type="button"
            className={tab === id ? "tab tab-on" : "tab"}
            aria-current={tab === id ? "page" : undefined}
            onClick={() => {
              setTab(id);
              window.scrollTo(0, 0);
            }}
          >
            <span className="tab-icon">
              <Icon name={icon} size={24} />
            </span>
            {t(label)}
          </button>
        ))}
      </nav>
    </>
  );
}
