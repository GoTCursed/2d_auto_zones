param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$BuildDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $root "src\LiraSlabZones.PreviewHost\bin\x64\$Configuration\net48"
} else {
    (Resolve-Path $BuildDirectory).Path
}
Add-Type -Path (Join-Path $build 'Newtonsoft.Json.dll')
Add-Type -Path (Join-Path $build 'Clipper2Lib.dll')
Add-Type -Path (Join-Path $build 'LiraSlabZones.Core.dll')

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function New-QuadPlate([int]$id, [double]$minX, [double]$maxX, [double]$minY, [double]$maxY,
    [double]$as1, [double]$z = 0) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(($minX + $maxX) / 2, ($minY + $maxY) / 2, $z)
    $plate.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $minY, $z))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $minY, $z))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $maxY, $z))
    $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $maxY, $z))
    $plate.Rebar = [LiraSlabZones.Core.PlateReinforcement]::new()
    $plate.Rebar.Ok = $true
    $plate.Rebar.As1 = $as1
    return $plate
}

function New-TestZone([double]$minX, [double]$maxX, [double]$minY, [double]$maxY,
    [LiraSlabZones.Core.ZoneDirection]$direction, [int]$diameter = 16, [int]$step = 200) {
    $zone = [LiraSlabZones.Core.AdditionalZone]::new()
    $zone.Layer = [LiraSlabZones.Core.RebarLayer]::As1
    $zone.Direction = $direction
    $zone.DiameterMm = $diameter
    $zone.BarStepMm = $step
    $zone.AsCoveredCm2PerM = [LiraSlabZones.Core.BarCapacity]::AsCm2PerM($diameter, $step)
    $zone.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::Straight
    $zone.FamilyFileName = 'SUM-30-test'
    $zone.LevelZM = 0
    $zone.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $minY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($maxX, $maxY, 0))
    $zone.Contour.Add([LiraSlabZones.Core.Point3]::new($minX, $maxY, 0))
    $zone.LengthM = if ($direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
        $maxX - $minX
    } else { $maxY - $minY }
    $zone.WidthM = if ($direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
        $maxY - $minY
    } else { $maxX - $minX }
    $zone.LengthMm = [Math]::Round($zone.LengthM * 1000)
    $zone.WidthMm = $zone.WidthM * 1000
    $zone.BarCount = [Math]::Max(2, [int][Math]::Round($zone.WidthMm / $step) + 1)
    $zone.Placement = [LiraSlabZones.Core.Point3]::new(
        ($minX + $maxX) / 2, ($minY + $maxY) / 2, 0)
    return $zone
}

$plates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$plates.Add((New-QuadPlate 10 0 1 0 1 5))
$plates.Add((New-QuadPlate 11 1 2 0 1 5))
$plates.Add((New-QuadPlate 12 50 51 50 51 99 2))
$settings = [LiraSlabZones.Core.AnalysisSettings]::new()
$settings.ShowAs1 = $true
$settings.ShowAs2 = $false
$settings.ShowAs3 = $false
$settings.ShowAs4 = $false
$settings.GridCellMm = 500
$settings.DetailSlider = 0
$settings.MinActiveElements = 0

$patches = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($plates, $settings, 0)
Assert ($patches.Count -eq 1) "Expected one connected patch on the selected elevation; got $($patches.Count)."
$patch = $patches[0]
Assert ($patch.Layer -eq [LiraSlabZones.Core.RebarLayer]::As1) 'Patch layer was not preserved.'
Assert ($patch.Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) 'Default As1 direction should be X.'
Assert ($patch.ElementIds.Count -eq 2) 'Element count must use distinct FE IDs, not the number of intersecting cells.'
Assert ($patch.Cells.Count -gt $patch.ElementIds.Count) 'Test geometry did not exercise multi-cell FE mapping.'
Assert ([Math]::Abs($patch.MinXM) -lt 1e-9 -and [Math]::Abs($patch.MaxXM - 2) -lt 1e-9) `
    'Patch bounds must follow the actual contours of its finite elements.'

$directionalPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$directionalPlates.Add((New-QuadPlate 19 0.25 0.26 0.25 0.26 0))
$directionalPlates.Add((New-QuadPlate 20 1 1.5 1 1.5 5))
$directionalPlates.Add((New-QuadPlate 21 0.5 1 1 2 1))
$directionalPlates.Add((New-QuadPlate 22 1 2 2 2.5 1))
$directionalPlates.Add((New-QuadPlate 23 2 2.5 1 2 1))
$directionalPlates.Add((New-QuadPlate 24 1 2 0.5 1 1))
$directionalPlates.Add((New-QuadPlate 25 1.5 2 1 1.5 5))
$directionalPlates.Add((New-QuadPlate 26 1 1.5 1.5 2 5))
$directionalPlates.Add((New-QuadPlate 27 1.5 2 1.5 2 5))
$directionalSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$directionalSettings.ShowAs1 = $true
$directionalSettings.ShowAs2 = $false
$directionalSettings.ShowAs3 = $false
$directionalSettings.ShowAs4 = $false
$directionalSettings.GridCellMm = 500
$directionalSettings.DetailSlider = 0
$directionalSettings.MinActiveElements = 0

$xPatches = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($directionalPlates, $directionalSettings, 0)
$xPatch = $xPatches | Where-Object { $_.ElementIds.Contains(20) } | Select-Object -First 1
Assert ($null -ne $xPatch) 'X-direction detail test did not produce a patch at its peak.'
Assert ($xPatch.ElementIds.Contains(22) -and $xPatch.ElementIds.Contains(24) -and
        $xPatch.ElementIds.Contains(25) -and $xPatch.ElementIds.Contains(26) -and
        $xPatch.ElementIds.Contains(27)) `
    'Positive cells transverse to X must extend the patch width.'
Assert (-not $xPatch.ElementIds.Contains(21) -and -not $xPatch.ElementIds.Contains(23)) `
    'At Max detail, low-As cells along X must split the patch horizontally.'

$directionalSettings.ReverseZoneDirections = $true
$yPatches = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($directionalPlates, $directionalSettings, 0)
$yPatch = $yPatches | Where-Object { $_.ElementIds.Contains(20) } | Select-Object -First 1
Assert ($null -ne $yPatch) 'Y-direction detail test did not produce a patch at its peak.'
Assert ($yPatch.ElementIds.Contains(21) -and $yPatch.ElementIds.Contains(23) -and
        $yPatch.ElementIds.Contains(25) -and $yPatch.ElementIds.Contains(26) -and
        $yPatch.ElementIds.Contains(27)) `
    'Positive cells transverse to Y must extend the patch width.'
Assert (-not $yPatch.ElementIds.Contains(22) -and -not $yPatch.ElementIds.Contains(24)) `
    'At Max detail, low-As cells along Y must split the patch vertically.'

$directionalSettings.ReverseZoneDirections = $false
$directionalSettings.DetailSlider = 1
$coarsePatches = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($directionalPlates, $directionalSettings, 0)
Assert ($coarsePatches.Count -eq 1) 'Min detail must merge the connected positive footprint with the near-zero threshold.'
Assert ($xPatches.Count -gt $coarsePatches.Count) `
    'Detail levels must change the patch count instead of keeping every positive cell in one patch.'
Assert (($coarsePatches[0].ElementIds | Sort-Object) -join ',' -eq '20,21,22,23,24,25,26,27') `
    'Min detail did not preserve all positive finite elements in the connected patch.'

$thresholdBridgePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$thresholdValues = @(10, 10, 6, 6, 6, 10, 10)
for ($i = 0; $i -lt $thresholdValues.Count; $i++) {
    $thresholdBridgePlates.Add((New-QuadPlate (40 + $i) ($i * 0.4) (($i + 1) * 0.4) 0 0.4 `
        $thresholdValues[$i]))
}
$thresholdSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$thresholdSettings.ShowAs1 = $true
$thresholdSettings.ShowAs2 = $false
$thresholdSettings.ShowAs3 = $false
$thresholdSettings.ShowAs4 = $false
$thresholdSettings.GridCellMm = 400
$thresholdSettings.MinActiveElements = 0
$thresholdSettings.DetailSlider = 0.5
$detailThree = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($thresholdBridgePlates, $thresholdSettings, 0)
$highPatchesAtThree = @($detailThree | Where-Object { $_.PeakAsAdditionalCm2PerM -ge 9.9 })
Assert ($highPatchesAtThree.Count -eq 2) `
    'At detail 3, two peak patches must remain separate when the intermediate bridge is below threshold.'
$thresholdSettings.DetailSlider = 0.75
$detailTwo = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($thresholdBridgePlates, $thresholdSettings, 0)
Assert (@($detailTwo | Where-Object { $_.PeakAsAdditionalCm2PerM -ge 9.9 }).Count -eq 1) `
    'At detail 2, the bridge above its 55% threshold must connect the two peaks.'
$thresholdSettings.DetailSlider = 1
$detailMin = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($thresholdBridgePlates, $thresholdSettings, 0)
Assert (@($detailMin | Where-Object { $_.PeakAsAdditionalCm2PerM -ge 9.9 }).Count -eq 1) `
    'At Min detail, positive bridge cells must connect the two peak regions.'

function New-FramePatch([int]$id, [LiraSlabZones.Core.ZoneDirection]$direction,
    [double]$minX, [double]$maxX, [double]$minY, [double]$maxY,
    [LiraSlabZones.Core.RebarLayer]$layer = [LiraSlabZones.Core.RebarLayer]::As1) {
    $patch = [LiraSlabZones.Core.ZonePatch]::new()
    $patch.PatchId = $id
    $patch.Layer = $layer
    $patch.Direction = $direction
    $patch.MinXM = $minX
    $patch.MaxXM = $maxX
    $patch.MinYM = $minY
    $patch.MaxYM = $maxY
    $patch.Cells = [Collections.Generic.List[LiraSlabZones.Core.ZonePatchCell]]::new()
    $cell = [LiraSlabZones.Core.ZonePatchCell]::new()
    $cell.MinXM = $minX
    $cell.MaxXM = $maxX
    $cell.MinYM = $minY
    $cell.MaxYM = $maxY
    $patch.Cells.Add($cell)
    return $patch
}

function New-FrameElements($patches, [double]$asAdditional = 1) {
    $elements = [Collections.Generic.List[LiraSlabZones.Core.ZonePatchFrameElement]]::new()
    foreach ($patch in $patches) {
        $contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
        $contour.Add([LiraSlabZones.Core.Point3]::new($patch.MinXM, $patch.MinYM, 0))
        $contour.Add([LiraSlabZones.Core.Point3]::new($patch.MaxXM, $patch.MinYM, 0))
        $contour.Add([LiraSlabZones.Core.Point3]::new($patch.MaxXM, $patch.MaxYM, 0))
        $contour.Add([LiraSlabZones.Core.Point3]::new($patch.MinXM, $patch.MaxYM, 0))
        $element = [LiraSlabZones.Core.ZonePatchFrameElement]::new()
        $element.ElementId = $patch.PatchId
        $element.Layer = $patch.Layer
        $element.AsAdditionalCm2PerM = $asAdditional
        $element.Contour = $contour
        $elements.Add($element)
    }
    return ,$elements
}

$xFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$xFramePatches.Add((New-FramePatch 1 ([LiraSlabZones.Core.ZoneDirection]::X) 0.5 1.5 1 2))
$xFramePatches.Add((New-FramePatch 2 ([LiraSlabZones.Core.ZoneDirection]::X) 0.5 1.5 3 4))
$xFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $xFramePatches, 0, 2, 0, 5, 0.1, (New-FrameElements $xFramePatches))
Assert ($xFrames.Count -eq 2) 'X-oriented patches must produce horizontal frames.'
Assert ($xFrames[0].MinY -eq 1 -and $xFrames[0].MaxY -eq 2 -and
        $xFrames[1].MinY -eq 3 -and $xFrames[1].MaxY -eq 4) `
    'X frame sections must trim empty longitudinal edge bands to their active mosaic cells.'
Assert ($xFrames[0].MinX -eq 0.5 -and $xFrames[0].MaxX -eq 1.5 -and
        $xFrames[1].MinX -eq 0.5 -and $xFrames[1].MaxX -eq 1.5) `
    'X frame sections must preserve occupied transverse extents.'

$yFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$yFramePatches.Add((New-FramePatch 3 ([LiraSlabZones.Core.ZoneDirection]::Y) 1 2 0.5 1.5))
$yFramePatches.Add((New-FramePatch 4 ([LiraSlabZones.Core.ZoneDirection]::Y) 3 4 0.5 1.5))
$yFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $yFramePatches, 0, 5, 0, 2, 0.1, (New-FrameElements $yFramePatches))
Assert ($yFrames.Count -eq 2) 'Y-oriented patches must produce vertical frames.'
Assert ($yFrames[0].MinX -eq 1 -and $yFrames[0].MaxX -eq 2 -and
        $yFrames[1].MinX -eq 3 -and $yFrames[1].MaxX -eq 4) `
    "Y frame sections must trim empty longitudinal edge bands; got [$($yFrames[0].MinX),$($yFrames[0].MaxX)] and [$($yFrames[1].MinX),$($yFrames[1].MaxX)]."
Assert ($yFrames[0].MinY -eq 0.5 -and $yFrames[0].MaxY -eq 1.5 -and
        $yFrames[1].MinY -eq 0.5 -and $yFrames[1].MaxY -eq 1.5) `
    'Y frame sections must preserve occupied transverse extents.'

$edgeTrimPatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$edgeTopPatch = New-FramePatch 40 ([LiraSlabZones.Core.ZoneDirection]::X) 0 4 2 3
$edgeTopPatch.Cells[0].AsAdditionalCm2PerM = 5
$edgeBottomPatch = New-FramePatch 41 ([LiraSlabZones.Core.ZoneDirection]::X) 0 4 0 2
$edgeBottomPatch.Cells[0].AsAdditionalCm2PerM = 5
$edgeTopPatch.ElementIds.Add(40)
$edgeBottomPatch.ElementIds.Add(41)
$edgeTrimPatches.Add($edgeBottomPatch)
$edgeTrimPatches.Add($edgeTopPatch)
$edgeTrimElements = New-FrameElements $edgeTrimPatches
$edgeTrimElements[1].Contour[0].X = 2
$edgeTrimElements[1].Contour[3].X = 2
$edgeTrimmedFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $edgeTrimPatches, 0, 4, 0, 4, 0.1, $edgeTrimElements)
Assert ($edgeTrimmedFrames.Count -eq 2) 'The detailed patch must remain split into two horizontal bands.'
Assert ($edgeTrimmedFrames[1].MinX -eq 2 -and $edgeTrimmedFrames[1].MaxX -eq 4 -and
        $edgeTrimmedFrames[1].MinY -eq 2 -and $edgeTrimmedFrames[1].MaxY -eq 3) `
    'A detailed patch band must be cropped where its leading edge has no active KEs.'
