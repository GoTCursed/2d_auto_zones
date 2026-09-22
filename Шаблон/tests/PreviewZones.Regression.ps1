param([string]$InputJson)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'src\LiraSlabZones.PreviewHost\bin\x64\Debug\net48'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
Add-Type -Path (Join-Path $build 'Newtonsoft.Json.dll')
Add-Type -Path (Join-Path $build 'Clipper2Lib.dll')
Add-Type -Path (Join-Path $build 'LiraSlabZones.Core.dll')
[void][Reflection.Assembly]::LoadFrom((Join-Path $build 'LiraSlabZones.PreviewHost.exe'))

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-ZoneRules($zones, [int]$backgroundDiameter) {
    foreach ($zone in $zones) {
        Assert ($zone.DiameterMm -ge $backgroundDiameter) "Zone $($zone.ZoneId) diameter is below background."
        Assert ($zone.BarStepMm -in @(100, 200)) "Zone $($zone.ZoneId) has unsupported spacing."
        $actualLengthMm = if ($zone.Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
            (($zone.Contour.X | Measure-Object -Maximum).Maximum - ($zone.Contour.X | Measure-Object -Minimum).Minimum) * 1000
        } else {
            (($zone.Contour.Y | Measure-Object -Maximum).Maximum - ($zone.Contour.Y | Measure-Object -Minimum).Minimum) * 1000
        }
        if ($zone.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight) {
            Assert ([Math]::Abs($actualLengthMm - $zone.LengthMm) -le 1.1) "Straight zone $($zone.ZoneId) label length differs from its contour."
        } else {
            $expectedBent = [LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(
                $actualLengthMm, $zone.VerticalLegMm, $zone.FamilyKind)
            Assert ([Math]::Abs($expectedBent - $zone.LengthMm) -le 1.1) "Bent zone $($zone.ZoneId) total length is wrong."
        }
    }
    for ($i = 0; $i -lt $zones.Count; $i++) {
        for ($j = $i + 1; $j -lt $zones.Count; $j++) {
            if ($zones[$i].Layer -ne $zones[$j].Layer) { continue }
            $a = $zones[$i].Contour
            $b = $zones[$j].Contour
            $overlapX = [Math]::Min(($a.X | Measure-Object -Maximum).Maximum, ($b.X | Measure-Object -Maximum).Maximum) -
                [Math]::Max(($a.X | Measure-Object -Minimum).Minimum, ($b.X | Measure-Object -Minimum).Minimum)
            $overlapY = [Math]::Min(($a.Y | Measure-Object -Maximum).Maximum, ($b.Y | Measure-Object -Maximum).Maximum) -
                [Math]::Max(($a.Y | Measure-Object -Minimum).Minimum, ($b.Y | Measure-Object -Minimum).Minimum)
            if ($overlapX -gt 0.000001 -and $overlapY -gt 0.000001) {
                $allowedMm = [LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($zones[$i], $zones[$j])
                $longOverlapMm = if ($zones[$i].Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
                    $overlapX * 1000
                } else {
                    $overlapY * 1000
                }
                Assert ($allowedMm -gt 0 -and ($longOverlapMm + 1) -ge $allowedMm) "Zones $($zones[$i].ZoneId) and $($zones[$j].ZoneId) have insufficient lap overlap: $([Math]::Round($longOverlapMm))/$allowedMm mm, lengths $($zones[$i].LengthMm)/$($zones[$j].LengthMm), placements $($zones[$i].Placement.X),$($zones[$i].Placement.Y) / $($zones[$j].Placement.X),$($zones[$j].Placement.Y), FE $($zones[$i].NodeIds -join ',') / $($zones[$j].NodeIds -join ',')."
                continue
            }
            $gapX = if ($overlapX -lt 0) { -1.0 * $overlapX } else { 0.0 }
            $gapY = if ($overlapY -lt 0) { -1.0 * $overlapY } else { 0.0 }
            $requiredGap = [Math]::Min($zones[$i].BarStepMm, $zones[$j].BarStepMm) / 1000.0
            $tooClose = if ($zones[$i].Direction -eq [LiraSlabZones.Core.ZoneDirection]::X) {
                $overlapX -gt 0.000001 -and $gapY -lt ($requiredGap - 0.000001)
            } else {
                $overlapY -gt 0.000001 -and $gapX -lt ($requiredGap - 0.000001)
            }
            Assert (-not $tooClose) "Zones $($zones[$i].ZoneId) and $($zones[$j].ZoneId) violate the $($requiredGap * 1000) mm gap: overlap=($overlapX,$overlapY), gap=($gapX,$gapY), steps=$($zones[$i].BarStepMm)/$($zones[$j].BarStepMm)."
        }
    }
}

$step200 = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $false)
$excluded = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $false, [int[]]@(16))
Assert ($excluded.DiameterMm -ne 16) 'Excluded diameter was selected.'
$mixed = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(11.0, 36, 12, $true)
$mixed200 = [LiraSlabZones.Core.BarCapacity]::SelectDiameterAndStep(6.0, 36, 12, $true)
Assert ($step200.Item1 -ge 12 -and $step200.Item2 -eq 200) 'Single-spacing mode must use 200 mm.'
Assert ($mixed.Item1 -ge 12 -and $mixed.Item2 -eq 100) 'Mixed-spacing mode did not select the economical 100 mm option.'
Assert ($mixed200.Item1 -ge 12 -and $mixed200.Item2 -eq 200) 'Mixed-spacing mode did not retain the economical 200 mm option.'
Write-Host 'PASS diameter floor and mixed 100/200 spacing selection'
$xDirection = [LiraSlabZones.Core.ZoneDirection]::X
$yDirection = [LiraSlabZones.Core.ZoneDirection]::Y
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As1, $false) -eq $xDirection) 'Normal As1 direction must be X.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As2, $false) -eq $yDirection) 'Normal As2 direction must be Y.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As3, $true) -eq $yDirection) 'Reversed As3 direction must be Y.'
Assert ([LiraSlabZones.Core.RebarTables]::DirectionForLayer([LiraSlabZones.Core.RebarLayer]::As4, $true) -eq $xDirection) 'Reversed As4 direction must be X.'
Write-Host 'PASS normal and reversed layer directions'

$editOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$editOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(10, 0, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(10, 10, 0))
$editOutline.Add([LiraSlabZones.Core.Point3]::new(0, 10, 0))
$editTemplate = [LiraSlabZones.Core.AdditionalZone]::new()
$editTemplate.Layer = [LiraSlabZones.Core.RebarLayer]::As1
$editTemplate.Direction = [LiraSlabZones.Core.ZoneDirection]::X
$editTemplate.DiameterMm = 16
$editTemplate.BarStepMm = 200
$edited = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, -1, 4, 2, 5, $editOutline)
Assert ($null -ne $edited -and (($edited.Contour.X | Measure-Object -Minimum).Minimum -ge -0.000001)) 'Clipper create did not trim the zone to the slab.'
Assert ([LiraSlabZones.Core.ZoneEditor]::Move($edited, 2, 1, $editOutline)) 'Clipper move failed.'
Assert ([LiraSlabZones.Core.ZoneEditor]::Resize($edited, 1, 7, 1, 7, $editOutline)) 'Clipper resize failed.'
$parts = [LiraSlabZones.Core.ZoneEditor]::Split($edited, 4, $true, $editOutline)
Assert ($parts.Count -eq 2) 'Clipper split did not produce two zones.'
$joined = [LiraSlabZones.Core.ZoneEditor]::Merge($parts[0], $parts[1], $editOutline)
Assert ($null -ne $joined -and $joined.Contour.Count -ge 4) 'Clipper merge failed.'
Write-Host 'PASS Clipper2 zone create, move, resize, split and merge'
$manual = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 1, 7, 2, 5, $editOutline)
[LiraSlabZones.Core.ZoneEditor]::SetDiameter($manual, 20)
Assert ($manual.DiameterMm -eq 20 -and $manual.AsCoveredCm2PerM -gt 0) 'Manual diameter was not applied.'
$edgeParts = [LiraSlabZones.Core.ZoneEditor]::SplitPerpendicularToEdge($manual, 4, 3.5, $false, $editOutline)
Assert ($edgeParts.Count -eq 2) 'Horizontal edge must create two zones by a vertical cut.'
Assert ([Math]::Abs($edgeParts[0].LengthM - 3) -lt 0.001) 'Vertical cut is not at the clicked X coordinate.'
$verticalParts = [LiraSlabZones.Core.ZoneEditor]::SplitPerpendicularToEdge($manual, 4, 3.5, $true, $editOutline)
Assert ($verticalParts.Count -eq 2) 'Vertical edge must create two zones by a horizontal cut.'
Assert ([LiraSlabZones.Core.ZoneEditor]::ResizeByDimensions($manual, 4000, 2000, $editOutline)) 'Manual dimensions were rejected.'
Assert ([Math]::Abs($manual.LengthMm - 4000) -lt 1 -and [Math]::Abs($manual.WidthMm - 2000) -lt 1) 'Manual dimensions were not applied.'
[LiraSlabZones.Core.ZoneEditor]::SetFamily($manual, [LiraSlabZones.Core.ZoneFamilyKind]::L, 'Custom SUM-31')
Assert ($manual.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::L -and $manual.FamilyFileName -eq 'Custom SUM-31') 'Manual family was not applied.'
Write-Host 'PASS manual diameter, edge split, dimensions and family'
$gapMoving = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 4, 6, 2, 4, $editOutline)
$gapFixed = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 6, 8, 2, 4, $editOutline)
$gapMoving.BarStepMm = 100
$gapFixed.BarStepMm = 200
$fixedMinBefore = ($gapFixed.Contour.X | Measure-Object -Minimum).Minimum
Assert ([LiraSlabZones.Core.ZoneEditor]::CreateGap($gapMoving, $gapFixed, $editOutline)) 'Manual gap creation failed.'
$movingMax = ($gapMoving.Contour.X | Measure-Object -Maximum).Maximum
$fixedMin = ($gapFixed.Contour.X | Measure-Object -Minimum).Minimum
Assert ([Math]::Abs(($fixedMin - $movingMax) - 0.1) -lt 0.001) 'Manual gap is not equal to the smaller 100 mm spacing.'
Assert ([Math]::Abs($fixedMin - $fixedMinBefore) -lt 0.000001) 'Reference zone moved while creating a gap.'
Write-Host 'PASS two-click gap uses the smaller zone spacing'
$holePlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
for ($hy = 0; $hy -lt 6; $hy++) {
    for ($hx = 0; $hx -lt 6; $hx++) {
        if (($hx -in @(2,3) -and $hy -in @(2,3)) -or ($hx -eq 4 -and $hy -eq 4)) { continue }
        $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
        $plate.Id = $hy * 6 + $hx + 1
        foreach ($point in @(@($hx,$hy),@(($hx+1),$hy),@(($hx+1),($hy+1)),@($hx,($hy+1)))) {
            $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
        }
        $holePlates.Add($plate)
    }
}
$holeOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($point in @(@(0,0),@(6,0),@(6,6),@(0,6))) {
    $holeOutline.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$detectedHoles = [LiraSlabZones.Core.SlabOpenings]::Detect($holePlates, $holeOutline)
Assert ($detectedHoles.Count -eq 1) "Expected only the 2x2 FE opening, got $($detectedHoles.Count)."
Assert ([Math]::Abs($detectedHoles[0].WidthM - 2) -lt 0.001 -and [Math]::Abs($detectedHoles[0].HeightM - 2) -lt 0.001) 'Opening dimensions are incorrect.'
Write-Host 'PASS opening detection ignores 1x1 FE gaps and keeps 2x2 FE gaps'
$openingSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$openingSettings.SlabThicknessMm = 200
$openingSettings.CoverTopMm = 25
$openingSettings.CoverBottomMm = 25
$crossing = [LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 0.5, 5.5, 0.5, 5.5, $holeOutline)
$holePieces = [LiraSlabZones.Core.ZoneEditor]::SplitAtOpenings(
    $crossing, $detectedHoles, $openingSettings)
Assert ($holePieces.Count -eq 4) 'Opening must split a crossing zone into four non-overlapping pieces.'
Assert (($holePieces | Where-Object FamilyKind -eq ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Unaffected bar lanes must remain straight.'
Assert (($holePieces | Where-Object FamilyKind -ne ([LiraSlabZones.Core.ZoneFamilyKind]::Straight)).Count -eq 2) 'Bars ending at the opening must use bent families.'
Assert (($holePieces | Where-Object { [LiraSlabZones.Core.ZoneEditor]::IntersectsOpening($_, $detectedHoles) }).Count -eq 0) 'A split piece still crosses the opening.'
Write-Host 'PASS opening splits bent end pieces and straight bypass pieces'
$holeResult = [LiraSlabZones.Core.AnalysisResult]::new()
$holeResult.Settings = $openingSettings
$holeResult.Outline = $holeOutline
$holeResult.Openings = $detectedHoles
$holeViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$holeViewport.SetData($holeResult, $openingSettings, $true, $false)
$privateInstance = [Reflection.BindingFlags]'NonPublic,Instance'
$holeViewport.GetType().GetMethod('BeginEdit', $privateInstance).Invoke($holeViewport, @()) | Out-Null
$holeResult.Zones.Add([LiraSlabZones.Core.ZoneEditor]::Create($editTemplate, 0.5, 5.5, 0.5, 5.5, $holeOutline))
$holeViewport.GetType().GetMethod('CommitEdits', $privateInstance).Invoke($holeViewport, @($null)) | Out-Null
Assert ($holeResult.Zones.Count -eq 4) 'Preview rejected a zone crossing an opening instead of splitting it.'
Assert ($holeViewport.UndoLastEdit() -and $holeResult.Zones.Count -eq 0) 'Undo did not restore the state before opening split.'
Write-Host 'PASS preview splits a crossing zone and supports undo'
$longZone = [LiraSlabZones.Core.AdditionalZone]::new()
$longZone.Direction = [LiraSlabZones.Core.ZoneDirection]::Y
$longZone.DiameterMm = 25
$longZone.BarStepMm = 200
$longZone.ConcreteClass = 'B40'
$longZone.FamilyKind = [LiraSlabZones.Core.ZoneFamilyKind]::PEqual
$longZone.VerticalLegMm = 150
$longZone.LengthMm = 12960
foreach ($point in @(@(0.5,0.5),@(1.5,0.5),@(1.5,13.16),@(0.5,13.16))) {
    $longZone.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$longZone.NodeIds.Add(1); $longZone.NodeIds.Add(2); $longZone.NodeIds.Add(3)
$longMosaic = [LiraSlabZones.Core.MosaicGrid]::new()
$longMosaic.PlateCentroids[1] = [LiraSlabZones.Core.Point3]::new(1, 1, 0)
$longMosaic.PlateCentroids[2] = [LiraSlabZones.Core.Point3]::new(1, 6, 0)
$longMosaic.PlateCentroids[3] = [LiraSlabZones.Core.Point3]::new(1, 12, 0)
$longOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
foreach ($point in @(@(0,0),@(4,0),@(4,14),@(0,14))) {
    $longOutline.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 0))
}
$longZones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$longZones.Add($longZone)
Assert ([LiraSlabZones.Core.RebarTables]::ExceedsMaxBarLength($longZone)) 'Overlong bent bar was not detected before splitting.'
$splitLong = [LiraSlabZones.Core.ZoneLayoutEngine].GetMethod('SplitOverlongZones', [Reflection.BindingFlags]'NonPublic,Static')
$splitLong.Invoke($null, @($longZones, $longMosaic, $longOutline, 0.0)) | Out-Null
Assert ($longZones.Count -eq 2) 'A 12660 mm bent zone was not divided.'
Assert (($longZones | Where-Object LengthMm -gt 11700).Count -eq 0) 'A divided bent zone still exceeds 11700 mm.'
Assert (($longZones | Where-Object { [LiraSlabZones.Core.RebarTables]::ExceedsMaxBarLength($_) }).Count -eq 0) 'Bent bar geometry still exceeds 11700 mm.'
$overlap = ([Math]::Min(($longZones[0].Contour.Y | Measure-Object -Maximum).Maximum, ($longZones[1].Contour.Y | Measure-Object -Maximum).Maximum) -
    [Math]::Max(($longZones[0].Contour.Y | Measure-Object -Minimum).Minimum, ($longZones[1].Contour.Y | Measure-Object -Minimum).Minimum)) * 1000
Assert ($overlap + 1 -ge (2 * [LiraSlabZones.Core.RebarTables]::LapLenMm('B40', 25))) 'Bent splice has insufficient overlap.'
Write-Host 'PASS 12660 mm bent zone splits into bars at most 11700 mm with tabular overlap'
Assert ([LiraSlabZones.Core.RebarTables]::PickFamilyLength(3460) -eq 3900) '3460 mm was not rounded up to the 3900 mm family length.'
Write-Host 'PASS family length rounds 3460 mm up to 3900 mm'
Assert ([LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(3460, 150, [LiraSlabZones.Core.ZoneFamilyKind]::L) -eq 3610) 'SUM-31 total length is wrong.'
Assert ([LiraSlabZones.Core.RebarTables]::BentBarTotalLengthMm(3460, 150, [LiraSlabZones.Core.ZoneFamilyKind]::PEqual) -eq 3760) 'SUM-32 total length is wrong.'
Write-Host 'PASS bent length = plan part + vertical legs'
$lapA = [LiraSlabZones.Core.AdditionalZone]::new()
$lapB = [LiraSlabZones.Core.AdditionalZone]::new()
$lapA.LengthMm = 11700; $lapA.DiameterMm = 12; $lapA.ConcreteClass = 'B40'
$lapB.LengthMm = 3900;  $lapB.DiameterMm = 16; $lapB.ConcreteClass = 'B40'
Assert ([LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($lapA, $lapB) -eq 2000) 'Allowed overlap must be two laps of the larger diameter.'
$lapA.LengthMm = 7800
Assert ([LiraSlabZones.Core.RebarTables]::AllowedZoneOverlapMm($lapA, $lapB) -eq 0) 'Overlap without a 11700 mm zone must be forbidden.'
Write-Host 'PASS 11700 zone overlap uses two laps of the larger diameter'

function Test-ZoneCoverage($plate, $zones) {
    foreach ($zone in $zones) {
        $minX = ($zone.Contour.X | Measure-Object -Minimum).Minimum
        $maxX = ($zone.Contour.X | Measure-Object -Maximum).Maximum
        $minY = ($zone.Contour.Y | Measure-Object -Minimum).Minimum
        $maxY = ($zone.Contour.Y | Measure-Object -Maximum).Maximum
        $dx = [Math]::Max(0, [Math]::Max($minX - $plate.Centroid.X, $plate.Centroid.X - $maxX))
        $dy = [Math]::Max(0, [Math]::Max($minY - $plate.Centroid.Y, $plate.Centroid.Y - $maxY))
        if ([Math]::Sqrt($dx * $dx + $dy * $dy) -le 0.01) {
            return $true
        }
    }
    return $false
}

function Test-PointCoverage($point, $zones) {
    foreach ($zone in $zones) {
        $minX = ($zone.Contour.X | Measure-Object -Minimum).Minimum
        $maxX = ($zone.Contour.X | Measure-Object -Maximum).Maximum
        $minY = ($zone.Contour.Y | Measure-Object -Minimum).Minimum
        $maxY = ($zone.Contour.Y | Measure-Object -Maximum).Maximum
        if ($point.X -ge ($minX - 0.01) -and $point.X -le ($maxX + 0.01) -and
            $point.Y -ge ($minY - 0.01) -and $point.Y -le ($maxY + 0.01)) {
            return $true
        }
    }
    return $false
}

function Assert-ZonesInsideOutline($zones, $outline) {
    foreach ($zone in $zones) {
        foreach ($point in $zone.Contour) {
            $x = $point.X + ($zone.Placement.X - $point.X) * 0.001
            $y = $point.Y + ($zone.Placement.Y - $point.Y) * 0.001
            Assert ([LiraSlabZones.Core.MeshBoundary]::PointInPolygon($x, $y, $outline)) "Zone $($zone.ZoneId) extends outside the slab outline."
        }
    }
}

function New-PolygonPlate([int]$id, [double[][]]$xy, [double]$as3) {
    $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
    $plate.Id = $id
    $plate.TypeCode = if ($xy.Count -eq 3) { 44 } else { 42 }
    foreach ($point in $xy) {
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($point[0], $point[1], 3.0))
    }
    $plate.Centroid = [LiraSlabZones.Core.Point3]::new(
        ($plate.Contour.X | Measure-Object -Average).Average,
        ($plate.Contour.Y | Measure-Object -Average).Average,
        3.0)
    $width = 0.0
    $length = 0.0
    [LiraSlabZones.Core.ContourFix]::EdgeAlignedSize($plate.Contour, [ref]$width, [ref]$length)
    $plate.WidthM = $width
    $plate.LengthM = $length
    $plate.Rebar.Ok = $true
    $plate.Rebar.As3 = $as3
    return $plate
}

function Assert-PolygonApproximation([string]$name, $plates, $settings, $outline) {
    $zones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($plates, $settings, $null, $outline, $null)
    Assert ($zones.Count -eq 1) "${name}: expected one approximating zone, got $($zones.Count)."
    $zone = $zones[0]
    Assert ($zone.Contour.Count -eq 4) "${name}: approximating zone is not rectangular."
    foreach ($plate in $plates) {
        Assert ($zone.NodeIds -contains $plate.Id) "${name}: FE $($plate.Id) is absent from the zone."
        foreach ($point in $plate.Contour) {
            Assert (Test-PointCoverage $point @($zone)) "${name}: vertex of FE $($plate.Id) is outside the zone."
        }
    }
    Write-Host "PASS $name`: $($plates.Count) polygon FE -> 1 rectangle, $($zone.WidthMm)x$($zone.LengthMm) mm"
}

$demo = [LiraSlabZones.Core.DemoSlabFactory]::Create($null)
$expected = $demo.Zones.Count
Assert ($expected -gt 0) 'Demo has no zones.'

# Reproduce old LIRA exports: contour sorted, node IDs still in FE order.
foreach ($plate in $demo.Plates) {
    $swap = $plate.NodeIds[2]
    $plate.NodeIds[2] = $plate.NodeIds[3]
    $plate.NodeIds[3] = $swap
}
$rebuilt = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($demo, 3, $demo.Settings)
Assert ($rebuilt.Zones.Count -eq $expected) 'Legacy node order changed the zone count.'
Write-Host "PASS legacy contour: $expected zones"

# Аппроксимация пятна: три активных КЭ 400x400 мм образуют букву "Г".
# Ожидается один охватывающий прямоугольник, а не три зоны по одному КЭ.
$approxSettings = [LiraSlabZones.Core.AnalysisSettings]::new()
$approxSettings.ShowAs1 = $false
$approxSettings.ShowAs2 = $false
$approxSettings.ShowAs3 = $true
$approxSettings.ShowAs4 = $false
$approxSettings.SlabSelected = $true
$approxSettings.BgBottomDiameterMm = 12
$approxSettings.BgBottomStepMm = 200
$approxSettings.BgTopDiameterMm = 12
$approxSettings.BgTopStepMm = 200
$approxSettings.ConcreteClass = 'B40'
$approxSettings.GridCellMm = 400
$approxSettings.DetailSlider = 1.0
$approxSettings.UseBarStep100 = $true
$approxSettings.MinZoneWidthM = 1.2
$approxSettings.SyncBackgroundAsFromBars()

$approxPlates = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$activeIds = @(15, 16, 21) # (2,2), (3,2), (2,3) в сетке 6x6
$elementId = 1
for ($iy = 0; $iy -lt 6; $iy++) {
    for ($ix = 0; $ix -lt 6; $ix++) {
        $x0 = $ix * 0.4
        $y0 = $iy * 0.4
        $plate = [LiraSlabZones.Core.LiraPlateElement]::new()
        $plate.Id = $elementId
        $plate.TypeCode = 42
        $plate.Centroid = [LiraSlabZones.Core.Point3]::new($x0 + 0.2, $y0 + 0.2, 3.0)
        $plate.WidthM = 0.4
        $plate.LengthM = 0.4
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0,       $y0,       3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0 + 0.4, $y0,       3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0 + 0.4, $y0 + 0.4, 3.0))
        $plate.Contour.Add([LiraSlabZones.Core.Point3]::new($x0,       $y0 + 0.4, 3.0))
        $plate.Rebar.Ok = $true
        $plate.Rebar.As3 = if ($activeIds -contains $elementId) {
            $approxSettings.AsMainAs3 + 4.0
        } else {
            $approxSettings.AsMainAs3
        }
        $approxPlates.Add($plate)
        $elementId++
    }
}

$approx = [LiraSlabZones.Core.SlabZoneAnalyzer]::BuildResult(
    'APPROX_L_SHAPE', '(synthetic)', 49, $approxPlates, $approxSettings,
    $null, 3.0, 'Z = 3.000 m', $true, $null)
Assert ($approx.Zones.Count -eq 1) 'L-shaped spot was not approximated by one zone.'
$approxZone = $approx.Zones[0]
Assert ($approxZone.Contour.Count -eq 4) 'Approximated zone is not rectangular.'
Assert ($approxZone.NodeIds.Count -eq 3) 'Approximated zone lost or added active FE.'
Assert (($activeIds | Where-Object { $_ -notin $approxZone.NodeIds }).Count -eq 0) 'Approximated zone does not contain every active FE.'
Assert ($approxZone.WidthMm -ge 1199) 'Approximated zone does not satisfy the 1200 mm minimum width.'
foreach ($id in $activeIds) {
    $plate = $approxPlates | Where-Object { $_.Id -eq $id }
    Assert (Test-ZoneCoverage $plate @($approxZone)) "Approximated zone does not cover FE $id."
}
Write-Host "PASS L-shape approximation: 3 FE -> 1 rectangle, $($approxZone.WidthMm)x$($approxZone.LengthMm) mm"

$approxSettings.ReverseZoneDirections = $true
$reversedApprox = [LiraSlabZones.Core.SlabZoneAnalyzer]::BuildResult(
    'APPROX_L_SHAPE_REVERSED', '(synthetic)', 49, $approxPlates, $approxSettings,
    $null, 3.0, 'Z = 3.000 m', $true, $null)
Assert ($reversedApprox.Zones.Count -eq 1) 'Reversed L-shaped spot changed the zone count.'
Assert ($reversedApprox.Zones[0].Direction -eq $yDirection) 'Reversed As3 zone was not laid along Y.'
foreach ($id in $activeIds) {
    $plate = $approxPlates | Where-Object { $_.Id -eq $id }
    Assert (Test-ZoneCoverage $plate @($reversedApprox.Zones[0])) "Reversed zone does not cover FE $id."
}
$approxSettings.ReverseZoneDirections = $false
Write-Host 'PASS reversed As3 layout uses Y and retains FE coverage'

$testOutline = [Collections.Generic.List[LiraSlabZones.Core.Point3]]::new()
$testOutline.Add([LiraSlabZones.Core.Point3]::new(0, 0, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(4, 0, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(4, 4, 3))
$testOutline.Add([LiraSlabZones.Core.Point3]::new(0, 4, 3))
$activeAs3 = $approxSettings.AsMainAs3 + 4.0

# Два треугольных КЭ составляют один квадрат и должны дать одну зону.
$trianglePair = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$trianglePair.Add((New-PolygonPlate 101 @(@(1.2,1.2), @(2.0,1.2), @(2.0,2.0)) $activeAs3))
$trianglePair.Add((New-PolygonPlate 102 @(@(1.2,1.2), @(2.0,2.0), @(1.2,2.0)) $activeAs3))
Assert-PolygonApproximation 'triangle pair' $trianglePair $approxSettings $testOutline

# Поворотный четырёхугольник пересекает несколько ячеек мозаики.
$rotatedQuad = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$rotatedQuad.Add((New-PolygonPlate 201 @(@(0.8,2.0), @(2.0,1.1), @(3.2,2.0), @(2.0,2.9)) $activeAs3))
Assert-PolygonApproximation 'rotated quadrilateral' $rotatedQuad $approxSettings $testOutline

# Треугольник возле края: стандартная длина зоны должна сдвинуться внутрь плиты.
$edgeTriangle = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$edgeTriangle.Add((New-PolygonPlate 301 @(@(0.05,0.8), @(1.1,1.4), @(0.05,2.0)) $activeAs3))
Assert-PolygonApproximation 'edge triangle' $edgeTriangle $approxSettings $testOutline

# MinActiveElements считает окрашенные КЭ, а не число занятых ими ячеек мозаики.
$approxSettings.MinActiveElements = 2
$singleLarge = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($rotatedQuad, $approxSettings, $null, $testOutline, $null)
$twoElements = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($trianglePair, $approxSettings, $null, $testOutline, $null)
Assert ($singleLarge.Count -eq 0) 'One colored FE was counted as several raster cells.'
Assert ($twoElements.Count -gt 0) 'Two colored FE did not satisfy MinActiveElements=2.'
$approxSettings.MinActiveElements = 0
Write-Host 'PASS minimum FE counts unique colored finite elements'

# Три одинаковые соосные зоны с промежутком в один шаг объединяются в одну.
$aligned = [Collections.Generic.List[LiraSlabZones.Core.LiraPlateElement]]::new()
$aligned.Add((New-PolygonPlate 401 @(@(1.0,0.4), @(2.0,0.4), @(2.0,0.8), @(1.0,0.8)) $activeAs3))
$aligned.Add((New-PolygonPlate 402 @(@(1.0,1.0), @(2.0,1.0), @(2.0,1.4), @(1.0,1.4)) $activeAs3))
$aligned.Add((New-PolygonPlate 403 @(@(1.0,1.6), @(2.0,1.6), @(2.0,2.0), @(1.0,2.0)) $activeAs3))
$savedMinWidth = $approxSettings.MinZoneWidthM
$approxSettings.MinZoneWidthM = 0
$alignedZones = [LiraSlabZones.Core.ZoneLayoutEngine]::Layout($aligned, $approxSettings, $null, $testOutline, $null)
$approxSettings.MinZoneWidthM = $savedMinWidth
Assert ($alignedZones.Count -eq 1) "Compatible aligned zones were not merged: $($alignedZones.Count)."
Assert ($alignedZones[0].NodeIds.Count -eq 3) 'Merged aligned zone lost colored FE.'
Write-Host 'PASS compatible aligned zones: 3 zones -> 1'

$tempJson = Join-Path ([IO.Path]::GetTempPath()) ('slab-regression-' + [guid]::NewGuid() + '.json')
try {
    $upper = [LiraSlabZones.Core.DemoSlabFactory]::Create($null)
    foreach ($plate in $upper.Plates) {
        $plate.Centroid.Z += 3
        foreach ($point in $plate.Contour) { $point.Z = 6 }
    }
    $rebuilt.AllPlates.AddRange($upper.Plates)
    $rebuilt.AvailableLevels = [LiraSlabZones.Core.MeshBoundary]::CollectLevels($rebuilt.AllPlates, $null)
    [LiraSlabZones.Core.SlabZoneAnalyzer]::SaveAllPlatesJson($rebuilt, $tempJson)
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson, $demo.Settings)
    Assert ($loaded.Settings.GridCellMm -eq 1000) 'All-plates import did not detect the FE cell size.'
    Assert ($loaded.Zones.Count -gt 0) 'All-plates import did not calculate zones.'
    Assert ($loaded.AllPlates.Count -eq 192) 'All-plates import lost another level.'
    Assert ($loaded.AvailableLevels.Count -eq 2) 'All-plates import lost level metadata.'
    Assert ($loaded.Settings.BgBottomDiameterMm -eq 12) 'Import reset the background.'
    $other = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($loaded, 6, $loaded.Settings)
    Assert ($other.Zones.Count -gt 0) 'Changing level lost zones.'
    $blocked = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson)
    Assert ($blocked.Zones.Count -eq 0) 'Import bypassed required layout settings.'
    $importedZoneCount = $loaded.Zones.Count
    [LiraSlabZones.Core.SlabZoneAnalyzer]::SaveJson($loaded, $tempJson)
    $roundTrip = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($tempJson)
    Assert ($roundTrip.Zones.Count -eq $importedZoneCount) 'Zones JSON round trip lost zones.'
    Write-Host 'PASS JSON imports, settings, level switch and layout gate'
}
finally {
    Remove-Item -LiteralPath $tempJson -ErrorAction SilentlyContinue
}

if ($InputJson) {
    $settings = [LiraSlabZones.Core.AnalysisSettings]::new()
    $settings.SlabSelected = $true
    $settings.BgBottomDiameterMm = 12
    $settings.BgBottomStepMm = 200
    $settings.BgTopDiameterMm = 12
    $settings.BgTopStepMm = 200
    $settings.ConcreteClass = 'B25'
    $settings.TargetElevationZM = 55.7
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    Assert ($loaded.Zones.Count -gt 0) 'Provided JSON still has no zones.'
    Assert ([Math]::Abs($loaded.ElevationZM - 55.7) -lt 0.005) 'Wrong level selected.'
    Assert ($loaded.Settings.GridCellMm -eq 400) 'Provided JSON cell size was not detected as 400 mm.'
    $loaded.Settings.MinZoneWidthM = 0.8
    $wide = [LiraSlabZones.Core.SlabZoneAnalyzer]::RebuildForElevation($loaded, 55.7, $loaded.Settings)
    Assert ($wide.Zones.Count -gt 0) 'Minimum width removed all zones.'
    $narrowInterior = @($wide.Zones | Where-Object {
        $_.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and $_.WidthMm -lt 799
    })
    Assert ($narrowInterior.Count -eq 0) "Interior straight zone $($narrowInterior[0].ZoneId) is narrower than the requested minimum: $($narrowInterior[0].WidthMm) mm."
    Assert (($wide.Zones | ForEach-Object { $_.NodeIds.Count } | Measure-Object -Maximum).Maximum -gt 1) 'Connected cells were not merged.'
    $loaded = $wide
    Write-Host "PASS provided JSON: $($loaded.Plates.Count) plates, $($loaded.Zones.Count) merged zones with min width 800 mm"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $false
    $settings.ShowAs3 = $true
    $settings.ShowAs4 = $false
    $settings.ConcreteClass = 'B40'
    $settings.UseBarStep100 = $true
    $settings.DetailSlider = 1.0
    $settings.MinZoneWidthM = 1.2
    $settings.TargetElevationZM = 63.2
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As3 - $loaded.Settings.AsMainAs3 -gt 0.01
    })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $loaded.Zones) })
    Assert ($loaded.Zones.Count -gt 1) 'Z=63.200 As3 collapsed to one zone.'
    Assert (($loaded.Zones | Where-Object {
        $_.FamilyKind -eq [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and $_.WidthMm -lt 1199
    }).Count -eq 0) 'Z=63.200 has an interior straight zone narrower than 1200 mm.'
    if ($uncovered.Count -gt 0) {
        Write-Host ('Uncovered FE: ' + (($uncovered | ForEach-Object {
            $plate = $_
            $distance = ($loaded.Zones | ForEach-Object {
                $minX = ($_.Contour.X | Measure-Object -Minimum).Minimum
                $maxX = ($_.Contour.X | Measure-Object -Maximum).Maximum
                $minY = ($_.Contour.Y | Measure-Object -Minimum).Minimum
                $maxY = ($_.Contour.Y | Measure-Object -Maximum).Maximum
                $dx = [Math]::Max(0, [Math]::Max($minX - $plate.Centroid.X, $plate.Centroid.X - $maxX))
                $dy = [Math]::Max(0, [Math]::Max($minY - $plate.Centroid.Y, $plate.Centroid.Y - $maxY))
                [Math]::Sqrt($dx * $dx + $dy * $dy)
            } | Measure-Object -Minimum).Minimum
            "$($_.Id) ($($distance.ToString('0.000000')))"
        }) -join ', '))
    }
    Assert ($uncovered.Count -eq 0) 'Z=63.200 has positive As3 values outside all zones.'
    Write-Host "PASS Z=63.200 As3: $($loaded.Zones.Count) zones cover all $($active.Count) positive FE"

    $settings.ShowAs3 = $false
    $settings.ShowAs4 = $true
    $settings.DetailSlider = 0.0
    $settings.MinZoneWidthM = 0
    $settings.TargetElevationZM = 66.95
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As4 - $loaded.Settings.AsMainAs4 -gt 0.01
    })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $loaded.Zones) })
    Assert ($active.Count -eq 68) 'Z=66.950 As4 active FE count changed.'
    Assert ($loaded.Zones.Count -gt 0) 'Z=66.950 As4 produced zero zones.'
    if ($uncovered.Count -gt 0) {
        Write-Host ('Z=66.950 uncovered: ' + (($uncovered | ForEach-Object {
            "$($_.Id) ($($_.Centroid.X.ToString('0.000')),$($_.Centroid.Y.ToString('0.000')))"
        }) -join ', '))
    }
    Assert ($uncovered.Count -eq 0) 'Z=66.950 has positive As4 values outside all non-overlapping zones.'
    Assert-ZonesInsideOutline $loaded.Zones $loaded.Outline
    Assert-ZoneRules $loaded.Zones 12
    Write-Host "PASS Z=66.950 As4 Max: $($loaded.Zones.Count) non-overlapping zones for $($active.Count) positive FE"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $false
    $settings.ShowAs3 = $true
    $settings.ShowAs4 = $false
    $settings.DetailSlider = 0.25
    $settings.UseBarStep100 = $true
    $settings.TargetElevationZM = 48.2
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As3 - $loaded.Settings.AsMainAs3 -gt 0.01
    })
    $layerZones = @($loaded.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As3 })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $layerZones) })
    Assert ($layerZones.Count -gt 0) 'Z=48.200 As3 detail 4 produced zero zones.'
    Assert ($layerZones.Count -gt 1) 'Z=48.200 detail 4 collapsed into one oversized zone.'
    Assert-ZoneRules $layerZones 12
    Write-Host "PASS Z=48.200 As3 detail 4: $($layerZones.Count) separated zones; $($uncovered.Count) FE centroids lie in mandatory gaps"

    $settings.ShowAs1 = $false
    $settings.ShowAs2 = $true
    $settings.ShowAs3 = $false
    $settings.ShowAs4 = $false
    $settings.DetailSlider = 0
    $settings.UseBarStep100 = $false
    $settings.MinZoneWidthM = 0.4
    $settings.TargetElevationZM = 29.45
    $loaded = [LiraSlabZones.Core.SlabZoneAnalyzer]::LoadJson($InputJson, $settings)
    $active = @($loaded.Plates | Where-Object {
        $_.Rebar.Ok -and $_.Rebar.As2 - $loaded.Settings.AsMainAs2 -gt 0.01
    })
    $layerZones = @($loaded.Zones | Where-Object { $_.Layer -eq [LiraSlabZones.Core.RebarLayer]::As2 })
    $uncovered = @($active | Where-Object { -not (Test-ZoneCoverage $_ $layerZones) })
    if ($uncovered.Count -gt 0) {
        $ux = $uncovered | ForEach-Object { $_.Centroid.X } | Measure-Object -Minimum -Maximum
        $uy = $uncovered | ForEach-Object { $_.Centroid.Y } | Measure-Object -Minimum -Maximum
        Write-Host ("Z=29.450 uncovered bounds: X={0:0.000}..{1:0.000}, Y={2:0.000}..{3:0.000}; sample IDs: {4}" -f `
            $ux.Minimum, $ux.Maximum, $uy.Minimum, $uy.Maximum,
            (($uncovered | Select-Object -First 12 | ForEach-Object { $_.Id }) -join ', '))
        $knownIds = @($layerZones | ForEach-Object { $_.NodeIds } | Select-Object -Unique)
        $uncoveredKnown = @($uncovered | Where-Object { $_.Id -in $knownIds })
        Write-Host "Z=29.450 uncovered IDs referenced by zones: $($uncoveredKnown.Count)/$($uncovered.Count); zones: $($layerZones.Count)"
    }
    Assert ($uncovered.Count -eq 0) "Z=29.450 As2 has $($uncovered.Count) colored FE without zones."
    $bentEdgeAs2 = @($layerZones | Where-Object {
        $_.FamilyKind -ne [LiraSlabZones.Core.ZoneFamilyKind]::Straight -and
        $_.CountBars -and -not $_.CountInSpec
    })
    Assert ($bentEdgeAs2.Count -gt 0) 'As2 has no SUM-31...SUM-34 zones at the slab edge.'
    Assert-ZonesInsideOutline $layerZones $loaded.Outline
    Assert-ZoneRules $layerZones 12
    Write-Host "PASS Z=29.450 As2 Max: $($layerZones.Count) zones cover all $($active.Count) colored FE; $($bentEdgeAs2.Count) bent edge zones"

}

function Render-Preview($result) {
    $viewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
    $viewport.Width = 1000
    $viewport.Height = 700
    $viewport.Measure([Windows.Size]::new(1000, 700))
    $viewport.Arrange([Windows.Rect]::new(0, 0, 1000, 700))
    $viewport.SetData($result, $result.Settings, $true, $false, $false, $true)
    $viewport.UpdateLayout()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(1000, 700, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($viewport)
    return $bitmap
}

$withZones = Render-Preview $loaded
$undoViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$undoViewport.SetData($loaded, $loaded.Settings, $true, $false)
$selectedField = $undoViewport.GetType().GetField('_selectedZoneId', [Reflection.BindingFlags]'NonPublic,Instance')
$selectedField.SetValue($undoViewport, $loaded.Zones[0].ZoneId)
$originalFamily = $loaded.Zones[0].FamilyFileName
for ($editIndex = 1; $editIndex -le 51; $editIndex++) {
    Assert ($undoViewport.SetSelectedFamily([LiraSlabZones.Core.ZoneFamilyKind]::Straight, "UndoTest-$editIndex")) 'Undo setup edit failed.'
}
for ($editIndex = 0; $editIndex -lt 50; $editIndex++) {
    Assert ($undoViewport.UndoLastEdit()) "Undo action $editIndex failed."
}
Assert ($loaded.Zones[0].FamilyFileName -eq 'UndoTest-1') 'Fifty-level undo did not retain the oldest edit.'
Assert (-not $undoViewport.UndoLastEdit()) 'Undo exceeded its fifty-action limit.'
$loaded.Zones[0].FamilyFileName = $originalFamily
Write-Host 'PASS undo restores at most fifty manual edits'
$edgeZone = $loaded.Zones[0]
$originalZones = $loaded.Zones
$loaded.Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$loaded.Zones.Add($edgeZone)
$edgeViewport = [LiraSlabZones.Revit2023.UI.PreviewViewport]::new()
$edgeViewport.Width = 1000; $edgeViewport.Height = 700
$edgeViewport.Measure([Windows.Size]::new(1000, 700))
$edgeViewport.Arrange([Windows.Rect]::new(0, 0, 1000, 700))
$edgeViewport.SetData($loaded, $loaded.Settings, $true, $false)
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$bounds = $edgeZone.Contour
$minX = ($bounds.X | Measure-Object -Minimum).Minimum
$maxX = ($bounds.X | Measure-Object -Maximum).Maximum
$midY = (($bounds.Y | Measure-Object -Minimum).Minimum + ($bounds.Y | Measure-Object -Maximum).Maximum) / 2
$hitMethod = $edgeViewport.GetType().GetMethod('HitResizeEdge', $flags)
$hit = $hitMethod.Invoke($edgeViewport, @([LiraSlabZones.Core.Point3]::new($maxX, $midY, $edgeZone.LevelZM)))
Assert ($hit.Item1 -eq $edgeZone -and $hit.Item2.ToString() -eq 'Right') 'Right zone edge was not detected for dragging.'
$edgeViewport.GetType().GetMethod('BeginEdit', $flags).Invoke($edgeViewport, @())
$edgeField = $edgeViewport.GetType().GetField('_resizeEdge', $flags)
$edgeField.SetValue($edgeViewport, [Enum]::Parse($edgeField.FieldType, 'Right'))
$boundsField = $edgeViewport.GetType().GetField('_resizeBounds', $flags)
$minY = ($bounds.Y | Measure-Object -Minimum).Minimum
$maxY = ($bounds.Y | Measure-Object -Maximum).Maximum
$boundsField.SetValue($edgeViewport, [ValueTuple[double,double,double,double]]::new($minX,$maxX,$minY,$maxY))
$dragMethod = $edgeViewport.GetType().GetMethod('ResizeDraggedEdge', $flags)
Assert ($dragMethod.Invoke($edgeViewport, @($edgeZone, [LiraSlabZones.Core.Point3]::new($maxX - 0.1, $midY, $edgeZone.LevelZM)))) 'Dragging right edge failed.'
Assert ([Math]::Abs((($edgeZone.Contour.X | Measure-Object -Minimum).Minimum) - $minX) -lt 0.001) 'Dragging right edge moved the opposite edge.'
$loaded.Zones = $originalZones
Write-Host 'PASS dragging a zone edge keeps the opposite edge fixed'
$savedZones = $loaded.Zones
$loaded.Zones = [Collections.Generic.List[LiraSlabZones.Core.AdditionalZone]]::new()
$withoutZones = Render-Preview $loaded
$loaded.Zones = $savedZones
$a = New-Object byte[] (1000 * 700 * 4)
$b = New-Object byte[] $a.Length
$withZones.CopyPixels($a, 4000, 0)
$withoutZones.CopyPixels($b, 4000, 0)
$changed = 0
for ($i = 0; $i -lt $a.Length; $i += 4) {
    if ($a[$i] -ne $b[$i] -or $a[$i+1] -ne $b[$i+1] -or $a[$i+2] -ne $b[$i+2]) { $changed++ }
}
Assert ($changed -gt 100) 'Zones produce no visible pixels in the WPF viewport.'
$imagePath = Join-Path ([IO.Path]::GetTempPath()) 'LiraSlabZones-preview-regression.png'
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($withZones))
$stream = [IO.File]::Create($imagePath)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Host "PASS WPF rendering: $changed changed pixels; $imagePath"
