#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd $SCRIPT_DIR/../
(sleep 1 && open http://127.0.0.1:8000/) &
mkdocs serve --livereload
