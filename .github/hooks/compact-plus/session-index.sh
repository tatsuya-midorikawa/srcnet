#!/usr/bin/env bash
# SessionStart hook (Copilot CLI / VS Code Copilot).
#
# Two jobs:
#   1. Maintain a stable cwd -> session_id index so the agent-driven skill
#      (which has no hook stdin) and the recovery hook can agree on where the
#      state file lives.
#   2. On a resumed session, replay any pending recovery marker once by
#      delegating to recovery-inject.sh.
#
# fail-open: never block the session. Always exit 0.

set -uo pipefail

INPUT=$(cat 2>/dev/null || true)

json_field() {
  printf '%s' "$INPUT" | jq -r "$1 // empty" 2>/dev/null || true
}

SESSION_ID=$(json_field '.session_id')
CWD=$(json_field '.cwd')
SOURCE=$(json_field '.source')
[[ -n "$CWD" ]] || CWD="$PWD"

# Portable, stable hash of the workspace path.
# cksum is POSIX and produces the same CRC on macOS and Linux.
CWD_HASH=$(printf '%s' "$CWD" | cksum 2>/dev/null | awk '{print $1}')
[[ -n "$CWD_HASH" ]] || CWD_HASH="unknown"

BASE="${TMPDIR:-/tmp}/compact-plus" # lint:allow-os-tmp
IDX_DIR="$BASE/index/by-cwd/$CWD_HASH"
mkdir -p "$IDX_DIR" 2>/dev/null || true

if [[ -n "$SESSION_ID" ]]; then
  printf '%s\n' "$SESSION_ID" > "$IDX_DIR/session_id" 2>/dev/null || true
fi
printf '%s\n' "$(date +%s)" > "$IDX_DIR/last_seen" 2>/dev/null || true

# Only replay recovery on an explicit resume. VS Code reports source "new", so
# its post-compaction recovery flows through PostToolUse instead; this avoids
# injecting a stale marker into an unrelated fresh session.
if [[ "$SOURCE" = "resume" ]]; then
  SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)
  if [[ -n "${SCRIPT_DIR:-}" && -f "$SCRIPT_DIR/recovery-inject.sh" ]]; then
    printf '%s' "$INPUT" | bash "$SCRIPT_DIR/recovery-inject.sh"
  fi
fi

exit 0
