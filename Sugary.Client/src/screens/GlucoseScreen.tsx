import { useCallback, useEffect, useId, useState } from "react";
import { api } from "../api";
import { useConfirm } from "../components/Confirm";
import { GlucoseChart } from "../components/GlucoseChart";
import { Icon } from "../components/Icon";
import { useMinute } from "../clock";
import {
  ageMinutes,
  ageText,
  band,
  formatGlucose,
  formatShare,
  glucoseErrorMessage,
  HIGH,
  HIGHEST,
  isStale,
  LOW,
  LOWEST,
  linkFailureText,
  PERIODS,
  rangeDraft,
  rangeProblem,
  REGIONS,
  retypeRange,
  shownRange,
  switchRangeUnit,
  trendOf,
  UNIT_LABEL,
  VERY_HIGH,
  VERY_LOW,
  windowOf,
  type Period,
} from "../glucose";
import { errorMessage } from "../errors";
import { formatShortDay, formatTime, locale } from "../format";
import type { Language, Translate } from "../i18n";
import { overRecord, useShown } from "../stale";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { GlucoseCurve, GlucoseLink, GlucoseNow, GlucoseSettings, GlucoseStatistics, GlucoseUnit } from "../types";

const UNITS: GlucoseUnit[] = ["mgdl", "mmol"];

const periodLabel = (days: Period, t: Translate) => (days === 1 ? t("glucosePeriodHours", { n: 24 }) : t("glucosePeriodDays", { n: days }));

