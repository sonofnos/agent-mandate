#!/usr/bin/env bash
# Runs the API against the compose Postgres. Set LLM_BASE_URL / LLM_API_KEY / LLM_MODELS to use a real model.
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose up -d --wait db
export ConnectionStrings__Mandate="Host=localhost;Port=5436;Database=mandate;Username=mandate;Password=mandate"
export LLM__BaseUrl="${LLM_BASE_URL:-}" LLM__ApiKey="${LLM_API_KEY:-}" LLM__Models="${LLM_MODELS:-}"
export RateLimit__SessionsPer10Min="${SESSIONS_PER_10MIN:-200}" RateLimit__ChatPerMinute="${CHAT_PER_MIN:-30}"
export Provider__LatencyMs=800 Executor__NotFoundSettleSeconds=10 ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5090}"
exec dotnet run --project src/Mandate.Api --no-launch-profile
