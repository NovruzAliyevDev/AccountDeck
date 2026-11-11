<#
  Builds Account Deck and installs it for the current Windows user.
  No administrator rights needed. Run again any time to update.

    .\install.ps1                 # build, install, add a Start menu shortcut, open the app
    .\install.ps1 -NoLaunch       # same, but do not open the app
    .\install.ps1 -SelfContained  # bundle .NET, so the result also runs on PCs without .NET 10
#>
param([switch]$NoLaunch, [switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$build = Join-Path $env:TEMP 'AccountDeck-build'   # short path: deep folders break MSBuild's 260-character limit
$target = Join-Path $env:LOCALAPPDATA 'Programs\AccountDeck'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Account Deck.lnk'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required to build Account Deck. Get it from https://dotnet.microsoft.com/download'
}

Write-Host 'Building Account Deck...'
$publishArgs = @('publish', (Join-Path $repo 'src\AccountDeck.App'), '-c', 'Release', '--artifacts-path', $build,
    '-o', (Join-Path $build 'out'), '--nologo', '-v', 'q')
if ($SelfContained) { $publishArgs += @('-r', 'win-x64', '--self-contained', 'true') }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'The build failed. See the messages above.' }

Get-Process AccountDeck -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $build 'out\*') $target -Recurse -Force

$exe = Join-Path $target 'AccountDeck.exe'
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.Description = 'Switch and monitor Claude Desktop, Claude Code and Codex accounts'
$link.Save()

Write-Host "Installed to $target"
Write-Host 'Open it from the Start menu: Account Deck'
if (-not $NoLaunch) { Start-Process $exe }
