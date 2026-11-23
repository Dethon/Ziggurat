import { useEffect, useState } from "react";
import { api } from "../api";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import { errorMessage } from "../errors";
import { formatDayTime } from "../format";
import { useSettings } from "../settings";
import { useToast } from "../toast";
import { widgetImageAddress } from "../widget";

// The address a KWGT widget reads this log's glucose by. It is answered once, when made, and held here
// only until Done: the server keeps its hash. It is the signer's own, so it is offered on a log they are
// only reading too, and it ends when this device's session does.
export function WidgetCard() {
  const { t, language } = useSettings();
  const toast = useToast();
  const [confirm, confirming] = useConfirm();
  const [createdAt, setCreatedAt] = useState<string | null | undefined>(undefined);
  const [address, setAddress] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.widget().then(
      (state) => setCreatedAt(state.createdAt),
      (failure) => setError(errorMessage(failure, t)),
    );
  }, [t]);

  async function mint() {
    setError(null);
    try {
      const minted = await api.mintWidget();
      setCreatedAt(minted.createdAt);
      setAddress(widgetImageAddress(window.location.origin, minted.token));
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  async function replace() {
    if (await confirm({ title: t("widgetReplaceTitle"), body: t("widgetReplaceBody"), confirm: t("widgetReplace") })) {
      await mint();
    }
  }

  async function remove() {
    if (!(await confirm({ title: t("widgetRemoveTitle"), body: t("widgetRemoveBody"), confirm: t("widgetRemove") }))) {
      return;
    }
    setError(null);
    try {
      await api.removeWidget();
      setCreatedAt(null);
      setAddress(null);
    } catch (failure) {
      setError(errorMessage(failure, t, "save"));
    }
  }

  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      toast({ text: t("widgetCopied") });
    } catch {
      setError(t("widgetCopyFailed"));
    }
  }

  return (
    <section className="card setting" aria-labelledby="widget-title">
      <div>
        <h2 id="widget-title">{t("widgetTitle")}</h2>
        <p className="muted small">{t("widgetHint")}</p>
      </div>
      {error && (
        <p className="notice notice-error" role="alert">
          <Icon name="alert" size={18} /> <span>{error}</span>
        </p>
      )}
      {address ? (
        <>
          <p className="notice" role="status">
            <Icon name="alert" size={18} /> <span>{t("widgetWarning")}</span>
          </p>
          <p className="muted small">{t("widgetSteps")}</p>
          <div className="widget-formula">
            <div>
              <strong className="small">{t("widgetImage")}</strong>
              <code className="small">{address}</code>
            </div>
            <button
              type="button"
              className="button button-quiet"
              aria-label={t("widgetCopy", { what: t("widgetImage") })}
              onClick={() => void copy(address)}
            >
              <Icon name="copy" size={20} />
            </button>
          </div>
          <button type="button" className="button button-secondary" onClick={() => setAddress(null)}>
            {t("widgetDone")}
          </button>
        </>
      ) : createdAt ? (
        <>
          <p className="small">{t("widgetActive", { date: formatDayTime(createdAt, language) })}</p>
          <div className="widget-actions">
            <button type="button" className="button button-secondary" onClick={() => void replace()}>
              {t("widgetReplace")}
            </button>
            <button type="button" className="button button-quiet danger" onClick={() => void remove()}>
              {t("widgetRemove")}
            </button>
          </div>
        </>
      ) : createdAt === null ? (
        <button type="button" className="button button-secondary" onClick={() => void mint()}>
          {t("widgetCreate")}
        </button>
      ) : null}
      {confirming}
    </section>
  );
}
