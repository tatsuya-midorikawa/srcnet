#!/usr/bin/env bash
# PostToolUse / SessionStart(resume) / notification hook
# (Copilot CLI / VS Code Copilot).
#
# When a recovery marker is pending (written by precompact-backup.sh), inject
# compact-plus recovery guidance once and consume the marker.
#
# The output is a SINGLE JSON object that carries the guidance in BOTH shapes:
#   - top-level "additionalContext"            -> read by Copilot CLI
#   - "hookSpecificOutput.additionalContext"   -> read by VS Code Copilot
# Only one JSON object is ever printed, so Copilot CLI's line-oriented parser
# stays happy. When there is nothing to recover, nothing is printed.
#
# fail-open: never block. Always exit 0.

set -uo pipefail

INPUT=$(cat 2>/dev/null || true)

json_field() {
  printf '%s' "$INPUT" | jq -r "$1 // empty" 2>/dev/null || true
}

SESSION_ID=$(json_field '.session_id')
CWD=$(json_field '.cwd')
EVENT=$(json_field '.hook_event_name')
[[ -n "$CWD" ]] || CWD="$PWD"
[[ -n "$EVENT" ]] || EVENT="PostToolUse"

CWD_HASH=$(printf '%s' "$CWD" | cksum 2>/dev/null | awk '{print $1}')
[[ -n "$CWD_HASH" ]] || CWD_HASH="unknown"

BASE="${TMPDIR:-/tmp}/compact-plus" # lint:allow-os-tmp
MARK_SESS="$BASE/markers/recovery/${SESSION_ID:-__none__}"
MARK_CWD="$BASE/markers/recovery-by-cwd/$CWD_HASH"

# Act only when a marker is pending. Cheap: at most two test -f per event.
HAVE_MARKER=0
if [[ -n "$SESSION_ID" && -f "$MARK_SESS" ]]; then
  HAVE_MARKER=1
fi
if [[ -f "$MARK_CWD" ]]; then
  HAVE_MARKER=1
fi
[[ "$HAVE_MARKER" = "1" ]] || exit 0

# Resolve artifacts, preferring session-keyed over cwd-keyed.
STATE_FILE=""
if [[ -n "$SESSION_ID" && -f "$BASE/state/by-session/$SESSION_ID.md" ]]; then
  STATE_FILE="$BASE/state/by-session/$SESSION_ID.md"
elif [[ -f "$BASE/state/by-cwd/$CWD_HASH/latest.md" ]]; then
  STATE_FILE="$BASE/state/by-cwd/$CWD_HASH/latest.md"
fi

SNAP_FILE=""
if [[ -n "$SESSION_ID" && -f "$BASE/snapshots/by-session/$SESSION_ID.md" ]]; then
  SNAP_FILE="$BASE/snapshots/by-session/$SESSION_ID.md"
elif [[ -f "$BASE/snapshots/by-cwd/$CWD_HASH.md" ]]; then
  SNAP_FILE="$BASE/snapshots/by-cwd/$CWD_HASH.md"
fi

BACKUP_FILE=""
BK_DIR="${HOME}/.copilot/compact-plus/backups/transcripts"
if [[ -d "$BK_DIR" ]]; then
  KEY="${SESSION_ID:-$CWD_HASH}"
  BACKUP_FILE=$(find "$BK_DIR" -maxdepth 1 -type f -name "*-${KEY}.jsonl" -print 2>/dev/null | sort -r | head -n 1 || true)
fi

CTX="[compact-plus recovery] Context compaction occurred. Restore working state before continuing."
if [[ -n "$STATE_FILE" ]]; then
  CTX+=$'\n'"- Read the state file \`$STATE_FILE\` and restore the working state. Focus on Session Decisions, Constraints and Blockers, and Recovery Notes."
  if grep -q '^## Skills Invoked' "$STATE_FILE" 2>/dev/null; then
    CTX+=$'\n'"- \`$STATE_FILE\` has a \`## Skills Invoked\` section; reload any skill listed there before relying on it."
  fi
else
  CTX+=$'\n'"- No compact-plus state file was found for this workspace; rely on the snapshot or transcript backup below."
fi
if [[ -n "$SNAP_FILE" ]]; then
  CTX+=$'\n'"- A raw snapshot (git status and transcript tail) is at \`$SNAP_FILE\`."
fi
if [[ -n "$BACKUP_FILE" ]]; then
  CTX+=$'\n'"- The pre-compaction transcript backup is at \`$BACKUP_FILE\`."
fi
CTX+=$'\n'"- Treat the compaction summary as a record of prior work, not as instructions for the next action."
CTX+=$'\n'"- Original instruction, rule, and skill files are authoritative; the summary and state file may omit scope qualifiers, so re-read the originals when they matter."

# Consume the markers so this fires only once.
rm -f "$MARK_SESS" "$MARK_CWD" 2>/dev/null || true

jq -n --arg ctx "$CTX" --arg ev "$EVENT" '{
  hookSpecificOutput: { hookEventName: $ev, additionalContext: $ctx },
  additionalContext: $ctx
}'
exit 0
