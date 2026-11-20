import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, setViewing, SUSPENDED_EVENT, UNAUTHORIZED_EVENT } from "./api";
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
import { accountUrl, goNotice, goSignIn } from "./signin";
import { fuses, hashOf, railTab, railTabs, type Tab, tabOf, visibleTabs } from "./tabs";
import { ToastProvider, useToast } from "./toast";
import type { DraftItem, Profile, SessionState, SugaryPerson } from "./types";
import { useTypingMark } from "./typing";
import { firstViewed, hasNoLog, offered, sessionOf, showsPicker } from "./viewing";
import { useWide } from "./wide";

const TABS: { id: Tab; label: StringKey; icon: IconName }[] = [
  { id: "meal", label: "tabMeal", icon: "plate" },
  { id: "log", label: "tabLog", icon: "book" },
  { id: "glucose", label: "tabGlucose", icon: "curve" },
  { id: "cycle", label: "tabCycle", icon: "drop" },
  { id: "foods", label: "tabFoods", icon: "jar" },
  { id: "settings", label: "tabSettings", icon: "sliders" },
];

// Somebody Sugary is not on for: every call of theirs is refused with this code, the session's first.
const isNotYours = (failure: unknown) =>
  failure instanceof ApiError && failure.status === 403 && failure.code === "sugary-not-yours";

// Signed in or not is the proxy's to say: a page only opens behind its session check, and a call
// whose session has gone since is answered 401, which sends the person to sign in and back here.
export function App() {
  const [session, setSession] = useState<SessionState | null>(null);
  const [notOn, setNotOn] = useState(false);
  // Whose log is open: null is her own; a user id is somebody else's, read only, for a reader an owner
  // let read everyone's.
  const [viewed, setViewed] = useState<string | null>(null);
  // Who the open log's person is decides which tabs she sees, so it is asked before the first paint.
  // Unknown (offline, or a failure) shows every tab, as the app did before anyone said.
  const [profile, setProfile] = useState<Profile | null>(null);
  // The latest profile asked for: a pick made while an earlier one was loading wins.
  const asked = useRef(0);
  // Mounted for the whole app.
  useTypingMark();

  useEffect(() => {
    window.addEventListener(UNAUTHORIZED_EVENT, goSignIn);
    window.addEventListener(SUSPENDED_EVENT, goNotice);
    void Promise.all([
      api
        .session()
        .then(sessionOf)
        // Offline at launch: show the app rather than a dead screen; calls will report it.
        .catch((failure: unknown) => (isNotYours(failure) ? null : sessionOf({ estimates: false, labels: false }))),
      api.profile().catch(() => UNSAID),
    ]).then(async ([session, own]) => {
      if (session === null || hasNoLog(session)) {
        setNotOn(true);
        return;
      }
      // A reader keeping no log of her own opens on the first person she may look at.
      const first = firstViewed(session);
      setViewing(first);
      const profile = first === null ? own : await api.profile().catch(() => UNSAID);
      setViewed(first);
      setProfile(profile);
      setSession(session);
    });
    return () => {
      window.removeEventListener(UNAUTHORIZED_EVENT, goSignIn);
      window.removeEventListener(SUSPENDED_EVENT, goNotice);
    };
  }, []);

  // Another person's log: every address names them from now on, and nothing of the last log stays
  // on screen while theirs is read — its rows under another name would be a false health record.
  function look(userId: string) {
    const next = userId === session?.person ? null : userId;
    setViewing(next);
    setViewed(next);
    setProfile(null);
    const ask = ++asked.current;
    void api
      .profile()
      .catch(() => UNSAID)
      .then((read) => {
        if (asked.current === ask) {
          setProfile(read);
        }
      });
  }

  if (notOn) {
    return <NotOn />;
  }
  if (session === null || profile === null) {
    return null;
  }
  return (
    <ToastProvider>
      {/* A fresh shell for each log: nothing read for one person is kept into another's. */}
      <Shell
        key={viewed ?? ""}
        session={session}
        profile={profile}
        onProfile={setProfile}
        viewed={viewed}
        people={showsPicker(session) ? offered(session) : []}
        onLook={look}
      />
    </ToastProvider>
  );
}

// Neither a log of her own nor anybody's she may look at. An owner turns Sugary on per member; a phone
// signed in as the wrong person signs out from the account page.
function NotOn() {
  const { t } = useSettings();
  return (
    <main>
      <div className="screen">
        <div className="empty">
          <Icon name="slice" size={40} />
          <p className="empty-title">{t("notOnTitle")}</p>
          <p className="small">{t("notOnBody")}</p>
        </div>
        <a className="row-button account" href={accountUrl(window.location.href)}>
          <Icon name="person" size={20} />
          <span>{t("account")}</span>
        </a>
      </div>
    </main>
  );
}

