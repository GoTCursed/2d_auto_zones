param([int[]]$Versions = @(2022, 2023, 2025, 2026))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

foreach ($version in $Versions) {
    if ($version -notin @(2022, 2023, 2025, 2026)) {
        throw "Unsupported Revit version: $version"
    }

    $framework = if ($version -ge 2025) { 'net8.0-windows' } else { 'net48' }
    $project = "LiraSlabZones.Revit$version"
    $src = Join-Path $root "src\$project\bin\x64\Release\$framework"
    $sourceDll = Join-Path $src "$project.dll"
    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "Build is missing: $sourceDll. Run dotnet build LiraSlabZones.sln -c Release -p:Platform=x64"
    }

    $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $deploy = Join-Path $addinDir 'LiraSlabZones'
    New-Item -ItemType Directory -Force -Path $deploy | Out-Null
    Copy-Item -Path (Join-Path $src '*.dll') -Destination $deploy -Force
    Copy-Item -Path (Join-Path $root 'interop\*.dll') -Destination $deploy -Force

    $defCfg = Join-Path $root 'DefaultSettings.cfg'
    if (Test-Path -LiteralPath $defCfg) {
        Copy-Item -LiteralPath $defCfg -Destination (Join-Path $deploy 'DefaultSettings.cfg') -Force
    }

    $targetDll = Join-Path $deploy "$project.dll"
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>LiraSlabZones</Name>
    <Assembly>$targetDll</Assembly>
    <AddInId>B7E6C2A1-4F3D-4A9E-9C11-8D2A6F0E5B21</AddInId>
    <FullClassName>LiraSlabZones.Revit2023.App</FullClassName>
    <VendorId>SUM</VendorId>
    <VendorDescription>LIRA to Revit slab additional rebar zones</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    [IO.File]::WriteAllText((Join-Path $addinDir 'LiraSlabZones.addin'), $xml, [Text.UTF8Encoding]::new($false))
    Write-Host "OK Revit ${version}: $deploy"
}
