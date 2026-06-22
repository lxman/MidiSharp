# Builds the Windows clean-room native plugin fixtures into ./out (git-ignored).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$clang = (Get-Command clang -ErrorAction SilentlyContinue)?.Source
if (-not $clang) { if (Test-Path "C:\clang\bin\clang.exe") { $clang = "C:\clang\bin\clang.exe" } else { throw "clang not found (PATH or C:\clang\bin)" } }

$clapInc = if ($env:CLAP_INCLUDE) { $env:CLAP_INCLUDE } else { "C:\Users\jorda\clap\include" }
if (-not (Test-Path $clapInc)) { throw "CLAP include path not found: $clapInc. Set the CLAP_INCLUDE env var to your clap/include directory." }

& $clang -shared -O2 -I $clapInc -o (Join-Path $out "midisharp_gui.clap") (Join-Path $root "clap_gui_fixture.c") -luser32
if ($LASTEXITCODE -ne 0) { throw "clang failed building clap fixture ($LASTEXITCODE)" }
Write-Host "Built: $(Join-Path $out 'midisharp_gui.clap')"
