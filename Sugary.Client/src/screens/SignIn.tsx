import { useState } from "react";
import { api, ApiError } from "../api";
import { Icon } from "../components/Icon";
import { errorMessage } from "../errors";
import { useSettings } from "../settings";

// The frosting on top of the sign-in, and nowhere else: decoration, never over content.
function Drip() {
  return (
    <svg className="drip" viewBox="0 0 390 72" preserveAspectRatio="none" aria-hidden="true">
      <path d="M0 0h390v34c-14 0-14 30-30 30s-15-30-31-30-18 14-36 14-14-22-34-22-20 34-38 34-14-26-34-26-17 12-36 12-17-20-36-20-16 30-34 30S83 34 64 34 46 46 29 46 12 34 0 34V0Z" />
    </svg>
  );
}

export function SignIn({ onSignedIn }: { onSignedIn: () => void }) {
  const { t } = useSettings();
  const [code, setCode] = useState("");
  const [shown, setShown] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.signIn(code);
      onSignedIn();
    } catch (failure) {
      setError(failure instanceof ApiError && failure.status === 401 ? t("wrongCode") : errorMessage(failure, t));
      setBusy(false);
    }
  }

  return (
    <div className="sign-in-page">
      <Drip />
      <form
        className="sign-in"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <img src="/icon.svg" alt="" width={104} height={104} />
        <div className="sign-in-title">
          <h1>{t("signInTitle")}</h1>
          <p className="tagline">{t("tagline")}</p>
        </div>
        <label className="field">
          <span className="field-label">{t("signInPrompt")}</span>
          <span className="secret">
            <input
              type={shown ? "text" : "password"}
              autoComplete="current-password"
              autoCapitalize="none"
              spellCheck={false}
              value={code}
              autoFocus
              aria-invalid={error !== null}
              aria-describedby={error ? "sign-in-error" : undefined}
              onChange={(event) => setCode(event.target.value)}
            />
            <button
              type="button"
              className="icon-button"
              aria-label={t(shown ? "hideCode" : "showCode")}
              aria-pressed={shown}
              onClick={() => setShown(!shown)}
            >
              <Icon name={shown ? "eyeOff" : "eye"} />
            </button>
          </span>
        </label>
        {error && (
          <p id="sign-in-error" className="notice notice-error" role="alert">
            <Icon name="alert" size={18} /> <span>{error}</span>
          </p>
        )}
        <button type="submit" className="button button-primary button-big" disabled={busy || !code}>
          {t("signIn")}
        </button>
      </form>
    </div>
  );
}
