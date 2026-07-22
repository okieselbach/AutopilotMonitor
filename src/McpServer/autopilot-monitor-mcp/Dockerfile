# Build context: repo root (run: docker build -f src/McpServer/autopilot-monitor-mcp/Dockerfile .)

# Stage 1: Build
FROM node:22-slim AS build
WORKDIR /app

COPY src/McpServer/autopilot-monitor-mcp/package.json src/McpServer/autopilot-monitor-mcp/package-lock.json ./
RUN npm ci --ignore-scripts

COPY src/McpServer/autopilot-monitor-mcp/tsconfig.json ./
COPY src/McpServer/autopilot-monitor-mcp/src/ ./src/
RUN npm run build

# Knowledge base rules — needed in the BUILD stage too: the embedding
# precompute below indexes them. Must stay byte-identical to the final-stage
# copy or the boot-time corpus hash check rejects the precomputed index.
COPY rules/analyze/ ./rules/analyze/
COPY rules/gather/ ./rules/gather/
COPY rules/ime-log-patterns/ ./rules/ime-log-patterns/

# Published customer documentation (autopilotmonitor-docs) — a SEPARATE public
# repo that the deploy workflow checks out into ./mcp-docs of the build context.
# Same byte-identical requirement as the rules above.
#
# This COPY is deliberately strict: if the workflow's docs checkout is ever
# removed, the build must fail loudly rather than quietly ship an image whose
# search_docs tool simply does not exist. For a local `docker build`, clone the
# bundle first:
#   git clone --depth 1 https://github.com/autopilotmonitor/autopilotmonitor-docs mcp-docs
COPY mcp-docs/ ./docs/

# Commit SHA of that docs checkout. Surfaced on /health so a running server can be
# diffed against the docs repo HEAD — the bundle changes independently of this
# repo, so a stale deploy is otherwise invisible.
ARG DOCS_COMMIT=unknown

# Pre-bake the ~23 MB embedding model AND precompute the search index at image
# build time. Embedding the static corpus at boot cost 35-55s of ONNX inference
# on the 0.25 vCPU container (measured); hydrating precomputed vectors is
# milliseconds, and the model cache removes the HuggingFace CDN dependency from
# scale-to-zero cold starts entirely.
#
# transformers.js does NOT retry its model fetch and undici aborts after a 10s
# connect timeout, so a single attempt is flaky on CI runners — retry with
# backoff. Build with --build-arg PREBAKE_MODEL=0 as the escape hatch during an
# extended HF outage: the image then ships a cold cache and no precomputed
# index, and the server's boot fallback (compute at boot, else fuse) takes over.
ENV HF_CACHE_DIR=/app/.hf-cache
ARG PREBAKE_MODEL=1
RUN mkdir -p "$HF_CACHE_DIR" /app/search-index && \
  if [ "$PREBAKE_MODEL" = "1" ]; then \
    ok=0; \
    for i in 1 2 3 4 5; do \
      node dist/precompute-embeddings.js ./rules /app/search-index/search-index.json ./docs && { ok=1; break; }; \
      echo "embedding precompute attempt $i/5 failed; retrying in 15s"; \
      sleep 15; \
    done && \
    [ "$ok" = "1" ] && \
    [ -n "$(ls -A "$HF_CACHE_DIR")" ] && \
    [ -s /app/search-index/search-index.json ]; \
  else \
    echo "PREBAKE_MODEL=0 - skipping model pre-bake + embedding precompute (boot computes; downloads or falls back to fuse)"; \
  fi

# Stage 2: Production
FROM node:22-slim
WORKDIR /app

ENV NODE_ENV=production
ENV HF_CACHE_DIR=/app/.hf-cache

COPY --from=build /app/package.json /app/package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force

COPY --from=build /app/dist/ ./dist/
COPY --from=build /app/.hf-cache/ ./.hf-cache/
# Directory (not file) so the copy also works under PREBAKE_MODEL=0.
COPY --from=build /app/search-index/ ./search-index/

# Knowledge base rules (analyze, gather, ime-log-patterns)
COPY rules/analyze/ ./rules/analyze/
COPY rules/gather/ ./rules/gather/
COPY rules/ime-log-patterns/ ./rules/ime-log-patterns/

# Customer documentation corpus — copied from the build stage rather than the
# context so it is provably the same bytes the embeddings were computed over.
COPY --from=build /app/docs/ ./docs/

ARG DOCS_COMMIT=unknown
ENV DOCS_COMMIT=${DOCS_COMMIT}

# Build identity of the server itself (deploy-mcp.yml supplies all three).
# BUILD_NUMBER completes the version as <major>.<minor>.<build>; left unset in a
# local `docker build`, which keeps package.json's version and marks it as such.
ARG BUILD_NUMBER=
ARG BUILD_COMMIT=unknown
ARG BUILD_UTC=unknown
ENV BUILD_NUMBER=${BUILD_NUMBER}
ENV BUILD_COMMIT=${BUILD_COMMIT}
ENV BUILD_UTC=${BUILD_UTC}

ENV PORT=8080
ENV RULES_DIR=/app/rules
ENV DOCS_DIR=/app/docs
ENV SEARCH_INDEX_PATH=/app/search-index/search-index.json
EXPOSE 8080

# Drop root: run as the unprivileged `node` user the base image ships (uid 1000).
# An internet-exposed ingress should not execute as uid 0 — an RCE in the OAuth
# proxy or a tool handler would otherwise own the container. Defence-in-depth.
#
# Everything the server READS (dist/, rules/, search-index/, node_modules) is
# COPY'd world-readable, so no chown is needed for those. The ONLY runtime write
# path is /app/.hf-cache: the prebaked path reads the model, but the documented
# boot fallback (missing/corrupt cache, or a PREBAKE_MODEL=0 image) downloads it
# at runtime and must be able to write — chown just that dir so the fallback (and
# any cache-lock writes during a normal load) keep working as non-root. A
# recursive chown of all of /app is avoided on purpose: it would duplicate the
# heavy node_modules/.hf-cache trees into a new layer.
RUN chown -R node:node /app/.hf-cache
USER node

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://localhost:8080/health').then(r => r.ok ? process.exit(0) : process.exit(1)).catch(() => process.exit(1))"

CMD ["node", "dist/index.js"]