Assert ($edgeTrimmedFrames[0].MinX -eq 0 -and $edgeTrimmedFrames[0].MaxX -eq 4) `
    'Trimming one band must not remove cells occupied in another detailed band.'

$mixedFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$mixedFramePatches.Add((New-FramePatch 5 ([LiraSlabZones.Core.ZoneDirection]::X) 0.5 1.5 0.5 1.5 `
    ([LiraSlabZones.Core.RebarLayer]::As1)))
$mixedFramePatches.Add((New-FramePatch 6 ([LiraSlabZones.Core.ZoneDirection]::X) 0.5 1.5 3.5 4.5 `
    ([LiraSlabZones.Core.RebarLayer]::As3)))
$mixedFramePatches.Add((New-FramePatch 7 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 1.5 0.5 1.5 `
    ([LiraSlabZones.Core.RebarLayer]::As2)))
$mixedFramePatches.Add((New-FramePatch 8 ([LiraSlabZones.Core.ZoneDirection]::Y) 3.5 4.5 0.5 1.5 `
    ([LiraSlabZones.Core.RebarLayer]::As4)))
$mixedFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $mixedFramePatches, 0, 5, 0, 5, 0.1, (New-FrameElements $mixedFramePatches))
Assert ($mixedFrames.Count -eq 3) 'Mixed X/Y layers must split along both axes and omit the empty corner frame.'

$tightYFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$tightYFramePatches.Add((New-FramePatch 9 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 1.5 0.5 1.5))
$tightYFramePatches.Add((New-FramePatch 10 ([LiraSlabZones.Core.ZoneDirection]::Y) 1.6 1.9 0.5 1.5))
$tightYFramePatches.Add((New-FramePatch 11 ([LiraSlabZones.Core.ZoneDirection]::Y) 2.5 3.5 0.5 1.5))
$tightYFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $tightYFramePatches, 0, 4, 0, 2, 0.1, (New-FrameElements $tightYFramePatches))
Assert ($tightYFrames.Count -ge 2) 'Y frame partitioning must preserve valid vertex-aligned divisions.'
Assert (@($tightYFrames | Where-Object { $_.MaxX - $_.MinX -lt 0.1 - 1e-9 }).Count -eq 0) `
    'No vertical Y frame section may be narrower than 100 mm.'

$overlappingYFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$overlappingYFramePatches.Add((New-FramePatch 12 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 1.5 0.5 1.5))
$overlappingYFramePatches.Add((New-FramePatch 13 ([LiraSlabZones.Core.ZoneDirection]::Y) 1.25 2.25 0.5 1.5))
$overlappingYFrameElements = New-FrameElements $overlappingYFramePatches
$overlappingYFrameElements[0].AsAdditionalCm2PerM = 2
$overlappingYFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $overlappingYFramePatches, 0, 3, 0, 2, 0.1, $overlappingYFrameElements)
Assert ($overlappingYFrames.Count -eq 2) `
    "Overlapping FE bounds may split when a finite-element vertex lies on the division line; got $($overlappingYFrames.Count) frames."
Assert (@(0.5, 1.25, 1.5, 2.25) -contains $overlappingYFrames[0].MaxX) `
    'An overlapping-frame split must snap to a finite-element vertex coordinate.'

$separatedCellPatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$separatedCellPatches.Add((New-FramePatch 30 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 4.2 0.5 1.5))
$separatedCellPatches.Add((New-FramePatch 31 ([LiraSlabZones.Core.ZoneDirection]::Y) 1.2 4.5 0.5 1.5))
$separatedCellPatches[0].Cells.Clear()
$separatedCellPatches[1].Cells.Clear()
foreach ($cellBounds in @(@(0.5, 1.5), @(3.0, 4.0))) {
    $cell = [LiraSlabZones.Core.ZonePatchCell]::new()
    $cell.MinXM = $cellBounds[0]
    $cell.MaxXM = $cellBounds[1]
    $cell.MinYM = 0.5
    $cell.MaxYM = 1.5
    if ($cellBounds[0] -lt 2) { $separatedCellPatches[0].Cells.Add($cell) }
    else { $separatedCellPatches[1].Cells.Add($cell) }
}
$separatedCellElements = New-FrameElements $separatedCellPatches
$separatedCellElements[0].Contour.Clear()
$separatedCellElements[1].Contour.Clear()
$separatedCellElements[0].Contour.Add([LiraSlabZones.Core.Point3]::new(0.5, 0.5, 0))
$separatedCellElements[0].Contour.Add([LiraSlabZones.Core.Point3]::new(1.5, 0.5, 0))
$separatedCellElements[0].Contour.Add([LiraSlabZones.Core.Point3]::new(1.5, 1.5, 0))
$separatedCellElements[0].Contour.Add([LiraSlabZones.Core.Point3]::new(0.5, 1.5, 0))
$separatedCellElements[1].Contour.Add([LiraSlabZones.Core.Point3]::new(3.0, 0.5, 0))
$separatedCellElements[1].Contour.Add([LiraSlabZones.Core.Point3]::new(4.0, 0.5, 0))
$separatedCellElements[1].Contour.Add([LiraSlabZones.Core.Point3]::new(4.0, 1.5, 0))
$separatedCellElements[1].Contour.Add([LiraSlabZones.Core.Point3]::new(3.0, 1.5, 0))
$separatedCellFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $separatedCellPatches, 0, 5, 0, 2, 0.1, $separatedCellElements)
Assert ($separatedCellFrames.Count -eq 2 -and
        [Math]::Abs($separatedCellFrames[0].MaxX - 1.5) -lt 1e-9) `
    'When FE bounding boxes overlap but mosaic cells are disconnected, split near the actual cell gap.'

$maxValuePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$maxValuePatches.Add((New-FramePatch 16 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 1.4 0.5 1.5))
$maxValuePatches.Add((New-FramePatch 17 ([LiraSlabZones.Core.ZoneDirection]::Y) 1.2 2.2 0.5 1.5))
$maxValueElement = [LiraSlabZones.Core.ZonePatchFrameElement]::new()
$maxValueElement.ElementId = 100
$maxValueElement.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$maxValueElement.AsAdditionalCm2PerM = 15
$maxValueElement.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$maxValueElement.Contour.Add([LiraSlabZones.Core.Point3]::new(1.0, 0.5, 0))
$maxValueElement.Contour.Add([LiraSlabZones.Core.Point3]::new(1.3, 1.0, 0))
$maxValueElement.Contour.Add([LiraSlabZones.Core.Point3]::new(1.6, 1.5, 0))
$maxElements = [Collections.Generic.List[LiraSlabZones.Core.ZonePatchFrameElement]]::new()
$maxElements.Add($maxValueElement)
$maxValueFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $maxValuePatches, 0, 3, 0, 2, 0.1, $maxElements)
Assert ($maxValueFrames.Count -eq 2 -and [Math]::Abs($maxValueFrames[0].MaxX - 1.6) -lt 1e-9) `
    'A candidate crossing the maximum-As element must be rejected in favor of its outer vertex.'

$touchingYFramePatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$touchingYFramePatches.Add((New-FramePatch 14 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.5 1.5 0.5 1.5))
$touchingYFramePatches.Add((New-FramePatch 15 ([LiraSlabZones.Core.ZoneDirection]::Y) 1.5 2.5 0.5 1.5))
$touchingYFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $touchingYFramePatches, 0, 3, 0, 2, 0.1, (New-FrameElements $touchingYFramePatches))
Assert ($touchingYFrames.Count -eq 2) `
    'Detail-separated patches touching at a shared FE edge must remain separate frames.'
Assert ([Math]::Abs($touchingYFrames[0].MaxX - $touchingYFrames[1].MinX) -lt 1e-9) `
    'Touching Y frames must split on the shared FE boundary.'

$hundredMmPatches = [Collections.Generic.List[LiraSlabZones.Core.ZonePatch]]::new()
$hundredMmPatches.Add((New-FramePatch 18 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.02 0.12 0.2 0.3))
$hundredMmPatches.Add((New-FramePatch 19 ([LiraSlabZones.Core.ZoneDirection]::Y) 0.13 0.23 0.2 0.3))
$hundredMmFrames = [LiraSlabZones.Core.ZonePatchFramePartitioner]::Split(
    $hundredMmPatches, 0, 1, 0, 1, 0.1, (New-FrameElements $hundredMmPatches))
Assert ($hundredMmFrames.Count -eq 2 -and
        $hundredMmFrames[0].MaxX - $hundredMmFrames[0].MinX -ge 0.1 - 1e-9) `
    'A 100 mm split must be allowed even when it is narrower than one mosaic cell.'

$as1Normal = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As1, $false)
$as3Normal = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As3, $false)
$as2Normal = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As2, $false)
$as4Normal = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As4, $false)
Assert ($as1Normal -eq [LiraSlabZones.Core.ZoneDirection]::X -and
        $as3Normal -eq [LiraSlabZones.Core.ZoneDirection]::X -and
        $as2Normal -eq [LiraSlabZones.Core.ZoneDirection]::Y -and
        $as4Normal -eq [LiraSlabZones.Core.ZoneDirection]::Y) `
    'Without reverse priority, As1/As3 must split along X and As2/As4 along Y.'
$as1Reverse = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As1, $true)
$as3Reverse = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As3, $true)
$as2Reverse = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As2, $true)
$as4Reverse = [LiraSlabZones.Core.RebarTables]::DirectionForLayer(
    [LiraSlabZones.Core.RebarLayer]::As4, $true)
Assert ($as1Reverse -eq [LiraSlabZones.Core.ZoneDirection]::Y -and
        $as3Reverse -eq [LiraSlabZones.Core.ZoneDirection]::Y -and
        $as2Reverse -eq [LiraSlabZones.Core.ZoneDirection]::X -and
        $as4Reverse -eq [LiraSlabZones.Core.ZoneDirection]::X) `
    'With reverse priority, As1/As3 must split along Y and As2/As4 along X.'

$settings.MinActiveElements = 3
$filtered = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($plates, $settings, 0)
Assert ($filtered.Count -eq 0) 'Minimum FE count must suppress a patch with only two unique elements.'

$settings.MinActiveElements = 0
$settings.ReverseZoneDirections = $true
$reversed = [LiraSlabZones.Core.ZonePatchAnalyzer]::Build($plates, $settings, 0)
Assert ($reversed[0].Direction -eq [LiraSlabZones.Core.ZoneDirection]::Y) 'Reverse direction was not applied to As1.'

$levelPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$levelPlates.Add($plates[0])
$levelPlates.Add($plates[1])
$settings.AutoLayout = $false
$settings.PlacementMode = 'ElementCenter'
$result = [LiraSlabZones.Core.SlabZoneAnalyzer]::BuildResult(
    'patch-test', '', 0, $levelPlates, $settings, $null, 0, 'Z=0', $true)
Assert ($result.PatchPreviewOnly) 'Legacy ElementCenter settings must still use diagnostic patch mode.'
Assert ($result.Zones.Count -eq 0) 'Patch mode must not run legacy zone installation or post-processing.'
Assert ($result.Patches.Count -gt 0) 'BuildResult did not forward generated patches.'
$roundTrip = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
    [Newtonsoft.Json.JsonConvert]::SerializeObject($result), [LiraSlabZones.Core.AnalysisResult])
Assert ($roundTrip.PatchPreviewOnly -and $roundTrip.Patches.Count -eq $result.Patches.Count) `
    'Patch preview state was not preserved in result JSON.'

$diameterAt200 = [LiraSlabZones.Core.BarCapacity]::MinDiameterForAs(6.0, 200, 36, 8)
$diameterAt100 = [LiraSlabZones.Core.BarCapacity]::MinDiameterForAs(6.0, 100, 36, 8)
Assert ($diameterAt200 -eq 16 -and $diameterAt100 -eq 10) `
    'Minimum bar diameters for 200/100 mm steps were not selected from the capacity table.'
$diameterWithExclusion = [LiraSlabZones.Core.BarCapacity]::MinDiameterForAs(6.0, 200, 36, 8, [int[]]@(16))
Assert ($diameterWithExclusion -eq 20) 'Excluded diameters must be skipped during frame demand sizing.'
$maxDiameter = [LiraSlabZones.Core.BarCapacity]::MinDiameterForAs(100.0, 200, 36, 8)
Assert ($maxDiameter -eq 36 -and [LiraSlabZones.Core.BarCapacity]::AsCm2PerM($maxDiameter, 200) -lt 100.0) `
    'A demand above the available table capacity must remain detectable as insufficient.'

$framePatch = [LiraSlabZones.Core.ZonePatch]::new()
$framePatch.PatchId = 700
$framePatch.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$framePatch.MinXM = 0
$framePatch.MaxXM = 2
$framePatch.MinYM = 0
$framePatch.MaxYM = 0.8
$framePatch.ElementIds.Add(700)
$insideCell = [LiraSlabZones.Core.ZonePatchCell]::new()
$insideCell.MinXM = 0
$insideCell.MaxXM = 1
$insideCell.MinYM = 0
$insideCell.MaxYM = 0.8
$insideCell.AsAdditionalCm2PerM = 15
$outsideCell = [LiraSlabZones.Core.ZonePatchCell]::new()
$outsideCell.MinXM = 1.2
$outsideCell.MaxXM = 2
$outsideCell.MinYM = 0
$outsideCell.MaxYM = 0.8
$outsideCell.AsAdditionalCm2PerM = 40
$framePatch.Cells.Add($insideCell)
$framePatch.Cells.Add($outsideCell)
$frameSelection = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
$frameSelection.MinXM = 0
$frameSelection.MaxXM = 1
$frameSelection.MinYM = 0
$frameSelection.MaxYM = 0.8
$frameSelection.Patches.Add($framePatch)
$settings.BgBottomDiameterMm = 12
$settings.BgBottomStepMm = 200
$settings.ConcreteClass = 'B25'
$settings.SyncBackgroundAsFromBars()
$settings.ExcludedZoneDiametersMm.Clear()
$settings.ReverseZoneDirections = $false
$frameZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ($frameZones.Count -eq 1) 'One candidate zone should be built for the active layer inside a frame.'
Assert ($frameZones[0].DiameterMm -eq 20 -and $frameZones[0].BarStepMm -eq 200) `
    'Frame sizing must choose 20 mm at 200 mm spacing when it meets demand with less As than 100 mm spacing.'
Assert ($frameZones[0].DiameterMm -ge $settings.BgBottomDiameterMm) `
    'The selected zone diameter must not be smaller than the background diameter.'
Assert ([Math]::Abs($frameZones[0].AsAdditional - 15) -lt 0.001) `
    'Frame demand must use only mosaic cells inside the selected frame.'
Assert ($frameZones[0].LengthMm -eq 2900 -and
    [Math]::Abs($frameZones[0].WidthMm - 1000) -lt 0.001) `
    'The straight bar must round the anchored length up to the nearest standard while preserving the selected frame width.'
