# Plan: the Hub, a central place for every account's Claude data

Account Deck already switches accounts and keeps spaces apart. This plan makes it the one place to
look after what each account and space keeps on disk: conversations, their size, Claude's memory
files, and the clutter left by worktrees and scratch folders.

## Project groups

Claude Code only knows folders. One product ends up as many rows: CloudErp is
`TayqaTech\CloudErp`, `...\TayqaTech.CloudErp`, `...\CloudErpPublishSource\sql-scripts\Versions\Version-2-0-1`
and every worktree made from those repos. A **group** gathers them.

- Groups are suggested automatically for every project folder (in every space):
  1. Find the git repository the folder is in. A worktree points back to the repository it was
     made from (its `.git` file names it), so worktrees join their repository.
     Folders that no longer exist are matched by name: `<repo>\.claude\worktrees\<name>`,
     `wt\<Repo>-<KEY>` next to `<Repo>`, `~\.claude\queues\<Name>-<date>-wt-NN`.
  2. The group is the nearest folder above that repository that Claude was also used in
     (`TayqaTech\CloudErp` for both CloudErp repositories, `TayqaSale` for ServicePortal and its
     worktrees). If there is none, the repository itself is the group (FiruzFitness, Ishtah, RiseOfNecromant).
     The home folder, the Desktop and drive roots are never groups.
- The user can rename a group, make a new one and move a folder to another group. Only those
  changes are saved (in Account Deck's state); nothing in Claude's folders changes.
- Groups are used everywhere: the conversation list, storage, memory, and (later) step 3 of the
  spaces guide and moving a whole group to another space.

## Conversations

The **Hub** (button next to Refresh, and in the tray menu; it works with or without spaces), tab **Conversations**:

- Every conversation, sorted into space → group, with title or first prompt, date, project folder
  and its size on disk (transcript, its folder, file history).
- Search, sort by newest or largest, filter by group.
- **Move** to another space (already built).
- **Delete** one or many. Everything that belongs to the conversation goes: transcript and its
  folder, file history, to-dos, its prompt-history lines and its Claude Desktop sidebar entries
  (in every account). Files go to the Recycle Bin, so a mistake can be undone. Claude Desktop is
  closed first and it is refused while Claude Code runs in a terminal, as for spaces.

## Storage

- Total per space, and per group, biggest first.
- What else takes room in `~/.claude` (plugins, skills, caches) and in Claude's scratch folder in Temp.
- Later: cleanup buttons (empty scratch folders, file history older than N days, worktrees whose
  branch is merged), each with a confirmation saying what and how much.

## Memory (next)

- Every project's memory (`memory\MEMORY.md` and its notes), per group, plus CLAUDE.md and rules.
- Read, edit, delete; flag notes that look stale or belong to the other space.

## Overview (next)

- One card per space: accounts and usage, projects and conversations, last used, disk use split by kind.

## Order

1. Groups, Conversations (size, delete), Storage totals. ← this round
2. Memory.
3. Cleanup buttons, Overview, groups in the spaces guide.

## Not changed

- Nothing here renames or moves Claude's own folders; groups are Account Deck's view.
- Deleting always confirms what and how much, and uses the Recycle Bin.
