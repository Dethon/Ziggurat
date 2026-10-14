import { useState } from "react";
import { api } from "../api";
import { Icon } from "../components/Icon";
import type { Language } from "../i18n";
import { useSettings, type RationSize } from "../settings";
import { FoodsSheet } from "./FoodsSheet";

const EXPORTS = [
  { table: "meals", label: "exportMeals" },
  { table: "items", label: "exportItems" },
  { table: "foods", label: "exportFoods" },
] as const;

const RATION_SIZES: RationSize[] = [10, 15];
const LANGUAGES: { code: Language; name: string }[] = [
  { code: "es", name: "Español" },
  { code: "en", name: "English" },
];

export function SettingsScreen({ onSignOut }: { onSignOut: (() => void) | null }) {
  const { t, rationSize, language, update } = useSettings();
  const [managingFoods, setManagingFoods] = useState(false);

  return (
    <div className="screen">
      <h1>{t("settingsTitle")}</h1>

      <section className="card setting">
        <h2>{t("rationSize")}</h2>
        <div className="segmented" role="group" aria-label={t("rationSize")}>
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

      <section className="card setting">
        <h2>{t("language")}</h2>
        <div className="segmented" role="group" aria-label={t("language")}>
          {LANGUAGES.map((option) => (
            <button
              key={option.code}
              type="button"
              className={language === option.code ? "segment segment-on" : "segment"}
              aria-pressed={language === option.code}
              onClick={() => update({ language: option.code })}
            >
              {option.name}
            </button>
          ))}
        </div>
      </section>

      <section className="card setting">
        <h2>{t("myFoods")}</h2>
        <p className="muted small">{t("myFoodsAbout")}</p>
        <button type="button" className="button button-secondary" onClick={() => setManagingFoods(true)}>
          <Icon name="bookmark" />
          {t("manageFoods")}
        </button>
      </section>

      <section className="card setting">
        <h2>{t("exportTitle")}</h2>
        <p className="muted small">{t("exportHint")}</p>
        <div className="downloads">
          {EXPORTS.map(({ table, label }) => (
            <a key={table} className="button button-secondary" href={api.exportUrl(table)} download>
              <Icon name="download" />
              {t(label)}
            </a>
          ))}
        </div>
      </section>

      <section className="card setting">
        <h2>{t("about")}</h2>
        <p className="muted">{t("aboutText")}</p>
      </section>

      {managingFoods && <FoodsSheet onClose={() => setManagingFoods(false)} />}

      {onSignOut && (
        <button type="button" className="button button-quiet" onClick={onSignOut}>
          {t("signOut")}
        </button>
      )}
    </div>
  );
}