Assert ($frameZones[0].Direction -eq [LiraSlabZones.Core.ZoneDirection]::Y -and
    [Math]::Abs($frameZones[0].Contour[0].Y - (-1.05)) -lt 1e-9 -and
    [Math]::Abs($frameZones[0].Contour[2].Y - 1.85) -lt 1e-9) `
    'The Y-oriented straight bar must be extended symmetrically to its standard length around the anchored segment.'
$settings.MinZoneWidthM = 1.2
$minimumWidthZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ([Math]::Abs($minimumWidthZones[0].WidthMm - 1200) -lt 0.001 -and
        [Math]::Abs($minimumWidthZones[0].Contour[0].X - (-0.1)) -lt 1e-9 -and
        [Math]::Abs($minimumWidthZones[0].Contour[1].X - 1.1) -lt 1e-9) `
    'The configured minimum zone width must expand the generated zone symmetrically without changing the along-bar geometry.'
$insideCell.MaxXM = 0.91
$settings.MinZoneWidthM = 0.95
$preNormalizationZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ([Math]::Abs($preNormalizationZones[0].WidthMm - 950) -lt 0.001 -and
        [Math]::Abs($preNormalizationZones[0].Contour[0].X - (-0.02)) -lt 1e-9 -and
        [Math]::Abs($preNormalizationZones[0].Contour[1].X - 0.93) -lt 1e-9) `
    'Initial zone width must preserve a non-step minimum width instead of rounding it up to the bar spacing.'
$insideCell.MaxXM = 1
$settings.MinZoneWidthM = 2.6
$cellOverrunZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ([Math]::Abs($cellOverrunZones[0].WidthMm - 2600) -lt 0.001 -and
        [Math]::Abs($cellOverrunZones[0].WidthMm / $cellOverrunZones[0].BarStepMm -
                    [Math]::Round($cellOverrunZones[0].WidthMm / $cellOverrunZones[0].BarStepMm)) -lt 1e-9) `
    'A minimum width that happens to be step-aligned must remain unchanged and may extend beyond the patch.'
$settings.MinZoneWidthM = 3.2
$unboundedMinimumZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ([Math]::Abs($unboundedMinimumZones[0].WidthMm - 3200) -lt 0.001 -and
        [Math]::Abs($unboundedMinimumZones[0].WidthMm / $unboundedMinimumZones[0].BarStepMm -
                    [Math]::Round($unboundedMinimumZones[0].WidthMm / $unboundedMinimumZones[0].BarStepMm)) -lt 1e-9 -and
        ($unboundedMinimumZones[0].Contour[0].X -lt -0.5 - 1e-9 -or
         $unboundedMinimumZones[0].Contour[1].X -gt 2.5 + 1e-9)) `
    'A minimum width larger than the patch must be preserved, rounded to a step, and allowed to extend beyond the patch.'
$settings.MinZoneWidthM = 0

$peakPatch = [LiraSlabZones.Core.ZonePatch]::new()
$peakPatch.Layer = [LiraSlabZones.Core.RebarLayer]::As2
$peakPatch.MinXM = 0
$peakPatch.MaxXM = 1.2
$peakPatch.MinYM = 0
$peakPatch.MaxYM = 1.2
$peakSelection = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
$peakSelection.MinXM = 0
$peakSelection.MaxXM = 1.2
$peakSelection.MinYM = 0
$peakSelection.MaxYM = 1.2
for ($iy = 0; $iy -lt 3; $iy++) {
    for ($ix = 0; $ix -lt 3; $ix++) {
        $cell = [LiraSlabZones.Core.ZonePatchCell]::new()
        $cell.Ix = $ix
        $cell.Iy = $iy
        $cell.MinXM = $ix * 0.4
        $cell.MaxXM = ($ix + 1) * 0.4
        $cell.MinYM = $iy * 0.4
        $cell.MaxYM = ($iy + 1) * 0.4
        $cell.AsAdditionalCm2PerM = if ($ix -eq 1 -and $iy -eq 1) { 30 } else { 4 }
        $cell.RawAsAdditionalCm2PerM = $cell.AsAdditionalCm2PerM
        $peakPatch.Cells.Add($cell)
    }
}
$peakSelection.Patches.Add($peakPatch)
$settings.AveragePatchPeaks = $false
$rawPeakZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($peakSelection, 4.5, $settings)
$settings.AveragePatchPeaks = $true
$averagedPeakZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($peakSelection, 4.5, $settings)
Assert ($rawPeakZones.Count -eq 1 -and $rawPeakZones[0].AsAdditional -eq 30 -and
        $averagedPeakZones.Count -eq 1 -and $averagedPeakZones[0].AsAdditional -lt 30 -and
        $averagedPeakZones[0].AsAdditional -gt 4) `
    'Peak averaging must reduce a local outlier for bar sizing, while the unchecked mode uses the raw peak.'
$settings.AveragePatchPeaks = $false

$widthPriorityZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$widthPriorityBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$strongWidthZone = New-TestZone 0 4 0 0.45 ([LiraSlabZones.Core.ZoneDirection]::X)
$strongWidthZone.AsAdditional = 10
$weakWidthZone = New-TestZone 0 4 0.45 0.9 ([LiraSlabZones.Core.ZoneDirection]::X)
$weakWidthZone.AsAdditional = 5
$widthPriorityZones.Add($weakWidthZone)
$widthPriorityZones.Add($strongWidthZone)
$widthPriorityBounds.Add($weakWidthZone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.45, 0.9))
$widthPriorityBounds.Add($strongWidthZone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.45))
[LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $widthPriorityZones, $widthPriorityBounds, 0) | Out-Null
Assert ($widthPriorityZones.Count -eq 2 -and
        [Math]::Abs($strongWidthZone.WidthMm - 600) -lt 0.001 -and
        [Math]::Abs($weakWidthZone.WidthMm - 400) -lt 0.001 -and
        [Math]::Abs($strongWidthZone.Contour[2].Y - $weakWidthZone.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($weakWidthZone.WidthMm / $weakWidthZone.BarStepMm -
                    [Math]::Round($weakWidthZone.WidthMm / $weakWidthZone.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($weakWidthZone.Contour[2].Y - 0.9) -le 0.2 + 1e-9) `
    'The highest peak must keep priority; the adjacent zone should align without overlap and remain step-sized.'

$minimumWidthPriorityZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$minimumWidthPriorityBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$strongMinimumZone = New-TestZone 0 4 0 0.45 ([LiraSlabZones.Core.ZoneDirection]::X)
$strongMinimumZone.AsAdditional = 10
$weakMinimumZone = New-TestZone 0 4 0.45 0.9 ([LiraSlabZones.Core.ZoneDirection]::X)
$weakMinimumZone.AsAdditional = 5
$minimumWidthPriorityZones.Add($weakMinimumZone)
$minimumWidthPriorityZones.Add($strongMinimumZone)
$minimumWidthPriorityBounds.Add($weakMinimumZone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.45, 0.9))
$minimumWidthPriorityBounds.Add($strongMinimumZone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.45))
$absorbedCount = [LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $minimumWidthPriorityZones, $minimumWidthPriorityBounds, 0.6)
Assert ($absorbedCount -eq 1 -and $minimumWidthPriorityZones.Count -eq 1 -and
        [Math]::Abs($strongMinimumZone.WidthMm - 1000) -lt 0.001 -and
        [Math]::Abs($strongMinimumZone.Contour[2].Y - 0.9) -le 0.2 + 1e-9) `
    'When the remaining neighbor strip is narrower than the configured minimum, remove it and expand the stronger zone toward its patch.'

$roundUp100Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$roundUp100Bounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$roundUp100Zone = New-TestZone 0 4 0 0.821 ([LiraSlabZones.Core.ZoneDirection]::X) 16 100
$roundUp100Zones.Add($roundUp100Zone)
$roundUp100Bounds.Add($roundUp100Zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.821))
[LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $roundUp100Zones, $roundUp100Bounds, 0) | Out-Null
Assert ([Math]::Abs($roundUp100Zone.WidthMm - 900) -lt 0.001) `
    'Unconstrained width normalization at 100 mm spacing must round 821 mm upward to 900 mm.'

$roundUp200Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$roundUp200Bounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$roundUp200Zone = New-TestZone 0 4 0 0.821 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$roundUp200Zones.Add($roundUp200Zone)
$roundUp200Bounds.Add($roundUp200Zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.821))
[LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $roundUp200Zones, $roundUp200Bounds, 0) | Out-Null
Assert ([Math]::Abs($roundUp200Zone.WidthMm - 1000) -lt 0.001) `
    'Unconstrained width normalization at 200 mm spacing must round 821 mm upward to 1000 mm.'

$partitionWidthZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$partitionWidthBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$partitionOuterBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$partitionWidthZone = New-TestZone 0 4 0.2 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$partitionWidthZone.AsAdditional = 5
$partitionWidthZones.Add($partitionWidthZone)
$partitionWidthBounds.Add($partitionWidthZone,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.2, 0.7))
$partitionOuterBounds.Add($partitionWidthZone,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 2))
[LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $partitionWidthZones, $partitionWidthBounds, 1.0, 0.2, $partitionOuterBounds) | Out-Null
Assert ([Math]::Abs($partitionWidthZone.WidthMm - 1000) -lt 0.001 -and
        $partitionWidthZone.Contour[0].Y -ge -0.2 - 1e-9 -and
        $partitionWidthZone.Contour[2].Y -le 2.2 + 1e-9) `
    'A detail partition must use the original patch boundary for the 200 mm overrun and minimum-width fit.'

$partialCoverageZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$partialCoverageBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$partialBlocker = New-TestZone 1 3 0 1 ([LiraSlabZones.Core.ZoneDirection]::X) 20 100
$partialBlocker.AsAdditional = 10
$partialCandidate = New-TestZone 0 4 0.4 0.8 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$partialCandidate.AsAdditional = 5
$partialCoverageZones.Add($partialCandidate)
$partialCoverageZones.Add($partialBlocker)
$partialCoverageBounds.Add($partialCandidate,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.4, 0.8))
$partialCoverageBounds.Add($partialBlocker,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(1, 3, 0, 1))
[LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $partialCoverageZones, $partialCoverageBounds, 0) | Out-Null
Assert ($partialCoverageZones.Count -eq 2 -and
        $partialCandidate.Comment.Contains('покрытие КЭ') -and
        $partialCandidate.Contour[0].X -le 0 + 1e-9 -and
        $partialCandidate.Contour[1].X -ge 4 - 1e-9) `
    'A blocker covering only part of a zone length must not cause the zone and its uncovered FE strip to disappear.'

$constrainedZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$constrainedBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$lowerBlocker = New-TestZone 25.938441 28.278441 15.429090 16.829090 `
    ([LiraSlabZones.Core.ZoneDirection]::X) 22 200
$lowerBlocker.AsAdditional = 15.83
$upperBlocker = New-TestZone 25.459091 28.359091 17.920833 19.320833 `
    ([LiraSlabZones.Core.ZoneDirection]::X) 25 200
$upperBlocker.AsAdditional = 21.02
$constrainedZone = New-TestZone 25.459091 28.359091 16.671000 18.071000 `
    ([LiraSlabZones.Core.ZoneDirection]::X) 20 200
$constrainedZone.AsAdditional = 15.52
$constrainedZones.Add($constrainedZone)
$constrainedZones.Add($lowerBlocker)
$constrainedZones.Add($upperBlocker)
$constrainedBounds.Add($lowerBlocker,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(25.938441, 28.278441, 15.429090, 16.829090))
$constrainedBounds.Add($upperBlocker,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(25.459091, 28.359091, 17.920833, 19.320833))
$constrainedBounds.Add($constrainedZone,
    [LiraSlabZones.Core.ZonePatchFrameBounds]::new(26.368182, 27.450000, 16.767000, 17.975000))
$constrainedRemoved = [LiraSlabZones.Core.ZonePatchZoneBuilder]::NormalizeWidthsAndResolveOverlaps(
    $constrainedZones, $constrainedBounds, 0)
Assert ($constrainedRemoved -eq 0 -and $constrainedZones.Count -eq 3 -and
        [Math]::Abs($constrainedZone.Contour[0].Y - 16.829090) -lt 0.001 -and
        [Math]::Abs($constrainedZone.Contour[2].Y - 17.829090) -lt 0.001 -and
        [Math]::Abs($constrainedZone.WidthMm - 1000) -lt 0.001 -and
        [Math]::Abs($lowerBlocker.WidthMm - 1400) -lt 0.001 -and
        [Math]::Abs($upperBlocker.Contour[0].Y - 17.829090) -lt 0.001 -and
        [Math]::Abs($upperBlocker.Contour[2].Y - 19.429090) -lt 0.001 -and
        [Math]::Abs($upperBlocker.WidthMm - 1600) -lt 0.001 -and
        [Math]::Abs($lowerBlocker.Contour[2].Y - $constrainedZone.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($constrainedZone.Contour[2].Y - $upperBlocker.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($lowerBlocker.WidthMm / $lowerBlocker.BarStepMm -
                    [Math]::Round($lowerBlocker.WidthMm / $lowerBlocker.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($constrainedZone.WidthMm / $constrainedZone.BarStepMm -
                    [Math]::Round($constrainedZone.WidthMm / $constrainedZone.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($upperBlocker.WidthMm / $upperBlocker.BarStepMm -
                    [Math]::Round($upperBlocker.WidthMm / $upperBlocker.BarStepMm)) -lt 1e-9 -and
        $constrainedZone.BarCount -eq 6 -and
        $upperBlocker.Contour[2].Y -le 19.320833 + 0.2 + 1e-9) `
    'When the rounded width cannot fit between stronger zones, round the remaining zone down to its step and transfer the sub-step remainder to a neighbor without gaps, overlaps, or exceeding the 200 mm patch overrun.'

