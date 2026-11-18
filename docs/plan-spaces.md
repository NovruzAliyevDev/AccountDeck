# Plan: Spaces — keep work and personal Claude history apart

Status: proposal, nothing built yet.

## What the user wants

1. Personal projects and work projects never mix: conversations, file history, project memory and
   the prompt history belong to one side only.
2. General instructions are written once and used by both sides, for example `CLAUDE.md` and
   personal skills. The user picks what is shared, once, in a clear screen.
3. Switching still happens in Account Deck, with one click.

## Why it does not work today

Account Deck swaps only the sign-in. Both Claude Code in a terminal and the Code tab of Claude
Desktop keep everything else in one folder, `~\.claude`, whichever account is signed in:

| In `~\.claude` | What it is |
| --- | --- |
| `projects\` | Conversations, one folder per project path, including each project's memory |
| `file-history\` | Earlier versions of files Claude edited, one folder per session |
| `history.jsonl` | Everything typed at the prompt, all projects |
| `CLAUDE.md`, `skills\`, `agents\`, `commands\` | The user's own instructions and tools |
| `settings.json`, `plugins\` | Permissions, hooks, enabled plugins |

On top of that, the setting "Share sessions between accounts" copies every Claude Desktop session
entry into every account's sidebar, so the work account lists personal sessions and the reverse.

## The idea: Spaces

A **Space** is a named side, such as *Work* or *Personal*. Each space has:

- one Claude Desktop account and/or one Claude Code account,
- its own copy of the private parts of `~\.claude` (conversations, file history, prompt history),
- access to the shared parts (instructions), which stay where they are.

The window gets a row of space buttons. Clicking **Personal** does everything at once: switches
Claude Desktop, switches Claude Code and swaps the history. Single accounts stay switchable as
today for anyone who does not turn Spaces on.

Linking both tools to a space is deliberate: Desktop and the terminal share `~\.claude`, so
"Desktop on Personal, terminal on Work" would write both sides into the same folder.

## How a switch works

The same pattern Account Deck already uses for Claude Desktop folders:

1. Check that nothing is using `~\.claude`: close Claude Desktop (existing code), and refuse to
   continue while a terminal `claude.exe` is running ("Close your Claude Code terminals first").
2. Move the private items of the current space from `~\.claude` into
   `%LOCALAPPDATA%\AccountDeck\spaces\<space id>\` (same drive, so these are instant renames).
3. Move the private items of the target space into `~\.claude`.
4. Switch the Desktop and Claude Code sign-ins (existing code).
5. Start Claude Desktop.

Shared items are never moved, so both spaces always see the same `CLAUDE.md` and skills, and an
edit made in either space is simply there.

Safety, as in the existing switcher:

- A marker file `~\.claude\.accountdeck-space.json` says which space is live. If the PC stops
  half way, the next start finishes or undoes the switch.
- If a move fails, everything already moved goes back.
- Session-sidebar copying ("Share sessions between accounts") is turned off while Spaces is on,
  because it would list sessions whose files are parked in the other space.

### Rejected: `CLAUDE_CONFIG_DIR`

Claude Code can be pointed at another folder with the `CLAUDE_CONFIG_DIR` environment variable,
but Account Deck starts the Store app through `explorer.exe shell:AppsFolder\…`, so the app would
only see it if the variable were written into the Windows user environment, which then also
changes every terminal. Swapping folders needs no environment changes and works for the terminal
and the app the same way.

## The one-time setup screen

Turning on **Settings > Keep spaces separate** opens a short guide. Plain words, no file names in
the main text (file names shown small, for those who want them).

**Step 1 — Your spaces.** Two spaces are suggested, *Work* and *Personal*. For each, pick the
Claude Desktop account and the Claude Code account from the saved accounts.

**Step 2 — What both spaces share.** One list, item by item, so the user can share some
instructions and keep others to one side. Every row has three choices: **Both**, **Work only**,
**Personal only**. Clicking a row shows its full text on the right, so nothing is chosen blind.

```
Your instructions                         Both   Work   Personal
  Session naming (Jira TST test tickets)    ○      ●      ○
  Read first, act later                     ●      ○      ○
  Worktree per ticket (ServicePortal)       ○      ●      ○
  Use the database, never guess             ○      ●      ○
  Lessons                                   ○      ●      ○
Your skills
  implement-ticket                          ○      ●      ○
  test-ticket                               ○      ●      ○
Your agents and commands
  (one row per file)
Settings
  Settings and permissions                  ○      ●      ○   (copied, then each side keeps its own)
  Plugins                                   ○      ●      ○
  MCP servers and trusted folders           ○      ●      ○   (~\.claude.json)
Always separate 🔒
  Conversations, file history, project memory, prompt history
