import { useCallback, useEffect, useRef, useState } from "react";
import { api, UNAUTHORIZED_EVENT } from "./api";
import { Icon, type IconName } from "./components/Icon";
import type { StringKey } from "./i18n";
import { useMinute } from "./clock";
import { pendingOutcomes } from "./log";
import { CycleScreen } from "./screens/CycleScreen";
import { FoodsScreen } from "./screens/FoodsScreen";
import { LogScreen } from "./screens/LogScreen";
import { MealScreen } from "./screens/MealScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { SignIn } from "./screens/SignIn";
import { useSettings } from "./settings";
import { ToastProvider, useToast } from "./toast";
import type { DraftItem, SessionState } from "./types";
import { useTypingMark } from "./typing";

export type Tab = "meal" | "log" | "cycle" | "foods" | "settings";

const TABS: { id: Tab; label: StringKey; icon: IconName }[] = [
  { id: "meal", label: "tabMeal", icon: "plate" },
  { id: "log", label: "tabLog", icon: "book" },
  { id: "cycle", label: "tabCycle", icon: "drop" },
  { id: "foods", label: "tabFoods", icon: "jar" },
  { id: "settings", label: "tabSettings", icon: "sliders" },
];

export function App() {
  const [session, setSession] = useState<SessionState | null>(null);
  // Mounted for the whole app, the sign-in included.
  useTypingMark();

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
  const adder = useRef<((item: DraftItem) => void) | null>(null);
  // Meals still waiting for how her glucose went, counted on the Log tab.
  const [pending, setPending] = useState(0);
  const refreshPending = useCallback(
    () => api.meals(0, 50).then((meals) => setPending(pendingOutcomes(meals).length), () => {}),
    [],
  );

  // Read again as the minutes pass and when the app comes back, so a meal crossing its two hours
  // is counted without a reload.
  const minute = useMinute();
  useEffect(() => {
    void refreshPending();
  }, [refreshPending, minute]);

  return (
    <>
      <main>
        {/* The meal screen stays mounted so an estimate in progress survives a look at the log. */}
        <div hidden={tab !== "meal"}>
          <MealScreen
            active={tab === "meal"}
            adder={adder}
            estimates={session.estimates}
            labels={session.labels}
            onSaved={() => {
              toast({ text: t("saved") });
              setTab("log");
              void refreshPending();
            }}
          />
        </div>
        {tab === "log" && <LogScreen labels={session.labels} onChanged={() => void refreshPending()} />}
        {tab === "cycle" && <CycleScreen />}
        {tab === "foods" && (
          <FoodsScreen
            onAddToMeal={(item) => {
              adder.current?.(item);
              toast({ text: t("addedToMeal"), action: { label: t("viewMeal"), run: () => setTab("meal") } });
            }}
          />
        )}
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
              {id === "log" && pending > 0 && (
                <span className="tab-badge" aria-hidden="true">
                  {pending}
                </span>
              )}
            </span>
            {t(label)}
            {id === "log" && pending > 0 && (
              <span className="visually-hidden">
                , {pending === 1 ? t("logBadgeSpokenOne") : t("logBadgeSpoken", { n: pending })}
              </span>
            )}
          </button>
        ))}
      </nav>
    </>
  );
}
