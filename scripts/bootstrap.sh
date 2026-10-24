#!/usr/bin/env bash
# Первичная настройка клона: git-хуки и restore. Работает в Linux и Git Bash.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

git config core.hooksPath .githooks
chmod +x .githooks/* scripts/*.sh
echo "bootstrap: core.hooksPath = $(git config core.hooksPath)"

dotnet restore traffic-city.sln
echo "bootstrap: ok"
