FROM mcr.microsoft.com/playwright:v1.63.0-noble

WORKDIR /app

# The registry install plus the Camoufox browser bundle are the largest network fetch in this
# repo's images, and they run alongside ~19 sibling builds during `compose up --build`. npm's
# stock budget is 2 retries, and it covers the registry leg ONLY -- `camoufox fetch` pulls the
# browser through its own code and ignores npm config entirely -- so the whole step gets an outer
# retry as well. Same class of failure as the pip one documented in tse-extractor/Dockerfile.
ENV NPM_CONFIG_FETCH_RETRIES=5 \
    NPM_CONFIG_FETCH_RETRY_MAXTIMEOUT=120000 \
    NPM_CONFIG_FETCH_TIMEOUT=300000

# Each @camoufox/camoufox release is stamped with the browser build released beside it, and
# `camoufox fetch` installs exactly that one, so the browser changes only when this version does
# -- updating Camoufox is bumping it. 0.5.7-beta.6 pairs with 156.0.1-beta.34, a prerelease taken
# on purpose: every build before 156.0.1-beta.32 installed uBlock Origin once per window, and a
# request then waited on a listener that had gone away, stalling the whole browser a few parallel
# browses in (daijro/camoufox#185). Its peer range says playwright-core <1.63, but the version here
# must match the .NET client's, and with patch-viewport.js it passes the integration suite --
# hence --legacy-peer-deps.
RUN set -eu; \
    for attempt in 1 2 3; do \
        if npm install --legacy-peer-deps @camoufox/camoufox@0.5.7-beta.6 playwright-core@1.63.0 && npx camoufox fetch; then \
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
