import react from "@vitejs/plugin-react";
import { defineConfig, loadEnv, type Plugin } from "vite";
import { VitePWA } from "vite-plugin-pwa";

// The Sugary server believes only the person a signed assertion names, and only the accounts
// service signs one, so the dev server does what the proxy's forward_auth does: before each /api
// call it asks the session check for Sugary's channel with a real session and copies the four
// headers it answers onto the call. The session is a `mint-session` cookie value (SUGARY_SESSION)
// for a person `create-user` made; without one the server answers every call 401. Asked per call,
// because an assertion lives ten minutes and this is one local request more.
const sessionCheck = (accounts: string, session: string | undefined): Plugin => ({
  name: "ziggurat-session-check",
  configureServer(server) {
    if (!session) {
      server.config.logger.warn(
        "SUGARY_SESSION is not set: /api calls go out signed out and are answered 401. " +
          "Set it to what `docker exec accounts dotnet Accounts.dll mint-session --user <id>` prints.",
      );
    }

    // Added here rather than returned, so it runs before Vite's own proxy forwards the request.
    server.middlewares.use(async (incoming, _res, next) => {
      // Node's request, typed only as far as this reads it: the config compiles under the app's
      // browser types, which know no Node.
      const req = incoming as unknown as { url?: string; headers: Record<string, string | string[] | undefined> };
      if (!session || !req.url?.startsWith("/api")) {
        next();
        return;
      }

      try {
        const check = await fetch(`${accounts}/accounts/verify?channel=sugary`, {
          headers: { cookie: `ziggurat_session=${session}` },
        });
        if (check.ok) {
          for (const name of ["x-ziggurat-user", "x-ziggurat-tenant", "x-ziggurat-roles", "x-ziggurat-assertion"]) {
            const value = check.headers.get(name);
            if (value) req.headers[name] = value;
          }
        } else {
          server.config.logger.warn(`the session check answered ${check.status}: the call goes out signed out`);
        }
      } catch (error) {
        server.config.logger.warn(`the session check at ${accounts} could not be reached: ${String(error)}`);
      }

      next();
    });
  },
});

export default defineConfig(({ mode }) => {
  // Every variable, not only VITE_ ones: the session is the dev server's and never reaches the app.
  const env = loadEnv(mode, ".", "");
  return {
    plugins: [
      sessionCheck(env.ACCOUNTS_URL || "http://localhost:5004", env.SUGARY_SESSION || undefined),
      react(),
      VitePWA({
        registerType: "autoUpdate",
        // The proxy's session check stands in front of every file, the manifest too, and a browser
        // fetches a manifest without cookies unless told to; without them it is sent to sign in.
        useCredentials: true,
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
          // Sign-in and the account page are the accounts service's, on this host through the proxy:
          // answered with the app's own index.html they would send a signed-out person back to the
          // app, which sends them to sign in, for ever.
          navigateFallbackDenylist: [/^\/api\//, /^\/mcp/, /^\/accounts\//],
          // The pen reminders' push and click handlers, kept beside the generated worker in public/.
          importScripts: ["push-handler.js"],
        },
      }),
    ],
    server: {
      // The Sugary server as compose publishes it, on loopback with no proxy in front; the person
      // rides on the headers sessionCheck copied onto the request.
      proxy: {
        "/api": { target: "http://localhost:6017" },
      },
    },
    build: {
      // The Sugary server serves the app from its wwwroot, so one container holds both halves.
      outDir: "../McpServerSugary/wwwroot",
      emptyOutDir: true,
    },
  };
});
