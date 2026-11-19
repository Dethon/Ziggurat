import { useCallback, useEffect, useRef, useState } from "react";
import { api, SUSPENDED_EVENT, UNAUTHORIZED_EVENT } from "./api";
import { Icon, type IconName } from "./components/Icon";
import type { StringKey } from "./i18n";
import { useMinute } from "./clock";
import { pendingOutcomes } from "./log";
import { resync } from "./push";
import { CycleScreen } from "./screens/CycleScreen";
import { FoodsScreen } from "./screens/FoodsScreen";
import { GlucoseScreen } from "./screens/GlucoseScreen";
import { LogScreen } from "./screens/LogScreen";
import { MealScreen } from "./screens/MealScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { showsCycle, UNSAID } from "./profile";
import { useSettings } from "./settings";
import { goNotice, goSignIn } from "./signin";
import { hashOf, type Tab, tabOf, visibleTabs } from "./tabs";
import { ToastProvider, useToast } from "./toast";
import type { DraftItem, Profile, SessionState } from "./types";
import { useTypingMark } from "./typing";

const TABS: { id: Tab; label: StringKey; icon: IconName }[] = [
  { id: "meal", label: "tabMeal", icon: "plate" },
  { id: "log", label: "tabLog", icon: "book" },
  { id: "glucose", label: "tabGlucose", icon: "curve" },
  { id: "cycle", label: "tabCycle", icon: "drop" },
  { id: "foods", label: "tabFoods", icon: "jar" },
  { id: "settings", label: "tabSettings", icon: "sliders" },
];

// Signed in or not is the proxy's to say: a page only opens behind its session check, and a call
// whose session has gone since is answered 401, which sends the person to sign in and back here.
export function App() {
  const [session, setSession] = useState<SessionState | null>(null);
  // Who she is decides which tabs she sees, so it is asked before the first paint. Unknown (offline,
  // or a failure) shows every tab, as the app did before anyone said.
  const [profile, setProfile] = useState<Profile | null>(null);
  // Mounted for the whole app.
  useTypingMark();

  useEffect(() => {
    window.addEventListener(UNAUTHORIZED_EVENT, goSignIn);
    window.addEventListener(SUSPENDED_EVENT, goNotice);
    void Promise.all([
      api
        .session()
        // Offline at launch: show the app rather than a dead screen; calls will report it.
        .catch((): SessionState => ({ estimates: false, labels: false })),
      api.profile().catch(() => UNSAID),
    ]).then(([session, profile]) => {
      setProfile(profile);
      setSession(session);
    });
    return () => {
      window.removeEventListener(UNAUTHORIZED_EVENT, goSignIn);
      window.removeEventListener(SUSPENDED_EVENT, goNotice);
    };
  }, []);

  if (session === null || profile === null) {
    return null;
  }
  return (
    <ToastProvider>
      <Shell session={session} profile={profile} onProfile={setProfile} />
    </ToastProvider>
  );
}

function Shell({
  session,
  profile,
  onProfile,
}: {
  session: SessionState;
  profile: Profile;
  onProfile: (profile: Profile) => void;
}) {
  const { t, language } = useSettings();
  const toast = useToast();
  const shown = visibleTabs(showsCycle(profile));
  const [tab, setTab] = useState<Tab>(() => tabOf(window.location.hash, shown));
  // Replaced, not pushed: the tabs are not pages, so Back leaves the app as it always has.
  useEffect(() => {
    window.history.replaceState(window.history.state, "", hashOf(tab));
  }, [tab]);
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
            person={session.person ?? null}
            onSaved={() => {
              toast({ text: t("saved") });
              setTab("log");
              void refreshPending();
            }}
          />
        </div>
        {tab === "log" && <LogScreen estimates={session.estimates} labels={session.labels} onChanged={() => void refreshPending()} />}
        {tab === "glucose" && <GlucoseScreen />}
        {tab === "cycle" && shown.includes("cycle") && <CycleScreen />}
        {tab === "foods" && (
          <FoodsScreen
            labels={session.labels}
            onAddToMeal={(item) => {
              adder.current?.(item);
              toast({ text: t("addedToMeal"), action: { label: t("viewMeal"), run: () => setTab("meal") } });
            }}
          />
        )}
        {tab === "settings" && <SettingsScreen profile={profile} onProfile={onProfile} />}
      </main>

      {/* A bar along the bottom on a phone, a rail down the left on a desktop, which names the app. */}
      <nav className="tabs" aria-label="Sugary">
        <p className="rail-brand" aria-hidden="true">
          <Icon name="slice" size={24} />
          Sugary
        </p>
        {TABS.filter(({ id }) => shown.includes(id)).map(({ id, label, icon }) => (
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
