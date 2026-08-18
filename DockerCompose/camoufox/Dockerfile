FROM mcr.microsoft.com/playwright:v1.62.0-noble

WORKDIR /app

# The registry install plus the Camoufox browser bundle are the largest network fetch in this
# repo's images, and they run alongside ~19 sibling builds during `compose up --build`. npm's
# stock budget is 2 retries, and it covers the registry leg ONLY -- `camoufox-js fetch` pulls the
# browser through its own code and ignores npm config entirely -- so the whole step gets an outer
# retry as well. Same class of failure as the pip one documented in tse-extractor/Dockerfile.
ENV NPM_CONFIG_FETCH_RETRIES=5 \
    NPM_CONFIG_FETCH_RETRY_MAXTIMEOUT=120000 \
    NPM_CONFIG_FETCH_TIMEOUT=300000

RUN set -eu; \
    for attempt in 1 2 3; do \
        if npm install camoufox-js@0.9.3 playwright-core@1.62.0 && npx camoufox-js fetch; then \
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
