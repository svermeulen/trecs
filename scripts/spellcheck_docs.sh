#!/bin/bash
# Spell-check the docs/ tree, plus README / CHANGELOG / CONTRIBUTING, with
# codespell. Designed to be runnable both locally and in CI.
#
# Install:   pip install codespell
# Run:       ./scripts/spellcheck_docs.sh
# Auto-fix:  ./scripts/spellcheck_docs.sh --write-changes
#
# False positives go in scripts/codespell_ignore_words.txt — one term per
# line, all lowercase. Codespell only matches against its dictionary, so
# adding a Trecs-specific term there means "treat this as correct".

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

if ! command -v codespell >/dev/null 2>&1; then
    echo "codespell not installed. Run: pip install codespell" >&2
    exit 2
fi

IGNORE_FILE="$SCRIPT_DIR/codespell_ignore_words.txt"
IGNORE_ARG=()
if [ -f "$IGNORE_FILE" ]; then
    IGNORE_ARG=(--ignore-words "$IGNORE_FILE")
fi

# Skip the rendered site, git internals, Unity-generated dirs.
SKIP_PATHS='./site,./.git,./UnityProject/Trecs/Library,./UnityProject/Trecs/Logs,./UnityProject/Trecs/Temp,./.unity_shadow'

codespell \
    --skip="$SKIP_PATHS" \
    ${IGNORE_ARG[@]+"${IGNORE_ARG[@]}"} \
    "$@" \
    docs/ \
    README.md \
    CHANGELOG.md \
    CONTRIBUTING.md
