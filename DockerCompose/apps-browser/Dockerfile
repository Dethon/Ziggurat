# The browser a built app's check runs in (McpServerApps/Checks): a Playwright browser server that
# launches one Chromium per connection with the options the apps server sends (`run-server --unsafe`,
# without which it drops the arguments and the sandbox). It is the only place the deployment runs
# Chromium, so the agent's code never runs beside the apps server's credential, Redis or volume; this
# container holds none of them (DeploymentConformanceTests).
FROM node:22-bookworm-slim

# The connect handshake demands an exact client/server minor match, so this moves in lockstep with the
# .NET projects' Microsoft.Playwright (DeploymentConformanceTests), as DockerCompose/camoufox does. Full
# Chromium alone, with the libraries it needs: the headless shell ignores the flag that makes the
# preview's plain-http origin a secure context, and nothing here runs Firefox or WebKit.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    NPM_CONFIG_FETCH_RETRIES=5 \
    NPM_CONFIG_UPDATE_NOTIFIER=false
WORKDIR /app
RUN npm install --omit=dev playwright-core@1.64.1 \
 && npx playwright-core install --with-deps --no-shell chromium \
 && rm -rf /var/lib/apt/lists/* /root/.npm \
 && chmod -R a+rX /ms-playwright

# The image's own unprivileged user, whose home Chromium writes its crash database to. Chromium's
# sandbox needs no privilege of its own: it makes user namespaces, which compose's syscall filter
# (DockerCompose/seccomp/ziggurat-apps-browser.json) lets through.
USER node

# A port Chromium refuses to dial (net's restricted ports: 10080 is one), so not even a compromised
# renderer's own network stack reaches the server that could launch anything here.
EXPOSE 10080

# The server stops when its standard input closes, so compose keeps it open (`stdin_open`).
CMD ["node", "node_modules/playwright-core/cli.js", "run-server", "--host", "0.0.0.0", "--port", "10080", "--unsafe"]
