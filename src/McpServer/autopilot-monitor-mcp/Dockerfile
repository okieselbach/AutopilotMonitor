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
      node dist/precompute-embeddings.js ./rules /app/search-index/search-index.json && { ok=1; break; }; \
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

ENV PORT=8080
ENV RULES_DIR=/app/rules
ENV SEARCH_INDEX_PATH=/app/search-index/search-index.json
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://localhost:8080/health').then(r => r.ok ? process.exit(0) : process.exit(1)).catch(() => process.exit(1))"

CMD ["node", "dist/index.js"]