$edgeZonePatch = New-FramePatch 702 ([LiraSlabZones.Core.ZoneDirection]::X) 0 4 0 1
$edgeZonePatch.Layer = [LiraSlabZones.Core.RebarLayer]::As1
$edgeZonePatch.Cells[0].AsAdditionalCm2PerM = 5
$edgeZonePatch.ElementIds.Add(702)
$edgeZoneFrame = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
$edgeZoneFrame.MinXM = 0
$edgeZoneFrame.MaxXM = 4
$edgeZoneFrame.MinYM = 0
$edgeZoneFrame.MaxYM = 1
$edgeZoneFrame.Patches.Add($edgeZonePatch)
$edgeZoneElement = [LiraSlabZones.Core.ZonePatchFrameElement]::new()
$edgeZoneElement.ElementId = 702
$edgeZoneElement.Layer = [LiraSlabZones.Core.RebarLayer]::As1
$edgeZoneElement.AsAdditionalCm2PerM = 5
$edgeZoneElement.Contour = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$edgeZoneElement.Contour.Add([LiraSlabZones.Core.Point3]::new(2, 0, 0))
$edgeZoneElement.Contour.Add([LiraSlabZones.Core.Point3]::new(4, 0, 0))
$edgeZoneElement.Contour.Add([LiraSlabZones.Core.Point3]::new(4, 1, 0))
$edgeZoneElement.Contour.Add([LiraSlabZones.Core.Point3]::new(2, 1, 0))
$edgeZoneFrame.Elements.Add($edgeZoneElement)
$edgeTrimmedZone = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($edgeZoneFrame, 4.5, $settings)
Assert ($edgeTrimmedZone.Count -eq 1 -and
        $edgeTrimmedZone[0].LengthMm -eq 3900 -and
        [Math]::Abs($edgeTrimmedZone[0].Contour[0].X - 1.05) -lt 1e-9 -and
        [Math]::Abs($edgeTrimmedZone[0].Contour[1].X - 4.95) -lt 1e-9) `
    'The reinforcement zone must use its active-cell bounds, add anchorage, then round up around the anchored segment center.'
$unclippedZone = New-TestZone 0.1 0.8 0.1 0.5 ([LiraSlabZones.Core.ZoneDirection]::X)
$slabOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$slabOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$slabOutline.Add([LiraSlabZones.Core.Point3]::new(1, 0, 0))
$slabOutline.Add([LiraSlabZones.Core.Point3]::new(1, 1, 0))
$slabOutline.Add([LiraSlabZones.Core.Point3]::new(0, 1, 0))
Assert ([LiraSlabZones.Core.ZoneEditor]::Move($unclippedZone, 0.5, 0, $slabOutline, $false) -and
        [Math]::Abs((($unclippedZone.Contour | Measure-Object -Property X -Maximum).Maximum) - 1.3) -lt 1e-6) `
    'Patch-preview edits must retain zone geometry outside the slab outline.'
$differentDiameterZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$differentDiameterZones.Add((New-TestZone 0 2.9 0 0.8 ([LiraSlabZones.Core.ZoneDirection]::X) 16))
$differentDiameterZones.Add((New-TestZone 0 2.9 0.8 1.6 ([LiraSlabZones.Core.ZoneDirection]::X) 20))
Assert ([LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeAdjacentCompatibleZones($differentDiameterZones) -eq 0 -and
        $differentDiameterZones.Count -eq 2) `
    'Zones with different diameters must remain separate.'
$endToEndZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$endToEndZones.Add((New-TestZone 0 2.9 0 0.8 ([LiraSlabZones.Core.ZoneDirection]::X)))
$endToEndZones.Add((New-TestZone 2.9 5.8 0 0.8 ([LiraSlabZones.Core.ZoneDirection]::X)))
Assert ([LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeAdjacentCompatibleZones($endToEndZones) -eq 0 -and
        $endToEndZones.Count -eq 2) `
    'End-to-end segments must not merge because that would extend a straight bar.'
$sideBySideZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$sideBySideFirst = New-TestZone 0 2.9 0 0.8 ([LiraSlabZones.Core.ZoneDirection]::X) 22 200
$sideBySideSecond = New-TestZone 0 2.9 0.8 1.6 ([LiraSlabZones.Core.ZoneDirection]::X) 22 200
$sideBySideFirst.LengthMm = 2900
$sideBySideSecond.LengthMm = 2900
$sideBySideZones.Add($sideBySideFirst)
$sideBySideZones.Add($sideBySideSecond)
$compatibleMergeCount = [LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeAdjacentCompatibleZones($sideBySideZones)
Assert ($compatibleMergeCount -eq 1 -and $sideBySideZones.Count -eq 1 -and
        $sideBySideZones[0].LengthMm -eq 2900 -and $sideBySideZones[0].DiameterMm -eq 22 -and
        $sideBySideZones[0].BarStepMm -eq 200 -and $sideBySideZones[0].BarCount -eq 9 -and
        [Math]::Abs($sideBySideZones[0].WidthMm - 1600) -lt 0.001) `
    'Touching zones with identical parameters must merge when the union preserves standard length, diameter, and step.'
$shiftableZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$shiftableBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$shiftableA = New-TestZone 0 1 -0.67 1.67 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$shiftableB = New-TestZone 0 1 -0.22 2.12 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$shiftableA.AsAdditional = 5
$shiftableA.AsRequired = 8
$shiftableB.AsAdditional = 7
$shiftableB.AsRequired = 10
$shiftableA.NodeIds.Add(701)
$shiftableB.NodeIds.Add(702)
$shiftableZones.Add($shiftableA)
$shiftableZones.Add($shiftableB)
$shiftableBounds.Add($shiftableA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 1, 0.5, 0.9))
$shiftableBounds.Add($shiftableB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 1, 0.95, 1.35))
$settings.ConcreteClass = 'B25'
$settings.GridCellMm = 400
$shiftableCount = [LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeShiftableAdjacentZonesAlongBars(
    $shiftableZones, $shiftableBounds, $settings)
$mergedShiftableContour = $shiftableZones[0].Contour
$mergedShiftableMinY = ($mergedShiftableContour | Measure-Object -Property Y -Minimum).Minimum
$mergedShiftableMaxY = ($mergedShiftableContour | Measure-Object -Property Y -Maximum).Maximum
$mergeAnchorMm = [LiraSlabZones.Core.RebarTables]::AnchorageLenMm('B25', 16)
Assert ($shiftableCount -eq 1 -and $shiftableZones.Count -eq 1 -and
        $shiftableZones[0].NodeIds.Count -eq 2 -and
        [Math]::Abs(($mergedShiftableMaxY - $mergedShiftableMinY) * 1000 - 2340) -lt 0.001 -and
        $mergedShiftableMinY * 1000 -le 500 - $mergeAnchorMm + 1 -and
        $mergedShiftableMaxY * 1000 -ge 1350 + $mergeAnchorMm - 1 -and
        [Math]::Abs($shiftableBounds[$shiftableZones[0]].MaxY - 1.35) -lt 1e-9) `
    'Adjacent same-width zones may merge by sliding a standard-length bar group only when both anchorage intervals remain covered.'
$tooLongZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$tooLongBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$tooLongA = New-TestZone 0 1 -0.67 1.67 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$tooLongB = New-TestZone 0 1 -0.12 2.22 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$tooLongZones.Add($tooLongA)
$tooLongZones.Add($tooLongB)
$tooLongBounds.Add($tooLongA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 1, 0.5, 1.0))
$tooLongBounds.Add($tooLongB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 1, 1.05, 1.55))
Assert ([LiraSlabZones.Core.ZonePatchZoneBuilder]::MergeShiftableAdjacentZonesAlongBars(
        $tooLongZones, $tooLongBounds, $settings) -eq 0 -and $tooLongZones.Count -eq 2) `
    'Zones must remain separate when one standard-length bar cannot retain both anchorage lengths.'
$emptyFrameSelection = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
$emptyFrameSelection.MinXM = 1.05
$emptyFrameSelection.MaxXM = 1.15
$emptyFrameSelection.MinYM = 0
$emptyFrameSelection.MaxYM = 0.8
$emptyFrameSelection.Patches.Add($framePatch)
$emptyFrameZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($emptyFrameSelection, 4.5, $settings)
Assert ($emptyFrameZones.Count -eq 0) 'A frame with no mosaic cells must not get a phantom zone from the parent patch peak.'
$settings.ReverseZoneDirections = $true
$reversedFrameZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ([Math]::Abs($reversedFrameZones[0].LengthMm -
        2900) -lt 0.001 -and
    [Math]::Abs($reversedFrameZones[0].WidthMm - 800) -lt 0.001) `
    'Candidate zone orientation must follow the existing reverse-direction setting.'
$settings.ReverseZoneDirections = $false
$insideCell.AsAdditionalCm2PerM = 3
$sameDiameterZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ($sameDiameterZones[0].DiameterMm -eq 12 -and $sameDiameterZones[0].BarStepMm -eq 200) `
    'When both steps meet demand at the background diameter, the lower-consumption 200 mm step must be preferred.'
$insideCell.AsAdditionalCm2PerM = 6
$lowerConsumptionZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ($lowerConsumptionZones[0].DiameterMm -eq 16 -and $lowerConsumptionZones[0].BarStepMm -eq 200 -and
        $lowerConsumptionZones[0].AsCoveredCm2PerM -lt [LiraSlabZones.Core.BarCapacity]::AsCm2PerM(12, 100)) `
    'When a slightly larger bar at 200 mm provides enough As with lower consumption, it must be selected.'
$insideCell.AsAdditionalCm2PerM = 15
$longPatch = New-FramePatch 703 ([LiraSlabZones.Core.ZoneDirection]::X) 0 20 0 0.8
$longPatch.Layer = [LiraSlabZones.Core.RebarLayer]::As1
$longPatch.Cells[0].AsAdditionalCm2PerM = 15
$longPatch.ElementIds.Add(703)
$longFrame = [LiraSlabZones.Core.ZonePatchFrameSelection]::new()
$longFrame.MinXM = 0
$longFrame.MaxXM = 20
$longFrame.MinYM = 0
$longFrame.MaxYM = 0.8
$longFrame.Patches.Add($longPatch)
$longZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($longFrame, 4.5, $settings)
$expectedLapMm = 2 * [LiraSlabZones.Core.RebarTables]::LapLenMm('B25', 20)
Assert ($longZones.Count -gt 1 -and
        @($longZones | Where-Object { $_.LengthMm -gt 11700.001 }).Count -eq 0) `
    'Straight bars longer than 11700 mm must be split into compliant pieces.'
Assert (@($longZones | Where-Object { $_.LengthMm -notin [LiraSlabZones.Core.RebarTables]::Sum3FamilyLengthsMm }).Count -eq 0) `
    'Every generated straight bar piece must use a standard SUM-3 family length.'
for ($i = 1; $i -lt $longZones.Count; $i++) {
    $overlapMm = [Math]::Min($longZones[$i - 1].Contour[1].X, $longZones[$i].Contour[1].X) * 1000 -
        [Math]::Max($longZones[$i - 1].Contour[0].X, $longZones[$i].Contour[0].X) * 1000
    Assert ($overlapMm + 0.001 -ge $expectedLapMm) `
        'Rounding pieces up to standard lengths must preserve at least two table lap lengths.'
}
$longFrame.SlabOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$longFrame.SlabOutline.Add([LiraSlabZones.Core.Point3]::new(20, 0, 0))
$longFrame.SlabOutline.Add([LiraSlabZones.Core.Point3]::new(20, 0.8, 0))
$longFrame.SlabOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0.8, 0))
$warnedLongZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($longFrame, 4.5, $settings)
Assert ($warnedLongZones[0].StatusColor -eq 'warn' -and
        $warnedLongZones[0].Comment.Contains('анкеровка выходит за контур плиты')) `
    'Anchorage extending outside the supplied slab outline must be visibly warned, not clipped.'

$gaplessSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$gaplessSettings.ShowAs1 = $true
$gaplessSettings.ShowAs2 = $false
$gaplessSettings.ShowAs3 = $false
$gaplessSettings.ShowAs4 = $false
$gaplessPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$gaplessPlates.Add((New-QuadPlate 801 0 4 0 0.4 10))
$gaplessPlates.Add((New-QuadPlate 802 0.1 4.1 0.4 0.8 10))
$gaplessZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$gaplessLeft = New-TestZone 0 4 -0.1 0.5 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$gaplessRight = New-TestZone 0.1 4.1 0.3 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 100
$gaplessLeft.AsAdditional = 10
$gaplessRight.AsAdditional = 10
$gaplessLeft.NodeIds.Add(801)
$gaplessRight.NodeIds.Add(802)
$gaplessZones.Add($gaplessLeft)
$gaplessZones.Add($gaplessRight)
$gaplessSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$gaplessPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$gaplessSources.Add($gaplessLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.4))
$gaplessSources.Add($gaplessRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0.1, 4.1, 0.4, 0.8))
$gaplessPatches.Add($gaplessLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4.1, 0, 0.8))
$gaplessPatches.Add($gaplessRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4.1, 0, 0.8))
$gaplessWarning = ''
$gaplessWarning = ''
$gaplessOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $gaplessZones, $gaplessSources, $gaplessPatches, $gaplessPlates, $gaplessSettings,
    $null, $null, 0, 0.2, [ref]$gaplessWarning)
