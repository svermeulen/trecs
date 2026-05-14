#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Mirrors the `sourcegen.yml` GitHub workflow's `build` job — restore, build,
# test. Format checking is intentionally separate (`format_csharp.sh`).
cd "$SCRIPT_DIR/../SourceGen/Trecs.SourceGen"

dotnet restore Trecs.SourceGen.sln
dotnet build Trecs.SourceGen.sln -c Release --no-restore
dotnet test Trecs.SourceGen.sln -c Release --no-build --logger "console;verbosity=normal" "$@"
