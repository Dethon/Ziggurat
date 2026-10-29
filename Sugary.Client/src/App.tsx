import { useCallback, useEffect, useRef, useState } from "react";
import { api, UNAUTHORIZED_EVENT } from "./api";
import { Icon, type IconName } from "./components/Icon";
import type { StringKey } from "./i18n";
import { useMinute } from "./clock";
import { pendingOutcomes } from "./log";
import { resync } from "./push";
import { CycleScreen } from "./screens/CycleScreen";
import { FoodsScreen } from "./screens/FoodsScreen";
import { LogScreen } from "./screens/LogScreen";
import { MealScreen } from "./screens/MealScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { useSettings } from "./settings";
import { goSignIn } from "./signin";
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

// Signed in or not is the proxy's to say: a page only opens behind its session check, and a call
// whose session has gone since is answered 401, which sends the person to sign in and back here.
export function App() {
  const [session, setSession] = useState<SessionState | null>(null);
  // Mounted for the whole app.
  useTypingMark();

  useEffect(() => {
    window.addEventListener(UNAUTHORIZED_EVENT, goSignIn);
    api
      .session()
      .then(setSession)
      // Offline at launch: show the app rather than a dead screen; calls will report it.
      .catch(() => setSession({ estimates: false, labels: false }));
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, goSignIn);
  }, []);

  if (session === null) {
    return null;
  }
  return (
    <ToastProvider>
      <Shell session={session} />
    </ToastProvider>
  );
}

function Shell({ session }: { session: SessionState }) {
  const { t, language } = useSettings();
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

  // The pen reminders' subscription, handed over again in the language they are to be written in.
  useEffect(() => {
    resync(language).catch(() => {});
  }, [language]);

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
        {tab === "log" && <LogScreen estimates={session.estimates} labels={session.labels} onChanged={() => void refreshPending()} />}
        {tab === "cycle" && <CycleScreen />}
        {tab === "foods" && (
          <FoodsScreen
            labels={session.labels}
            onAddToMeal={(item) => {
              adder.current?.(item);
              toast({ text: t("addedToMeal"), action: { label: t("viewMeal"), run: () => setTab("meal") } });
            }}
          />
        )}
        {tab === "settings" && <SettingsScreen />}
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
