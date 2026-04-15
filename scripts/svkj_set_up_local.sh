#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

create_symlink() {
    local link_path="$1"
    local target="$2"

    if [ -L "$link_path" ]; then
        echo "Symlink already exists at $link_path"
    elif [ -e "$link_path" ]; then
        echo "Error: $link_path already exists and is not a symlink" >&2
        exit 1
    else
        ln -s "$target" "$link_path"
        echo "Created symlink: $link_path -> $target"
    fi
}

create_symlink "$SCRIPT_DIR/../UnityProject/Trecs/Assets/svkj-local" "../../../../trecs-svkj/svkj-local"
create_symlink "$SCRIPT_DIR/../CLAUDE.md" "../trecs-svkj/CLAUDE.md"