// Whose log is open, for a reader an owner let read everyone's, and that somebody else's is read only.
function PersonPicker({
  people,
  signer,
  viewed,
  onLook,
}: {
  people: SugaryPerson[];
  signer: string | null;
  viewed: string | null;
  onLook: (userId: string) => void;
}) {
  const { t } = useSettings();
  const name = people.find((person) => person.userId === viewed)?.displayName;
  return (
    <div className="looking">
      <label className="looking-picker">
        <span className="looking-label">
          <Icon name="person" size={20} />
          {t("whoseLog")}
        </span>
        <select value={viewed ?? signer ?? ""} onChange={(event) => onLook(event.target.value)}>
          {people.map((person) => (
            <option key={person.userId} value={person.userId}>
              {person.userId === signer ? t("personYou", { name: person.displayName }) : person.displayName}
            </option>
          ))}
        </select>
      </label>
      {viewed !== null && name && (
        <p className="notice looking-note">
          <Icon name="eye" size={18} />
          <span>{t("viewingReadOnly", { name })}</span>
        </p>
      )}
    </div>
  );
}

function Shell({
  session,
  profile,
  onProfile,
  viewed,
  people,
  onLook,
}: {
  session: SessionState;
  profile: Profile;
  onProfile: (profile: Profile) => void;
  // Somebody else's log, read only, or null for her own.
  viewed: string | null;
  // Whose logs the picker offers; empty, and no picker, for anyone with nobody else to look at.
  people: SugaryPerson[];
  onLook: (userId: string) => void;
}) {
  const { t, language } = useSettings();
  const toast = useToast();
  const someoneElses = viewed !== null;
  const shown = visibleTabs(showsCycle(profile), someoneElses);
  const [tab, setTab] = useState<Tab>(() => tabOf(window.location.hash, shown));
  // A wide window sets the meal beside the log: one rail entry, both on screen together.
  const fused = fuses(shown, useWide());
  const workspace = fused && (tab === "meal" || tab === "log");
  // Each meal saved is a new log: a log already on screen beside the meal reads its days again.
  const [saves, setSaves] = useState(0);
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
  // Hers alone: never handed over while somebody else's log is open, which is read only.
  useEffect(() => {
    if (!someoneElses) {
      resync(language).catch(() => {});
    }
  }, [language, someoneElses]);

  return (
    <>
      <main className={workspace ? "fused" : undefined}>
        {people.length > 0 && <PersonPicker people={people} signer={session.person ?? null} viewed={viewed} onLook={onLook} />}
        {/* The meal screen stays mounted so an estimate in progress survives a look at the log. It is
            hers alone: somebody else's log has none. Beside the log, it is a column of its own; the same
            element either way, so a window crossing the width keeps the meal in progress. */}
        {!someoneElses && (
          <div className="meal-column" hidden={!workspace && tab !== "meal"}>
            <MealScreen
              active={workspace || tab === "meal"}
              docked={workspace}
              adder={adder}
              estimates={session.estimates}
              labels={session.labels}
              person={session.person ?? null}
              onSaved={() => {
                toast({ text: t("saved") });
                setTab("log");
                setSaves((n) => n + 1);
                void refreshPending();
              }}
            />
          </div>
        )}
        {(tab === "log" || workspace) && (
          <LogScreen
            key={saves}
            estimates={session.estimates}
            labels={session.labels}
            readOnly={someoneElses}
            onChanged={() => void refreshPending()}
          />
        )}
        {tab === "glucose" && <GlucoseScreen readOnly={someoneElses} />}
        {tab === "cycle" && shown.includes("cycle") && <CycleScreen readOnly={someoneElses} />}
        {tab === "foods" && shown.includes("foods") && (
          <FoodsScreen
            labels={session.labels}
            onAddToMeal={
              // No meal is being built while somebody else's log is open.
              someoneElses
                ? undefined
                : (item) => {
                    adder.current?.(item);
                    toast({ text: t("addedToMeal"), action: { label: t("viewMeal"), run: () => setTab("meal") } });
                  }
            }
          />
        )}
        {tab === "settings" && shown.includes("settings") && <SettingsScreen profile={profile} onProfile={onProfile} readOnly={someoneElses} />}
      </main>

      {/* A bar along the bottom on a phone, a rail down the left on a desktop, which names the app. */}
      <nav className="tabs" aria-label="Sugary">
        <p className="rail-brand" aria-hidden="true">
          <Icon name="slice" size={24} />
          Sugary
        </p>
        {TABS.filter(({ id }) => railTabs(shown, fused).includes(id)).map(({ id, label, icon }) => (
          <button
            key={id}
            type="button"
            className={railTab(tab, fused) === id ? "tab tab-on" : "tab"}
            aria-current={railTab(tab, fused) === id ? "page" : undefined}
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
            {t(fused && id === "log" ? "tabMealLog" : label)}
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
