FROM mcr.microsoft.com/playwright:v1.63.0-noble

WORKDIR /app

# The registry install plus the Camoufox browser bundle are the largest network fetch in this
# repo's images, and they run alongside ~19 sibling builds during `compose up --build`. npm's
# stock budget is 2 retries, and it covers the registry leg ONLY -- `camoufox-js fetch` pulls the
# browser through its own code and ignores npm config entirely -- so the whole step gets an outer
# retry as well. Same class of failure as the pip one documented in tse-extractor/Dockerfile.
ENV NPM_CONFIG_FETCH_RETRIES=5 \
    NPM_CONFIG_FETCH_RETRY_MAXTIMEOUT=120000 \
    NPM_CONFIG_FETCH_TIMEOUT=300000

# `camoufox-js fetch` takes the newest browser release it accepts, and has no version flag. Before
# 0.12 it accepted prereleases, so the 156.0.1 betas (2026-09-30) arrived on the next rebuild and
# crashed the server at launch: their properties.json drops navigator.product, which every
# camoufox-js still writes into the config. 0.12 skips prereleases and lands on the newest stable.
# Its peer range says playwright-core <1.61, but the version here must match the .NET client's,
# and with patch-viewport.js it passes the integration suite -- hence --legacy-peer-deps.
RUN set -eu; \
    for attempt in 1 2 3; do \
        if npm install --legacy-peer-deps camoufox-js@0.12.0 playwright-core@1.63.0 && npx camoufox-js fetch; then \
            exit 0; \
        fi; \
        echo "camoufox fetch attempt ${attempt} failed; retrying in 15s"; \
        sleep 15; \
    done; \
    echo "camoufox fetch failed after 3 attempts"; \
    exit 1

# Guard a playwright-core crash on locationless uncaught page errors (see patch-playwright.js).
COPY patch-playwright.js .
RUN node patch-playwright.js

# Playwright's client/server version check is an exact minor match, so playwright-core here must
# track Microsoft.Playwright in the .NET projects. Since 1.61 it sends juggler viewport fields
# Camoufox's Firefox build does not describe (see patch-viewport.js).
COPY patch-viewport.js .
RUN node patch-viewport.js

COPY server.js .

EXPOSE 9377

CMD ["node", "server.js"]
