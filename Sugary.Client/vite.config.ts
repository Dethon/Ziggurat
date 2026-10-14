import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      includeAssets: ["icon.svg", "apple-touch-icon.png"],
      manifest: {
        name: "Sugary",
        short_name: "Sugary",
        description: "Carb counting from a photo",
        display: "standalone",
        orientation: "portrait",
        start_url: "/",
        background_color: "#f6f2ec",
        theme_color: "#5b34c9",
        icons: [
          { src: "icon-192.png", sizes: "192x192", type: "image/png" },
          { src: "icon-512.png", sizes: "512x512", type: "image/png" },
          { src: "icon-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
        ],
      },
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,wasm}"],
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
        // The API is never served from cache: a stale estimate or log would be worse than an error.
        navigateFallbackDenylist: [/^\/api\//, /^\/mcp/],
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