Assert ($gaplessOk -and $gaplessZones.Count -eq 2 -and
        [Math]::Abs($gaplessZones[0].WidthMm - 400) -lt 0.001 -and
        [Math]::Abs($gaplessZones[0].WidthMm / $gaplessZones[0].BarStepMm -
                    [Math]::Round($gaplessZones[0].WidthMm / $gaplessZones[0].BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($gaplessZones[1].WidthMm / $gaplessZones[1].BarStepMm -
                    [Math]::Round($gaplessZones[1].WidthMm / $gaplessZones[1].BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($gaplessZones[0].Contour[2].Y - $gaplessZones[1].Contour[0].Y) -lt 1e-9 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $gaplessPlates, $gaplessZones, $gaplessSettings).UncoveredCount -eq 0)) `
    'Global layout must remove overlap, produce adjacent step-sized widths, and retain full FE coverage.'

$unboundedZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$unboundedA = New-TestZone 0 9 0 0.6 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$unboundedB = New-TestZone 5 14 0 0.6 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$unboundedA.ZoneId = 855
$unboundedB.ZoneId = 856
$unboundedZones.Add($unboundedA)
$unboundedZones.Add($unboundedB)
$unboundedSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$unboundedPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$unboundedSources.Add($unboundedA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 9, 0, 0.6))
$unboundedSources.Add($unboundedB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(5, 14, 0, 0.6))
$unboundedPatches.Add($unboundedA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 9, 0, 0.6))
$unboundedPatches.Add($unboundedB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(5, 14, 0, 0.6))
$unboundedWarning = ''
$unboundedOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $unboundedZones, $unboundedSources, $unboundedPatches,
    [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new(),
    $gaplessSettings, $null, $null, 0, 0, [ref]$unboundedWarning)
$unboundedShifted = $unboundedZones | Where-Object ZoneId -eq 856 | Select-Object -First 1
$unboundedShiftMin = ($unboundedShifted.Contour | Measure-Object Y -Minimum).Minimum
$unboundedShiftMax = ($unboundedShifted.Contour | Measure-Object Y -Maximum).Maximum
$unboundedCrossOverlap = [Math]::Min(
    ($unboundedZones[0].Contour | Measure-Object Y -Maximum).Maximum,
    ($unboundedZones[1].Contour | Measure-Object Y -Maximum).Maximum) -
    [Math]::Max(
    ($unboundedZones[0].Contour | Measure-Object Y -Minimum).Minimum,
    ($unboundedZones[1].Contour | Measure-Object Y -Minimum).Minimum)
Assert ($unboundedOk -and $unboundedZones.Count -eq 2 -and
        $unboundedCrossOverlap -le 1e-6 -and
        ($unboundedShiftMin -lt -1e-9 -or $unboundedShiftMax -gt 0.6 + 1e-9) -and
        [Math]::Abs($unboundedShifted.WidthMm / $unboundedShifted.BarStepMm -
                    [Math]::Round($unboundedShifted.WidthMm / $unboundedShifted.BarStepMm)) -lt 1e-9 -and
        @($unboundedZones | Where-Object { $_.LengthMm -gt 11700 }).Count -eq 0) `
    "When two overlapping zones cannot be merged without exceeding 11700 mm, the solver must move one outside the patch without overlap. ok=$unboundedOk warning='$unboundedWarning' intervals=$(($unboundedZones | ForEach-Object { '{0:0.###}..{1:0.###}' -f $_.Contour[0].Y, $_.Contour[2].Y }) -join ';')"

$peakOrderZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$peakOrderLeft = New-TestZone 0 6 -0.075 0.525 ([LiraSlabZones.Core.ZoneDirection]::X) 25 200
$peakOrderRight = New-TestZone 0 6 0.475 1.075 ([LiraSlabZones.Core.ZoneDirection]::X) 25 200
$peakOrderLeft.ZoneId = 861
$peakOrderRight.ZoneId = 862
$peakOrderLeft.AsAdditional = 12
$peakOrderRight.AsAdditional = 8
$peakOrderLeft.NodeIds.Add(861)
$peakOrderRight.NodeIds.Add(862)
$peakOrderZones.Add($peakOrderLeft)
$peakOrderZones.Add($peakOrderRight)
$peakOrderPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$peakOrderPlates.Add((New-QuadPlate 861 0 6 0 0.45 8))
$peakOrderPlates.Add((New-QuadPlate 862 0 6 0.55 1.0 12))
$peakOrderSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$peakOrderPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$peakOrderSources.Add($peakOrderLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.45))
$peakOrderSources.Add($peakOrderRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.55, 1.0))
$peakOrderPatches.Add($peakOrderLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$peakOrderPatches.Add($peakOrderRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$peakOrderWarning = ''
$peakOrderOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $peakOrderZones, $peakOrderSources, $peakOrderPatches, $peakOrderPlates,
    $gaplessSettings, $null, $null, 0, 0.4, [ref]$peakOrderWarning)
$peakOrderPeakZone = $peakOrderZones | Where-Object ZoneId -eq 862 | Select-Object -First 1
$peakOrderOverlap = [Math]::Min($peakOrderZones[0].Contour[2].Y, $peakOrderZones[1].Contour[2].Y) -
    [Math]::Max($peakOrderZones[0].Contour[0].Y, $peakOrderZones[1].Contour[0].Y)
Assert ($peakOrderOk -and $null -ne $peakOrderPeakZone -and
        [Math]::Abs($peakOrderPeakZone.Contour[0].Y - 0.475) -lt 1e-9 -and
        $peakOrderOverlap -le 1e-6 -and
        [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $peakOrderPlates, $peakOrderZones, $gaplessSettings).UncoveredCount -eq 0) `
    "The zone containing the layer's highest FE As must be placed first even when its zone AsAdditional is lower; overlap=$peakOrderOverlap warning='$peakOrderWarning'."

$shrinkZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$shrinkLeft = New-TestZone 0 6 0 0.6 ([LiraSlabZones.Core.ZoneDirection]::X) 20 200
$shrinkRight = New-TestZone 0 6 0.4 1.0 ([LiraSlabZones.Core.ZoneDirection]::X) 25 200
$shrinkLeft.ZoneId = 871
$shrinkRight.ZoneId = 872
$shrinkLeft.AsAdditional = 8
$shrinkRight.AsAdditional = 20
$shrinkLeft.NodeIds.Add(871)
$shrinkRight.NodeIds.Add(872)
$shrinkRight.NodeIds.Add(873)
$shrinkZones.Add($shrinkLeft)
$shrinkZones.Add($shrinkRight)
$shrinkPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$shrinkPlates.Add((New-QuadPlate 871 0 6 0 0.4 8))
$shrinkPlates.Add((New-QuadPlate 872 0 6 0.6 1.0 12))
$shrinkPlates.Add((New-QuadPlate 873 0 6 0.4 0.6 20))
$shrinkSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$shrinkPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$shrinkSources.Add($shrinkLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.6))
$shrinkSources.Add($shrinkRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.4, 1.0))
$shrinkPatches.Add($shrinkLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$shrinkPatches.Add($shrinkRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$shrinkWarning = ''
$shrinkOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $shrinkZones, $shrinkSources, $shrinkPatches, $shrinkPlates,
    $gaplessSettings, $null, $null, 0, 0, [ref]$shrinkWarning)
$shrinkOrdered = @($shrinkZones | Sort-Object { $_.Contour[0].Y })
$shrinkCrossOverlap = [Math]::Min($shrinkOrdered[0].Contour[2].Y, $shrinkOrdered[1].Contour[2].Y) -
    [Math]::Max($shrinkOrdered[0].Contour[0].Y, $shrinkOrdered[1].Contour[0].Y)
$shrinkStepSized = @($shrinkOrdered | Where-Object {
    [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
}).Count -eq 0
Assert ($shrinkOk -and $shrinkOrdered.Count -eq 2 -and
        $shrinkCrossOverlap -le 1e-6 -and $shrinkStepSized -and
        [Math]::Abs($shrinkOrdered[0].Contour[2].Y - $shrinkOrdered[1].Contour[0].Y) -lt 1e-9 -and
        [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $shrinkPlates, $shrinkZones, $gaplessSettings).UncoveredCount -eq 0) `
    "Zones may keep their rounded widths by moving outside the patch; they must remain contiguous, step-sized, non-overlapping, and cover every FE. overlap=$shrinkCrossOverlap widths=$($shrinkOrdered.WidthMm -join '/') warning='$shrinkWarning'."

$weakShrinkZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$weakShrinkLeft = New-TestZone 0 6 0 0.6 ([LiraSlabZones.Core.ZoneDirection]::X) 12 200
$weakShrinkRight = New-TestZone 0 6 0.4 1.0 ([LiraSlabZones.Core.ZoneDirection]::X) 12 200
$weakShrinkLeft.ZoneId = 881
$weakShrinkRight.ZoneId = 882
$weakShrinkLeft.AsAdditional = 8
$weakShrinkRight.AsAdditional = 12
$weakShrinkLeft.NodeIds.Add(881)
$weakShrinkRight.NodeIds.Add(882)
$weakShrinkZones.Add($weakShrinkLeft)
$weakShrinkZones.Add($weakShrinkRight)
$weakShrinkPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$weakShrinkPlates.Add((New-QuadPlate 881 0 6 0 0.4 8))
$weakShrinkPlates.Add((New-QuadPlate 882 0 6 0.6 1.0 12))
$weakShrinkSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$weakShrinkPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$weakShrinkSources.Add($weakShrinkLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.6))
$weakShrinkSources.Add($weakShrinkRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.4, 1.0))
$weakShrinkPatches.Add($weakShrinkLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$weakShrinkPatches.Add($weakShrinkRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 1.0))
$weakShrinkWarning = ''
$weakShrinkOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $weakShrinkZones, $weakShrinkSources, $weakShrinkPatches, $weakShrinkPlates,
    $gaplessSettings, $null, $null, 0, 0, [ref]$weakShrinkWarning)
$weakCrossOverlap = [Math]::Min(
    ($weakShrinkZones[0].Contour | Measure-Object Y -Maximum).Maximum,
    ($weakShrinkZones[1].Contour | Measure-Object Y -Maximum).Maximum) -
    [Math]::Max(
    ($weakShrinkZones[0].Contour | Measure-Object Y -Minimum).Minimum,
    ($weakShrinkZones[1].Contour | Measure-Object Y -Minimum).Minimum)
Assert ($weakShrinkOk -and $weakShrinkZones.Count -eq 2 -and
        [Math]::Abs($weakShrinkZones[0].WidthMm - 600) -lt 1e-6 -and
        [Math]::Abs($weakShrinkZones[1].WidthMm - 600) -lt 1e-6 -and
        $weakCrossOverlap -le 1e-6) `
    "Under-capacity zones must not be narrowed; they may be shifted outside their patches to remove intersection. ok=$weakShrinkOk count=$($weakShrinkZones.Count) overlap=$weakCrossOverlap widths=$(($weakShrinkZones | ForEach-Object { $_.WidthMm }) -join '/') warning='$weakShrinkWarning'."

$neighborShrinkZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$neighborPeak = New-TestZone 0 6 0 0.4 ([LiraSlabZones.Core.ZoneDirection]::X) 25 200
$neighborCurrent = New-TestZone 0 6 0.2 0.4 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$neighborPeak.ZoneId = 891
$neighborCurrent.ZoneId = 892
$neighborPeak.NodeIds.Add(891)
$neighborCurrent.NodeIds.Add(892)
$neighborShrinkZones.Add($neighborPeak)
$neighborShrinkZones.Add($neighborCurrent)
$neighborShrinkPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$neighborShrinkPlates.Add((New-QuadPlate 891 0 6 0 0.2 15))
$neighborShrinkPlates.Add((New-QuadPlate 892 0 6 0.2 0.4 8))
$neighborShrinkSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$neighborShrinkPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$neighborShrinkSources.Add($neighborPeak, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$neighborShrinkSources.Add($neighborCurrent, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.2, 0.4))
$neighborShrinkPatches.Add($neighborPeak, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$neighborShrinkPatches.Add($neighborCurrent, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$neighborShrinkWarning = ''
$neighborShrinkOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $neighborShrinkZones, $neighborShrinkSources, $neighborShrinkPatches, $neighborShrinkPlates,
    $gaplessSettings, $null, $null, 0, 0, [ref]$neighborShrinkWarning)
$neighborPeakResult = $neighborShrinkZones | Where-Object ZoneId -eq 891 | Select-Object -First 1
$neighborCurrentResult = $neighborShrinkZones | Where-Object ZoneId -eq 892 | Select-Object -First 1
$neighborOverlap = [Math]::Min($neighborPeakResult.Contour[2].Y, $neighborCurrentResult.Contour[2].Y) -
    [Math]::Max($neighborPeakResult.Contour[0].Y, $neighborCurrentResult.Contour[0].Y)
Assert ($neighborShrinkOk -and $null -ne $neighborPeakResult -and $null -ne $neighborCurrentResult -and
        $neighborOverlap -le 1e-6 -and
        [Math]::Abs($neighborPeakResult.WidthMm / $neighborPeakResult.BarStepMm -
                    [Math]::Round($neighborPeakResult.WidthMm / $neighborPeakResult.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($neighborCurrentResult.WidthMm / $neighborCurrentResult.BarStepMm -
                    [Math]::Round($neighborCurrentResult.WidthMm / $neighborCurrentResult.BarStepMm)) -lt 1e-9 -and
        [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $neighborShrinkPlates, $neighborShrinkZones, $gaplessSettings).UncoveredCount -eq 0) `
    "A narrow zone may remain separate or be absorbed only when needed; placement must retain step widths, remove overlaps, and cover every FE. ok=$neighborShrinkOk widths=$($neighborPeakResult.WidthMm)/$($neighborCurrentResult.WidthMm) overlap=$neighborOverlap intervals=$($neighborPeakResult.Contour[0].Y)..$($neighborPeakResult.Contour[2].Y),$($neighborCurrentResult.Contour[0].Y)..$($neighborCurrentResult.Contour[2].Y) warning='$neighborShrinkWarning'."

$longOverlapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$longOverlapA = New-TestZone 0 6 0.1 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$longOverlapB = New-TestZone 0 6 0.3 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 100
$longOverlapA.AsAdditional = 10
$longOverlapB.AsAdditional = 10
$longOverlapA.Comment = 'часть 1/2'
$longOverlapB.Comment = 'часть 2/2'
$longOverlapA.NodeIds.Add(821)
$longOverlapB.NodeIds.Add(822)
$longOverlapZones.Add($longOverlapA)
$longOverlapZones.Add($longOverlapB)
$longOverlapSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$longOverlapPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$longOverlapSources.Add($longOverlapA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.3))
$longOverlapSources.Add($longOverlapB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.5, 0.8))
$longOverlapPatches.Add($longOverlapA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.3))
$longOverlapPatches.Add($longOverlapB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.5, 0.8))
$longOverlapPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$longOverlapPlates.Add((New-QuadPlate 821 0 6 0 0.4 10))
$longOverlapPlates.Add((New-QuadPlate 822 0 6 0.4 0.8 10))
$longOverlapWarning = ''
$longOverlapOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $longOverlapZones, $longOverlapSources, $longOverlapPatches, $longOverlapPlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$longOverlapWarning)
Assert ($longOverlapOk -and $longOverlapZones.Count -eq 2 -and
        [Math]::Abs($longOverlapZones[0].Contour[2].Y - $longOverlapZones[1].Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($longOverlapZones[0].WidthMm / $longOverlapZones[0].BarStepMm -
                    [Math]::Round($longOverlapZones[0].WidthMm / $longOverlapZones[0].BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($longOverlapZones[1].WidthMm / $longOverlapZones[1].BarStepMm -
                    [Math]::Round($longOverlapZones[1].WidthMm / $longOverlapZones[1].BarStepMm)) -lt 1e-9 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $longOverlapPlates, $longOverlapZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "Overlapping long zones from different patch frames must be arranged together without overlap while retaining step multiples and FE coverage. ok=$longOverlapOk warning='$longOverlapWarning' zones=$($longOverlapZones.Count) intervals=$(($longOverlapZones | ForEach-Object { '{0:0.###}..{1:0.###}/w{2:0}' -f $_.Contour[0].Y, $_.Contour[2].Y, $_.WidthMm }) -join ';') uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($longOverlapPlates, $longOverlapZones, $gaplessSettings).UncoveredCount)"

$priorityTransferZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$priorityTransferHigh = New-TestZone 0 11.7 0 0.6 `
    ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$priorityTransferLow = New-TestZone 0 11.7 0.4 1.0 `
    ([LiraSlabZones.Core.ZoneDirection]::X) 12 200
$priorityTransferHigh.ZoneId = 8501
$priorityTransferLow.ZoneId = 8502
$priorityTransferHigh.AsAdditional = 8
$priorityTransferLow.AsAdditional = 5
$priorityTransferHigh.NodeIds.Add(8501)
$priorityTransferLow.NodeIds.Add(8502)
$priorityTransferZones.Add($priorityTransferHigh)
$priorityTransferZones.Add($priorityTransferLow)
$priorityTransferPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$priorityTransferPlates.Add((New-QuadPlate 8501 0 11.7 0 0.4 8))
$priorityTransferPlates.Add((New-QuadPlate 8502 0 11.7 0.6 0.8 5))
$priorityTransferPlates.Add((New-QuadPlate 8503 0 11.7 0.8 1.0 5))
$priorityTransferMethod = [LiraSlabZones.Core.ZonePatchGaplessLayout].GetMethod(
    'TryTransferOneBarStep', [Reflection.BindingFlags]::Static -bor
    [Reflection.BindingFlags]::NonPublic)
$priorityTransferOk = $priorityTransferMethod.Invoke($null, [object[]]@(
    $priorityTransferHigh, $priorityTransferLow, 0.0, -0.2, 1.2))
$priorityTransferHighResult = $priorityTransferHigh
$priorityTransferLowResult = $priorityTransferLow
Assert ($priorityTransferOk -and $priorityTransferZones.Count -eq 2 -and
        [Math]::Abs($priorityTransferHighResult.WidthMm - 800) -lt 1e-6 -and
        [Math]::Abs($priorityTransferLowResult.WidthMm - 400) -lt 1e-6 -and
        [Math]::Abs($priorityTransferHighResult.Contour[2].Y -
                    $priorityTransferLowResult.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($priorityTransferHighResult.WidthMm / $priorityTransferHighResult.BarStepMm -
                    [Math]::Round($priorityTransferHighResult.WidthMm / $priorityTransferHighResult.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($priorityTransferLowResult.WidthMm / $priorityTransferLowResult.BarStepMm -
                    [Math]::Round($priorityTransferLowResult.WidthMm / $priorityTransferLowResult.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($priorityTransferHighResult.LengthMm - 11700) -lt 1e-6 -and
        [Math]::Abs($priorityTransferLowResult.LengthMm - 11700) -lt 1e-6 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $priorityTransferPlates, $priorityTransferZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "The higher-demand zone must gain one 200 mm step while its neighbor loses that step, with no gaps, uncovered FEs, or change to the 11700 mm bar length. ok=$priorityTransferOk high=$($priorityTransferHighResult.WidthMm) low=$($priorityTransferLowResult.WidthMm)"

$roundedConflictZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$roundedConflictA = New-TestZone 0 6 0 0.35 ([LiraSlabZones.Core.ZoneDirection]::X) 20 200
$roundedConflictB = New-TestZone 0 6 0.37 0.72 ([LiraSlabZones.Core.ZoneDirection]::X) 25 100
$roundedConflictA.AsAdditional = 10
$roundedConflictB.AsAdditional = 10
$roundedConflictA.NodeIds.Add(823)
$roundedConflictB.NodeIds.Add(824)
$roundedConflictZones.Add($roundedConflictA)
$roundedConflictZones.Add($roundedConflictB)
$roundedConflictSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$roundedConflictPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$roundedConflictSources.Add($roundedConflictA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.35))
$roundedConflictSources.Add($roundedConflictB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.37, 0.72))
$roundedConflictPatches.Add($roundedConflictA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.35))
$roundedConflictPatches.Add($roundedConflictB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.37, 0.72))
$roundedConflictPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$roundedConflictPlates.Add((New-QuadPlate 823 0 6 0 0.35 10))
$roundedConflictPlates.Add((New-QuadPlate 824 0 6 0.37 0.72 10))
$roundedConflictWarning = ''
$roundedConflictOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $roundedConflictZones, $roundedConflictSources, $roundedConflictPatches, $roundedConflictPlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$roundedConflictWarning)
$roundedConflictOverlap = [Math]::Min($roundedConflictZones[0].Contour[2].Y, $roundedConflictZones[1].Contour[2].Y) -
    [Math]::Max($roundedConflictZones[0].Contour[0].Y, $roundedConflictZones[1].Contour[0].Y)
Assert ($roundedConflictOk -and $roundedConflictZones.Count -eq 2 -and
        $roundedConflictOverlap -le 1e-6 -and
        [Math]::Abs($roundedConflictZones[0].WidthMm / $roundedConflictZones[0].BarStepMm -
                    [Math]::Round($roundedConflictZones[0].WidthMm / $roundedConflictZones[0].BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($roundedConflictZones[1].WidthMm / $roundedConflictZones[1].BarStepMm -
                    [Math]::Round($roundedConflictZones[1].WidthMm / $roundedConflictZones[1].BarStepMm)) -lt 1e-9 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $roundedConflictPlates, $roundedConflictZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "Zones whose allowable patch overrun overlaps after width rounding must be arranged together; overlap=$roundedConflictOverlap mm warning='$roundedConflictWarning'."

$savedConflictZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$savedConflictLeft = New-TestZone 0 6 0.1 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$savedConflictRight = New-TestZone 0 6 0.3 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 100
$savedConflictUnrelated = New-TestZone 10 12 1.1 1.433 ([LiraSlabZones.Core.ZoneDirection]::X) 20 200
$savedConflictLeft.AsAdditional = 10
$savedConflictRight.AsAdditional = 10
$savedConflictZones.Add($savedConflictLeft)
$savedConflictZones.Add($savedConflictRight)
$savedConflictZones.Add($savedConflictUnrelated)
$savedConflictSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$savedConflictPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$savedConflictSources.Add($savedConflictLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$savedConflictSources.Add($savedConflictRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.4, 0.8))
$savedConflictPatches.Add($savedConflictLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$savedConflictPatches.Add($savedConflictRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$savedUnrelatedContour = $savedConflictUnrelated.Contour | ForEach-Object { @($_.X, $_.Y, $_.Z) } | ConvertTo-Json -Compress
$savedConflictWarning = ''
$savedConflictOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $savedConflictZones, $savedConflictSources, $savedConflictPatches, $longOverlapPlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$savedConflictWarning)
$resolvedConflictLeft = $savedConflictZones[0]
$resolvedConflictRight = $savedConflictZones[1]
$resolvedUnrelatedContour = $savedConflictZones[2].Contour | ForEach-Object { @($_.X, $_.Y, $_.Z) } | ConvertTo-Json -Compress
Assert ($savedConflictOk -and
        [Math]::Abs($resolvedConflictLeft.Contour[2].Y - $resolvedConflictRight.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($resolvedConflictLeft.WidthMm / $resolvedConflictLeft.BarStepMm -
                    [Math]::Round($resolvedConflictLeft.WidthMm / $resolvedConflictLeft.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($resolvedConflictRight.WidthMm / $resolvedConflictRight.BarStepMm -
                    [Math]::Round($resolvedConflictRight.WidthMm / $resolvedConflictRight.BarStepMm)) -lt 1e-9 -and
        $savedUnrelatedContour -eq $resolvedUnrelatedContour -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $longOverlapPlates, $savedConflictZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "Saved preview zones must resolve local overlaps without changing unrelated manual geometry or losing FE coverage. ok=$savedConflictOk warning='$savedConflictWarning' count=$($savedConflictZones.Count) uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($longOverlapPlates, $savedConflictZones, $gaplessSettings).UncoveredCount)"

$lapGroupZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$lapPart1 = New-TestZone 0.2 0.6 0 11.7 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$lapPart2 = New-TestZone 0.2 0.6 8.5 20.2 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$lapBlocker1 = New-TestZone 0.4 0.8 0 8.4 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$lapBlocker2 = New-TestZone 0 0.4 11.8 20.2 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$lapPart1.Comment = 'часть 1/2; нахлёст 3200 мм'
$lapPart2.Comment = 'часть 2/2; нахлёст 3200 мм'
$lapPart1.NodeIds.Add(9001)
$lapPart2.NodeIds.Add(9001)
$lapBlocker1.NodeIds.Add(9002)
$lapBlocker2.NodeIds.Add(9003)
foreach ($zone in @($lapPart1, $lapPart2, $lapBlocker1, $lapBlocker2)) {
    $zone.AsAdditional = 10
    $lapGroupZones.Add($zone)
}
$lapGroupSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$lapGroupPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
foreach ($zone in $lapGroupZones) {
    $lapGroupSources.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        $zone.Contour[0].X, $zone.Contour[1].X, $zone.Contour[0].Y, $zone.Contour[2].Y))
    $lapGroupPatches.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 1, 0, 20.2))
}
$lapGroupWarning = ''
$lapGroupOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $lapGroupZones, $lapGroupSources, $lapGroupPatches,
    [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new(),
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$lapGroupWarning)
$lapPairAligned = [Math]::Abs($lapPart1.Contour[0].X - $lapPart2.Contour[0].X) -lt 1e-9 -and
    [Math]::Abs($lapPart1.Contour[1].X - $lapPart2.Contour[1].X) -lt 1e-9
$lapUnexpectedOverlaps = 0
for ($i = 0; $i -lt $lapGroupZones.Count; $i++) {
    for ($j = $i + 1; $j -lt $lapGroupZones.Count; $j++) {
        if (($i -eq 0 -and $j -eq 1) -or ($i -eq 1 -and $j -eq 0)) { continue }
        $first = $lapGroupZones[$i].Contour
        $second = $lapGroupZones[$j].Contour
        $crossOverlap = [Math]::Min($first[1].X, $second[1].X) -
            [Math]::Max($first[0].X, $second[0].X)
        $axialOverlap = [Math]::Min($first[2].Y, $second[2].Y) -
            [Math]::Max($first[0].Y, $second[0].Y)
        if ($crossOverlap -gt 1e-6 -and $axialOverlap -gt 1e-6) { $lapUnexpectedOverlaps++ }
    }
}
Assert ($lapGroupOk -and $lapGroupZones.Count -eq 4 -and $lapPairAligned -and
        $lapUnexpectedOverlaps -eq 0 -and
        [Math]::Abs($lapPart1.LengthMm - 11700) -lt 1e-6 -and
        [Math]::Abs($lapPart2.LengthMm - 11700) -lt 1e-6 -and
        [Math]::Abs($lapPart1.WidthMm / $lapPart1.BarStepMm -
    [Math]::Round($lapPart1.WidthMm / $lapPart1.BarStepMm)) -lt 1e-9) `
    "Overlapping 11700 mm splice parts must share one width placement, keeping the lap aligned and separating neighboring zones. aligned=$lapPairAligned extraOverlaps=$lapUnexpectedOverlaps warning='$lapGroupWarning'"

$mergeLapZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$mergeLapPart1 = New-TestZone 0.2 0.6 0 11.7 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$mergeLapNeighbor = New-TestZone 0.6 1.0 0 11.7 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$mergeLapPart2 = New-TestZone 0.2 0.6 8.5 20.2 ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
$mergeLapPart1.Comment = 'часть 1/2; нахлёст 3200 мм'
$mergeLapPart2.Comment = 'часть 2/2; нахлёст 3200 мм'
$mergeLapPart1.NodeIds.Add(9300)
$mergeLapPart2.NodeIds.Add(9300)
$mergeLapNeighbor.NodeIds.Add(9301)
$mergeLapZones.Add($mergeLapPart1)
$mergeLapZones.Add($mergeLapNeighbor)
$mergeLapZones.Add($mergeLapPart2)
$mergeLapBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
foreach ($zone in $mergeLapZones) {
    $bounds = [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        $zone.Contour[0].X, $zone.Contour[1].X, $zone.Contour[0].Y, $zone.Contour[2].Y)
    $mergeLapBounds.Add($zone, $bounds)
    $zone.AsAdditional = 5
}
$mergeLapWarning = ''
$mergeLapOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $mergeLapZones, $mergeLapBounds, $null,
    [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new(),
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$mergeLapWarning)
Assert ($mergeLapOk -and $mergeLapZones.Count -eq 3 -and
        [Math]::Abs($mergeLapZones[0].WidthMm - 400) -lt 1e-6 -and
        [Math]::Abs($mergeLapZones[1].WidthMm - 400) -lt 1e-6 -and
        [Math]::Abs($mergeLapZones[0].Contour[1].X - $mergeLapZones[1].Contour[0].X) -lt 1e-6 -and
        [Math]::Abs($mergeLapZones[1].Contour[0].X - $mergeLapZones[2].Contour[1].X) -lt 1e-6) `
    "A compatible-zone merge must be rolled back if it turns an intentional 11700 mm lap into an overlap. ok=$mergeLapOk zones=$($mergeLapZones.Count) warning='$mergeLapWarning'"

$sameBandZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$sameBandPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$sameBandPlates.Add((New-QuadPlate 9100 0.1 0.5 0 27 5))
$sameBandBounds = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$sameBandPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$sameBandRanges = @(@(0, 11.7), @(9.7, 21.4), @(19.4, 27.0))
for ($i = 0; $i -lt $sameBandRanges.Count; $i++) {
    $range = $sameBandRanges[$i]
    $zone = New-TestZone 0.1 0.5 $range[0] $range[1] `
        ([LiraSlabZones.Core.ZoneDirection]::Y) 16 200
    $zone.ZoneId = 9101 + $i
    $zone.AsAdditional = 5
    $zone.ConcreteClass = 'B40'
    $zone.NodeIds.Add(9200 + $i)
    $sameBandZones.Add($zone)
    $bounds = [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0.1, 0.5, $range[0], $range[1])
    $sameBandBounds.Add($zone, $bounds)
    $sameBandPatches.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0.1, 0.5, 0, 27))
}
$sameBandZones[0].NodeIds.Clear()
$sameBandZones[0].NodeIds.Add(9200)
$sameBandZones[1].NodeIds.Clear()
$sameBandZones[1].NodeIds.Add(9200)
$sameBandZones[0].Comment = 'часть 1/2; нахлёст 2000 мм'
$sameBandZones[1].Comment = 'часть 2/2; нахлёст 2000 мм'
$sameBandWarning = ''
$sameBandOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $sameBandZones, $sameBandBounds, $sameBandPatches, $sameBandPlates,
    $gaplessSettings, $null, $null, 0.4, 0.2, [ref]$sameBandWarning)
$sameBandUncovered = [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
    $sameBandPlates, $sameBandZones, $gaplessSettings).UncoveredCount
$sameBandAllowedOverlap = 2 * [LiraSlabZones.Core.RebarTables]::LapLenMm('B40', 16)
$sameBandValidSegments = $sameBandZones.Count -eq 3
for ($i = 0; $i -lt $sameBandZones.Count; $i++) {
    $sameBandValidSegments = $sameBandValidSegments -and
        $sameBandZones[$i].LengthMm -le 11700 + 1e-6 -and
        [int][Math]::Round($sameBandZones[$i].LengthMm) -in
            [LiraSlabZones.Core.RebarTables]::Sum3FamilyLengthsMm -and
        [Math]::Abs($sameBandZones[$i].WidthMm / $sameBandZones[$i].BarStepMm -
                    [Math]::Round($sameBandZones[$i].WidthMm / $sameBandZones[$i].BarStepMm)) -lt 1e-9
    if ($i -gt 0) {
        $overlap = ($sameBandZones[$i - 1].Contour[2].Y -
            $sameBandZones[$i].Contour[0].Y) * 1000
        $sameBandValidSegments = $sameBandValidSegments -and
            $overlap + 1e-6 -ge $sameBandAllowedOverlap
    }
}
Assert ($sameBandOk -and $sameBandValidSegments -and $sameBandUncovered -eq 0 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $sameBandPlates, $sameBandZones, $gaplessSettings).Issues |
            Where-Object Kind -eq PlacementConflict).Count -eq 0) `
    "Overlapping zones on one transverse bar band must coalesce into a covered SUM-30 splice series no longer than 11700 mm. ok=$sameBandOk valid=$sameBandValidSegments uncovered=$sameBandUncovered warning='$sameBandWarning'"

$coverageSweepZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$coverageSweepZones.Add((New-TestZone 1.249 1.649 19.483 27.283 ([LiraSlabZones.Core.ZoneDirection]::Y) 22 200))
$coverageSweepZones.Add((New-TestZone 1.556 1.956 19.700 27.500 ([LiraSlabZones.Core.ZoneDirection]::Y) 22 200))
$coverageSweepZones.Add((New-TestZone 1.855 2.055 19.700 27.500 ([LiraSlabZones.Core.ZoneDirection]::Y) 22 200))
$coverageSweepZones.Add((New-TestZone 1.974 2.374 20.300 28.100 ([LiraSlabZones.Core.ZoneDirection]::Y) 22 200))
$coverageSweepZones.Add((New-TestZone 2.273 2.473 20.300 28.100 ([LiraSlabZones.Core.ZoneDirection]::Y) 12 200))
foreach ($zone in $coverageSweepZones) { $zone.AsAdditional = 4 }
$coverageSweepSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$coverageSweepPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
foreach ($zone in $coverageSweepZones) {
    $coverageSweepSources.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        $zone.Contour[0].X, $zone.Contour[2].X, $zone.Contour[0].Y, $zone.Contour[2].Y))
    $coverageSweepPatches.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        1.25, 2.446, 19.483, 28.1))
}
$coverageSweepPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$coverageSweepPlates.Add((New-QuadPlate 889 1.25 2.446 21 26.8 4))
$coverageSweepWarning = ''
$coverageSweepOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $coverageSweepZones, $coverageSweepSources, $coverageSweepPatches, $coverageSweepPlates,
    $gaplessSettings, $null, $null, 0, 0.4, [ref]$coverageSweepWarning)
$coverageSweepOrdered = @($coverageSweepZones | Sort-Object { $_.Contour[0].X })
$coverageSweepNoGaps = $true
for ($i = 0; $i -lt $coverageSweepOrdered.Count - 1; $i++) {
    $coverageSweepNoGaps = $coverageSweepNoGaps -and
        [Math]::Abs($coverageSweepOrdered[$i + 1].Contour[0].X - $coverageSweepOrdered[$i].Contour[2].X) -lt 1e-6
}
Assert ($coverageSweepOk -and $coverageSweepNoGaps -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $coverageSweepPlates, $coverageSweepZones, $gaplessSettings).UncoveredCount -eq 0) -and
        ($coverageSweepZones | Where-Object {
            [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -ge 1e-9 -or
            $_.LengthMm -gt 11700
        }).Count -eq 0) `
    "A five-zone overlap chain must be tightly packed without losing the wide FE it crosses internally. ok=$coverageSweepOk noGaps=$coverageSweepNoGaps warning='$coverageSweepWarning'"

$touchChainZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$touchChainA = New-TestZone 0 4 0.1 0.5 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$touchChainB = New-TestZone 0 4 0.3 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$touchChainC = New-TestZone 0 4 0.7 1.1 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$touchChainZones.Add($touchChainA)
$touchChainZones.Add($touchChainB)
$touchChainZones.Add($touchChainC)
$touchChainSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$touchChainPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$touchChainSources.Add($touchChainA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.4))
$touchChainSources.Add($touchChainB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.2, 0.5))
$touchChainSources.Add($touchChainC, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.5, 0.9))
$touchChainPatches.Add($touchChainA, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.4))
$touchChainPatches.Add($touchChainB, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.2, 0.5))
$touchChainPatches.Add($touchChainC, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.5, 0.9))
$touchChainPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$touchChainPlates.Add((New-QuadPlate 861 0 4 0 0.4 10))
$touchChainPlates.Add((New-QuadPlate 862 0 4 0.2 0.5 10))
$touchChainPlates.Add((New-QuadPlate 863 0 4 0.5 0.9 10))
$touchChainCContour = $touchChainC.Contour | ForEach-Object { @($_.X, $_.Y, $_.Z) } | ConvertTo-Json -Compress
$touchChainWarning = ''
$touchChainOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $touchChainZones, $touchChainSources, $touchChainPatches, $touchChainPlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$touchChainWarning)
$touchChainCResolvedContour = $touchChainZones[2].Contour | ForEach-Object { @($_.X, $_.Y, $_.Z) } | ConvertTo-Json -Compress
Assert ($touchChainOk -and $touchChainCContour -eq $touchChainCResolvedContour -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $touchChainPlates, $touchChainZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "An exactly touching zone must not be dragged into a separate overlap group's width packing. ok=$touchChainOk warning='$touchChainWarning'"

$touchingArrangeZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$touchingArrangeA = New-TestZone 0 4 0.1 0.5 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$touchingArrangeB = New-TestZone 0 4 0.5 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 200
$touchingArrangeZones.Add($touchingArrangeA)
$touchingArrangeZones.Add($touchingArrangeB)
$touchingArrangeSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$touchingArrangePatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
foreach ($zone in $touchingArrangeZones) {
    $bounds = [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4,
        $zone.Contour[0].Y, $zone.Contour[2].Y)
    $touchingArrangeSources.Add($zone, $bounds)
    $touchingArrangePatches.Add($zone, $bounds)
}
$touchingArrangeOriginal = @($touchingArrangeZones | ForEach-Object {
    '{0:R}|{1:R}|{2:R}|{3:R}' -f $_.Contour[0].X, $_.Contour[0].Y,
        $_.Contour[2].X, $_.Contour[2].Y
})
$touchingArrangeWarning = ''
$touchingArrangeOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $touchingArrangeZones, $touchingArrangeSources, $touchingArrangePatches,
    [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new(),
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$touchingArrangeWarning)
$touchingArrangeAfter = @($touchingArrangeZones | ForEach-Object {
    '{0:R}|{1:R}|{2:R}|{3:R}' -f $_.Contour[0].X, $_.Contour[0].Y,
        $_.Contour[2].X, $_.Contour[2].Y
})
Assert ($touchingArrangeOk -and
        (($touchingArrangeOriginal -join '|') -eq ($touchingArrangeAfter -join '|'))) `
    "Zones that only touch and already have step-sized widths must not be moved during arrangement. ok=$touchingArrangeOk original='$($touchingArrangeOriginal -join ';')' after='$($touchingArrangeAfter -join ';')' warning='$touchingArrangeWarning'"

$widthNormalizeZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$widthNormalizeZone = New-TestZone 0 4 0.1 0.51 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$widthNormalizeZones.Add($widthNormalizeZone)
$widthNormalizeSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$widthNormalizePatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$widthNormalizeSupport = [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.1, 0.51)
$widthNormalizeSources.Add($widthNormalizeZone, $widthNormalizeSupport)
$widthNormalizePatches.Add($widthNormalizeZone, $widthNormalizeSupport)
$widthNormalizeWarning = ''
$widthNormalizeOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $widthNormalizeZones, $widthNormalizeSources, $widthNormalizePatches,
    [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new(),
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$widthNormalizeWarning)
Assert ($widthNormalizeOk -and [Math]::Abs($widthNormalizeZones[0].WidthMm - 600) -lt 1e-6 -and
        $widthNormalizeZones[0].Contour[0].Y -le 0.1 + 1e-9 -and
        $widthNormalizeZones[0].Contour[2].Y -ge 0.51 - 1e-9) `
    "Every zone must first grow to the next bar-step width while retaining its source bounds. width=$($widthNormalizeZones[0].WidthMm) warning='$widthNormalizeWarning'"

$vertexConflictZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$vertexConflictLeft = New-TestZone 0 6 0.1 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$vertexConflictRight = New-TestZone 0 6 0.3 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 100
$vertexConflictLeft.AsAdditional = 10
$vertexConflictRight.AsAdditional = 10
$vertexConflictZones.Add($vertexConflictLeft)
$vertexConflictZones.Add($vertexConflictRight)
$vertexConflictSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$vertexConflictPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$vertexConflictSources.Add($vertexConflictLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$vertexConflictSources.Add($vertexConflictRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.4, 0.8))
$vertexConflictPatches.Add($vertexConflictLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$vertexConflictPatches.Add($vertexConflictRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$vertexConflictPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$vertexConflictPlates.Add((New-QuadPlate 824 0 6 0 0.4 10))
$vertexConflictPlates.Add((New-QuadPlate 825 0 6 0.4 0.8 10))
$crossingTriangle = New-QuadPlate 826 0 6 0.1 0.8 10
$crossingTriangle.Contour.Clear()
$crossingTriangle.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 0.1, 0))
$crossingTriangle.Contour.Add([LiraSlabZones.Core.Point3]::new(6, 0.43, 0))
$crossingTriangle.Contour.Add([LiraSlabZones.Core.Point3]::new(0, 0.8, 0))
$crossingTriangle.Centroid = [LiraSlabZones.Core.Point3]::new(2, 0.4433333333, 0)
$vertexConflictPlates.Add($crossingTriangle)
$vertexConflictWarning = ''
$vertexConflictOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $vertexConflictZones, $vertexConflictSources, $vertexConflictPatches, $vertexConflictPlates,
    $gaplessSettings, $null, $null, 0.5, 0.2, [ref]$vertexConflictWarning)
$resolvedVertexLeft = $vertexConflictZones[0]
$resolvedVertexRight = $vertexConflictZones[1]
Assert ($vertexConflictOk -and
        $resolvedVertexLeft.Contour[2].Y -gt 0.43 -and
        $resolvedVertexLeft.Contour[2].Y -lt 0.8 -and
        [Math]::Abs($resolvedVertexLeft.Contour[2].Y - $resolvedVertexRight.Contour[0].Y) -lt 1e-9 -and
        [Math]::Abs($resolvedVertexLeft.WidthMm / $resolvedVertexLeft.BarStepMm -
                    [Math]::Round($resolvedVertexLeft.WidthMm / $resolvedVertexLeft.BarStepMm)) -lt 1e-9 -and
        [Math]::Abs($resolvedVertexRight.WidthMm / $resolvedVertexRight.BarStepMm -
                    [Math]::Round($resolvedVertexRight.WidthMm / $resolvedVertexRight.BarStepMm)) -lt 1e-9 -and
    ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $vertexConflictPlates, $vertexConflictZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "A peak finite element may be crossed away from its vertices when neighboring zones jointly cover it; resolving the conflict must preserve coverage and step widths. ok=$vertexConflictOk warning='$vertexConflictWarning' intervals=$($resolvedVertexLeft.Contour[0].Y)-$($resolvedVertexLeft.Contour[2].Y),$($resolvedVertexRight.Contour[0].Y)-$($resolvedVertexRight.Contour[2].Y) widths=$($resolvedVertexLeft.WidthMm)/$($resolvedVertexRight.WidthMm) uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($vertexConflictPlates, $vertexConflictZones, $gaplessSettings).UncoveredCount)"

$multiZonePlate = New-QuadPlate 827 0 6 0.1 0.9 10
$multiZoneZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$multiZoneZones.Add((New-TestZone 0 6 0.1 0.3 ([LiraSlabZones.Core.ZoneDirection]::X) 16 100))
$multiZoneZones.Add((New-TestZone 0 6 0.3 0.6 ([LiraSlabZones.Core.ZoneDirection]::X) 16 100))
$multiZoneZones.Add((New-TestZone 0 6 0.6 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 16 100))
$multiZonePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$multiZonePlates.Add($multiZonePlate)
$multiZoneDiagnostics = [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
    $multiZonePlates, $multiZoneZones, $gaplessSettings)
Assert ($multiZoneDiagnostics.UncoveredCount -eq 0) `
    'A peak FE crossed by multiple zone boundaries away from its vertices is covered by the union of zones with adequate As.'

$generatedVertexZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$generatedVertexZones.Add($vertexConflictLeft)
$generatedVertexZones.Add($vertexConflictRight)
$generatedVertexWarning = ''
$generatedVertexOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $generatedVertexZones, $vertexConflictSources, $vertexConflictPatches, $vertexConflictPlates,
    $gaplessSettings, $null, $null, 0.5, 0.2, [ref]$generatedVertexWarning)
Assert ($generatedVertexOk -and
        $generatedVertexZones[0].Contour[2].Y -gt 0.43 -and
        $generatedVertexZones[0].Contour[2].Y -lt 0.8 -and
        [Math]::Abs($generatedVertexZones[0].Contour[2].Y - $generatedVertexZones[1].Contour[0].Y) -lt 1e-9 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $vertexConflictPlates, $generatedVertexZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "Newly generated zones must use the same FE-crossing, touching-zone coverage rules as saved preview zones. ok=$generatedVertexOk warning='$generatedVertexWarning' zones=$($generatedVertexZones.Count) intervals=$(($generatedVertexZones | ForEach-Object { '{0:0.###}..{1:0.###}' -f $_.Contour[0].Y, $_.Contour[2].Y }) -join ';') uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($vertexConflictPlates, $generatedVertexZones, $gaplessSettings).UncoveredCount)"

$infeasibleMinZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$infeasibleMinLeft = New-TestZone 0 6 0.1 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 200
$infeasibleMinRight = New-TestZone 0.1 6.1 0.3 0.9 ([LiraSlabZones.Core.ZoneDirection]::X) 12 100
$infeasibleMinZones.Add($infeasibleMinLeft)
$infeasibleMinZones.Add($infeasibleMinRight)
$infeasibleMinSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$infeasibleMinPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$infeasibleMinSources.Add($infeasibleMinLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.4))
$infeasibleMinSources.Add($infeasibleMinRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0.4, 0.8))
$infeasibleMinPatches.Add($infeasibleMinLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$infeasibleMinPatches.Add($infeasibleMinRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, 0, 0.8))
$infeasibleMinWarning = ''
$infeasibleMinOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $infeasibleMinZones, $infeasibleMinSources, $infeasibleMinPatches, $longOverlapPlates,
    $gaplessSettings, $null, $null, 1.0, 0, [ref]$infeasibleMinWarning)
$infeasibleMinHasOverlap = $false
for ($i = 0; $i -lt $infeasibleMinZones.Count; $i++) {
    for ($j = $i + 1; $j -lt $infeasibleMinZones.Count; $j++) {
        $xOverlap = [Math]::Min(
            ($infeasibleMinZones[$i].Contour | Measure-Object X -Maximum).Maximum,
            ($infeasibleMinZones[$j].Contour | Measure-Object X -Maximum).Maximum) -
            [Math]::Max(
            ($infeasibleMinZones[$i].Contour | Measure-Object X -Minimum).Minimum,
            ($infeasibleMinZones[$j].Contour | Measure-Object X -Minimum).Minimum)
        $yOverlap = [Math]::Min(
            ($infeasibleMinZones[$i].Contour | Measure-Object Y -Maximum).Maximum,
            ($infeasibleMinZones[$j].Contour | Measure-Object Y -Maximum).Maximum) -
            [Math]::Max(
            ($infeasibleMinZones[$i].Contour | Measure-Object Y -Minimum).Minimum,
            ($infeasibleMinZones[$j].Contour | Measure-Object Y -Minimum).Minimum)
        if ($xOverlap -gt 1e-6 -and $yOverlap -gt 1e-6) { $infeasibleMinHasOverlap = $true }
    }
}
$infeasibleMinBadWidth = @($infeasibleMinZones | Where-Object {
    [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
}).Count -gt 0
Assert ($infeasibleMinOk -and -not $infeasibleMinHasOverlap -and -not $infeasibleMinBadWidth -and
        @($infeasibleMinZones | Where-Object LengthMm -gt 11700).Count -eq 0) `
    "When the minimum width exceeds the patch, the solver may extend/merge zones but must remove every overlap and keep widths step-sized and bars <=11700 mm. ok=$infeasibleMinOk overlap=$infeasibleMinHasOverlap badWidth=$infeasibleMinBadWidth warning='$infeasibleMinWarning' zones=$($infeasibleMinZones.Count)"

$atomicZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$atomicSpecs = @(
    @{ X0 = 0; X1 = 4; Y0 = 0.1; Y1 = 0.7; Support0 = 0; Support1 = 0.4; Patch0 = 0; Patch1 = 0.8; Id = 9351; Diameter = 16; Step = 200; Part = 1 },
    @{ X0 = 0; X1 = 4; Y0 = 0.3; Y1 = 0.9; Support0 = 0.4; Support1 = 0.8; Patch0 = 0; Patch1 = 0.8; Id = 9352; Diameter = 12; Step = 100; Part = 2 },
    @{ X0 = 10; X1 = 14; Y0 = 0.1; Y1 = 0.7; Support0 = 0; Support1 = 0.15; Patch0 = 0; Patch1 = 0.15; Id = 9353; Diameter = 16; Step = 200; Part = 1 },
    @{ X0 = 10; X1 = 14; Y0 = 0.3; Y1 = 0.9; Support0 = 0; Support1 = 0.15; Patch0 = 0; Patch1 = 0.15; Id = 9354; Diameter = 12; Step = 100; Part = 2 }
)
$atomicSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$atomicPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$atomicPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
foreach ($spec in $atomicSpecs) {
    $zone = New-TestZone $spec.X0 $spec.X1 $spec.Y0 $spec.Y1 `
        ([LiraSlabZones.Core.ZoneDirection]::X) $spec.Diameter $spec.Step
    $zone.AsAdditional = 10
    $zone.Comment = "часть $($spec.Part)/2"
    $zone.NodeIds.Add($spec.Id)
    $atomicZones.Add($zone)
    $atomicSources.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        $spec.X0, $spec.X1, $spec.Support0, $spec.Support1))
    $atomicPatches.Add($zone, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(
        $spec.X0, $spec.X1, $spec.Patch0, $spec.Patch1))
    $atomicPlates.Add((New-QuadPlate $spec.Id $spec.X0 $spec.X1 $spec.Y0 ($spec.Y0 + 0.1) 10))
    $atomicPlates.Add((New-QuadPlate ($spec.Id + 100) $spec.X0 $spec.X1 ($spec.Y1 - 0.1) $spec.Y1 10))
}
$atomicOriginalContours = @($atomicZones | ForEach-Object {
    '{0:R}|{1:R}|{2:R}|{3:R}' -f $_.Contour[0].X, $_.Contour[0].Y,
        $_.Contour[2].X, $_.Contour[2].Y
})
$atomicWarning = ''
$atomicOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryResolveExistingConflicts(
    $atomicZones, $atomicSources, $atomicPatches, $atomicPlates,
    $gaplessSettings, $null, $null, 0.2, 0, [ref]$atomicWarning)
