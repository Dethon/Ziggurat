import { useState } from "react";
import { api, ApiError } from "../api";
import { errorMessage } from "../errors";
import { useSettings } from "../settings";

export function SignIn({ onSignedIn }: { onSignedIn: () => void }) {
  const { t } = useSettings();
  const [code, setCode] = useState("");
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
    <form
      className="sign-in"
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
    >
      <img src="/icon.svg" alt="" width={72} height={72} />
      <h1>{t("signInTitle")}</h1>
      <label className="field">
        <span className="field-label">{t("signInPrompt")}</span>
        <input
          type="password"
          autoComplete="current-password"
          value={code}
          autoFocus
          onChange={(event) => setCode(event.target.value)}
        />
      </label>
      {error && (
        <p className="notice notice-error" role="alert">
          {error}
        </p>
      )}
      <button type="submit" className="button button-primary button-big" disabled={busy || !code}>
        {t("signIn")}
      </button>
    </form>
  );
}
