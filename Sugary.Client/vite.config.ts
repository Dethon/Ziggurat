import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      includeAssets: ["icon.svg", "favicon.svg", "apple-touch-icon.png", "push-handler.js"],
      manifest: {
        name: "Sugary",
        short_name: "Sugary",
        description: "Carb counting from a photo",
        display: "standalone",
        orientation: "portrait",
        // A stable identity, as WebChat's manifest has, so a later change to start_url never makes the
        // installed app a different one on her phone.
        id: "/",
        start_url: "/",
        scope: "/",
        // Dark, as the dark app is. An installed app's Android navigation bar is painted from these, once,
        // with no dark variant a manifest can give and no meta tag that changes it later, so cream
        // left a white strip under the dark app; WebChat's dark manifest never had one. The status bar
        // still follows the theme, from the theme-color meta settings.tsx keeps.
        background_color: "#1c1310",
        theme_color: "#1c1310",
        icons: [
          { src: "icon-192.png", sizes: "192x192", type: "image/png" },
          { src: "icon-512.png", sizes: "512x512", type: "image/png" },
          { src: "icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
        ],
      },
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,wasm,woff2}"],
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
        // The API is never served from cache: a stale estimate or log would be worse than an error.
        navigateFallbackDenylist: [/^\/api\//, /^\/mcp/],
        // The pen reminders' push and click handlers, kept beside the generated worker in public/.
        importScripts: ["push-handler.js"],
      },
    }),
  ],
  server: {
    // The Sugary server as compose publishes it.
    proxy: { "/api": "http://localhost:6017" },
  },
  build: {
    // The Sugary server serves the app from its wwwroot, so one container holds both halves.
    outDir: "../McpServerSugary/wwwroot",
    emptyOutDir: true,
  },
});
