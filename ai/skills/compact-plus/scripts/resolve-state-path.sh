#!/usr/bin/env bash
# compact-plus skill helper.
#
# Resolves where the agent should write the pre-compaction state file so the
# recovery hook can find it again after compaction. The skill and the hooks
# derive the same cwd hash, so their paths line up without the skill needing a
# hook-provided session_id.
#
# Usage:
#   resolve-state-path.sh          Print KEY=VALUE lines and create parent dirs:
#                                    STATE_FILE          primary write target
#                                    SESSION_STATE_FILE  session copy, or empty
#   resolve-state-path.sh --sync   Copy STATE_FILE to SESSION_STATE_FILE (run
#                                    after writing STATE_FILE) so the hook can
#                                    also find it by session_id.
#
# fail-open-ish: on trouble it still prints a usable cwd-keyed path.

set -uo pipefail

MODE="${1:-print}"
CWD="$PWD"

CWD_HASH=$(printf '%s' "$CWD" | cksum 2>/dev/null | awk '{print $1}')
[[ -n "$CWD_HASH" ]] || CWD_HASH="unknown"

BASE="${TMPDIR:-/tmp}/compact-plus" # lint:allow-os-tmp
CWD_STATE_DIR="$BASE/state/by-cwd/$CWD_HASH"
SESS_STATE_DIR="$BASE/state/by-session"
mkdir -p "$CWD_STATE_DIR" "$SESS_STATE_DIR" 2>/dev/null || true

# Resolve session id: explicit override first, else the index a hook wrote.
SESSION_ID="${COMPACT_PLUS_SESSION_ID:-}"
if [[ -z "$SESSION_ID" && -f "$BASE/index/by-cwd/$CWD_HASH/session_id" ]]; then
  SESSION_ID=$(cat "$BASE/index/by-cwd/$CWD_HASH/session_id" 2>/dev/null || true)
fi

STATE_FILE="$CWD_STATE_DIR/latest.md"
SESSION_STATE_FILE=""
if [[ -n "$SESSION_ID" ]]; then
  SESSION_STATE_FILE="$SESS_STATE_DIR/$SESSION_ID.md"
fi

if [[ "$MODE" = "--sync" ]]; then
  if [[ -n "$SESSION_STATE_FILE" && -f "$STATE_FILE" ]]; then
    cp "$STATE_FILE" "$SESSION_STATE_FILE" 2>/dev/null || true
  fi
  exit 0
fi

printf 'STATE_FILE=%s\n' "$STATE_FILE"
printf 'SESSION_STATE_FILE=%s\n' "$SESSION_STATE_FILE"
exit 0
