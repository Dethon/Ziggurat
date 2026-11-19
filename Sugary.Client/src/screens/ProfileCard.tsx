import { useCallback, useEffect, useId, useState } from "react";
import { api } from "../api";
import { useMinute } from "../clock";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { localDay } from "../cycle";
import { errorMessage } from "../errors";
import { formatDays, formatUnits } from "../format";
import { DIABETES_TYPES, latest, MEASUREMENT_KINDS, SEXES, UNIT } from "../profile";
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
  // Every one, the newest included, so the latest can be corrected too: the rows above add a new one.
  const history = measurements ?? [];
  const opened = sheet?.measurement;
  const newest = sheet && latest(measurements ?? [], sheet.kind);

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

      <label className="field" htmlFor={`${id}-diagnosed`}>
        <span className="field-label">{t("diagnosedOn")}</span>
        <input
          id={`${id}-diagnosed`}
          type="date"
          max={today}
          value={profile.diagnosedOn ?? ""}
          onChange={(event) => {
            const value = event.target.value || null;
            if (value === null || value <= today) {
              void save({ diagnosedOn: value });
            }
          }}
        />
      </label>

      {measurements && (
        <ul className="rows">
          {MEASUREMENT_KINDS.map((kind) => {
            const current = latest(measurements, kind);
            return (
              <li key={kind}>
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
              </li>
            );
          })}
        </ul>
      )}
      <p className="muted small">{t("measurementsHint")}</p>
      {history.length > 0 && (
        <details className="pen-history">
          <summary>
            {t("measurementHistory")}
            <Icon name="chevron" size={18} />
          </summary>
          <ul className="rows">
            {history.map((m) => (
              <li key={m.id}>
                <button
                  type="button"
                  className="row-link period-row measurement-row"
                  aria-haspopup="dialog"
                  aria-label={`${t(`measurementEdit_${m.kind}`)}, ${day(m.on)}`}
                  onClick={() => setSheet({ kind: m.kind, measurement: m })}
                >
                  <Icon name={ICON[m.kind]} size={18} />
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
      {sheet && (
        <MeasurementSheet
          kind={sheet.kind}
          measurement={opened}
          initial={
            opened
              ? { on: opened.on, value: String(opened.value) }
              : { on: today, value: newest ? String(newest.value) : "" }
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
