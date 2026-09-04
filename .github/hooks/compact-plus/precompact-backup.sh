#!/usr/bin/env bash
# PreCompact hook (Copilot CLI / VS Code Copilot).
#
# Rich state generation is agent-driven through the compact-plus skill. This
# hook is a mechanical, no-LLM safety net that runs when compaction is about to
# start. It:
#   1. refreshes the cwd -> session_id index,
#   2. backs up the transcript (when the surface exposes transcript_path),
#   3. writes a raw snapshot (git status + a bounded transcript tail),
#   4. drops a one-shot recovery marker for recovery-inject.sh.
#
# The marker is keyed by BOTH session_id and cwd hash because VS Code's
# PreCompact payload treats session_id as optional.
#
# fail-open: never block compaction. Always exit 0.

set -uo pipefail

INPUT=$(cat 2>/dev/null || true)

json_field() {
  printf '%s' "$INPUT" | jq -r "$1 // empty" 2>/dev/null || true
}

SESSION_ID=$(json_field '.session_id')
CWD=$(json_field '.cwd')
TRANSCRIPT_PATH=$(json_field '.transcript_path')
TRIGGER=$(json_field '.trigger')
CUSTOM=$(json_field '.custom_instructions')
[[ -n "$CWD" ]] || CWD="$PWD"
[[ -n "$TRIGGER" ]] || TRIGGER="unknown"

CWD_HASH=$(printf '%s' "$CWD" | cksum 2>/dev/null | awk '{print $1}')
[[ -n "$CWD_HASH" ]] || CWD_HASH="unknown"

BASE="${TMPDIR:-/tmp}/compact-plus" # lint:allow-os-tmp
IDX_DIR="$BASE/index/by-cwd/$CWD_HASH"
MARK_SESS_DIR="$BASE/markers/recovery"
MARK_CWD_DIR="$BASE/markers/recovery-by-cwd"
SNAP_SESS_DIR="$BASE/snapshots/by-session"
SNAP_CWD_DIR="$BASE/snapshots/by-cwd"
mkdir -p "$IDX_DIR" "$MARK_SESS_DIR" "$MARK_CWD_DIR" "$SNAP_SESS_DIR" "$SNAP_CWD_DIR" 2>/dev/null || true

# 1. Index refresh.
if [[ -n "$SESSION_ID" ]]; then
  printf '%s\n' "$SESSION_ID" > "$IDX_DIR/session_id" 2>/dev/null || true
fi
printf '%s\n' "$(date +%s)" > "$IDX_DIR/last_seen" 2>/dev/null || true

KEY="${SESSION_ID:-$CWD_HASH}"

# 2. Transcript backup (best-effort; keep 20 most recent per key).
if [[ -n "$TRANSCRIPT_PATH" && -f "$TRANSCRIPT_PATH" ]]; then
  BACKUP_DIR="${HOME}/.copilot/compact-plus/backups/transcripts"
  mkdir -p "$BACKUP_DIR" 2>/dev/null || true
  EPOCH=$(date +%s)
  cp "$TRANSCRIPT_PATH" "$BACKUP_DIR/${EPOCH}-${KEY}.jsonl" 2>/dev/null || true
  # mapfile is bash 4+, and stock macOS bash is 3.2, so prune with while-read.
  find "$BACKUP_DIR" -maxdepth 1 -type f -name "*-${KEY}.jsonl" -print 2>/dev/null \
    | sort -r \
    | tail -n +21 \
    | while IFS= read -r old; do
        rm -f "$old" 2>/dev/null || true
      done
fi

# 3. No-LLM raw snapshot. Cheap facts plus a bounded transcript tail; no JSONL
#    parsing, so it is safe across surfaces with different transcript schemas.
SNAP_TMP=$(mktemp "${TMPDIR:-/tmp}/compact-plus-snap.XXXXXX" 2>/dev/null || printf '%s' "${TMPDIR:-/tmp}/compact-plus-snap.$$") # lint:allow-os-tmp
{
  printf '# Compact Plus Raw Snapshot\n\n'
  printf -- '- captured: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date 2>/dev/null || true)"
  printf -- '- session_id: %s\n' "${SESSION_ID:-Not verified}"
  printf -- '- cwd: %s\n' "$CWD"
  printf -- '- compact trigger: %s\n' "$TRIGGER"
  if [[ -n "$CUSTOM" ]]; then
    printf -- '- custom instructions: %s\n' "$CUSTOM"
  fi
  BRANCH=$(cd "$CWD" 2>/dev/null && git rev-parse --abbrev-ref HEAD 2>/dev/null || true)
  if [[ -n "$BRANCH" ]]; then
    printf -- '- git branch: %s\n' "$BRANCH"
  fi
  GIT_STATUS=$(cd "$CWD" 2>/dev/null && git status --short 2>/dev/null | head -n 40 || true)
  if [[ -n "$GIT_STATUS" ]]; then
    printf '\n## git status --short (top 40)\n\n```\n%s\n```\n' "$GIT_STATUS"
  fi
  if [[ -n "$TRANSCRIPT_PATH" && -f "$TRANSCRIPT_PATH" ]]; then
    printf '\n## transcript tail (raw, last ~4000 bytes)\n\n```\n'
    tail -c 4000 "$TRANSCRIPT_PATH" 2>/dev/null || true
    printf '\n```\n'
  fi
} > "$SNAP_TMP" 2>/dev/null || true

if [[ -s "$SNAP_TMP" ]]; then
  if [[ -n "$SESSION_ID" ]]; then
    cp "$SNAP_TMP" "$SNAP_SESS_DIR/$SESSION_ID.md" 2>/dev/null || true
  fi
  cp "$SNAP_TMP" "$SNAP_CWD_DIR/$CWD_HASH.md" 2>/dev/null || true
fi
rm -f "$SNAP_TMP" 2>/dev/null || true

# 4. Recovery markers (one-shot; consumed by recovery-inject.sh).
NOW=$(date +%s)
if [[ -n "$SESSION_ID" ]]; then
  printf '%s\n' "$NOW" > "$MARK_SESS_DIR/$SESSION_ID" 2>/dev/null || true
fi
printf '%s\n' "$NOW" > "$MARK_CWD_DIR/$CWD_HASH" 2>/dev/null || true

exit 0