// Her glucose: where she is now, how her days went, what a clinic would read off them, and the
// LibreLinkUp account it all comes from. `readOnly` is somebody else's: her readings and figures, but
// never her account, which is hers to link, change or unlink.
export function GlucoseScreen({ readOnly = false }: { readOnly?: boolean }) {
  const { t, language } = useSettings();
  const minute = useMinute();
  const [state, setState] = useState<{ now: GlucoseNow; fetchedAt: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [period, setPeriod] = useState<Period>(1);
  const [curve, setCurve] = useState<GlucoseCurve | null>(null);
  const [statistics, setStatistics] = useState<GlucoseStatistics | null>(null);

  const load = useCallback(async () => {
    try {
      const now = await api.glucoseNow();
      setState({ now, fetchedAt: Date.now() });
      setError(null);
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  // Read again every minute, as the poller does: the reading on screen is never older than that.
  useEffect(() => {
    void load();
  }, [load, minute]);

  const newest = state?.now.reading?.at ?? null;
  // The curve and the figures move only when a reading arrives, or she picks another period.
  useEffect(() => {
    if (newest === null) {
      return;
    }
    const window = windowOf(period, Date.now());
    let current = true;
    Promise.all([api.glucoseCurve(window.from, window.to), api.glucoseStatistics(period)]).then(
      ([points, figures]) => {
        if (current) {
          setCurve(points);
          setStatistics(figures);
        }
      },
      (failure) => current && setError(errorMessage(failure, t)),
    );
    return () => {
      current = false;
    };
  }, [newest, period, t]);

  const now = state?.now;
  return (
    <div className="screen">
      <h1 className="marked">
        <Icon name="curve" size={28} />
        {t("tabGlucose")}
      </h1>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}

      {/* On a desktop her reading leads; under it one tray holds what the period chooses (her curve
          and her figures, side by side, the periods heading both), and her sensor, which no period
          touches, stands outside it. */}
      <div className="panes panes-glucose">
        <div className="pane pane-lead">
          {now && (now.reading || now.link) && <Current now={now} fetchedAt={state.fetchedAt} />}

          {now?.reading && (
            <div className="glucose-period">
              <div className="glucose-period-head">
                <h2 id="period-title" className="glucose-period-title">
                  {t("glucosePeriodScope")}
                </h2>
                <div className="segmented" role="group" aria-labelledby="period-title">
                  {PERIODS.map((days) => (
                    <button
                      key={days}
                      type="button"
                      className={period === days ? "segment segment-on" : "segment"}
                      aria-pressed={period === days}
                      onClick={() => setPeriod(days)}
                    >
                      {periodLabel(days, t)}
                    </button>
                  ))}
                </div>
              </div>
              <section className="card" aria-labelledby="curve-title">
                <h2 id="curve-title" className="section-title">
                  {t("curveTitle")}
                </h2>
                {curve && curve.points.length > 0 ? (
                  <GlucoseChart
                    curve={curve}
                    unit={now.unit}
                    targetLow={now.targetLow}
                    targetHigh={now.targetHigh}
                    label={t("curveSpoken", {
                      period: periodLabel(period, t),
                      low: formatGlucose(now.targetLow, now.unit, language),
                      high: formatGlucose(now.targetHigh, now.unit, language),
                      unit: UNIT_LABEL[now.unit],
                    })}
                  />
                ) : (
                  curve && <p className="muted small">{t("curveEmpty")}</p>
                )}
              </section>
              {statistics && <Statistics figures={statistics} unit={now.unit} />}
            </div>
          )}
        </div>

        <div className="pane pane-side">
          {now && !readOnly && <LinkCard now={now} onChanged={() => void load()} />}
        </div>
      </div>
    </div>
  );
}

function Current({ now, fetchedAt }: { now: GlucoseNow; fetchedAt: number }) {
  const { t, language } = useSettings();
  const clock = useMinute();
  const { reading, link, unit } = now;
  if (!reading) {
    return (
      <section className="card glucose-now" aria-labelledby="glucose-now-title">
        <h2 id="glucose-now-title" className="section-title">
          {t("glucoseNoReading")}
        </h2>
        <p className="muted small">{t("glucoseNoReadingHint")}</p>
      </section>
    );
  }

  const minutes = ageMinutes(reading.ageSeconds, fetchedAt, Math.max(clock, fetchedAt));
  const stale = isStale(minutes);
  const trend = trendOf(reading.trend);
  const value = formatGlucose(reading.mgPerDl, unit, language);
  const age = ageText(minutes, t);
  return (
    <section
      className={`card glucose-now glucose-${band(reading.mgPerDl, now.targetLow, now.targetHigh)}${stale ? " glucose-stale" : ""}`}
      aria-label={t("glucoseReadingSpoken", { value, unit: UNIT_LABEL[unit], trend: t(trend.label), age })}
    >
      <div className="glucose-reading" aria-hidden="true">
        <span className="glucose-value">{value}</span>
        <span className="glucose-unit">{UNIT_LABEL[unit]}</span>
        <span className="glucose-trend" style={{ transform: `rotate(${trend.rotation}deg)` }}>
          <Icon name={trend.icon} size={40} />
        </span>
      </div>
      <p className="glucose-meta" aria-hidden="true">
        {t(trend.label)} · {age}
      </p>
      {stale && (
        <p className="notice notice-caution">
          <Icon name="clock" size={18} />
          <span>{t("glucoseStale")}</span>
        </p>
      )}
      {link?.lastFailure && link.failingSince && (
        <p className="notice notice-caution" role="status">
          <Icon name="alert" size={18} />
          <span>
            {t("glucoseFailingSince", { time: when(link.failingSince, language) })} {linkFailureText(link.lastFailure, t)}
          </span>
        </p>
      )}
    </section>
  );
}

// "14:05", or "mon 13 14:05" when it was not in the last day.
function when(iso: string, language: Language): string {
  const time = formatTime(iso, language);
  return Date.now() - Date.parse(iso) < 20 * 3_600_000 ? time : `${formatShortDay(iso, language)} ${time}`;
}

function Statistics({ figures, unit }: { figures: GlucoseStatistics; unit: GlucoseUnit }) {
  const { t, language } = useSettings();
  const threshold = (mgPerDl: number) => formatGlucose(mgPerDl, unit, language);
  const bands = [
    { key: "veryHigh", label: t("statsVeryHigh", { n: threshold(VERY_HIGH) }), share: figures.veryHigh },
    { key: "high", label: t("statsHigh", { n: threshold(HIGH) }), share: figures.high },
    {
      key: "inRange",
      label: t("statsInRange", { low: threshold(figures.targetLow), high: threshold(figures.targetHigh) }),
      share: figures.inRange,
    },
    { key: "low", label: t("statsLow", { n: threshold(LOW) }), share: figures.low },
    { key: "veryLow", label: t("statsVeryLow", { n: threshold(VERY_LOW) }), share: figures.veryLow },
  ];
  return (
    <section className="card" aria-labelledby="stats-title">
      <h2 id="stats-title" className="section-title">
        {t("statsTitle")}
      </h2>
      {figures.count === 0 || figures.mean === null ? (
        <p className="muted small">{t("curveEmpty")}</p>
      ) : (
        <>
          <ul className="glucose-bands">
            {bands.map(({ key, label, share }) => (
              <li key={key} className={`glucose-band glucose-band-${key}`}>
                <span className="glucose-band-label">{label}</span>
                <span className="glucose-band-value">{formatShare(share ?? 0, language)}</span>
                <span className="glucose-band-bar" aria-hidden="true">
                  <span style={{ width: `${Math.round((share ?? 0) * 100)}%` }} />
                </span>
              </li>
            ))}
          </ul>
          <p className="muted small">{t("statsCumulative")}</p>
          <dl className="glucose-figures">
            <div>
              <dt>{t("statsMean")}</dt>
              <dd>
                {formatGlucose(figures.mean, unit, language)} <span className="muted">{UNIT_LABEL[unit]}</span>
              </dd>
            </div>
            <div>
              <dt>{t("statsGmi")}</dt>
              <dd>{figures.gmi === null ? "" : `${figures.gmi.toLocaleString(locale(language), { maximumFractionDigits: 1, minimumFractionDigits: 1 })}%`}</dd>
            </div>
            <div>
              <dt>{t("statsCv")}</dt>
              <dd>{figures.cv === null ? "" : formatShare(figures.cv, language)}</dd>
            </div>
            <div>
              <dt>{t("statsCoverage")}</dt>
              <dd>
                {formatShare(figures.coverage, language)}
                <span className="glucose-figure-note">{t("statsReadings", { n: figures.count })}</span>
              </dd>
            </div>
          </dl>
          {figures.coverage < 0.7 && (
            <p className="notice">
              <Icon name="info" size={18} />
              <span>{t("statsLowCoverage", { n: Math.round(figures.coverage * 100) })}</span>
            </p>
          )}
        </>
      )}
    </section>
  );
}

// The account her readings come from: the form while there is none (or its password was refused),
// a choice where it follows several people, and otherwise its state, how she reads glucose and a way
// out.
function LinkCard({ now, onChanged }: { now: GlucoseNow; onChanged: () => void }) {
  const { t, language } = useSettings();
  const toast = useToast();
  const [confirm, confirming] = useConfirm();
  const [error, setError] = useState<string | null>(null);
  const [relinking, setRelinking] = useState(false);
  const link = now.link;

  async function choose(patientId: string) {
    setError(null);
    try {
      await api.chooseGlucoseConnection(patientId);
      onChanged();
    } catch (failure) {
      setError(glucoseErrorMessage(failure, t));
    }
  }

  async function unlink() {
    if (!(await confirm({ title: t("glucoseUnlinkTitle"), body: t("glucoseUnlinkBody"), confirm: t("glucoseUnlink") }))) {
      return;
    }
    try {
      await api.unlinkGlucose();
      toast({ text: t("glucoseUnlinked") });
      onChanged();
    } catch (failure) {
      setError(glucoseErrorMessage(failure, t));
    }
  }

  // A password refused, or one this server can no longer open: either way she types it again.
  const refused = link?.lastFailure === "credentials-refused" || link?.lastFailure === "credentials-unreadable";
  return (
    <section className="card setting" aria-labelledby="glucose-link-title">
      <div>
        <h2 id="glucose-link-title">{t("glucoseLinkTitle")}</h2>
        {!link && <p className="muted small">{t("glucoseLinkIntro")}</p>}
      </div>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}

      {!link && !now.canLink && (
        <p className="notice">
          <Icon name="info" size={18} />
          <span>{now.reading ? `${t("glucoseLinkingOffNote")} ${t("linkingOff")}` : t("linkingOff")}</span>
        </p>
      )}
      {!link && now.canLink && <LinkForm onLinked={onChanged} />}

      {link && link.patientId === null && (
        <div className="glucose-choose">
          <h3 className="field-label">{t("glucoseChooseTitle")}</h3>
          <p className="muted small">{t("glucoseChooseHint")}</p>
          <ul className="rows">
            {link.connections.map((connection) => (
              <li key={connection.patientId}>
                <button type="button" className="row-link glucose-connection" onClick={() => void choose(connection.patientId)}>
                  <Icon name="person" />
                  <span>{connection.name}</span>
                  <Icon name="chevronRight" size={20} />
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {link && link.patientId !== null && (
        <>
          <div className="glucose-linked">
            <span className="item-name">{t("glucoseLinkedAs", { name: link.name ?? link.email })}</span>
            <span className="item-meta">{link.email}</span>
            {link.lastFailure ? (
              <span className="item-meta glucose-link-failing">
                {link.failingSince && `${t("glucoseFailingSince", { time: when(link.failingSince, language) })} `}
                {linkFailureText(link.lastFailure, t)}
              </span>
            ) : (
              <span className="item-meta glucose-link-ok">
                <Icon name="checkCircle" size={16} /> {t("glucoseLinkWorking")}
              </span>
            )}
          </div>
          {(refused || relinking) && now.canLink ? (
            <LinkForm email={link.email} onLinked={() => {
              setRelinking(false);
              onChanged();
            }} />
          ) : (
            now.canLink && (
              <button type="button" className="button button-quiet" onClick={() => setRelinking(true)}>
                <Icon name="restart" size={20} />
                {t("glucoseRelink")}
              </button>
            )
          )}
          <SettingsForm key={`${link.unit}:${link.targetLow}:${link.targetHigh}`} link={link} onSaved={onChanged} />
        </>
      )}

      {link && (
        <button type="button" className="button button-quiet danger" onClick={() => void unlink()}>
          <Icon name="trash" size={20} />
          {t("glucoseUnlink")}
        </button>
      )}
      {confirming}
    </section>
  );
}

function LinkForm({ email: kept = "", onLinked }: { email?: string; onLinked: () => void }) {
  const { t } = useSettings();
  const [email, setEmail] = useState(kept);
  const [password, setPassword] = useState("");
  const [region, setRegion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const id = useId();

  async function link() {
    setBusy(true);
    setError(null);
    try {
      await api.linkGlucose({ email: email.trim(), password, region: region || null });
      setPassword("");
      onLinked();
    } catch (failure) {
      setError(glucoseErrorMessage(failure, t));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="sheet-form"
      onSubmit={(event) => {
        event.preventDefault();
        void link();
      }}
    >
      <label className="field">
        <span className="field-label">{t("glucoseEmail")}</span>
        <input type="email" required autoComplete="off" value={email} onChange={(event) => setEmail(event.target.value)} />
      </label>
      <label className="field">
        <span className="field-label">{t("glucosePassword")}</span>
        <input
          type="password"
          required
          autoComplete="new-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
        <span className="muted small">{t("glucosePasswordKept")}</span>
      </label>
      <label className="field" htmlFor={`${id}-region`}>
        <span className="field-label">{t("glucoseRegion")}</span>
        <select id={`${id}-region`} value={region} onChange={(event) => setRegion(event.target.value)}>
          <option value="">{t("glucoseRegionAuto")}</option>
          {REGIONS.map((code) => (
            <option key={code} value={code}>
              {code.toUpperCase()}
            </option>
          ))}
        </select>
        <span className="muted small">{t("glucoseRegionHint")}</span>
      </label>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      <button type="submit" className="button button-primary" disabled={busy || email.trim() === "" || password === ""}>
        {t(busy ? "glucoseLinking" : "glucoseLink")}
      </button>
    </form>
  );
}

// Her unit and range, typed in her unit and kept in mg/dL. A field she did not touch goes back as
// recorded (overRecord), so a range shown in mmol/L and rounded is never taken for her change.
function SettingsForm({ link, onSaved }: { link: GlucoseLink; onSaved: () => void }) {
  const { t } = useSettings();
  const toast = useToast();
  const recorded: GlucoseSettings = { unit: link.unit, targetLow: link.targetLow, targetHigh: link.targetHigh };
  const [draft, setDraft] = useState(() => rangeDraft(link.targetLow, link.targetHigh, link.unit));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { unit } = draft;
  const values: GlucoseSettings = { unit, targetLow: draft.low, targetHigh: draft.high };
  const shown = useShown(values);
  const changed = JSON.stringify(values) !== JSON.stringify(shown);
  const wrong = rangeProblem(values.targetLow, values.targetHigh);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.saveGlucoseSettings(overRecord(recorded, shown, values), recorded);
      toast({ text: t("glucoseSettingsSaved") });
      onSaved();
    } catch (failure) {
      setError(glucoseErrorMessage(failure, t));
      setBusy(false);
    }
  }

  const step = unit === "mmol" ? 0.1 : 1;
  const bound = (mgPerDl: number) => shownRange(mgPerDl, mgPerDl, unit).low;
  return (
    <form
      className="sheet-form glucose-settings"
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <h3 className="field-label">{t("glucoseSettingsTitle")}</h3>
      <div className="segmented" role="group" aria-label={t("glucoseUnit")}>
        {UNITS.map((option) => (
          <button
            key={option}
            type="button"
            className={unit === option ? "segment segment-on" : "segment"}
            aria-pressed={unit === option}
            onClick={() => setDraft(switchRangeUnit(draft, option))}
          >
            {UNIT_LABEL[option]}
          </button>
        ))}
      </div>
      <fieldset className="glucose-range">
        <legend className="field-label">{t("glucoseTarget")}</legend>
        <label className="field">
          <span className="field-label">{t("glucoseTargetLow")}</span>
          <input
            type="number"
            inputMode="decimal"
            step={step}
            min={bound(LOWEST)}
            max={bound(HIGHEST)}
            value={draft.lowText}
            onChange={(event) => setDraft(retypeRange(draft, "low", event.target.value))}
          />
        </label>
        <label className="field">
          <span className="field-label">{t("glucoseTargetHigh")}</span>
          <input
            type="number"
            inputMode="decimal"
            step={step}
            min={bound(LOWEST)}
            max={bound(HIGHEST)}
            value={draft.highText}
            onChange={(event) => setDraft(retypeRange(draft, "high", event.target.value))}
          />
        </label>
      </fieldset>
      {wrong && (
        <p className="field-problem" role="alert">
          {t("glucoseRangeProblem", { low: bound(LOWEST), high: bound(HIGHEST), unit: UNIT_LABEL[unit] })}
        </p>
      )}
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      {changed && (
        <button type="submit" className="button button-primary" disabled={busy || wrong}>
          {t("glucoseSaveSettings")}
        </button>
      )}
    </form>
  );
}