$atomicResolvedContours = @($atomicZones | ForEach-Object {
    '{0:R}|{1:R}|{2:R}|{3:R}' -f $_.Contour[0].X, $_.Contour[0].Y,
        $_.Contour[2].X, $_.Contour[2].Y
})
$atomicHasOverlap = $false
for ($i = 0; $i -lt $atomicZones.Count; $i++) {
    for ($j = $i + 1; $j -lt $atomicZones.Count; $j++) {
        $xOverlap = [Math]::Min($atomicZones[$i].Contour[2].X, $atomicZones[$j].Contour[2].X) -
            [Math]::Max($atomicZones[$i].Contour[0].X, $atomicZones[$j].Contour[0].X)
        $yOverlap = [Math]::Min($atomicZones[$i].Contour[2].Y, $atomicZones[$j].Contour[2].Y) -
            [Math]::Max($atomicZones[$i].Contour[0].Y, $atomicZones[$j].Contour[0].Y)
        if ($xOverlap -gt 1e-6 -and $yOverlap -gt 1e-6) { $atomicHasOverlap = $true }
    }
}
$atomicBadWidth = @($atomicZones | Where-Object {
    [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
}).Count -gt 0
$atomicUncovered = [LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
    $atomicPlates, $atomicZones, $gaplessSettings).UncoveredCount
Assert ($atomicOk -and -not $atomicHasOverlap -and -not $atomicBadWidth -and
        $atomicUncovered -eq 0 -and
        @($atomicZones | Where-Object { $_.LengthMm -gt 11700 }).Count -eq 0) `
    "Groups whose normalized widths exceed their source patches must be arranged outside the patch, not rejected; overlap=$atomicHasOverlap badWidth=$atomicBadWidth uncovered=$atomicUncovered ok=$atomicOk warning='$atomicWarning'."

$cascadeZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$cascadeSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$cascadePatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$cascadePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$cascadeSpecs = @(
    @{ Id = 831; Min = 0.0; Max = 0.4; ZoneMin = 0.1; ZoneMax = 0.7; Diameter = 16; Step = 200 },
    @{ Id = 832; Min = 0.4; Max = 0.8; ZoneMin = 0.3; ZoneMax = 0.9; Diameter = 12; Step = 100 },
    @{ Id = 833; Min = 0.8; Max = 1.2; ZoneMin = 0.7; ZoneMax = 1.3; Diameter = 20; Step = 200 },
    @{ Id = 834; Min = 1.2; Max = 1.6; ZoneMin = 1.1; ZoneMax = 1.7; Diameter = 25; Step = 100 }
)
foreach ($spec in $cascadeSpecs) {
    $zone = New-TestZone 0 6 $spec.ZoneMin $spec.ZoneMax `
        ([LiraSlabZones.Core.ZoneDirection]::X) $spec.Diameter $spec.Step
    $zone.AsAdditional = 10
    $zone.NodeIds.Add($spec.Id)
    $cascadeZones.Add($zone)
    $cascadeSources.Add($zone,
        [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, $spec.Min, $spec.Max))
    $cascadePatches.Add($zone,
        [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 6, $spec.Min, $spec.Max))
    $cascadePlates.Add((New-QuadPlate $spec.Id 0 6 $spec.Min $spec.Max 10))
}
$cascadeWarning = ''
$cascadeOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $cascadeZones, $cascadeSources, $cascadePatches, $cascadePlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$cascadeWarning)
$cascadeHasOverlap = $false
for ($i = 0; $i -lt $cascadeZones.Count; $i++) {
    for ($j = $i + 1; $j -lt $cascadeZones.Count; $j++) {
        $cross = [Math]::Min($cascadeZones[$i].Contour[2].Y, $cascadeZones[$j].Contour[2].Y) -
            [Math]::Max($cascadeZones[$i].Contour[0].Y, $cascadeZones[$j].Contour[0].Y)
        $axial = [Math]::Min($cascadeZones[$i].Contour[1].X, $cascadeZones[$j].Contour[1].X) -
            [Math]::Max($cascadeZones[$i].Contour[0].X, $cascadeZones[$j].Contour[0].X)
        if ($cross -gt 1e-6 -and $axial -gt 1e-6) { $cascadeHasOverlap = $true }
    }
}
Assert ($cascadeOk -and -not $cascadeHasOverlap -and
        @($cascadeZones | Where-Object {
            [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
        }).Count -eq 0 -and
    ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $cascadePlates, $cascadeZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "A cascade of overlapping zones across distinct long-patch frames must resolve all same-strip intersections and preserve step multiples and coverage. ok=$cascadeOk warning='$cascadeWarning' zones=$($cascadeZones.Count) overlap=$cascadeHasOverlap uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($cascadePlates, $cascadeZones, $gaplessSettings).UncoveredCount) widths=$(($cascadeZones | ForEach-Object { [Math]::Round($_.WidthMm) }) -join ',')"

$absorbedZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$absorbedLeft = New-TestZone 0 4 0 0.4 ([LiraSlabZones.Core.ZoneDirection]::X) 25 300
$absorbedRight = New-TestZone 0 4 0.3 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 25 300
$absorbedLeft.AsAdditional = 10
$absorbedRight.AsAdditional = 10
$absorbedLeft.NodeIds.Add(851)
$absorbedRight.NodeIds.Add(852)
$absorbedZones.Add($absorbedLeft)
$absorbedZones.Add($absorbedRight)
$absorbedSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$absorbedPatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$absorbedSources.Add($absorbedLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.35))
$absorbedSources.Add($absorbedRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.35, 0.7))
$absorbedPatches.Add($absorbedLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.7))
$absorbedPatches.Add($absorbedRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.7))
$absorbedPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$absorbedPlates.Add((New-QuadPlate 851 0 4 0 0.35 10))
$absorbedPlates.Add((New-QuadPlate 852 0 4 0.35 0.7 10))
$absorbedWarning = ''
$absorbedOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $absorbedZones, $absorbedSources, $absorbedPatches, $absorbedPlates,
    $gaplessSettings, $null, $null, 0, 0.2, [ref]$absorbedWarning)
