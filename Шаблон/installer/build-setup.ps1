# Build LiraSlabZones-Setup.exe (≤100 MB)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not (Test-Path (Join-Path $root 'LiraSlabZones.sln'))) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
Set-Location -LiteralPath $root

Write-Host "== Build Release x64 =="
dotnet build ".\LiraSlabZones.sln" -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$stage = Join-Path $root "dist\stage"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
@(
    "addin",
    "tools",
    "data\config",
    "data\families",
    "data\output"
) | ForEach-Object { New-Item -ItemType Directory -Force -Path (Join-Path $stage $_) | Out-Null }

$revitBin = Join-Path $root "src\LiraSlabZones.Revit2023\bin\x64\Release\net48"
$coreBin  = Join-Path $root "src\LiraSlabZones.Core\bin\x64\Release\net48"
$previewBin = Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\Release\net48"

Copy-Item (Join-Path $revitBin "LiraSlabZones.Revit2023.dll") (Join-Path $stage "addin") -Force
foreach ($name in @("LiraSlabZones.Core.dll","Newtonsoft.Json.dll","LiraSapr.Interop.dll","LiraResAPI.Interop.dll")) {
    $src = Join-Path $coreBin $name
    if (Test-Path -LiteralPath $src) { Copy-Item $src (Join-Path $stage "addin") -Force }
}

Copy-Item (Join-Path $previewBin "LiraSlabZones.PreviewHost.exe") (Join-Path $stage "tools") -Force
Copy-Item (Join-Path $previewBin "*.dll") (Join-Path $stage "tools") -Force

@'
{
  "ConcreteClass": "B25",
  "AutoLayout": true,
  "GridCellMm": 300,
  "FamilyName": "SUM-30-Зона дополнительного армирования"
}
'@ | Set-Content -LiteralPath (Join-Path $stage "data\config\settings.json") -Encoding UTF8

$famDir = Join-Path $root "families"
if (Test-Path -LiteralPath $famDir) {
    Get-ChildItem -LiteralPath $famDir -Filter *.rfa | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage "data\families\$($_.Name)") -Force
    }
}

$stageMb = [math]::Round(((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Stage size: $stageMb MB"

$iscc = @(
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install JRSoftware.InnoSetup." }

$iss = Join-Path $root "installer\LiraSlabZones.iss"
Write-Host "== Compile $iss =="
& $iscc $iss
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $root "dist\LiraSlabZones-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Setup.exe not produced" }
$mb = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 2)
Write-Host "OK: $setup ($mb MB)"
if ($mb -gt 100) { throw "Setup exceeds 100 MB ($mb MB)" }
