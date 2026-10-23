import { api } from "../api";
import { useConfirm } from "../components/Confirm";
import { Icon } from "../components/Icon";
import type { Language } from "../i18n";
import { useSettings, type RationSize } from "../settings";
import { THEMES } from "../theme";
import { PensCard } from "./PensCard";

const EXPORTS = [
  { table: "meals", label: "exportMeals" },
  { table: "items", label: "exportItems" },
  { table: "doses", label: "exportDoses" },
  { table: "exercise", label: "exportExercise" },
  { table: "periods", label: "exportPeriods" },
  { table: "day-notes", label: "exportDayNotes" },
  { table: "foods", label: "exportFoods" },
] as const;

const RATION_SIZES: RationSize[] = [10, 15];
const LANGUAGES: { code: Language; name: string }[] = [
  { code: "es", name: "Español" },
  { code: "en", name: "English" },
];

export function SettingsScreen({ onSignOut }: { onSignOut: (() => void) | null }) {
  const { t, rationSize, language, theme, update } = useSettings();
  const [confirm, confirming] = useConfirm();

  return (
    <div className="screen">
      <h1 className="marked">
        <Icon name="slice" size={28} />
        {t("settingsTitle")}
      </h1>

      <PensCard />

      <section className="card setting" aria-labelledby="ration-title">
        <h2 id="ration-title">{t("rationSize")}</h2>
        <div className="segmented" role="group" aria-labelledby="ration-title">
          {RATION_SIZES.map((size) => (
            <button
              key={size}
              type="button"
              className={rationSize === size ? "segment segment-on" : "segment"}
              aria-pressed={rationSize === size}
              onClick={() => update({ rationSize: size })}
            >
              {t("grams", { n: size })}
            </button>
          ))}
        </div>
        <p className="muted small">{t("rationSizeHint")}</p>
      </section>

      <section className="card setting" aria-labelledby="language-title">
        <h2 id="language-title">{t("language")}</h2>
        <div className="segmented" role="group" aria-labelledby="language-title">
          {LANGUAGES.map((option) => (
            <button
              key={option.code}
              type="button"
              lang={option.code}
              className={language === option.code ? "segment segment-on" : "segment"}
              aria-pressed={language === option.code}
              onClick={() => update({ language: option.code })}
            >
              {option.name}
            </button>
          ))}
        </div>
      </section>

      <section className="card setting" aria-labelledby="theme-title">
        <h2 id="theme-title">{t("theme")}</h2>
        <div className="segmented" role="group" aria-labelledby="theme-title">
          {THEMES.map((option) => (
            <button
              key={option}
              type="button"
              className={theme === option ? "segment segment-on" : "segment"}
              aria-pressed={theme === option}
              onClick={() => update({ theme: option })}
            >
              {t(`theme_${option}`)}
            </button>
          ))}
        </div>
      </section>

      <section className="card setting" aria-labelledby="data-title">
        <div>
          <h2 id="data-title">{t("exportTitle")}</h2>
          <p className="muted small">{t("exportHint")}</p>
        </div>
        <ul className="rows">
          {EXPORTS.map(({ table, label }) => (
            <li key={table}>
              <a className="row-link" href={api.exportUrl(table)} download>
                <Icon name="file" />
                <span>{t(label)}</span>
                <span className="row-tag">CSV</span>
                <Icon name="download" size={20} />
              </a>
            </li>
          ))}
        </ul>
      </section>

      <details className="card setting about">
        <summary>
          <h2>{t("about")}</h2>
          <Icon name="chevron" size={20} />
        </summary>
        <p className="muted">{t("aboutText")}</p>
      </details>

      {onSignOut && (
        <button
          type="button"
          className="row-button sign-out"
          onClick={async () => {
            if (await confirm({ title: t("signOutTitle"), body: t("signOutBody"), confirm: t("signOut") })) {
              onSignOut();
            }
          }}
        >
          <Icon name="signOut" size={20} />
          <span>{t("signOut")}</span>
        </button>
      )}

      <p className="colophon">
        <Icon name="slice" size={18} />
        Sugary · {t("tagline")}
      </p>
      {confirming}
    </div>
  );
}