```

Where the rows come from:

- **Instructions:** `~\.claude\CLAUDE.md` is cut at its `##` headings; each heading is one row,
  named by the heading. Text above the first `##` heading is its own row ("General"). Each file
  already in `~\.claude\rules\` is a row too.
- **Skills:** one row per folder in `~\.claude\skills\`, named by the skill's `name`, with its
  `description` as the help text.
- **Agents and commands:** one row per file in `~\.claude\agents\` and `~\.claude\commands\`.

Defaults: rows that mention a work word the user typed in step 1 (for example "Tayqa", "Jira",
"ServicePortal") start on the Work side; everything else starts on **Both**. The user checks and
changes the rows; nothing is decided without being shown.

How a per-section split is stored, so Claude reads the right instructions on each side:

- First setup turns each `##` section of `~\.claude\CLAUDE.md` into its own file in
  `~\.claude\rules\` (for example `rules\read-first-act-later.md`). Claude Code loads every file
  in `~/.claude/rules/` for every project, the same as `CLAUDE.md` (documented under "User-level
  rules" on code.claude.com/docs/en/memory). Existing files in `rules\` become rows too.
  Tested on 2026-11-14 with Claude Code 2.1.322: a file placed in `~\.claude\rules\` was picked up
  by a fresh `claude -p` session started in an unrelated folder.
- Rule files set to **Both** stay in `rules\`. Rule files set to one side are swapped with that
  space, like conversations. So each side sees its own rules plus the shared ones, and every
  switch only moves real files.
- `CLAUDE.md` is left with whatever the user did not split out, and stays shared.
- Skills, agents and commands marked for one side are swapped with that space; shared ones stay
  in place.

Why rule files and not an `@shared/...` import line in a per-space `CLAUDE.md`: imports from the
user-level `CLAUDE.md` do work without an approval dialog (per the docs). But in Cowork sessions Claude Code skips
user-level imports that point outside the session's working folder, and skips a `CLAUDE.md` that
is a symlink or hard link. Plain files in `rules\` avoid both limits.
- A section added later goes wherever the user writes it. **Settings > Spaces > Instructions**
  shows the same list again to move a section or skill to the other side at any time.

**Step 3 — Sort your existing projects.** Account Deck lists every project folder it finds in
`~\.claude\projects` with its real path and how many conversations it has, and suggests a space
for each. The user changes any row with a dropdown. Suggestions come from simple rules the user
can edit, for example "paths under `D:\GameDev` or `Desktop\Development` → Personal".

**Step 4 — Check and confirm.** A summary ("124 conversations go to Work, 61 to Personal;
shared: 1 instruction; Work only: 4 instructions, 2 skills"), a note that a backup copy is made
first, and **Start**.

The choices can be changed later in Settings; changing them shows the same screens.

## What moves where (first setup)

- `projects\<path>\` goes to the space chosen in step 3.
- `file-history\<session id>\` follows the session: a session id belongs to the space of the
  project that holds `<session id>.jsonl`.
- `history.jsonl` is split line by line using each line's `project` path.
- Items unchecked in step 2 are copied, so each space starts with the same settings, then drift
  apart.
- Runtime folders (`sessions\`, `session-env\`, `shell-snapshots\`, `ide\`, `cache\`) are left
  alone; they hold nothing worth keeping per space.

## Code changes

| Where | Change |
| --- | --- |
| `Models.cs` | `Space` (id, name, Desktop profile id, Claude Code profile id), `SpaceSettings` (enabled, shared items, sorting rules), `AppState.Spaces`, `ActiveSpaceId` |
| `AppPaths.cs` | `SpacesStore` = `%LOCALAPPDATA%\AccountDeck\spaces` |
| `CredentialSwitchers.cs` | When `~\.claude.json` is per side, write `oauthAccount` after the space swap, not before |
| New `SpaceSwitcher.cs` | Reconcile, switch, first-time split, rollback; built like `DesktopSwitcher` |
| New `InstructionCatalog.cs` | Reads `CLAUDE.md` sections, `rules\` files, skills, agents and commands into rows; splits `CLAUDE.md` sections into `rules\*.md` files on first setup |
| `IDesktopHost` | `IsClaudeCodeRunning()` for terminal sessions not started by the app |
| `DesktopSwitcher` | Skip `SyncSessions` while Spaces is on |
| `DeckService` | `SwitchSpaceAsync`, `SetUpSpacesAsync` |
| App (WPF) and Mac (Avalonia) | Space buttons, tray menu entries, the four-step guide |
| `Tests.cs` | Split, switch, interrupted switch, shared items untouched, running terminal blocks the switch |
| `README.md` | New section "Keep work and personal apart" |

## Decisions

1. **`~\.claude.json` is a checklist row** ("MCP servers and trusted folders"). It sits outside
   `~\.claude` and also holds `oauthAccount`, which `ClaudeCodeSwitcher` rewrites. When it is set
   to one side, a space switch swaps the file first and writes the sign-in into it afterwards, so
   the account written last always wins. When it is shared, it stays in place as today.
2. **Session entries already copied** into the other account's sidebar by the old sharing setting
   are left as they are. First setup does not delete anything from Claude Desktop's data.
3. **Offered upstream** as a pull request to `NovruzAliyevDev/AccountDeck`, on a feature branch,
   only after the user has tried it and says yes to opening the PR.
