import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { localDay } from "../cycle";
import { DOSE_ICON } from "../dose";
import { errorMessage } from "../errors";
import { useMinute } from "../clock";
import { formatDays } from "../format";
import type { Translate } from "../i18n";
import { canStartNext, DEFAULT_PEN_DAYS, nextPen, openPens, PEN_KINDS, penStatus, replacedPens, type PenStatus } from "../pens";
import { pushState, turnOff, turnOn, type PushState } from "../push";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import { PenSheet } from "./PenSheet";
import type { DoseKind, Pen, SavePen } from "../types";

function statusText(status: PenStatus, replaceOn: string, date: (day: string) => string, t: Translate): string {
  switch (status.kind) {
    case "left":
      return t("penLeft", { n: status.days, date: date(replaceOn) });
    case "tomorrow":
      return t("penTomorrow");
    case "today":
      return t("penToday");
    case "overdue":
      return status.days === 1 ? t("penOverdueOne") : t("penOverdue", { n: status.days });
  }
}

// Her open pen of each kind with the day it is replaced, a tap to start the next one, the pens
// before it, and whether this phone is told when one is due.
export function PensCard() {
  const { t, language } = useSettings();
  const toast = useToast();
  const [confirm, confirming] = useConfirm();
  const today = localDay(new Date(useMinute()));
  const thisYear = today.slice(0, 4);
  const [pens, setPens] = useState<Pen[] | null>(null);
  const [publicKey, setPublicKey] = useState<string | null>(null);
  const [reminders, setReminders] = useState<PushState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sheet, setSheet] = useState<{ pen?: Pen; initial: SavePen } | null>(null);
  const day = (value: string) => formatDays(value, value, language, thisYear);

  const load = useCallback(async () => {
    try {
      setPens(await api.pens());
    } catch (failure) {
      setError(errorMessage(failure, t));
    }
  }, [t]);

  useEffect(() => {
    void load();
    void api.push().then(
      async ({ publicKey }) => {
        setPublicKey(publicKey);
        setReminders(await pushState(publicKey));
      },
      () => setReminders("unavailable"),
    );
  }, [load]);

  async function startNext(pen: Pen) {
    if (!(await confirm({ title: t("penNewTitle", { insulin: pen.insulin }), body: t("penNewBody"), confirm: t("penNew") }))) {
      return;
    }
    try {
      await api.createPen(nextPen(pen, today));
      toast({ text: t("penSaved") });
      await load();
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  async function remove(pen: Pen) {
    if (!(await confirm({ title: t("penDeleteTitle"), body: t("penDeleteBody"), confirm: t("deletePen") }))) {
      return;
    }
    try {
      await api.deletePen(pen.id);
      setSheet(null);
      await load();
    } catch (failure) {
      setSheet(null);
      setError(errorMessage(failure, t, "save"));
    }
  }

  async function switchReminders(on: boolean) {
    setError(null);
    try {
      if (on && publicKey) {
        setReminders(await turnOn(publicKey, language));
      } else {
        await turnOff();
        setReminders("off");
      }
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  const open = openPens(pens ?? []);
  const add = (kind: DoseKind) => setSheet({ initial: { kind, insulin: "", openedOn: today, days: DEFAULT_PEN_DAYS } });
  const edit = (pen: Pen) =>
    setSheet({ pen, initial: { kind: pen.kind, insulin: pen.insulin, openedOn: pen.openedOn, days: pen.days } });
  const history = PEN_KINDS.flatMap((kind) => replacedPens(pens ?? [], kind));

  return (
    <section className="card setting" aria-labelledby="pens-title">
      <div>
        <h2 id="pens-title">{t("pensTitle")}</h2>
        <p className="muted small">{t("pensHint")}</p>
      </div>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      {pens && (
        <ul className="rows">
          {PEN_KINDS.map((kind) => {
            const pen = open[kind];
            const status = pen && penStatus(pen, today);
            return (
              <li key={kind} className="pen-row">
                <button
                  type="button"
                  className="row-link period-row"
                  aria-haspopup="dialog"
                  aria-label={pen ? t("penEdit", { insulin: pen.insulin }) : t("penAdd")}
                  onClick={() => (pen ? edit(pen) : add(kind))}
                >
                  <span className={`dose-mark dose-mark-small dose-mark-${kind}`}>
                    <Icon name={DOSE_ICON[kind]} size={18} />
                  </span>
                  <span className="period-row-text">
                    <span className="item-name">{pen ? pen.insulin : t(`dose_${kind}`)}</span>
                    <span className="item-meta">
                      {pen ? `${t(`dose_${kind}`)} · ${t("penStarted", { date: day(pen.openedOn) })}` : t("penNone")}
                    </span>
                    {pen && status && (
                      <span className={status.kind === "left" ? "item-meta" : "item-meta pen-due"}>
                        {statusText(status, pen.replaceOn, day, t)}
                      </span>
                    )}
                  </span>
                  {!pen && <Icon name="plus" size={20} />}
                </button>
                {pen && canStartNext(pen, today) && (
                  <button type="button" className="button button-small" onClick={() => void startNext(pen)}>
                    <Icon name="insulin" size={18} />
                    {t("penNew")}
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {history.length > 0 && (
        <details className="pen-history">
          <summary>
            {t("penHistory")}
            <Icon name="chevron" size={18} />
          </summary>
          <ul className="rows">
            {history.map((pen) => (
              <li key={pen.id} className="pen-history-row">
                <span className="item-name">
                  {t(`dose_${pen.kind}`)} · {pen.insulin}
                </span>
                <span className="item-meta">{formatDays(pen.openedOn, pen.replacedOn!, language, thisYear)}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
      {reminders && <Reminders state={reminders} onSwitch={(on) => void switchReminders(on)} t={t} />}
      {sheet && (
        <PenSheet
          pen={sheet.pen}
          initial={sheet.initial}
          today={today}
          onClose={() => setSheet(null)}
          onSaved={() => {
            setSheet(null);
            toast({ text: t("penSaved") });
            void load();
          }}
          onDelete={(pen) => void remove(pen)}
        />
      )}
      {confirming}
    </section>
  );
}

function Reminders({ state, onSwitch, t }: { state: PushState; onSwitch: (on: boolean) => void; t: Translate }) {
  switch (state) {
    case "on":
      return (
        <p className="pen-reminders">
          <Icon name="checkCircle" size={18} />
          <span>{t("remindersOn")}</span>
          <button type="button" className="button button-quiet button-small" onClick={() => onSwitch(false)}>
            {t("remindersTurnOff")}
          </button>
        </p>
      );
    case "off":
      return (
        <button type="button" className="button button-primary" onClick={() => onSwitch(true)}>
          <Icon name="bell" size={20} />
          {t("remindersTurnOn")}
        </button>
      );
    default:
      return (
        <p className="muted small pen-reminders">
          <Icon name="info" size={18} />
          <span>
            {t(state === "denied" ? "remindersDenied" : state === "unsupported" ? "remindersUnsupported" : "remindersUnavailable")}
          </span>
        </p>
      );
  }
}
