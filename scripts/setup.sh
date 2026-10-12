#!/usr/bin/env bash
# One-step developer setup for Linux/macOS (the desktop app itself only runs on Windows;
# here it compiles and its cross-platform tests run).
#   ./scripts/setup.sh            install dependencies, start the database, run migrations
#   ./scripts/setup.sh --no-db    skip the database steps
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
WITH_DB=1
[[ "${1:-}" == "--no-db" ]] && WITH_DB=0

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
fail() { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

say "Checking Node.js (>= 22.18)"
command -v node >/dev/null || fail "Node.js is not installed. Install Node 22.18+ (see .nvmrc)."
node -e 'const [a,b]=process.versions.node.split(".").map(Number); if (a<22||(a===22&&b<18)) process.exit(1)' \
  || fail "Node $(node --version) is too old; NimPad needs 22.18+ (native TypeScript support)."

say "Checking pnpm"
if ! command -v pnpm >/dev/null; then
  command -v corepack >/dev/null || fail "pnpm is missing and corepack is unavailable. Install pnpm 10."
  corepack enable
fi

say "Checking .NET SDK (version from global.json)"
SDK_VERSION="$(node -p 'require("./global.json").sdk.version')"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q "^${SDK_VERSION%.*}"; then
  if [[ -x "$HOME/.dotnet/dotnet" ]] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q "^${SDK_VERSION%.*}"; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    say "Installing .NET SDK $SDK_VERSION to ~/.dotnet"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    echo "Add ~/.dotnet to your PATH to use dotnet in new shells."
  fi
fi

say "Installing JavaScript dependencies"
pnpm install --frozen-lockfile

say "Restoring and building the desktop solution"
dotnet build apps/windows/NimPad.slnx

[[ -f .env ]] || { cp .env.example .env; echo "Created .env from .env.example"; }

if [[ $WITH_DB == 1 ]]; then
  say "Starting PostgreSQL"
  if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
    docker compose up -d --wait db
  else
    echo "Docker is not available. Make sure PostgreSQL 16 is reachable as described in .env (DATABASE_URL_MIGRATE)."
  fi
  say "Running database migrations"
  pnpm db:migrate
fi

say "Done."
cat <<'MSG'
Next steps:
  pnpm test                                   run the backend tests
  pnpm dev:api                                start the API on http://127.0.0.1:8787
  dotnet test --project apps/windows/tests/NimPad.Core.Tests
  (Windows) dotnet run --project apps/windows/src/NimPad.App
MSG
