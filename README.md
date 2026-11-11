# Account Deck

Switch between several accounts in **Claude Desktop**, **Claude Code** and **Codex** on Windows,
and watch how much of each account's plan limit is used, from one window and a tray icon.

## Install

You need Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\install.ps1
```

This builds the app, installs it for your Windows user (no administrator rights), adds
**Account Deck** to the Start menu and opens it. Run it again to update. `.\uninstall.ps1` removes it.

To try the app safely first, run it on sample accounts. Nothing real is touched:

```powershell
& "$env:LOCALAPPDATA\Programs\AccountDeck\AccountDeck.exe" --demo
```

## Using it

The first time it opens, Account Deck saves whatever is signed in right now as your first account
in each section. After that:

| To do this | Do this |
| --- | --- |
| Add a second Claude Desktop account | **Add account** in the Claude Desktop section. Your current account is parked, Claude Desktop restarts with an empty sign-in screen, and you log in with the other account. |
| Add a second Claude Code or Codex account | **Add account** in that section. A terminal opens with the tool's own login command. The new account appears in the list as soon as you finish. |
| Switch | **Switch** next to the account, or right-click the tray icon and pick it. |
| See usage | The bars show the 5-hour and weekly limits. **Refresh** updates them now; otherwise they update every few minutes. |
| Rename or remove | The buttons on each account. A removed Claude Desktop account goes to the Recycle Bin. |

Settings has: start with Windows, keep running in the tray, how often to check usage, a notification
when the Claude Code account in use gets close to a limit, and automatic switching of Claude Code to
the account with the most room left.

### Good to know

- **Switching Claude Desktop closes and reopens it.** Anything it is doing at that moment stops.
  Each Claude Desktop account has its own chats list, settings, MCP configuration and Cowork/Code
  sessions on this computer, because the whole data folder is swapped.
- **Claude Code and Codex switch instantly**, but a session that is already open keeps the account
  it started with until you restart it.
- Claude Desktop usage is what Claude Desktop itself last recorded, so it can be a little old for
  accounts you are not using. Claude Code usage comes live from Anthropic. Codex does not report usage.

## How it works

| Tool | Where the sign-in lives | What a switch does |
| --- | --- | --- |
| Claude Desktop (Microsoft Store) | `%LOCALAPPDATA%\Claude-Data\claude_<id>` | Close Claude, rename that folder into `%LOCALAPPDATA%\AccountDeck\desktop-profiles\<id>`, rename the chosen profile folder into its place, start Claude. |
| Claude Desktop (classic installer) | `%APPDATA%\Claude` | The same, with that folder. |
| Claude Code | `claudeAiOauth` in `~\.claude\.credentials.json`, `oauthAccount` in `~\.claude.json` | Rewrite only those two entries; everything else in the files is kept. |
| Codex | `~\.codex\auth.json` (or `%CODEX_HOME%`) | Replace the file. |

The Store build is the reason this project exists: it keeps its data in `Claude-Data`, not in
`%APPDATA%\Claude`, so tools that swap `%APPDATA%\Claude` change nothing.

Safety measures:

- A switch is two renames on the same drive, so it is instant regardless of size. If the second
  rename fails, the first is undone.
- A small marker file (`.accountdeck-profile.json`) in every profile folder says which account it
  is. If the app or the PC stops in the middle of a switch, the next start puts things back, and the
  account list can be rebuilt from the folders even if the app's own state file is lost.
- Before renaming, Claude Desktop and everything it started (bundled Claude Code, shells, MCP
  servers) are closed, because any of them can hold the folder open. Nothing is moved if they do
  not close.
- Saved Claude Code and Codex sign-ins are encrypted with Windows DPAPI for your Windows user and
  stored in `%LOCALAPPDATA%\AccountDeck\state.json`. Tokens are only ever sent to Anthropic's own
  endpoints (`api.anthropic.com` for usage, `platform.claude.com` to renew an expired sign-in).

`AccountDeck.exe --diagnose` writes a read-only report of what the app detects on this PC.

## Development

```
src/AccountDeck.Core    switching logic, no UI
src/AccountDeck.App     WPF window + tray icon
tests/AccountDeck.Tests xUnit tests that run every switch on temporary folders
```

```powershell
dotnet test tests\AccountDeck.Tests --artifacts-path "$env:TEMP\AccountDeck-build"
dotnet run --project src\AccountDeck.App --artifacts-path "$env:TEMP\AccountDeck-build" -- --demo
```

`--artifacts-path` keeps build output in a short folder; it is only needed when the repository
sits in a very deep path.

## Status

Version 0.1. Every switch is covered by automated tests on temporary folders, and the whole app can
be exercised in demo mode. Detection of the real Claude Desktop (data folder, how to start it, which
processes to close) is verified on a Microsoft Store install. Claude Desktop is never switched
automatically, on purpose: switching it closes the app.
