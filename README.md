# Account Deck

Switch between several accounts in **Claude Desktop**, **Claude Code** and **Codex** on Windows
and macOS, and watch how much of each account's plan limit is used, from one window and a tray
(menu bar) icon.

## Install

1. Download **AccountDeck-Setup.exe** from the [latest release](https://github.com/NovruzAliyevDev/AccountDeck/releases/latest).
2. Double-click it and choose **Install**.

That is all: nothing else needs to be installed, and no administrator rights are needed. Account
Deck appears in the Start menu and opens straight away. To update, run a newer setup file; your
saved accounts are kept. To remove it, use Windows Settings > Apps > Installed apps.

The file is not code-signed, so Windows may show "Windows protected your PC" the first time.
Choose **More info > Run anyway**.

To try the app on sample accounts without touching anything real:

```powershell
& "$env:LOCALAPPDATA\Programs\AccountDeck\AccountDeck.exe" --demo
```

### macOS

1. Download **AccountDeck-mac.zip** from the latest release, or build it yourself with
   `./build-mac.sh` (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)).
2. Unzip it and drag **Account Deck** into Applications (or run `./build-mac.sh --install`).
3. The app is not notarized, so the first time, right-click it and choose **Open**
   (or System Settings > Privacy & Security > **Open Anyway**).

Account Deck lives in the menu bar; closing its window keeps it running there. To try it on sample
accounts without touching anything real:

```bash
open -n "/Applications/Account Deck.app" --args --demo
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
- **Claude Desktop sessions are shared between accounts by default** (Settings > Share sessions
  between accounts). All accounts use one data folder, so Code sessions, settings, MCP
  configuration and files are there whichever account is signed in; only the sign-in is swapped.
  Claude Desktop lists Code sessions per account, so Account Deck also copies each session's entry
  to the other accounts (newest version wins, deleted sessions stay deleted). Cowork sessions are
  not copied. Turn the setting off to give every account its own complete, separate data folder.
- **Claude Code and Codex switch instantly**, but a session that is already open keeps the account
  it started with until you restart it.
- Claude Desktop usage is what Claude Desktop itself last recorded, so it can be a little old for
  accounts you are not using. Claude Code usage comes live from Anthropic. Codex does not report usage.

## How it works

| Tool | Where the sign-in lives | What a switch does |
| --- | --- | --- |
| Claude Desktop (Microsoft Store), shared sessions | `%LOCALAPPDATA%\Claude-Data\claude_<id>` | Close Claude, move only the sign-in pieces (`Network`, `Local Storage`, `Session Storage`, `IndexedDB`, `WebStorage`, `SharedStorage` and the `oauth:*` entries of `config.json`) into `%LOCALAPPDATA%\AccountDeck\desktop-profiles\<id>`, move the chosen account's pieces in, copy session entries across `claude-code-sessions\<account>\<org>`, start Claude. |
| Claude Desktop (Microsoft Store), separate | same folder | Close Claude, rename the whole folder into `desktop-profiles\<id>`, rename the chosen profile folder into its place, start Claude. |
| Claude Desktop (classic installer) | `%APPDATA%\Claude` | The same, with that folder. |
| Claude Code | `claudeAiOauth` in `~\.claude\.credentials.json`, `oauthAccount` in `~\.claude.json` | Rewrite only those two entries; everything else in the files is kept. |
| Claude Desktop (macOS) | `~/Library/Application Support/Claude` | The same as on Windows. Claude is asked to quit, then it and everything it started are ended. The sign-in pieces also include the top-level `Cookies` file. |
| Claude Code (macOS) | `claudeAiOauth` in the login Keychain item `Claude Code-credentials`, `oauthAccount` in `~/.claude.json` | Rewrite only those entries, through `/usr/bin/security`. |
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
  stored in `%LOCALAPPDATA%\AccountDeck\state.json`. On macOS they are encrypted with AES-GCM using
  a random key kept in your login Keychain (item `AccountDeck`), and stored in
  `~/Library/Application Support/AccountDeck/state.json`. A removed Claude Desktop account goes to the Trash. Tokens are only ever sent to Anthropic's own
  endpoints (`api.anthropic.com` for usage, `platform.claude.com` to renew an expired sign-in).

`AccountDeck.exe --diagnose` writes a read-only report of what the app detects on this PC. On macOS:
`"/Applications/Account Deck.app/Contents/MacOS/AccountDeck" --diagnose`.

## Development

```
src/AccountDeck.Core    switching logic, no UI (net10.0; Windows and macOS hosts)
src/AccountDeck.App     Windows: WPF window + tray icon
src/AccountDeck.Mac     macOS: Avalonia window + menu bar icon
tests/AccountDeck.Tests xUnit tests that run every switch on temporary folders (run on Windows and macOS)
```

```powershell
dotnet test tests\AccountDeck.Tests --artifacts-path "$env:TEMP\AccountDeck-build"
dotnet run --project src\AccountDeck.App --artifacts-path "$env:TEMP\AccountDeck-build" -- --demo
.\install.ps1    # build from source and install (needs the .NET 10 SDK)
.\release.ps1    # run the tests and build dist\AccountDeck-Setup.exe
```

On macOS:

```bash
dotnet test tests/AccountDeck.Tests
dotnet run --project src/AccountDeck.Mac -- --demo
./build-mac.sh             # run the tests and build dist/Account Deck.app and dist/AccountDeck-mac.zip
./build-mac.sh --install   # same, then copy it to ~/Applications and open it
```

The setup file is the app itself, published as one self-contained file. Started under a name
containing "Setup" it copies itself to `%LOCALAPPDATA%\Programs\AccountDeck`, adds the Start menu
shortcut and the Installed apps entry, and opens the installed copy.

## Status

Version 1.0. Every switch is covered by automated tests on temporary folders, and the whole app can
be exercised in demo mode. Switching and session sharing are in daily use on a Microsoft Store
install of Claude Desktop; the classic installer build and Codex are covered by tests only.
