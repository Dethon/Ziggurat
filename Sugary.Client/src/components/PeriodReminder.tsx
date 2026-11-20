import { useEffect, useState } from "react";
import { api } from "../api";
import { errorMessage } from "../errors";
import { pushState, turnOn, type PushState } from "../push";
import { useSettings } from "../settings";
import { Icon } from "./Icon";

// Her phones told two days before her period (the server's PeriodReminders): off until she turns it
// on, since it shows on a lock screen. Turning it on here also lets this phone show reminders.
export function PeriodReminder() {
  const { t, language } = useSettings();
  const [on, setOn] = useState<boolean | null>(null);
  const [phone, setPhone] = useState<PushState | null>(null);
  const [publicKey, setPublicKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.cycleReminder().then(({ on }) => setOn(on), () => setOn(null));
    api.push().then(
      async ({ publicKey }) => {
        setPublicKey(publicKey);
        setPhone(await pushState(publicKey));
      },
      () => setPhone("unavailable"),
    );
  }, []);

  async function change(next: boolean) {
    setError(null);
    try {
      if (next && phone === "off" && publicKey) {
        setPhone(await turnOn(publicKey, language));
      }
      setOn((await api.setCycleReminder(next)).on);
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  if (on === null || phone === null || phone === "unavailable") {
    return null;
  }
  return (
    <div className="period-reminder">
      {on ? (
        <p className="period-reminder-on">
          <Icon name="bell" size={18} />
          <span>{t("periodReminderOn")}</span>
          <button type="button" className="button button-quiet button-small" onClick={() => void change(false)}>
            {t("remindersTurnOff")}
          </button>
        </p>
      ) : (
        <button type="button" className="button button-secondary" onClick={() => void change(true)}>
          <Icon name="bell" size={20} />
          {t("periodReminderTurnOn")}
        </button>
      )}
      {on && (phone === "denied" || phone === "unsupported") && (
        <p className="muted small">{t(phone === "denied" ? "remindersDenied" : "remindersUnsupported")}</p>
      )}
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
    </div>
  );
}
