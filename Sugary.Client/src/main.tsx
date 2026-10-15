import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
// Self-hosted: the UI face (with tabular figures) and the display face for titles, latin only by
// unicode-range, so a phone downloads just what the page shows.
import "@fontsource-variable/atkinson-hyperlegible-next/wght.css";
import "@fontsource-variable/fraunces/soft.css";
import { App } from "./App";
import { SettingsProvider } from "./settings";
import "./tokens.css";
import "./styles.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <SettingsProvider>
      <App />
    </SettingsProvider>
  </StrictMode>,
);
