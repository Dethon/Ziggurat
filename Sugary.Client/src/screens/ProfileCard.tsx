import { useCallback, useEffect, useId, useState } from "react";
import { api } from "../api";
import { useMinute } from "../clock";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { localDay } from "../cycle";
import { errorMessage } from "../errors";
import { formatDays, formatUnits } from "../format";
import { MeasurementChart } from "../components/MeasurementChart";
import { DIABETES_TYPES, history, latest, MEASUREMENT_KINDS, SEXES, UNIT } from "../profile";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import type { DiabetesType, Measurement, MeasurementKind, Profile } from "../types";
import { MeasurementSheet } from "./MeasurementSheet";

const ICON = { height: "ruler", weight: "scale" } as const;

// Who she is: her sex (the Cycle tab follows it), her diabetes and when it was diagnosed, each saved as
// she changes it; and her height and weight as a history of days. The agent reads all of it beside
// her log; nothing she leaves empty is filled in for her.
export function ProfileCard({ profile, onChange }: { profile: Profile; onChange: (profile: Profile) => void }) {
  const { t, language } = useSettings();
  const toast = useToast();
  const [confirm, confirming] = useConfirm();
  const id = useId();
  const today = localDay(new Date(useMinute()));
  const thisYear = today.slice(0, 4);
  const [measurements, setMeasurements] = useState<Measurement[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sheet, setSheet] = useState<{ kind: MeasurementKind; measurement?: Measurement } | null>(null);

  const load = useCallback(async () => {
    try {
      setMeasurements(await api.measurements());
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
  }, [load]);

  async function save(change: Partial<Profile>) {
    setError(null);
    try {
      onChange(await api.saveProfile({ ...profile, ...change }, profile));
      toast({ text: t("profileSaved") });
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  async function remove(measurement: Measurement) {
    if (!(await confirm({ title: t("measurementDeleteTitle"), body: t("measurementDeleteBody"), confirm: t("deleteMeasurement") }))) {
      return;
    }
    setSheet(null);
    try {
      await api.deleteMeasurement(measurement.id);
      await load();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  const day = (value: string) => formatDays(value, value, language, thisYear);
  const shownValue = (m: Measurement) => `${formatUnits(m.value, language)} ${UNIT[m.kind]}`;
  const opened = sheet?.measurement;

  return (
    <section className="card setting" aria-labelledby={`${id}-title`}>
      <div>
        <h2 id={`${id}-title`}>{t("profileTitle")}</h2>
        <p className="muted small">{t("profileHint")}</p>
      </div>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}

      <div className="field">
        <span className="field-label" id={`${id}-sex`}>
          {t("sex")}
        </span>
        <div className="segmented" role="group" aria-labelledby={`${id}-sex`}>
          {SEXES.map((sex) => (
            <button
              key={sex}
              type="button"
              className={profile.sex === sex ? "segment segment-on" : "segment"}
              aria-pressed={profile.sex === sex}
              onClick={() => void save({ sex: profile.sex === sex ? null : sex })}
            >
              {t(`sex_${sex}`)}
            </button>
          ))}
        </div>
        <span className="muted small">{t("sexHint")}</span>
      </div>

      <label className="field" htmlFor={`${id}-type`}>
        <span className="field-label">{t("diabetesType")}</span>
        <select
          id={`${id}-type`}
          value={profile.diabetesType ?? ""}
          onChange={(event) => void save({ diabetesType: (event.target.value || null) as DiabetesType | null })}
        >
          <option value="">{t("diabetesTypeUnsaid")}</option>
          {DIABETES_TYPES.map((type) => (
            <option key={type} value={type}>
              {t(`diabetes_${type}`)}
            </option>
          ))}
        </select>
      </label>

      <DayField
        label={t("bornOn")}
        saved={profile.bornOn}
        min="1900-01-01"
        max={today}
        onSave={(bornOn) => void save({ bornOn })}
      />
      <DayField
        label={t("diagnosedOn")}
        saved={profile.diagnosedOn}
        min={profile.bornOn ?? "1900-01-01"}
        max={today}
        onSave={(diagnosedOn) => void save({ diagnosedOn })}
      />

      {measurements && (
        <div className="rows">
          {MEASUREMENT_KINDS.map((kind) => {
            const current = latest(measurements, kind);
            const past = history(measurements, kind);
            return (
              <div key={kind} className="measurement-kind">
                <button
                  type="button"
                  className="row-link period-row measurement-row"
                  aria-haspopup="dialog"
                  aria-label={t(`measurementAdd_${kind}`)}
                  onClick={() => setSheet({ kind })}
                >
                  <Icon name={ICON[kind]} size={20} />
                  <span className="period-row-text">
                    <span className="item-name">{t(`measure_${kind}`)}</span>
                    <span className="item-meta">
                      {current ? `${shownValue(current)} · ${t("measurementSince", { date: day(current.on) })}` : t("measurementNone")}
                    </span>
                  </span>
                  <Icon name="plus" size={20} />
                </button>
                {past.length > 0 && (
                  <MeasurementChart
                    kind={kind}
                    measurements={past}
                    label={t(`measurementChart_${kind}`)}
                    onOpen={(measurement) => setSheet({ kind, measurement })}
                  />
                )}
                {past.length > 0 && (
                  <details className="pen-history">
                    <summary>
                      {t("measurementHistory", { n: past.length })}
                      <Icon name="chevron" size={18} />
                    </summary>
                    <ul className="rows">
                      {past.map((m) => (
                        <li key={m.id}>
                          <button
                            type="button"
                            className="row-link period-row measurement-row"
                            aria-haspopup="dialog"
                            aria-label={`${t(`measurementEdit_${m.kind}`)}, ${day(m.on)}`}
                            onClick={() => setSheet({ kind: m.kind, measurement: m })}
                          >
                            <span className="period-row-text">
                              <span className="item-name">{shownValue(m)}</span>
                              <span className="item-meta">{day(m.on)}</span>
                            </span>
                            <Icon name="pencil" size={18} />
                          </button>
                        </li>
                      ))}
                    </ul>
                  </details>
                )}
              </div>
            );
          })}
        </div>
      )}
      <p className="muted small">{t("measurementsHint")}</p>
      {sheet && (
        <MeasurementSheet
          kind={sheet.kind}
          measurement={opened}
          initial={
            opened
              ? { on: opened.on, value: formatUnits(opened.value, language) }
              : { on: today, value: "" }
          }
          others={measurements ?? []}
          today={today}
          onClose={() => setSheet(null)}
          onSaved={() => {
            setSheet(null);
            toast({ text: t("measurementSaved") });
            void load();
          }}
          onDelete={(m) => void remove(m)}
        />
      )}
      {confirming}
    </section>
  );
}

// A day of hers, saved when she leaves the field: a typed year passes through 0002 and 0020 on its way
// to 2015, and each save of those would race the next. Emptied, it is taken back.
function DayField({
  label,
  saved,
  min,
  max,
  onSave,
}: {
  label: string;
  saved: string | null;
  min: string;
  max: string;
  onSave: (day: string | null) => void;
}) {
  const id = useId();
  const [typed, setTyped] = useState(saved ?? "");
  useEffect(() => setTyped(saved ?? ""), [saved]);
  return (
    <label className="field" htmlFor={id}>
      <span className="field-label">{label}</span>
      <input
        id={id}
        type="date"
        min={min}
        max={max}
        value={typed}
        onChange={(event) => setTyped(event.target.value)}
        onBlur={() => {
          const day = typed || null;
          if (day !== saved && (day === null || (day >= min && day <= max))) {
            onSave(day);
          }
        }}
      />
    </label>
  );
}