$absorbedOrdered = @($absorbedZones | Sort-Object { $_.Contour[0].Y })
$absorbedContinuous = $absorbedOrdered.Count -gt 0
for ($i = 0; $i -lt $absorbedOrdered.Count - 1; $i++) {
    $absorbedContinuous = $absorbedContinuous -and
        [Math]::Abs($absorbedOrdered[$i].Contour[2].Y - $absorbedOrdered[$i + 1].Contour[0].Y) -lt 1e-6
}
$absorbedStepSized = @($absorbedZones | Where-Object {
    [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
}).Count -eq 0
Assert ($absorbedOk -and $absorbedZones.Count -le 2 -and $absorbedContinuous -and
        $absorbedStepSized -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $absorbedPlates, $absorbedZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "An intersecting group with no vertex split may be resolved by step-width trimming or absorption, but must remain tightly packed and fully cover its FEs. ok=$absorbedOk warning='$absorbedWarning' zones=$($absorbedZones.Count) widths=$(($absorbedZones | ForEach-Object { $_.WidthMm }) -join '/') continuous=$absorbedContinuous uncovered=$([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate($absorbedPlates, $absorbedZones, $gaplessSettings).UncoveredCount)"

$impossiblePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$impossiblePlates.Add((New-QuadPlate 811 0 4 0 0.35 1))
$impossiblePlates.Add((New-QuadPlate 812 0 4 0.35 0.7 1))
$impossibleZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$impossibleLeft = New-TestZone 0 4 0 0.3 ([LiraSlabZones.Core.ZoneDirection]::X) 16 300
$impossibleRight = New-TestZone 0 4 0.4 0.7 ([LiraSlabZones.Core.ZoneDirection]::X) 16 300
$impossibleLeft.NodeIds.Add(811)
$impossibleRight.NodeIds.Add(812)
$impossibleZones.Add($impossibleLeft)
$impossibleZones.Add($impossibleRight)
$impossibleSources = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$impossiblePatches = [Collections.Generic.Dictionary[LiraSlabZones.Core.AdditionalZone,LiraSlabZones.Core.ZonePatchFrameBounds]]::new()
$impossibleSources.Add($impossibleLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.35))
$impossibleSources.Add($impossibleRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0.35, 0.7))
$impossiblePatches.Add($impossibleLeft, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.7))
$impossiblePatches.Add($impossibleRight, [LiraSlabZones.Core.ZonePatchFrameBounds]::new(0, 4, 0, 0.7))
$impossibleWarning = ''
$impossibleOk = [LiraSlabZones.Core.ZonePatchGaplessLayout]::TryArrange(
    $impossibleZones, $impossibleSources, $impossiblePatches, $impossiblePlates, $gaplessSettings,
    $null, $null, 0, 0.2, [ref]$impossibleWarning)
$resolvedImpossible = if ($impossibleZones.Count -gt 0) { $impossibleZones[0] } else { $null }
$resolvedImpossibleMinY = ($resolvedImpossible.Contour | Measure-Object Y -Minimum).Minimum
$resolvedImpossibleMaxY = ($resolvedImpossible.Contour | Measure-Object Y -Maximum).Maximum
$impossibleOrdered = @($impossibleZones | Sort-Object { $_.Contour[0].Y })
$impossibleContinuous = $impossibleOrdered.Count -gt 0
for ($i = 0; $i -lt $impossibleOrdered.Count - 1; $i++) {
    $impossibleContinuous = $impossibleContinuous -and
        [Math]::Abs($impossibleOrdered[$i].Contour[2].Y - $impossibleOrdered[$i + 1].Contour[0].Y) -lt 1e-6
}
$impossibleStepSized = @($impossibleZones | Where-Object {
    [Math]::Abs($_.WidthMm / $_.BarStepMm - [Math]::Round($_.WidthMm / $_.BarStepMm)) -gt 1e-9
}).Count -eq 0
Assert ($impossibleOk -and $impossibleZones.Count -le 2 -and
        $impossibleStepSized -and $impossibleContinuous -and
        ($impossibleZones | Measure-Object WidthMm -Sum).Sum -ge 700 -and
        ([LiraSlabZones.Core.ZoneLayoutDiagnostics]::Evaluate(
            $impossiblePlates, $impossibleZones, $gaplessSettings).UncoveredCount -eq 0)) `
    "The solver must trim or absorb intersecting step-width zones into a continuous span that covers FEs crossed away from vertices. ok=$impossibleOk zones=$($impossibleZones.Count) widths=$(($impossibleZones | ForEach-Object { $_.WidthMm }) -join '/') continuous=$impossibleContinuous bounds=$resolvedImpossibleMinY..$resolvedImpossibleMaxY warning='$impossibleWarning'."

$insideCell.AsAdditionalCm2PerM = 3
$settings.BgBottomDiameterMm = 0
$settings.SyncBackgroundAsFromBars()
$noBackgroundZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ($noBackgroundZones.Count -eq 0) 'A zone must not be sized when its background diameter is unknown.'
$settings.BgBottomDiameterMm = 12
$settings.SyncBackgroundAsFromBars()
$settings.ConcreteClass = ''
$noConcreteZones = [LiraSlabZones.Core.ZonePatchZoneBuilder]::Build($frameSelection, 4.5, $settings)
Assert ($noConcreteZones.Count -eq 0) 'A zone must not be generated when the concrete class is not selected.'

Write-Host 'PASS patches and frame zones: detail/reverse rules, bar sizing, unclipped edits, and compatible zone merging'
