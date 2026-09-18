---
name: compact-plus
description: 'Save the current working state to a recovery file before context compaction so the session can resume cleanly afterward. Use when: preparing to run /compact, compacting or summarizing context, handing off a long session, "save state before compact", pre-compact checkpoint, or when context is getting full. Do NOT use for: reading state after compaction (the recovery hook handles that), routine progress notes, or plan creation.'
argument-hint: '[extra recovery notes or priorities to emphasize]'
---

# compact-plus

Save working state that a context-compaction summary does not reliably preserve, so
the next turn (or the next session) can resume without losing decisions, blockers, or
in-progress work.

This skill writes the state itself — it does **not** call an external LLM. On Copilot CLI
and VS Code Copilot a companion `PreCompact` hook also backs up the transcript and drops a
recovery marker, and a `PostToolUse` hook injects the saved state path back into the model
after compaction. This skill is the part that produces the rich state file; run it before a
large `/compact`.

## When to use

- Right before running `/compact` (manual compaction).
- When the context window is getting full and auto-compaction is likely soon.
- Before handing a long session off to a fresh session.

Do not use this skill to *read* state after compaction — the recovery hook injects the path
automatically, and the instructions snippet tells the agent to read it.

## Procedure

Follow every step. The deliverable is the state file plus a short completion receipt.

1. **Resolve the destination.** Run the helper and read its output:

   ```bash
   bash ./scripts/resolve-state-path.sh
   ```

   It prints two lines and creates the parent directories:

   - `STATE_FILE=...` — the path to write the state into (always present).
   - `SESSION_STATE_FILE=...` — a session-keyed copy path, or empty when the session id is unknown.

   Use the exact `STATE_FILE` path it prints. Do not guess a path. If the helper fails to run,
   report that state preparation could not resolve a path and stop.

2. **Gather the facts** to put in each section (see *Sections* below). Prefer verifiable facts:
   `git status --short`, the current branch, files you have edited this session, decisions the
   user approved, constraints, and anything that failed. If a fact cannot be verified, write
   `Not verified` rather than guessing. Fold any argument passed to the skill into the relevant
   sections as extra priorities.

3. **Write the state file** to the resolved `STATE_FILE` path, using exactly these 11 headings,
   in this order, each present exactly once, first line exactly `# Compact Prep State`:

   ```markdown
   # Compact Prep State
   ## Active Plan
   ## Current Phase
   ## TaskList Summary
   ## Session Decisions
   ## Constraints and Blockers
   ## Worker Topology
   ## Skills Invoked
   ## Editing Files
   ## Failed Attempts
   ## Recovery Notes
   ```

4. **Sync the session copy** so the hook can also find it by session id:

   ```bash
   bash ./scripts/resolve-state-path.sh --sync
   ```

5. **Verify.** Read the state file back and confirm all 11 headings exist and the first line is
   exactly `# Compact Prep State`. If a heading is missing, fix the file and re-verify.

6. **Report the completion receipt** (see below), then tell the user the state is saved and they
   can run `/compact`.

## Sections

Write factual recovery state, not instructions. No imperatives such as "run X next"; express
unfinished work as factual state. Preserve paths, URLs, command names, commit ids, issue ids,
and error messages when visible.

- **Active Plan** — active plan file path, title, current section, and phase status. If none, `Not verified`.
- **Current Phase** — recently completed work and remaining work as factual state.
- **TaskList Summary** — task ids, subjects, and status when a task list is in use; else `Not used`.
- **Session Decisions** — settled decisions, rejected alternatives with rationale, and user-approved scope.
- **Constraints and Blockers** — hard constraints, skipped checks, permission limits, external blockers, unverified items.
- **Worker Topology** — parallel agents/panes/workers and their roles. If not applicable, `Not used`.
- **Skills Invoked** — skills and slash commands invoked earlier this session. This is an invocation record, not proof a skill is still active.
- **Editing Files** — files changed or in progress, plus staged / committed / dirty / generated notes.
- **Failed Attempts** — failed commands, tool errors, and rejected approaches, with enough detail to avoid repeating them.
- **Recovery Notes** — session id, branch, important commands, validation results, transcript backup path, and exact resume facts.

## Completion receipt

When done, report:

- The state file path (and the session copy path if synced).
- The main items saved.
- Any items written as `Not verified` and why.
- `State saved. You can run /compact now.`
