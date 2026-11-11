<#
  Builds the release file: dist\AccountDeck-Setup.exe
  One self-contained file (includes .NET), so users need nothing else installed.

    .\release.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$build = Join-Path $env:TEMP 'AccountDeck-release'   # short path: deep folders break MSBuild's 260-character limit
$dist = Join-Path $repo 'dist'

& dotnet test (Join-Path $repo 'tests\AccountDeck.Tests') --nologo -v q --artifacts-path $build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release was built.' }

& dotnet publish (Join-Path $repo 'src\AccountDeck.App') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none --artifacts-path $build -o (Join-Path $build 'out') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The build failed. See the messages above.' }

New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist 'AccountDeck-Setup.exe'
Copy-Item (Join-Path $build 'out\AccountDeck.exe') $setup -Force
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ("Built {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host "SHA256 $hash"
