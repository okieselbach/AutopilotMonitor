# Build context: repo root (run: docker build -f src/McpServer/autopilot-monitor-mcp/Dockerfile .)

# Stage 1: Build
FROM node:22-slim AS build
WORKDIR /app

COPY src/McpServer/autopilot-monitor-mcp/package.json src/McpServer/autopilot-monitor-mcp/package-lock.json ./
RUN npm ci --ignore-scripts

COPY src/McpServer/autopilot-monitor-mcp/tsconfig.json ./
COPY src/McpServer/autopilot-monitor-mcp/src/ ./src/
RUN npm run build

# Pre-bake the ~23 MB embedding model at image build time so a scale-to-zero cold
# start never depends on the HuggingFace CDN (ephemeral FS would re-download on
# every cold start; an unreachable CDN would crash-loop boot). Warming up via the
# built provider keeps the model name/dtype single-sourced in
# vector-search-provider.ts, which honors HF_CACHE_DIR.
ENV HF_CACHE_DIR=/app/.hf-cache
RUN node --input-type=module -e 'const { embed } = await import("/app/dist/vector-search-provider.js"); await embed("model prefetch warmup");' \
  && test -d /app/.hf-cache

# Stage 2: Production
FROM node:22-slim
WORKDIR /app

ENV NODE_ENV=production
ENV HF_CACHE_DIR=/app/.hf-cache

COPY --from=build /app/package.json /app/package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force

COPY --from=build /app/dist/ ./dist/
COPY --from=build /app/.hf-cache/ ./.hf-cache/

# Knowledge base rules (analyze, gather, ime-log-patterns)
COPY rules/analyze/ ./rules/analyze/
COPY rules/gather/ ./rules/gather/
COPY rules/ime-log-patterns/ ./rules/ime-log-patterns/

ENV PORT=8080
ENV RULES_DIR=/app/rules
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://localhost:8080/health').then(r => r.ok ? process.exit(0) : process.exit(1)).catch(() => process.exit(1))"

CMD ["node", "dist/index.js"]
