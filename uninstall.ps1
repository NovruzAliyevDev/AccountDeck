<#
  Removes the Account Deck program, its Start menu shortcut and its "start with Windows" entry.

  Saved accounts are NOT removed: parked Claude Desktop accounts are real data (chats, settings,
  sign-ins) and live in %LOCALAPPDATA%\AccountDeck. Switch back to the account you want to keep
  using first, then delete that folder yourself if you no longer need the others.
#>
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\AccountDeck'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Account Deck.lnk'
$data = Join-Path $env:LOCALAPPDATA 'AccountDeck'

Get-Process AccountDeck -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AccountDeck' -ErrorAction SilentlyContinue

Write-Host 'Account Deck was removed.'
if (Test-Path $data) { Write-Host "Saved accounts were kept in $data" }
