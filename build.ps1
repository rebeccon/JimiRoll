# ============================================================
#  JimiRoll  --  build & package
#
#  Produces a ready-to-publish zip in  dist\
#
#  Usage:
#    .\build.ps1                 build + package
#    .\build.ps1 -SkipBuild      package only (reuse last build)
#    .\build.ps1 -Deploy         also copy into the game's mods folder
#
#  NOTE: ASCII ONLY. Windows PowerShell 5.1 reads .ps1 files using the
#  system OEM codepage unless there is a BOM, so UTF-8 Chinese text here
#  would get mangled. Chinese docs live in README.md / CHANGELOG.md.
# ============================================================
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$csproj  = Join-Path $root 'JimiRoll.csproj'
$manifest= Join-Path $root 'JimiRoll.json'
$outDir  = Join-Path $root 'dist'

# ---- read id / version straight from the manifest (single source of truth) ----
$mf      = Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$modId   = $mf.id
$modName = $mf.name
$version = $mf.version

Write-Host "[build] $modName  (id=$modId  v$version)" -ForegroundColor Cyan

# ---- build ----
if (-not $SkipBuild) {
    Write-Host '[build] compiling...' -ForegroundColor Cyan
    $gameDir = if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { 'D:\steam\steamapps\common\Slay the Spire 2' }
    dotnet build $csproj -c Release -p:SkipDeploy=true -p:GameDir="$gameDir"
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}

$binDir = Join-Path $root 'bin\Release'
if (-not (Test-Path $binDir)) { $binDir = Join-Path $root 'bin\Release\net9.0' }
$dll = Join-Path $binDir "$modId.dll"
if (-not (Test-Path $dll)) { throw "compiled dll not found: $dll" }

# ---- stage ----
$stage = Join-Path $outDir "$modId-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $dll $stage
$pdb = Join-Path $binDir "$modId.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $stage }

# manifest: rewrite with the version taken from itself (keeps them in sync)
$mfOut = $mf | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText((Join-Path $stage "$modId.json"), $mfOut, (New-Object System.Text.UTF8Encoding($false)))

foreach ($extra in @('Pause.cmd', 'README.md', 'LICENSE', 'CHANGELOG.md')) {
    $p = Join-Path $root $extra
    if (Test-Path $p) { Copy-Item $p $stage }
}

# ---- zip ----
$zip = Join-Path $outDir "$modId-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Write-Host ''
Write-Host "[build] packaged: $zip" -ForegroundColor Green
Write-Host '[build] contents:' -ForegroundColor Green
Get-ChildItem $stage | ForEach-Object { Write-Host ("         {0,-22} {1,8} bytes" -f $_.Name, $_.Length) }

# ---- optional deploy ----
if ($Deploy) {
    $gameDir = if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { 'D:\steam\steamapps\common\Slay the Spire 2' }
    $modsDir = Join-Path $gameDir "mods\$modId"
    New-Item -ItemType Directory -Force -Path $modsDir | Out-Null
    Copy-Item (Join-Path $stage '*') $modsDir -Force
    Write-Host "[build] deployed to $modsDir" -ForegroundColor Green
}
