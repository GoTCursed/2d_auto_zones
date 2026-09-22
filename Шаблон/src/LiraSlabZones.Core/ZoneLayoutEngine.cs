using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    /// <summary>
    /// Автораскладка зон доп. армирования по мозаике As−фон (см²/м).
    /// Длина = пятно + 2×анкеровка → SUM-3 вверх; ширина кратна шагу и покрывает пятно;
    /// соседние зоны с зазором = шаг стержней перпендикулярно длине.
    /// </summary>
    public static class ZoneLayoutEngine
    {
        public static List<AdditionalZone> Layout(
            IList<LiraPlateElement> plates,
            AnalysisSettings settings,
            IList<OpeningInfo>? openings = null,
            IList<Point3>? outline = null,
            IList<ConstructionAxis>? axes = null)
        {
            if (!settings.AutoLayout ||
                string.Equals(settings.PlacementMode, "ElementCenter", StringComparison.OrdinalIgnoreCase))
            {
                return SlabZoneAnalyzer.BuildZonesPerElement(plates, settings);
            }

            settings.SyncBackgroundAsFromBars();
            if (!settings.CanLayoutAdditionalZones(out _))
                return new List<AdditionalZone>();

            settings.DetailLevel = DetailOptimizer.FromSlider(settings.DetailSlider);
            settings.BarStepMm = 200;

            var zones = new List<AdditionalZone>();
            var zoneId = 1;
            var levelZM = plates.Count > 0 ? plates.Average(p => p.Centroid.Z) : 0;
            openings ??= Array.Empty<OpeningInfo>();

            var layers = new (RebarLayer Layer, bool Show, double AsMain)[]
            {
                (RebarLayer.As1, settings.ShowAs1, settings.AsMainAs1),
                (RebarLayer.As2, settings.ShowAs2, settings.AsMainAs2),
                (RebarLayer.As3, settings.ShowAs3, settings.AsMainAs3),
                (RebarLayer.As4, settings.ShowAs4, settings.AsMainAs4)
            };

            foreach (var (layer, show, asMain) in layers)
            {
                if (!show) continue;
                var mosaic = MosaicBuilder.Build(plates, layer, asMain, settings.GridCellMm, levelZM);
                if (mosaic.Nx == 0 || mosaic.Ny == 0) continue;

                zones.AddRange(LayoutLayer(mosaic, layer, settings, openings, outline, axes, ref zoneId));
            }

            return zones;
        }

        private static List<AdditionalZone> LayoutLayer(
            MosaicGrid mosaic,
            RebarLayer layer,
            AnalysisSettings settings,
            IList<OpeningInfo> openings,
            IList<Point3>? outline,
            IList<ConstructionAxis>? axes,
            ref int zoneId)
        {
            var direction = RebarTables.DirectionForLayer(layer, settings.ReverseZoneDirections);
            var detailStep = DetailOptimizer.StepIndexFromSlider(settings.DetailSlider);
            var values = detailStep == DetailOptimizer.StepCount - 1
                ? mosaic.Values
                : MosaicBuilder.SmoothSingleSpike(mosaic.Values);
            var ny = mosaic.Ny;
            var nx = mosaic.Nx;
            var cellMm = mosaic.CellMm;
            var thresholdRatio = DetailOptimizer.ThresholdRatioForSlider(settings.DetailSlider);
            var concrete = RebarTables.NormalizeConcrete(settings.ConcreteClass);
            var maxD = settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36;
            var minFamilyLen = RebarTables.Sum3FamilyLengthsMm[0];
            // Лимиты из UI (мм). 0 = без ограничения. Поддержка «старых» значений ≥50, сохранённых как «м».
            var minWidthMm = SettingToMm(settings.MinZoneWidthM);
            var maxWidthMm = SettingToMm(settings.MaxZoneWidthM);
            var minLengthMm = SettingToMm(settings.MinZoneLengthM);
            if (minLengthMm > minFamilyLen) minFamilyLen = (int)Math.Round(minLengthMm);
            var minFe = settings.MinActiveElements;
            var peaks = new List<(double V, int Ix, int Iy)>();
            for (var iy = 0; iy < ny; iy++)
            for (var ix = 0; ix < nx; ix++)
                if (values[iy][ix] > 0.01)
                    peaks.Add((values[iy][ix], ix, iy));
            // Все положительные ячейки являются кандидатами. После обработки высоких
            // диапазонов оставшиеся низкие значения также должны получить покрытие.
            peaks.Sort((a, b) => b.V.CompareTo(a.V));

            var assigned = new bool[ny, nx];
            var result = new List<AdditionalZone>();
            // Уже размещённые AABB (м) для контроля зазора/перекрытий
            var placed = new List<(double MinX, double MaxX, double MinY, double MaxY, int StepMm)>();

            foreach (var (vPeak, ix0, iy0) in peaks)
            {
                if (assigned[iy0, ix0]) continue;

                var backgroundDiameter = GetBackgroundDiameter(settings, layer);
                var barOption = BarCapacity.SelectDiameterAndStep(
                    vPeak, maxD, backgroundDiameter, settings.UseBarStep100,
                    settings.ExcludedZoneDiametersMm?.ToArray());
                var dZone = barOption.DiameterMm;
                var step = barOption.StepMm;
                if (dZone <= 0) continue;

                // В режиме Min весь положительный связный диапазон образует одно пятно.
                // На остальных ступенях сохраняется градация относительно локального пика.
                var aThr = detailStep == DetailOptimizer.StepCount - 1
                    ? 0.01
                    : thresholdRatio * vPeak;
                var activeRegion = GrowConnectedRegion(
                    values, assigned, ix0, iy0, aThr,
                    out var iLeft, out var iRight, out var jDown, out var jUp);

                // Считаем уникальные окрашенные КЭ, а не ячейки регулярной мозаики:
                // один крупный/треугольный КЭ может занимать несколько ячеек.
                var activeElementCount = activeRegion
                    .SelectMany(c => mosaic.PlateIds[c.Iy][c.Ix])
                    .Distinct()
                    .Count();
                if (minFe > 0 && activeElementCount < minFe) continue;

                var x0 = mosaic.OriginXM + iLeft * (cellMm / 1000.0);
                var x1 = mosaic.OriginXM + (iRight + 1) * (cellMm / 1000.0);
                var y0 = mosaic.OriginYM + jDown * (cellMm / 1000.0);
                var y1 = mosaic.OriginYM + (jUp + 1) * (cellMm / 1000.0);
                // Центр пика (ячейка) — зона обязана его покрывать после всех сдвигов
                var peakXM = mosaic.OriginXM + (ix0 + 0.5) * (cellMm / 1000.0);
                var peakYM = mosaic.OriginYM + (iy0 + 0.5) * (cellMm / 1000.0);

                double longStartM, longEndM, spanPerpMm, corePerp0, corePerp1;
                if (direction == ZoneDirection.X)
                {
                    longStartM = x0;
                    longEndM = x1;
                    spanPerpMm = (jUp - jDown + 1) * cellMm;
                    corePerp0 = y0;
                    corePerp1 = y1;
                }
                else
                {
                    longStartM = y0;
                    longEndM = y1;
                    spanPerpMm = (iRight - iLeft + 1) * cellMm;
                    corePerp0 = x0;
                    corePerp1 = x1;
                }

                var ancMm = RebarTables.AnchorageLenMm(concrete, dZone);
                // Пятно + анкеровка; у края плиты длина укоротится клипом (как SmartRebar)
                var longStartMm = UnitConversion.MetersToMm(longStartM) - ancMm;
                var longEndMm = UnitConversion.MetersToMm(longEndM) + ancMm;

                var (barCount, widthMm) = BarCapacity.BarsForSpanAndAs(vPeak, dZone, step, spanPerpMm);
                if (widthMm + 1e-6 < spanPerpMm)
                {
                    barCount = Math.Max(2, (int)Math.Ceiling(spanPerpMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                }
                // Ограничение макс. ширины (как SmartRebar)
                if (maxWidthMm > 0 && widthMm > maxWidthMm)
                {
                    barCount = Math.Max(2, (int)Math.Floor(maxWidthMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                }
                // Мин. ширина: расширяем до порога или пропускаем пятно
                if (minWidthMm > 0 && widthMm + 1 < minWidthMm)
                {
                    barCount = Math.Max(2, (int)Math.Ceiling(minWidthMm / step) + 1);
                    widthMm = (barCount - 1) * step;
                    if (maxWidthMm > 0 && widthMm > maxWidthMm)
                        continue; // нельзя удовлетворить min и max
                }
                var asCovered = BarCapacity.AsCm2PerM(dZone, step);

                var segments = SplitByMaxLength(longStartMm, longEndMm, dZone, concrete, settings.AlphaCoef);

                var elementIds = new List<int>();
                foreach (var cell in activeRegion)
                    elementIds.AddRange(mosaic.PlateIds[cell.Iy][cell.Ix]);

                var coreCx = (x0 + x1) / 2.0;
                var coreCy = (y0 + y1) / 2.0;
                var placedAnyForPeak = false;

                foreach (var (segStartMm, segEndMm) in segments)
                {
                    var segLen = segEndMm - segStartMm;
                    var familyLen = RebarTables.PickFamilyLength(segLen);
                    if (familyLen < minFamilyLen) continue;

                    var mid = (segStartMm + segEndMm) / 2.0;
                    var sAdj = mid - familyLen / 2.0;
                    var eAdj = mid + familyLen / 2.0;

                    var perpMid = (corePerp0 + corePerp1) / 2.0;
                    var halfW = UnitConversion.MmToMeters(widthMm) / 2.0;

                    double minXM, maxXM, minYM, maxYM;
                    if (direction == ZoneDirection.X)
                    {
                        minXM = UnitConversion.MmToMeters(sAdj);
                        maxXM = UnitConversion.MmToMeters(eAdj);
                        minYM = perpMid - halfW;
                        maxYM = perpMid + halfW;
                    }
                    else
                    {
                        minYM = UnitConversion.MmToMeters(sAdj);
                        maxYM = UnitConversion.MmToMeters(eAdj);
                        minXM = perpMid - halfW;
                        maxXM = perpMid + halfW;
                    }

                    FitRectInsideSlabBounds(
                        ref minXM, ref maxXM, ref minYM, ref maxYM,
                        outline, settings.SlabEdgeInsetMm);

                    // 1) Подрезка контуром плиты (пересечение, не «сдвиг наружу»)
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;

                    // Длина после клипа → SUM-3, которая реально влезает
                    double availLenMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM));
                    familyLen = RebarTables.PickFamilyLengthFit(availLenMm);
                    if (familyLen < minFamilyLen) continue;
                    CenterAlongLength(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, familyLen);

                    // Ширина кратна шагу, не шире клипа и в пределах min/max
                    double availWidMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM));
                    if (maxWidthMm > 0) availWidMm = Math.Min(availWidMm, maxWidthMm);
                    var barCountFinal = Math.Max(2, (int)Math.Floor(availWidMm / step) + 1);
                    var widthMmFinal = (barCountFinal - 1) * step;
                    if (widthMmFinal < step) continue;
                    if (minWidthMm > 0 && widthMmFinal + 1 < minWidthMm) continue;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);

                    // 2) Привязка к осям (ограниченный сдвиг) + повторный клип
                    var tie = AxisSnapper.SnapRect(ref minXM, ref maxXM, ref minYM, ref maxYM, axes);
                    FitRectInsideSlabBounds(
                        ref minXM, ref maxXM, ref minYM, ref maxYM,
                        outline, settings.SlabEdgeInsetMm);
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    // пересчёт длины/ширины после snap+clip
                    availLenMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM));
                    familyLen = RebarTables.PickFamilyLengthFit(availLenMm);
                    if (familyLen < minFamilyLen) continue;
                    CenterAlongLength(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, familyLen);
                    availWidMm = UnitConversion.MetersToMm(
                        direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM));
                    if (maxWidthMm > 0) availWidMm = Math.Min(availWidMm, maxWidthMm);
                    barCountFinal = Math.Max(2, (int)Math.Floor(availWidMm / step) + 1);
                    widthMmFinal = (barCountFinal - 1) * step;
                    if (widthMmFinal < step) continue;
                    if (minWidthMm > 0 && widthMmFinal + 1 < minWidthMm) continue;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);

                    // 3) Зазор без увода с пятна
                    var beforeGap = (minXM, maxXM, minYM, maxYM);
                    // Сначала пытаемся выдержать зазор. Если места нет, функция вернёт
                    // исходное положение: покрытие расчётного пятна важнее непересечения зон.
                    ResolvePerpGap(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, step, placed);
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        (minXM, maxXM, minYM, maxYM) = beforeGap;
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        continue;

                    var familyKind = ZoneFamilyKind.Straight;
                    var verticalLeg = 0.0;
                    var comment = "";
                    var countInSpec = true;
                    var countBars = false;
                    ApplyHoleBent(
                        settings, openings, outline, layer, direction, dZone, concrete,
                        ref minXM, ref maxXM, ref minYM, ref maxYM, coreCx, coreCy,
                        ref familyKind, ref verticalLeg, ref comment, ref countInSpec, ref countBars);

                    if (maxXM - minXM < 0.05 || maxYM - minYM < 0.05) continue;
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;

                    var cx = (minXM + maxXM) / 2.0;
                    var cy = (minYM + maxYM) / 2.0;
                    double lengthM = direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM);
                    double widthM = direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM);
                    var lenMmCheck = UnitConversion.MetersToMm(lengthM);
                    familyLen = RebarTables.PickFamilyLengthFit(lenMmCheck);
                    if (familyLen < minFamilyLen) continue;

                    // Финальная ширина по фактическому AABB
                    barCountFinal = Math.Max(2, (int)Math.Round(UnitConversion.MetersToMm(widthM) / step) + 1);
                    widthMmFinal = (barCountFinal - 1) * step;
                    CenterPerpWidth(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, widthMmFinal);
                    if (!MeshBoundary.ClipRectToSlab(ref minXM, ref maxXM, ref minYM, ref maxYM, outline, settings.SlabEdgeInsetMm))
                        continue;
                    beforeGap = (minXM, maxXM, minYM, maxYM);
                    ResolvePerpGap(ref minXM, ref maxXM, ref minYM, ref maxYM, direction, step, placed);
                    if (!CoversPoint(minXM, maxXM, minYM, maxYM, peakXM, peakYM) &&
                        !OverlapsCore(minXM, maxXM, minYM, maxYM, x0, x1, y0, y1))
                        (minXM, maxXM, minYM, maxYM) = beforeGap;

                    ShiftRectToCoverCore(
                        ref minXM, ref maxXM, ref minYM, ref maxYM,
                        x0, x1, y0, y1);

                    // ShiftRectToCoverCore может вернуть зону вплотную к соседней.
                    // Финальный проход восстанавливает требуемый парный зазор; близость
                    // сама по себе не является причиной укрупнять зоны.
                    ResolvePerpGap(
                        ref minXM, ref maxXM, ref minYM, ref maxYM,
                        direction, step, placed);
                    if (!MeshBoundary.ClipRectToSlab(
                        ref minXM, ref maxXM, ref minYM, ref maxYM,
                        outline, settings.SlabEdgeInsetMm))
                        continue;

                    cx = (minXM + maxXM) / 2.0;
                    cy = (minYM + maxYM) / 2.0;
                    tie = ComputeTie(minXM, minYM, axes);

                    placed.Add((minXM, maxXM, minYM, maxYM, step));
                    placedAnyForPeak = true;

                    result.Add(new AdditionalZone
                    {
                        ZoneId = zoneId++,
                        ElementId = elementIds.FirstOrDefault(),
                        Layer = layer,
                        NodeIds = elementIds.Distinct().ToList(),
                        Placement = new Point3(cx, cy, mosaic.LevelZM),
                        Contour = new List<Point3>
                        {
                            new Point3(minXM, minYM, mosaic.LevelZM),
                            new Point3(maxXM, minYM, mosaic.LevelZM),
                            new Point3(maxXM, maxYM, mosaic.LevelZM),
                            new Point3(minXM, maxYM, mosaic.LevelZM),
                        },
                        WidthM = direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM),
                        LengthM = direction == ZoneDirection.X ? (maxXM - minXM) : (maxYM - minYM),
                        LevelZM = mosaic.LevelZM,
                        AsRequired = vPeak + GetAsMain(settings, layer),
                        AsAdditional = vPeak,
                        Comment = comment,
                        IsValid = true,
                        StatusColor = familyKind == ZoneFamilyKind.Straight ? "ok" : "warn",
                        Direction = direction,
                        DiameterMm = dZone,
                        BarStepMm = step,
                        BarCount = barCountFinal,
                        WidthMm = widthMmFinal,
                        LengthMm = familyLen,
                        FamilyKind = familyKind,
                        FamilyFileName = settings.GetFamilyName(familyKind),
                        AsCoveredCm2PerM = asCovered,
                        ConcreteClass = concrete,
                        AlphaCoef = settings.AlphaCoef,
                        RotationDeg = direction == ZoneDirection.Y ? 90.0 : 0.0,
                        CountInSpec = countInSpec,
                        CountBars = countBars,
                        VerticalLegMm = verticalLeg,
                        AxisNameX = tie.AxisNameX,
                        AxisPosXM = tie.AxisPosXM,
                        OffsetFromAxisXMm = tie.OffsetFromAxisXMm,
                        AxisNameY = tie.AxisNameY,
                        AxisPosYM = tie.AxisPosYM,
                        OffsetFromAxisYMm = tie.OffsetFromAxisYMm,
                        AxisTieLabel = tie.Label
                    });
                }

                // Помечаем ячейки только после успешной укладки — иначе пятно «съедается» без зоны
                if (placedAnyForPeak)
                {
                    foreach (var cell in activeRegion)
                        assigned[cell.Iy, cell.Ix] = true;
                }
            }

            return MergeOverlappingZones(result, mosaic, layer, settings, openings, outline, axes);
        }

        /// <summary>
        /// Настройка в UI — мм; в DTO исторически «метры».
        /// Значения ≥ 50 считаем уже мм (пользователь вводил 800/1950 в поле «м»).
        /// </summary>
        private static double SettingToMm(double stored)
        {
            if (stored <= 0) return 0;
            if (stored >= 50) return stored;
            return stored * 1000.0;
        }

        private static void CenterAlongLength(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, double familyLenMm)
        {
            var lenM = UnitConversion.MmToMeters(familyLenMm);
            if (direction == ZoneDirection.X)
            {
                var cx = (minXM + maxXM) / 2.0;
                minXM = cx - lenM / 2.0;
                maxXM = cx + lenM / 2.0;
            }
            else
            {
                var cy = (minYM + maxYM) / 2.0;
                minYM = cy - lenM / 2.0;
                maxYM = cy + lenM / 2.0;
            }
        }

        private static void CenterPerpWidth(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, double widthMm)
        {
            var wM = UnitConversion.MmToMeters(widthMm);
            if (direction == ZoneDirection.X)
            {
                var cy = (minYM + maxYM) / 2.0;
                minYM = cy - wM / 2.0;
                maxYM = cy + wM / 2.0;
            }
            else
            {
                var cx = (minXM + maxXM) / 2.0;
                minXM = cx - wM / 2.0;
                maxXM = cx + wM / 2.0;
            }
        }

        private static bool CoversPoint(double minX, double maxX, double minY, double maxY, double px, double py)
            => px >= minX - 1e-6 && px <= maxX + 1e-6 && py >= minY - 1e-6 && py <= maxY + 1e-6;

        private static void ShiftRectToCoverCore(
            ref double minX, ref double maxX, ref double minY, ref double maxY,
            double coreMinX, double coreMaxX, double coreMinY, double coreMaxY)
        {
            if (maxX - minX + 1e-6 >= coreMaxX - coreMinX)
            {
                var dx = minX > coreMinX ? coreMinX - minX
                    : maxX < coreMaxX ? coreMaxX - maxX
                    : 0;
                minX += dx;
                maxX += dx;
            }
            if (maxY - minY + 1e-6 >= coreMaxY - coreMinY)
            {
                var dy = minY > coreMinY ? coreMinY - minY
                    : maxY < coreMaxY ? coreMaxY - maxY
                    : 0;
                minY += dy;
                maxY += dy;
            }
        }

        private static bool OverlapsCore(
            double minX, double maxX, double minY, double maxY,
            double cMinX, double cMaxX, double cMinY, double cMaxY)
        {
            var ox0 = Math.Max(minX, cMinX);
            var ox1 = Math.Min(maxX, cMaxX);
            var oy0 = Math.Max(minY, cMinY);
            var oy1 = Math.Min(maxY, cMaxY);
            if (ox1 <= ox0 || oy1 <= oy0) return false;
            var coreA = Math.Max(1e-9, (cMaxX - cMinX) * (cMaxY - cMinY));
            return (ox1 - ox0) * (oy1 - oy0) / coreA >= 0.35;
        }

        private static AxisSnapper.TieInfo ComputeTie(double minXM, double minYM, IList<ConstructionAxis>? axes)
        {
            var info = new AxisSnapper.TieInfo();
            if (axes == null || axes.Count == 0) return info;

            string? bestVX = null;
            double bestVPos = 0, bestVDist = double.MaxValue;
            string? bestHY = null;
            double bestHPos = 0, bestHDist = double.MaxValue;

            foreach (var ax in axes)
            {
                var name = string.IsNullOrWhiteSpace(ax.Name) ? "?" : ax.Name;
                bool vertical = ax.Vertical;
                double pos = ax.Position;
                if (ax.IsSegment)
                {
                    var dx = Math.Abs(ax.X2 - ax.X1);
                    var dy = Math.Abs(ax.Y2 - ax.Y1);
                    vertical = dx <= dy;
                    pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                }
                if (vertical)
                {
                    var d = Math.Abs(pos - minXM);
                    if (d < bestVDist) { bestVDist = d; bestVX = name; bestVPos = pos; }
                }
                else
                {
                    var d = Math.Abs(pos - minYM);
                    if (d < bestHDist) { bestHDist = d; bestHY = name; bestHPos = pos; }
                }
            }

            if (bestVX != null && bestVDist <= 12.0)
            {
                info.AxisNameX = bestVX;
                info.AxisPosXM = bestVPos;
                info.OffsetFromAxisXMm = Math.Round((minXM - bestVPos) * 1000.0 / 10.0) * 10.0;
            }
            if (bestHY != null && bestHDist <= 12.0)
            {
                info.AxisNameY = bestHY;
                info.AxisPosYM = bestHPos;
                info.OffsetFromAxisYMm = Math.Round((minYM - bestHPos) * 1000.0 / 10.0) * 10.0;
            }
            return info;
        }

        /// <summary>
        /// Собирает всё связное пятно значений ≥ порога и возвращает его габаритный прямоугольник.
        /// Так ступенчатые и Г-образные диапазоны не распадаются на зоны по одной ячейке.
        /// </summary>
        private static List<(int Ix, int Iy)> GrowConnectedRegion(
            double[][] area, bool[,] assigned, int ix0, int iy0, double aThr,
            out int iLeft, out int iRight, out int jDown, out int jUp)
        {
            var ny = area.Length;
            var nx = area[0].Length;
            iLeft = iRight = ix0;
            jDown = jUp = iy0;
            var result = new List<(int Ix, int Iy)>();
            var visited = new bool[ny, nx];
            var queue = new Queue<(int Ix, int Iy)>();
            queue.Enqueue((ix0, iy0));
            visited[iy0, ix0] = true;

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                if (assigned[cell.Iy, cell.Ix] || area[cell.Iy][cell.Ix] < aThr) continue;
                result.Add(cell);
                iLeft = Math.Min(iLeft, cell.Ix);
                iRight = Math.Max(iRight, cell.Ix);
                jDown = Math.Min(jDown, cell.Iy);
                jUp = Math.Max(jUp, cell.Iy);

                var neighbours = new[]
                {
                    (cell.Ix - 1, cell.Iy), (cell.Ix + 1, cell.Iy),
                    (cell.Ix, cell.Iy - 1), (cell.Ix, cell.Iy + 1)
                };
                foreach (var next in neighbours)
                {
                    if (next.Item1 < 0 || next.Item1 >= nx || next.Item2 < 0 || next.Item2 >= ny) continue;
                    if (visited[next.Item2, next.Item1]) continue;
                    visited[next.Item2, next.Item1] = true;
                    if (!assigned[next.Item2, next.Item1] && area[next.Item2][next.Item1] >= aThr)
                        queue.Enqueue((next.Item1, next.Item2));
                }
            }
            return result;
        }

        private static int GetBackgroundDiameter(AnalysisSettings settings, RebarLayer layer) =>
            layer is RebarLayer.As1 or RebarLayer.As2
                ? settings.BgBottomDiameterMm
                : settings.BgTopDiameterMm;

        private static bool RectanglesOverlapArea(
            double minX1, double maxX1, double minY1, double maxY1,
            double minX2, double maxX2, double minY2, double maxY2) =>
            Math.Min(maxX1, maxX2) - Math.Max(minX1, minX2) > 1e-6 &&
            Math.Min(maxY1, maxY2) - Math.Max(minY1, minY2) > 1e-6;

        private static List<AdditionalZone> MergeOverlappingZones(
            List<AdditionalZone> zones, MosaicGrid mosaic, RebarLayer layer, AnalysisSettings settings,
            IList<OpeningInfo> openings, IList<Point3>? outline, IList<ConstructionAxis>? axes)
        {
            var merged = new List<AdditionalZone>(zones);
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < merged.Count && !changed; i++)
                for (var j = i + 1; j < merged.Count; j++)
                {
                    var a = merged[i];
                    var b = merged[j];
                    if (a.Layer != b.Layer || a.Contour.Count < 3 || b.Contour.Count < 3)
                        continue;

                    var aMinX = a.Contour.Min(p => p.X);
                    var aMaxX = a.Contour.Max(p => p.X);
                    var aMinY = a.Contour.Min(p => p.Y);
                    var aMaxY = a.Contour.Max(p => p.Y);
                    var bMinX = b.Contour.Min(p => p.X);
                    var bMaxX = b.Contour.Max(p => p.X);
                    var bMinY = b.Contour.Min(p => p.Y);
                    var bMaxY = b.Contour.Max(p => p.Y);
                    if (!RectanglesOverlapArea(aMinX, aMaxX, aMinY, aMaxY, bMinX, bMaxX, bMinY, bMaxY))
                        continue;

                    var minX = Math.Min(aMinX, bMinX);
                    var maxX = Math.Max(aMaxX, bMaxX);
                    var minY = Math.Min(aMinY, bMinY);
                    var maxY = Math.Max(aMaxY, bMaxY);
                    var governing = a.AsAdditional >= b.AsAdditional ? a : b;
                    a.AsRequired = Math.Max(a.AsRequired, b.AsRequired);
                    a.AsAdditional = Math.Max(a.AsAdditional, b.AsAdditional);
                    a.DiameterMm = governing.DiameterMm;
                    a.BarStepMm = governing.BarStepMm;
                    a.AsCoveredCm2PerM = governing.AsCoveredCm2PerM;
                    a.NodeIds = a.NodeIds.Concat(b.NodeIds).Distinct().ToList();
                    a.ElementId = a.NodeIds.FirstOrDefault();
                    a.Contour = new List<Point3>
                    {
                        new Point3(minX, minY, a.LevelZM),
                        new Point3(maxX, minY, a.LevelZM),
                        new Point3(maxX, maxY, a.LevelZM),
                        new Point3(minX, maxY, a.LevelZM)
                    };
                    a.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, a.LevelZM);
                    a.LengthM = a.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
                    a.WidthM = a.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
                    a.WidthMm = UnitConversion.MetersToMm(a.WidthM);
                    a.BarCount = Math.Max(2, (int)Math.Ceiling(a.WidthMm / a.BarStepMm) + 1);
                    a.WidthMm = (a.BarCount - 1) * a.BarStepMm;
                    a.LengthMm = RebarTables.PickFamilyLength(UnitConversion.MetersToMm(a.LengthM));
                    a.Comment = string.IsNullOrWhiteSpace(a.Comment) ? b.Comment : a.Comment;

                    merged.RemoveAt(j);
                    changed = true;
                    break;
                }
            }

            // Phase 1: reduce provisional rectangles and apply basic constraints.
            TrimZonesToActiveCells(merged, mosaic, settings);
            EnforceZoneGaps(merged, mosaic.CellMm);
            MergeCompatibleAlignedZones(merged);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);
            EnforceZoneGaps(merged, mosaic.CellMm);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);

            // Phase 2: recover coverage before standard-length stabilization.
            MergeActualOverlaps(merged);
            TrimZonesToActiveCells(merged, mosaic, settings);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            MergeActualOverlaps(merged);
            TrimZonesToActiveCells(merged, mosaic, settings);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);
            MergeActualOverlaps(merged);
            TrimZonesToActiveCells(merged, mosaic, settings);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);

            // Phase 3: final coverage and lap validation.
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeActualOverlaps(merged);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeActualOverlaps(merged);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeActualOverlaps(merged);
            NormalizeZoneLengths(merged, outline, settings.SlabEdgeInsetMm);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            MergeActualOverlaps(merged);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            RefreshZoneDimensions(merged);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeSharedCoverageZones(merged, mosaic, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeSharedCoverageZones(merged, mosaic, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            MergeActualOverlaps(merged);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            MergeSharedCoverageZones(merged, mosaic, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            AssignNestedZoneElements(merged);
            TrimZonesToActiveCells(merged, mosaic, settings);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            AssignSharedZoneElements(merged, mosaic);
            PartitionInvalidOverlaps(merged, mosaic);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            PartitionInvalidOverlaps(merged, mosaic);
            MergeActualOverlaps(merged);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            AddRecoveryZones(merged, mosaic, layer, settings, openings, outline, axes);
            PartitionInvalidOverlaps(merged, mosaic);
            AssignNestedZoneElements(merged);
            AssignSharedZoneElements(merged, mosaic);
            TrimZonesToActiveCells(merged, mosaic, settings);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            AssignSharedZoneElements(merged, mosaic);
            TrimZonesToActiveCells(merged, mosaic, settings);
            PartitionInvalidOverlaps(merged, mosaic);
            RefreshZoneDimensions(merged);
            RemoveRedundantZones(merged, mosaic);
            RemoveCoveredOverlapZones(merged, mosaic);
            MergeActualOverlaps(merged);
            FitZonesInsideSlab(merged, outline, settings.SlabEdgeInsetMm);
            RefreshZoneDimensions(merged);
            ApplyFinalEdgeFamilies(merged, settings, outline);
            SplitOverlongStraightZones(merged, mosaic, outline, settings.SlabEdgeInsetMm);
            for (var i = 0; i < merged.Count; i++)
                merged[i].ZoneId = i + 1;
            return merged;
        }

        private static void AssignSharedZoneElements(
            List<AdditionalZone> zones, MosaicGrid mosaic)
        {
            for (var i = 0; i < zones.Count; i++)
            for (var j = i + 1; j < zones.Count; j++)
            {
                var a = zones[i];
                var b = zones[j];
                if (a.Layer != b.Layer || a.Contour.Count < 3 || b.Contour.Count < 3)
                    continue;
                var overlapX = Math.Min(a.Contour.Max(p => p.X), b.Contour.Max(p => p.X)) -
                               Math.Max(a.Contour.Min(p => p.X), b.Contour.Min(p => p.X));
                var overlapY = Math.Min(a.Contour.Max(p => p.Y), b.Contour.Max(p => p.Y)) -
                               Math.Max(a.Contour.Min(p => p.Y), b.Contour.Min(p => p.Y));
                if (overlapX <= 1e-6 || overlapY <= 1e-6)
                    continue;
                foreach (var id in a.NodeIds.Intersect(b.NodeIds).ToList())
                {
                    if (!mosaic.PlateCentroids.TryGetValue(id, out var centroid))
                        continue;
                    var da = Math.Pow(centroid.X - a.Placement.X, 2) +
                             Math.Pow(centroid.Y - a.Placement.Y, 2);
                    var db = Math.Pow(centroid.X - b.Placement.X, 2) +
                             Math.Pow(centroid.Y - b.Placement.Y, 2);
                    if (da <= db) b.NodeIds.Remove(id);
                    else a.NodeIds.Remove(id);
                }
                a.ElementId = a.NodeIds.FirstOrDefault();
                b.ElementId = b.NodeIds.FirstOrDefault();
            }
            zones.RemoveAll(z => z.NodeIds.Count == 0);
        }

        private static void AssignNestedZoneElements(List<AdditionalZone> zones)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
            for (var j = i - 1; j >= 0; j--)
            {
                var a = zones[i];
                var b = zones[j];
                if (a.Layer != b.Layer || a.NodeIds.Count == 0 || b.NodeIds.Count == 0)
                    continue;
                var aIds = new HashSet<int>(a.NodeIds);
                var bIds = new HashSet<int>(b.NodeIds);
                AdditionalZone? superset = null;
                HashSet<int>? localIds = null;
                if (aIds.IsProperSupersetOf(bIds)) { superset = a; localIds = bIds; }
                else if (bIds.IsProperSupersetOf(aIds)) { superset = b; localIds = aIds; }
                if (superset == null || localIds == null)
                    continue;
                superset.NodeIds = superset.NodeIds.Where(id => !localIds.Contains(id)).ToList();
                superset.ElementId = superset.NodeIds.FirstOrDefault();
            }
            zones.RemoveAll(z => z.NodeIds.Count == 0);
        }

        private static void PartitionInvalidOverlaps(
            List<AdditionalZone> zones, MosaicGrid mosaic)
        {
            for (var pass = 0; pass < zones.Count; pass++)
            {
                var changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i];
                    var b = zones[j];
                    if (a.Layer != b.Layer || a.Direction != b.Direction ||
                        a.Contour.Count < 3 || b.Contour.Count < 3)
                        continue;
                    var aMinX = a.Contour.Min(p => p.X);
                    var aMaxX = a.Contour.Max(p => p.X);
                    var aMinY = a.Contour.Min(p => p.Y);
                    var aMaxY = a.Contour.Max(p => p.Y);
                    var bMinX = b.Contour.Min(p => p.X);
                    var bMaxX = b.Contour.Max(p => p.X);
                    var bMinY = b.Contour.Min(p => p.Y);
                    var bMaxY = b.Contour.Max(p => p.Y);
                    var overlapX = Math.Min(aMaxX, bMaxX) - Math.Max(aMinX, bMinX);
                    var overlapY = Math.Min(aMaxY, bMaxY) - Math.Max(aMinY, bMinY);
                    var requiredGap = UnitConversion.MmToMeters(Math.Min(a.BarStepMm, b.BarStepMm));
                    var localGapConflict = a.Direction == ZoneDirection.X
                        ? overlapX > 1e-6 && overlapY > -requiredGap + 1e-6
                        : overlapY > 1e-6 && overlapX > -requiredGap + 1e-6;
                    if ((!RectanglesOverlapArea(
                             aMinX, aMaxX, aMinY, aMaxY,
                             bMinX, bMaxX, bMinY, bMaxY) && !localGapConflict) ||
                        IsAllowedLapOverlap(
                            a, b, aMinX, aMaxX, aMinY, aMaxY,
                            bMinX, bMaxX, bMinY, bMaxY))
                        continue;

                    var shared = new HashSet<int>(a.NodeIds.Intersect(b.NodeIds));
                    bool SharedRemainCovered(
                        double aaMinX, double aaMaxX, double aaMinY, double aaMaxY,
                        double bbMinX, double bbMaxX, double bbMinY, double bbMaxY) =>
                        shared.All(id => mosaic.PlateCentroids.TryGetValue(id, out var centroid) &&
                            ((centroid.X >= aaMinX - 1e-6 && centroid.X <= aaMaxX + 1e-6 &&
                              centroid.Y >= aaMinY - 1e-6 && centroid.Y <= aaMaxY + 1e-6) ||
                             (centroid.X >= bbMinX - 1e-6 && centroid.X <= bbMaxX + 1e-6 &&
                              centroid.Y >= bbMinY - 1e-6 && centroid.Y <= bbMaxY + 1e-6)));
                    var aUnique = a.NodeIds.Where(id => !shared.Contains(id) &&
                        mosaic.PlateCentroids.ContainsKey(id)).ToList();
                    var bUnique = b.NodeIds.Where(id => !shared.Contains(id) &&
                        mosaic.PlateCentroids.ContainsKey(id)).ToList();
                    var aPerp = aUnique.Count > 0
                        ? aUnique.Average(id => a.Direction == ZoneDirection.X
                            ? mosaic.PlateCentroids[id].Y : mosaic.PlateCentroids[id].X)
                        : a.Direction == ZoneDirection.X ? a.Placement.Y : a.Placement.X;
                    var bPerp = bUnique.Count > 0
                        ? bUnique.Average(id => b.Direction == ZoneDirection.X
                            ? mosaic.PlateCentroids[id].Y : mosaic.PlateCentroids[id].X)
                        : b.Direction == ZoneDirection.X ? b.Placement.Y : b.Placement.X;
                    if (Math.Abs(aPerp - bPerp) < 1e-6)
                        continue;
                    var gap = requiredGap;
                    var aIsLower = aPerp < bPerp;
                    var lowerIds = aIsLower ? aUnique : bUnique;
                    var upperIds = aIsLower ? bUnique : aUnique;
                    var lowerFallback = aIsLower ? aPerp : bPerp;
                    var upperFallback = aIsLower ? bPerp : aPerp;
                    double Perp(int id) => a.Direction == ZoneDirection.X
                        ? mosaic.PlateCentroids[id].Y
                        : mosaic.PlateCentroids[id].X;
                    var lowerCoreMax = lowerIds.Count > 0 ? lowerIds.Max(Perp) : lowerFallback;
                    var upperCoreMin = upperIds.Count > 0 ? upperIds.Min(Perp) : upperFallback;
                    if (upperCoreMin - lowerCoreMax < gap - 1e-6)
                    {
                        double Long(int id) => a.Direction == ZoneDirection.X
                            ? mosaic.PlateCentroids[id].X
                            : mosaic.PlateCentroids[id].Y;
                        var aLong = aUnique.Count > 0 ? aUnique.Average(Long) :
                            a.Direction == ZoneDirection.X ? a.Placement.X : a.Placement.Y;
                        var bLong = bUnique.Count > 0 ? bUnique.Average(Long) :
                            b.Direction == ZoneDirection.X ? b.Placement.X : b.Placement.Y;
                        if (Math.Abs(aLong - bLong) < 1e-6)
                            continue;
                        var aBefore = aLong < bLong;
                        var beforeIds = aBefore ? aUnique : bUnique;
                        var afterIds = aBefore ? bUnique : aUnique;
                        var beforeMax = beforeIds.Count > 0 ? beforeIds.Max(Long) : Math.Min(aLong, bLong);
                        var afterMin = afterIds.Count > 0 ? afterIds.Min(Long) : Math.Max(aLong, bLong);
                        var longSplit = afterMin < beforeMax - 1e-6
                            ? (aLong + bLong) / 2.0
                            : (beforeMax + afterMin) / 2.0;
                        if (a.Direction == ZoneDirection.X)
                        {
                            if (aBefore) { aMaxX = Math.Min(aMaxX, longSplit); bMinX = Math.Max(bMinX, longSplit); }
                            else { bMaxX = Math.Min(bMaxX, longSplit); aMinX = Math.Max(aMinX, longSplit); }
                        }
                        else
                        {
                            if (aBefore) { aMaxY = Math.Min(aMaxY, longSplit); bMinY = Math.Max(bMinY, longSplit); }
                            else { bMaxY = Math.Min(bMaxY, longSplit); aMinY = Math.Max(aMinY, longSplit); }
                        }
                        if (aMaxX - aMinX < 0.05 || aMaxY - aMinY < 0.05 ||
                            bMaxX - bMinX < 0.05 || bMaxY - bMinY < 0.05 ||
                            !SharedRemainCovered(
                                aMinX, aMaxX, aMinY, aMaxY,
                                bMinX, bMaxX, bMinY, bMaxY))
                            continue;
                        SetZoneBounds(a, aMinX, aMaxX, aMinY, aMaxY);
                        SetZoneBounds(b, bMinX, bMaxX, bMinY, bMaxY);
                        changed = true;
                        break;
                    }
                    var split = (lowerCoreMax + upperCoreMin) / 2.0;
                    if (a.Direction == ZoneDirection.X)
                    {
                        if (aIsLower)
                        {
                            aMaxY = Math.Min(aMaxY, split - gap / 2.0);
                            bMinY = Math.Max(bMinY, split + gap / 2.0);
                        }
                        else
                        {
                            bMaxY = Math.Min(bMaxY, split - gap / 2.0);
                            aMinY = Math.Max(aMinY, split + gap / 2.0);
                        }
                    }
                    else
                    {
                        if (aIsLower)
                        {
                            aMaxX = Math.Min(aMaxX, split - gap / 2.0);
                            bMinX = Math.Max(bMinX, split + gap / 2.0);
                        }
                        else
                        {
                            bMaxX = Math.Min(bMaxX, split - gap / 2.0);
                            aMinX = Math.Max(aMinX, split + gap / 2.0);
                        }
                    }
                    if (aMaxX - aMinX < 0.05 || aMaxY - aMinY < 0.05 ||
                        bMaxX - bMinX < 0.05 || bMaxY - bMinY < 0.05 ||
                        !SharedRemainCovered(
                            aMinX, aMaxX, aMinY, aMaxY,
                            bMinX, bMaxX, bMinY, bMaxY))
                        continue;
                    SetZoneBounds(a, aMinX, aMaxX, aMinY, aMaxY);
                    SetZoneBounds(b, bMinX, bMaxX, bMinY, bMaxY);
                    changed = true;
                    break;
                }
                if (!changed) break;
            }
        }

        private static void RemoveCoveredOverlapZones(
            List<AdditionalZone> zones, MosaicGrid mosaic)
        {
            bool IsCoveredWithout(AdditionalZone candidate)
            {
                return candidate.NodeIds.All(id =>
                {
                    if (!mosaic.PlateCentroids.TryGetValue(id, out var centroid))
                        return false;
                    return zones.Any(other =>
                        !ReferenceEquals(other, candidate) &&
                        other.Layer == candidate.Layer &&
                        other.AsCoveredCm2PerM + 1e-6 >= candidate.AsCoveredCm2PerM &&
                        other.Contour.Count >= 3 &&
                        centroid.X >= other.Contour.Min(p => p.X) - 1e-6 &&
                        centroid.X <= other.Contour.Max(p => p.X) + 1e-6 &&
                        centroid.Y >= other.Contour.Min(p => p.Y) - 1e-6 &&
                        centroid.Y <= other.Contour.Max(p => p.Y) + 1e-6);
                });
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i];
                    var b = zones[j];
                    if (a.Layer != b.Layer || a.Contour.Count < 3 || b.Contour.Count < 3)
                        continue;
                    var aMinX = a.Contour.Min(p => p.X);
                    var aMaxX = a.Contour.Max(p => p.X);
                    var aMinY = a.Contour.Min(p => p.Y);
                    var aMaxY = a.Contour.Max(p => p.Y);
                    var bMinX = b.Contour.Min(p => p.X);
                    var bMaxX = b.Contour.Max(p => p.X);
                    var bMinY = b.Contour.Min(p => p.Y);
                    var bMaxY = b.Contour.Max(p => p.Y);
                    if (!RectanglesOverlapArea(
                            aMinX, aMaxX, aMinY, aMaxY,
                            bMinX, bMaxX, bMinY, bMaxY) ||
                        IsAllowedLapOverlap(
                            a, b, aMinX, aMaxX, aMinY, aMaxY,
                            bMinX, bMaxX, bMinY, bMaxY))
                        continue;
                    var first = a.NodeIds.Count <= b.NodeIds.Count ? a : b;
                    var second = ReferenceEquals(first, a) ? b : a;
                    var removable = IsCoveredWithout(first) ? first :
                        IsCoveredWithout(second) ? second : null;
                    if (removable == null)
                        continue;
                    zones.Remove(removable);
                    changed = true;
                    break;
                }
            }
        }

        private static void MergeSharedCoverageZones(
            List<AdditionalZone> zones,
            MosaicGrid mosaic,
            IList<Point3>? outline,
            double slabEdgeInsetMm)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i];
                    var b = zones[j];
                    if (a.Layer != b.Layer || a.Direction != b.Direction ||
                        a.Contour.Count < 3 || b.Contour.Count < 3 ||
                        !a.NodeIds.Intersect(b.NodeIds).Any())
                        continue;
                    var minX = Math.Min(a.Contour.Min(p => p.X), b.Contour.Min(p => p.X));
                    var maxX = Math.Max(a.Contour.Max(p => p.X), b.Contour.Max(p => p.X));
                    var minY = Math.Min(a.Contour.Min(p => p.Y), b.Contour.Min(p => p.Y));
                    var maxY = Math.Max(a.Contour.Max(p => p.Y), b.Contour.Max(p => p.Y));
                    var allIds = a.NodeIds.Concat(b.NodeIds).Distinct().ToList();
                    var aIds = new HashSet<int>(a.NodeIds);
                    var bIds = new HashSet<int>(b.NodeIds);
                    var exactSameIds = aIds.SetEquals(bIds);
                    var governingCapacity = Math.Max(a.AsCoveredCm2PerM, b.AsCoveredCm2PerM);
                    var inset = a.FamilyKind == ZoneFamilyKind.Straight &&
                                b.FamilyKind == ZoneFamilyKind.Straight
                        ? slabEdgeInsetMm
                        : 0;
                    if (!MeshBoundary.ClipRectToSlab(
                            ref minX, ref maxX, ref minY, ref maxY, outline, inset) ||
                        (!exactSameIds && !allIds.All(id =>
                        {
                            if (!mosaic.PlateCentroids.TryGetValue(id, out var centroid))
                                return false;
                            var coveredByMerged = centroid.X >= minX - 1e-6 && centroid.X <= maxX + 1e-6 &&
                                                  centroid.Y >= minY - 1e-6 && centroid.Y <= maxY + 1e-6;
                            return coveredByMerged || zones.Any(other =>
                                !ReferenceEquals(other, a) && !ReferenceEquals(other, b) &&
                                other.Layer == a.Layer &&
                                other.AsCoveredCm2PerM + 1e-6 >= governingCapacity &&
                                other.Contour.Count >= 3 &&
                                centroid.X >= other.Contour.Min(p => p.X) - 1e-6 &&
                                centroid.X <= other.Contour.Max(p => p.X) + 1e-6 &&
                                centroid.Y >= other.Contour.Min(p => p.Y) - 1e-6 &&
                                centroid.Y <= other.Contour.Max(p => p.Y) + 1e-6);
                        })))
                        continue;
                    var governing = a.AsCoveredCm2PerM >= b.AsCoveredCm2PerM ? a : b;
                    SetZoneBounds(a, minX, maxX, minY, maxY);
                    a.NodeIds = allIds;
                    a.ElementId = allIds.FirstOrDefault();
                    a.AsRequired = Math.Max(a.AsRequired, b.AsRequired);
                    a.AsAdditional = Math.Max(a.AsAdditional, b.AsAdditional);
                    a.DiameterMm = governing.DiameterMm;
                    a.BarStepMm = governing.BarStepMm;
                    a.AsCoveredCm2PerM = governing.AsCoveredCm2PerM;
                    if (b.FamilyKind != ZoneFamilyKind.Straight)
                    {
                        a.FamilyKind = b.FamilyKind;
                        a.FamilyFileName = b.FamilyFileName;
                        a.VerticalLegMm = b.VerticalLegMm;
                        a.CountInSpec = b.CountInSpec;
                        a.CountBars = b.CountBars;
                    }
                    zones.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        private static void RefreshZoneDimensions(IEnumerable<AdditionalZone> zones)
        {
            foreach (var zone in zones.Where(z => z.Contour.Count >= 3))
            {
                var minX = zone.Contour.Min(p => p.X);
                var maxX = zone.Contour.Max(p => p.X);
                var minY = zone.Contour.Min(p => p.Y);
                var maxY = zone.Contour.Max(p => p.Y);
                var planLengthMm = UnitConversion.MetersToMm(zone.Direction == ZoneDirection.X
                    ? maxX - minX
                    : maxY - minY);
                var widthMm = UnitConversion.MetersToMm(zone.Direction == ZoneDirection.X
                    ? maxY - minY
                    : maxX - minX);
                zone.LengthM = UnitConversion.MmToMeters(planLengthMm);
                zone.WidthM = UnitConversion.MmToMeters(widthMm);
                zone.WidthMm = widthMm;
                zone.BarCount = Math.Max(2, (int)Math.Round(widthMm / zone.BarStepMm) + 1);
                if (zone.FamilyKind == ZoneFamilyKind.Straight)
                    zone.LengthMm = planLengthMm;
            }
        }

        private static void RemoveRedundantZones(List<AdditionalZone> zones, MosaicGrid mosaic)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var candidate = zones[i];
                if (candidate.NodeIds.Count == 0)
                    continue;
                var candidateIds = new HashSet<int>(candidate.NodeIds);
                var redundant = zones.Any(other =>
                {
                    if (ReferenceEquals(other, candidate) ||
                        other.Layer != candidate.Layer ||
                        other.AsCoveredCm2PerM + 1e-6 < candidate.AsCoveredCm2PerM ||
                        other.Contour.Count < 3 ||
                        !candidateIds.All(other.NodeIds.Contains))
                        return false;
                    var minX = other.Contour.Min(p => p.X);
                    var maxX = other.Contour.Max(p => p.X);
                    var minY = other.Contour.Min(p => p.Y);
                    var maxY = other.Contour.Max(p => p.Y);
                    return candidateIds.All(id =>
                        mosaic.PlateCentroids.TryGetValue(id, out var centroid) &&
                        centroid.X >= minX - 1e-6 && centroid.X <= maxX + 1e-6 &&
                        centroid.Y >= minY - 1e-6 && centroid.Y <= maxY + 1e-6);
                });
                if (redundant)
                    zones.RemoveAt(i);
            }
        }

        private static void ApplyFinalEdgeFamilies(
            IEnumerable<AdditionalZone> zones,
            AnalysisSettings settings,
            IList<Point3>? outline)
        {
            if (!settings.ApplyBentRules || outline == null || outline.Count < 3)
                return;

            var minOutlineX = outline.Min(p => p.X);
            var maxOutlineX = outline.Max(p => p.X);
            var minOutlineY = outline.Min(p => p.Y);
            var maxOutlineY = outline.Max(p => p.Y);
            var offsetM = UnitConversion.MmToMeters(settings.EdgeOffsetMm);

            foreach (var zone in zones.Where(z => z.Contour.Count >= 3))
            {
                var minX = zone.Contour.Min(p => p.X);
                var maxX = zone.Contour.Max(p => p.X);
                var minY = zone.Contour.Min(p => p.Y);
                var maxY = zone.Contour.Max(p => p.Y);
                var nearEdge = minX < minOutlineX + offsetM || maxX > maxOutlineX - offsetM ||
                               minY < minOutlineY + offsetM || maxY > maxOutlineY - offsetM;
                if (nearEdge && zone.FamilyKind == ZoneFamilyKind.Straight)
                {
                    zone.VerticalLegMm = HoleBentRules.VerticalLegAvailableMm(
                        settings.SlabThicknessMm, settings.CoverTopMm,
                        settings.CoverBottomMm, zone.DiameterMm);
                    zone.FamilyKind = HoleBentRules.ChooseBentFamily(zone.VerticalLegMm, zone.DiameterMm);
                    zone.FamilyFileName = settings.GetFamilyName(zone.FamilyKind);
                    zone.CountInSpec = false;
                    zone.CountBars = true;
                    if (string.IsNullOrEmpty(zone.Comment)) zone.Comment = "торец: гнутая деталь";
                }
                if (zone.FamilyKind == ZoneFamilyKind.Straight)
                    continue;
                var planLengthMm = UnitConversion.MetersToMm(zone.Direction == ZoneDirection.X
                    ? maxX - minX
                    : maxY - minY);
                zone.LengthMm = RebarTables.BentBarTotalLengthMm(
                    planLengthMm, zone.VerticalLegMm, zone.FamilyKind);
                zone.LengthM = UnitConversion.MmToMeters(planLengthMm);
                zone.StatusColor = "warn";
            }
        }

        private static void AddRecoveryZones(
            List<AdditionalZone> zones,
            MosaicGrid mosaic,
            RebarLayer layer,
            AnalysisSettings settings,
            IList<OpeningInfo> openings,
            IList<Point3>? outline,
            IList<ConstructionAxis>? axes)
        {
            var cellM = mosaic.CellMm / 1000.0;
            var direction = RebarTables.DirectionForLayer(layer, settings.ReverseZoneDirections);
            var zoneBounds = zones
                .Where(z => z.Contour.Count >= 3)
                .Select(z => (
                    Zone: z,
                    MinX: z.Contour.Min(p => p.X),
                    MaxX: z.Contour.Max(p => p.X),
                    MinY: z.Contour.Min(p => p.Y),
                    MaxY: z.Contour.Max(p => p.Y)))
                .ToList();
            var activeIds = new HashSet<int>();
            for (var iy = 0; iy < mosaic.Ny; iy++)
            for (var ix = 0; ix < mosaic.Nx; ix++)
            {
                if (mosaic.Values[iy][ix] <= 0.01) continue;
                foreach (var id in mosaic.PlateIds[iy][ix]) activeIds.Add(id);
            }
            var uncoveredIds = new HashSet<int>(activeIds.Where(id =>
                mosaic.PlateCentroids.TryGetValue(id, out var centroid) &&
                !zoneBounds.Any(b =>
                    centroid.X >= b.MinX - 1e-6 && centroid.X <= b.MaxX + 1e-6 &&
                    centroid.Y >= b.MinY - 1e-6 && centroid.Y <= b.MaxY + 1e-6)));
            bool IsCurrentlyUncovered(int id)
            {
                if (!uncoveredIds.Contains(id) ||
                    !mosaic.PlateCentroids.TryGetValue(id, out var centroid))
                    return false;
                return !zoneBounds.Any(b =>
                    centroid.X >= b.MinX - 1e-6 && centroid.X <= b.MaxX + 1e-6 &&
                    centroid.Y >= b.MinY - 1e-6 && centroid.Y <= b.MaxY + 1e-6);
            }
            bool NeedsRecovery(int ix, int iy) =>
                mosaic.Values[iy][ix] > 0.01 &&
                mosaic.PlateIds[iy][ix].Any(IsCurrentlyUncovered);

            var visited = new bool[mosaic.Ny, mosaic.Nx];
            for (var startY = 0; startY < mosaic.Ny; startY++)
            for (var startX = 0; startX < mosaic.Nx; startX++)
            {
                if (visited[startY, startX] || !NeedsRecovery(startX, startY))
                    continue;
                var component = new List<(int Ix, int Iy)>();
                var queue = new Queue<(int Ix, int Iy)>();
                queue.Enqueue((startX, startY));
                visited[startY, startX] = true;
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    component.Add(cell);
                    var neighbours = direction == ZoneDirection.X
                        ? new[] { (cell.Ix - 1, cell.Iy), (cell.Ix + 1, cell.Iy) }
                        : new[] { (cell.Ix, cell.Iy - 1), (cell.Ix, cell.Iy + 1) };
                    foreach (var next in neighbours)
                    {
                        if (next.Item1 < 0 || next.Item1 >= mosaic.Nx ||
                            next.Item2 < 0 || next.Item2 >= mosaic.Ny ||
                            visited[next.Item2, next.Item1] || !NeedsRecovery(next.Item1, next.Item2))
                            continue;
                        visited[next.Item2, next.Item1] = true;
                        queue.Enqueue((next.Item1, next.Item2));
                    }
                }

                var elementIds = component
                    .SelectMany(c => mosaic.PlateIds[c.Iy][c.Ix])
                    .Where(IsCurrentlyUncovered)
                    .Distinct()
                    .ToList();
                if (elementIds.Count == 0)
                    continue;
                if (settings.MinActiveElements > 0 && elementIds.Count < settings.MinActiveElements)
                    continue;
                var peak = component.Max(c => mosaic.Values[c.Iy][c.Ix]);
                var backgroundDiameter = GetBackgroundDiameter(settings, layer);
                var option = BarCapacity.SelectDiameterAndStep(
                    peak, settings.MaxDiameterMm > 0 ? settings.MaxDiameterMm : 36,
                    backgroundDiameter, settings.UseBarStep100,
                    settings.ExcludedZoneDiametersMm?.ToArray());
                if (option.DiameterMm <= 0) continue;

                var minX = mosaic.OriginXM + component.Min(c => c.Ix) * cellM;
                var maxX = mosaic.OriginXM + (component.Max(c => c.Ix) + 1) * cellM;
                var minY = mosaic.OriginYM + component.Min(c => c.Iy) * cellM;
                var maxY = mosaic.OriginYM + (component.Max(c => c.Iy) + 1) * cellM;
                var centroids = elementIds
                    .Where(mosaic.PlateCentroids.ContainsKey)
                    .Select(id => mosaic.PlateCentroids[id])
                    .ToList();
                if (centroids.Count > 0)
                {
                    minX = Math.Min(minX, centroids.Min(p => p.X));
                    maxX = Math.Max(maxX, centroids.Max(p => p.X));
                    minY = Math.Min(minY, centroids.Min(p => p.Y));
                    maxY = Math.Max(maxY, centroids.Max(p => p.Y));
                }
                var elementBounds = elementIds
                    .Where(mosaic.PlateBounds.ContainsKey)
                    .Select(id => mosaic.PlateBounds[id])
                    .ToList();
                if (elementBounds.Count > 0)
                {
                    minX = Math.Min(minX, elementBounds.Min(b => b.MinX));
                    maxX = Math.Max(maxX, elementBounds.Max(b => b.MaxX));
                    minY = Math.Min(minY, elementBounds.Min(b => b.MinY));
                    maxY = Math.Max(maxY, elementBounds.Max(b => b.MaxY));
                }
                var concrete = RebarTables.NormalizeConcrete(settings.ConcreteClass);
                var anchorageM = UnitConversion.MmToMeters(
                    RebarTables.AnchorageLenMm(concrete, option.DiameterMm));
                if (direction == ZoneDirection.X) { minX -= anchorageM; maxX += anchorageM; }
                else { minY -= anchorageM; maxY += anchorageM; }

                var spanMm = UnitConversion.MetersToMm(
                    direction == ZoneDirection.X ? maxY - minY : maxX - minX);
                var minWidthMm = SettingToMm(settings.MinZoneWidthM);
                var widthMm = Math.Max(minWidthMm,
                    Math.Ceiling(spanMm / option.StepMm) * option.StepMm);
                var perpMid = direction == ZoneDirection.X ? (minY + maxY) / 2 : (minX + maxX) / 2;
                if (direction == ZoneDirection.X)
                {
                    minY = perpMid - UnitConversion.MmToMeters(widthMm) / 2;
                    maxY = perpMid + UnitConversion.MmToMeters(widthMm) / 2;
                }
                else
                {
                    minX = perpMid - UnitConversion.MmToMeters(widthMm) / 2;
                    maxX = perpMid + UnitConversion.MmToMeters(widthMm) / 2;
                }
                var longStartMm = UnitConversion.MetersToMm(
                    direction == ZoneDirection.X ? minX : minY);
                var longEndMm = UnitConversion.MetersToMm(
                    direction == ZoneDirection.X ? maxX : maxY);
                var segments = SplitByMaxLength(
                    longStartMm, longEndMm, option.DiameterMm, concrete, settings.AlphaCoef);

                foreach (var (segmentStartMm, segmentEndMm) in segments)
                {
                    var desiredStartMm = segmentStartMm;
                    var desiredEndMm = segmentEndMm;
                    var initialFamilyLengthMm = RebarTables.PickFamilyLength(segmentEndMm - segmentStartMm);
                    var initialMidMm = (segmentStartMm + segmentEndMm) / 2.0;
                    var initialMinM = UnitConversion.MmToMeters(initialMidMm - initialFamilyLengthMm / 2.0);
                    var initialMaxM = UnitConversion.MmToMeters(initialMidMm + initialFamilyLengthMm / 2.0);
                    foreach (var existingBounds in zoneBounds.Where(b =>
                        b.Zone.Layer == layer && b.Zone.Direction == direction &&
                        b.Zone.LengthMm >= 11700 - 1))
                    {
                        var existing = existingBounds.Zone;
                        var eMinX = existingBounds.MinX;
                        var eMaxX = existingBounds.MaxX;
                        var eMinY = existingBounds.MinY;
                        var eMaxY = existingBounds.MaxY;
                        var perpendicularOverlapM = direction == ZoneDirection.X
                            ? Math.Min(maxY, eMaxY) - Math.Max(minY, eMinY)
                            : Math.Min(maxX, eMaxX) - Math.Max(minX, eMinX);
                        var longitudinalOverlapMm = UnitConversion.MetersToMm(direction == ZoneDirection.X
                            ? Math.Min(initialMaxM, eMaxX) - Math.Max(initialMinM, eMinX)
                            : Math.Min(initialMaxM, eMaxY) - Math.Max(initialMinM, eMinY));
                        if (perpendicularOverlapM <= 1e-6 || longitudinalOverlapMm <= 1e-6)
                            continue;
                        var requiredOverlapMm = 2.0 * RebarTables.LapLenMm(
                            concrete, Math.Max(option.DiameterMm, existing.DiameterMm));
                        if (longitudinalOverlapMm + 1 < requiredOverlapMm)
                        {
                            var existingStartMm = UnitConversion.MetersToMm(
                                direction == ZoneDirection.X ? eMinX : eMinY);
                            var existingEndMm = UnitConversion.MetersToMm(
                                direction == ZoneDirection.X ? eMaxX : eMaxY);
                            var existingMidMm = (existingStartMm + existingEndMm) / 2.0;
                            if (existingMidMm < initialMidMm)
                                desiredStartMm = Math.Min(desiredStartMm, existingEndMm - requiredOverlapMm);
                            else
                                desiredEndMm = Math.Max(desiredEndMm, existingStartMm + requiredOverlapMm);
                        }
                    }
                    var familyLengthMm = RebarTables.PickFamilyLength(desiredEndMm - desiredStartMm);
                    var segmentMidMm = (desiredStartMm + desiredEndMm) / 2.0;
                    var segmentMinM = UnitConversion.MmToMeters(segmentMidMm - familyLengthMm / 2.0);
                    var segmentMaxM = UnitConversion.MmToMeters(segmentMidMm + familyLengthMm / 2.0);
                    var zoneMinX = direction == ZoneDirection.X ? segmentMinM : minX;
                    var zoneMaxX = direction == ZoneDirection.X ? segmentMaxM : maxX;
                    var zoneMinY = direction == ZoneDirection.Y ? segmentMinM : minY;
                    var zoneMaxY = direction == ZoneDirection.Y ? segmentMaxM : maxY;

                    FitRectInsideSlabBounds(
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                        outline, 0);

                    AxisSnapper.SnapRect(
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY, axes);

                    var familyKind = ZoneFamilyKind.Straight;
                    var verticalLeg = 0.0;
                    var comment = "";
                    var countInSpec = true;
                    var countBars = false;
                    ApplyHoleBent(
                        settings, openings, outline, layer, direction, option.DiameterMm, concrete,
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                        (minX + maxX) / 2.0, (minY + maxY) / 2.0,
                        ref familyKind, ref verticalLeg, ref comment, ref countInSpec, ref countBars);
                    if (comment.StartsWith("торец", StringComparison.Ordinal) && elementBounds.Count > 0)
                    {
                        ShiftRectToCoverCore(
                            ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                            elementBounds.Min(b => b.MinX), elementBounds.Max(b => b.MaxX),
                            elementBounds.Min(b => b.MinY), elementBounds.Max(b => b.MaxY));
                    }
                    var effectiveInsetMm = familyKind == ZoneFamilyKind.Straight
                        ? settings.SlabEdgeInsetMm
                        : 0;
                    FitRectInsideSlabBounds(
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                        outline, effectiveInsetMm);
                    var beforeContourClip = (MinX: zoneMinX, MaxX: zoneMaxX, MinY: zoneMinY, MaxY: zoneMaxY);
                    var beforeConflictClip = (zoneMinX, zoneMaxX, zoneMinY, zoneMaxY);
                    if (!MeshBoundary.ClipRectToSlab(
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                        outline, effectiveInsetMm))
                        continue;
                    var conflictClipTrimmed = Math.Abs(zoneMinX - beforeConflictClip.zoneMinX) > 1e-6 ||
                                              Math.Abs(zoneMaxX - beforeConflictClip.zoneMaxX) > 1e-6 ||
                                              Math.Abs(zoneMinY - beforeConflictClip.zoneMinY) > 1e-6 ||
                                              Math.Abs(zoneMaxY - beforeConflictClip.zoneMaxY) > 1e-6;
                    if (conflictClipTrimmed && settings.ApplyBentRules &&
                        familyKind == ZoneFamilyKind.Straight)
                    {
                        verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                            settings.SlabThicknessMm, settings.CoverTopMm,
                            settings.CoverBottomMm, option.DiameterMm);
                        familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, option.DiameterMm);
                        countInSpec = false;
                        countBars = true;
                        if (string.IsNullOrEmpty(comment)) comment = "контур: гнутая деталь";
                    }
                    var trimmedBySlab = Math.Abs(zoneMinX - beforeContourClip.MinX) > 1e-6 ||
                                        Math.Abs(zoneMaxX - beforeContourClip.MaxX) > 1e-6 ||
                                        Math.Abs(zoneMinY - beforeContourClip.MinY) > 1e-6 ||
                                        Math.Abs(zoneMaxY - beforeContourClip.MaxY) > 1e-6;
                    if (trimmedBySlab && settings.ApplyBentRules && familyKind == ZoneFamilyKind.Straight)
                    {
                        verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                            settings.SlabThicknessMm, settings.CoverTopMm,
                            settings.CoverBottomMm, option.DiameterMm);
                        familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, option.DiameterMm);
                        countInSpec = false;
                        countBars = true;
                        if (string.IsNullOrEmpty(comment)) comment = "контур: гнутая деталь";
                    }

                    foreach (var existingBounds in zoneBounds.Where(b =>
                        b.Zone.Layer == layer && b.Zone.Direction == direction &&
                        (Math.Abs(b.Zone.LengthMm - 11700) <= 1 ||
                         Math.Abs(UnitConversion.MetersToMm(b.Zone.LengthM) - 11700) <= 1)))
                    {
                        var perpendicularOverlapM = direction == ZoneDirection.X
                            ? Math.Min(zoneMaxY, existingBounds.MaxY) - Math.Max(zoneMinY, existingBounds.MinY)
                            : Math.Min(zoneMaxX, existingBounds.MaxX) - Math.Max(zoneMinX, existingBounds.MinX);
                        var longitudinalOverlapM = direction == ZoneDirection.X
                            ? Math.Min(zoneMaxX, existingBounds.MaxX) - Math.Max(zoneMinX, existingBounds.MinX)
                            : Math.Min(zoneMaxY, existingBounds.MaxY) - Math.Max(zoneMinY, existingBounds.MinY);
                        if (perpendicularOverlapM <= 1e-6 || longitudinalOverlapM <= 1e-6)
                            continue;
                        var requiredOverlapM = UnitConversion.MmToMeters(2.0 * RebarTables.LapLenMm(
                            concrete, Math.Max(option.DiameterMm, existingBounds.Zone.DiameterMm)));
                        if (longitudinalOverlapM + 0.001 >= requiredOverlapM)
                            continue;

                        var shift = requiredOverlapM - longitudinalOverlapM;
                        var existingMid = direction == ZoneDirection.X
                            ? (existingBounds.MinX + existingBounds.MaxX) / 2.0
                            : (existingBounds.MinY + existingBounds.MaxY) / 2.0;
                        var zoneMid = direction == ZoneDirection.X
                            ? (zoneMinX + zoneMaxX) / 2.0
                            : (zoneMinY + zoneMaxY) / 2.0;
                        var beforeLapShift = (zoneMinX, zoneMaxX, zoneMinY, zoneMaxY);
                        if (direction == ZoneDirection.X)
                        {
                            var dx = existingMid < zoneMid ? -shift : shift;
                            zoneMinX += dx;
                            zoneMaxX += dx;
                        }
                        else
                        {
                            var dy = existingMid < zoneMid ? -shift : shift;
                            zoneMinY += dy;
                            zoneMaxY += dy;
                        }
                        if (!MeshBoundary.ClipRectToSlab(
                            ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                            outline, effectiveInsetMm))
                        {
                            (zoneMinX, zoneMaxX, zoneMinY, zoneMaxY) = beforeLapShift;
                            continue;
                        }
                        perpendicularOverlapM = direction == ZoneDirection.X
                            ? Math.Min(zoneMaxY, existingBounds.MaxY) - Math.Max(zoneMinY, existingBounds.MinY)
                            : Math.Min(zoneMaxX, existingBounds.MaxX) - Math.Max(zoneMinX, existingBounds.MinX);
                        longitudinalOverlapM = direction == ZoneDirection.X
                            ? Math.Min(zoneMaxX, existingBounds.MaxX) - Math.Max(zoneMinX, existingBounds.MinX)
                            : Math.Min(zoneMaxY, existingBounds.MaxY) - Math.Max(zoneMinY, existingBounds.MinY);
                        if (perpendicularOverlapM > 1e-6 && longitudinalOverlapM > 1e-6 &&
                            longitudinalOverlapM + 0.001 < requiredOverlapM)
                        {
                            var gapM = UnitConversion.MmToMeters(Math.Min(
                                option.StepMm, existingBounds.Zone.BarStepMm));
                            if (direction == ZoneDirection.X)
                            {
                                var existingPerpMid = (existingBounds.MinY + existingBounds.MaxY) / 2.0;
                                var zonePerpMid = (zoneMinY + zoneMaxY) / 2.0;
                                if (zonePerpMid < existingPerpMid)
                                    zoneMaxY = Math.Min(zoneMaxY, existingBounds.MinY - gapM);
                                else
                                    zoneMinY = Math.Max(zoneMinY, existingBounds.MaxY + gapM);
                            }
                            else
                            {
                                var existingPerpMid = (existingBounds.MinX + existingBounds.MaxX) / 2.0;
                                var zonePerpMid = (zoneMinX + zoneMaxX) / 2.0;
                                if (zonePerpMid < existingPerpMid)
                                    zoneMaxX = Math.Min(zoneMaxX, existingBounds.MinX - gapM);
                                else
                                    zoneMinX = Math.Max(zoneMinX, existingBounds.MaxX + gapM);
                            }
                        }
                    }

                    // Recovery rectangles are added after the normal gap pass.  Resolve their
                    // conflicts here, keeping the side that contains the FE being recovered.
                    var coreMinX = centroids.Count > 0 ? centroids.Min(p => p.X) :
                        elementBounds.Count > 0 ? elementBounds.Min(b => b.MinX) : zoneMinX;
                    var coreMaxX = centroids.Count > 0 ? centroids.Max(p => p.X) :
                        elementBounds.Count > 0 ? elementBounds.Max(b => b.MaxX) : zoneMaxX;
                    var coreMinY = centroids.Count > 0 ? centroids.Min(p => p.Y) :
                        elementBounds.Count > 0 ? elementBounds.Min(b => b.MinY) : zoneMinY;
                    var coreMaxY = centroids.Count > 0 ? centroids.Max(p => p.Y) :
                        elementBounds.Count > 0 ? elementBounds.Max(b => b.MaxY) : zoneMaxY;
                    foreach (var existingBounds in zoneBounds.Where(b =>
                        b.Zone.Layer == layer && b.Zone.Direction == direction))
                    {
                        var overlapX = Math.Min(zoneMaxX, existingBounds.MaxX) - Math.Max(zoneMinX, existingBounds.MinX);
                        var overlapY = Math.Min(zoneMaxY, existingBounds.MaxY) - Math.Max(zoneMinY, existingBounds.MinY);
                        if (overlapX <= 1e-6 || overlapY <= 1e-6)
                            continue;

                        var longOverlapMm = UnitConversion.MetersToMm(
                            direction == ZoneDirection.X ? overlapX : overlapY);
                        var requiredLapMm = 2.0 * RebarTables.LapLenMm(
                            concrete, Math.Max(option.DiameterMm, existingBounds.Zone.DiameterMm));
                        var has11700 = Math.Abs(familyLengthMm - 11700) <= 1 ||
                                       Math.Abs(existingBounds.Zone.LengthMm - 11700) <= 1;
                        if (has11700 && longOverlapMm + 1 >= requiredLapMm)
                            continue;

                        var gapM = UnitConversion.MmToMeters(Math.Min(
                            option.StepMm, existingBounds.Zone.BarStepMm));
                        if (direction == ZoneDirection.X)
                        {
                            if (coreMaxX <= existingBounds.MinX + 1e-6)
                                zoneMaxX = Math.Min(zoneMaxX, existingBounds.MinX);
                            else if (coreMinX >= existingBounds.MaxX - 1e-6)
                                zoneMinX = Math.Max(zoneMinX, existingBounds.MaxX);
                            else if (coreMaxY <= existingBounds.MinY + 1e-6)
                            {
                                var width = zoneMaxY - zoneMinY;
                                zoneMaxY = Math.Min(zoneMaxY, existingBounds.MinY - gapM);
                                zoneMinY = zoneMaxY - width;
                            }
                            else if (coreMinY >= existingBounds.MaxY - 1e-6)
                            {
                                var width = zoneMaxY - zoneMinY;
                                zoneMinY = Math.Max(zoneMinY, existingBounds.MaxY + gapM);
                                zoneMaxY = zoneMinY + width;
                            }
                        }
                        else
                        {
                            if (coreMaxY <= existingBounds.MinY + 1e-6)
                                zoneMaxY = Math.Min(zoneMaxY, existingBounds.MinY);
                            else if (coreMinY >= existingBounds.MaxY - 1e-6)
                                zoneMinY = Math.Max(zoneMinY, existingBounds.MaxY);
                            else if (coreMaxX <= existingBounds.MinX + 1e-6)
                            {
                                var width = zoneMaxX - zoneMinX;
                                zoneMaxX = Math.Min(zoneMaxX, existingBounds.MinX - gapM);
                                zoneMinX = zoneMaxX - width;
                            }
                            else if (coreMinX >= existingBounds.MaxX - 1e-6)
                            {
                                var width = zoneMaxX - zoneMinX;
                                zoneMinX = Math.Max(zoneMinX, existingBounds.MaxX + gapM);
                                zoneMaxX = zoneMinX + width;
                            }
                        }
                    }

                    if (!MeshBoundary.ClipRectToSlab(
                        ref zoneMinX, ref zoneMaxX, ref zoneMinY, ref zoneMaxY,
                        outline, effectiveInsetMm))
                        continue;

                    if (zoneMaxX - zoneMinX < 0.05 || zoneMaxY - zoneMinY < 0.05)
                        continue;

                    var actualPlanLengthMm = UnitConversion.MetersToMm(direction == ZoneDirection.X
                        ? zoneMaxX - zoneMinX
                        : zoneMaxY - zoneMinY);
                    var actualWidthMm = UnitConversion.MetersToMm(direction == ZoneDirection.X
                        ? zoneMaxY - zoneMinY
                        : zoneMaxX - zoneMinX);
                    if (familyKind == ZoneFamilyKind.Straight && minWidthMm > 0 &&
                        actualWidthMm + 1 < minWidthMm)
                        continue;
                    var resultingLengthMm = familyKind == ZoneFamilyKind.Straight
                        ? actualPlanLengthMm
                        : RebarTables.BentBarTotalLengthMm(actualPlanLengthMm, verticalLeg, familyKind);
                    var tie = ComputeTie(zoneMinX, zoneMinY, axes);

                    var absorbIndex = zoneBounds.FindIndex(b =>
                        b.Zone.Layer == layer &&
                        b.Zone.AsCoveredCm2PerM + 1e-6 >= BarCapacity.AsCm2PerM(option.DiameterMm, option.StepMm) &&
                        elementIds.All(b.Zone.NodeIds.Contains));
                    if (absorbIndex >= 0)
                    {
                        var target = zoneBounds[absorbIndex];
                        var absorbMinX = Math.Min(target.MinX, zoneMinX);
                        var absorbMaxX = Math.Max(target.MaxX, zoneMaxX);
                        var absorbMinY = Math.Min(target.MinY, zoneMinY);
                        var absorbMaxY = Math.Max(target.MaxY, zoneMaxY);
                        if (MeshBoundary.ClipRectToSlab(
                                ref absorbMinX, ref absorbMaxX, ref absorbMinY, ref absorbMaxY,
                                outline, target.Zone.FamilyKind == ZoneFamilyKind.Straight
                                    ? settings.SlabEdgeInsetMm
                                    : 0) &&
                            elementIds.All(id =>
                                mosaic.PlateCentroids.TryGetValue(id, out var centroid) &&
                                centroid.X >= absorbMinX - 1e-6 && centroid.X <= absorbMaxX + 1e-6 &&
                                centroid.Y >= absorbMinY - 1e-6 && centroid.Y <= absorbMaxY + 1e-6))
                        {
                            SetZoneBounds(target.Zone, absorbMinX, absorbMaxX, absorbMinY, absorbMaxY);
                            target.Zone.NodeIds = target.Zone.NodeIds.Concat(elementIds).Distinct().ToList();
                            if (target.Zone.FamilyKind == ZoneFamilyKind.Straight)
                                target.Zone.LengthMm = UnitConversion.MetersToMm(target.Zone.LengthM);
                            zoneBounds[absorbIndex] =
                                (target.Zone, absorbMinX, absorbMaxX, absorbMinY, absorbMaxY);
                            continue;
                        }
                    }

                    var zone = new AdditionalZone
                    {
                        Layer = layer,
                        NodeIds = elementIds,
                        ElementId = elementIds.FirstOrDefault(),
                        LevelZM = mosaic.LevelZM,
                        Direction = direction,
                        DiameterMm = option.DiameterMm,
                        BarStepMm = option.StepMm,
                        BarCount = Math.Max(2, (int)Math.Round(actualWidthMm / option.StepMm) + 1),
                        WidthMm = actualWidthMm,
                        WidthM = UnitConversion.MmToMeters(actualWidthMm),
                        LengthMm = resultingLengthMm,
                        LengthM = UnitConversion.MmToMeters(actualPlanLengthMm),
                        AsAdditional = peak,
                        AsRequired = peak + GetAsMain(settings, layer),
                        AsCoveredCm2PerM = BarCapacity.AsCm2PerM(option.DiameterMm, option.StepMm),
                        ConcreteClass = concrete,
                        FamilyKind = familyKind,
                        FamilyFileName = settings.GetFamilyName(familyKind),
                        CountInSpec = countInSpec,
                        CountBars = countBars,
                        VerticalLegMm = verticalLeg,
                        Comment = comment,
                        AxisNameX = tie.AxisNameX,
                        AxisPosXM = tie.AxisPosXM,
                        OffsetFromAxisXMm = tie.OffsetFromAxisXMm,
                        AxisNameY = tie.AxisNameY,
                        AxisPosYM = tie.AxisPosYM,
                        OffsetFromAxisYMm = tie.OffsetFromAxisYMm,
                        AxisTieLabel = tie.Label,
                        IsValid = true,
                        StatusColor = familyKind == ZoneFamilyKind.Straight ? "ok" : "warn"
                    };
                    SetZoneBounds(zone, zoneMinX, zoneMaxX, zoneMinY, zoneMaxY);
                    zone.LengthMm = resultingLengthMm;
                    zone.LengthM = UnitConversion.MmToMeters(actualPlanLengthMm);
                    zones.Add(zone);
                    zoneBounds.Add((zone, zoneMinX, zoneMaxX, zoneMinY, zoneMaxY));
                }
            }
        }

        private static void MergeActualOverlaps(List<AdditionalZone> zones)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i];
                    var b = zones[j];
                    if (a.Layer != b.Layer || a.Contour.Count < 3 || b.Contour.Count < 3)
                        continue;
                    var aMinX = a.Contour.Min(p => p.X);
                    var aMaxX = a.Contour.Max(p => p.X);
                    var aMinY = a.Contour.Min(p => p.Y);
                    var aMaxY = a.Contour.Max(p => p.Y);
                    var bMinX = b.Contour.Min(p => p.X);
                    var bMaxX = b.Contour.Max(p => p.X);
                    var bMinY = b.Contour.Min(p => p.Y);
                    var bMaxY = b.Contour.Max(p => p.Y);
                    if (IsAllowedLapOverlap(
                        a, b,
                        aMinX, aMaxX, aMinY, aMaxY,
                        bMinX, bMaxX, bMinY, bMaxY))
                        continue;
                    var overlapX = Math.Min(aMaxX, bMaxX) - Math.Max(aMinX, bMinX);
                    var overlapY = Math.Min(aMaxY, bMaxY) - Math.Max(aMinY, bMinY);
                    var requiredGapM = UnitConversion.MmToMeters(Math.Min(a.BarStepMm, b.BarStepMm));
                    var perpGapM = a.Direction == ZoneDirection.X
                        ? Math.Max(0, -overlapY)
                        : Math.Max(0, -overlapX);
                    var longitudinalOverlapM = a.Direction == ZoneDirection.X ? overlapX : overlapY;
                    var isLocalGapConflict = a.Direction == b.Direction &&
                        longitudinalOverlapM > 1e-6 && perpGapM < requiredGapM - 1e-6;
                    if (!RectanglesOverlapArea(
                        aMinX, aMaxX, aMinY, aMaxY,
                        bMinX, bMaxX, bMinY, bMaxY) && !isLocalGapConflict)
                        continue;

                    var governing = a.AsAdditional >= b.AsAdditional ? a : b;
                    SetZoneBounds(
                        a,
                        Math.Min(aMinX, bMinX), Math.Max(aMaxX, bMaxX),
                        Math.Min(aMinY, bMinY), Math.Max(aMaxY, bMaxY));
                    a.NodeIds = a.NodeIds.Concat(b.NodeIds).Distinct().ToList();
                    a.ElementId = a.NodeIds.FirstOrDefault();
                    a.AsRequired = Math.Max(a.AsRequired, b.AsRequired);
                    a.AsAdditional = Math.Max(a.AsAdditional, b.AsAdditional);
                    a.DiameterMm = governing.DiameterMm;
                    a.BarStepMm = governing.BarStepMm;
                    a.AsCoveredCm2PerM = governing.AsCoveredCm2PerM;
                    zones.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        private static void FitZonesInsideSlab(
            List<AdditionalZone> zones, IList<Point3>? outline, double slabEdgeInsetMm)
        {
            if (outline == null || outline.Count < 3) return;
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var zone = zones[i];
                if (zone.Contour.Count < 3)
                {
                    zones.RemoveAt(i);
                    continue;
                }
                var minX = zone.Contour.Min(p => p.X);
                var maxX = zone.Contour.Max(p => p.X);
                var minY = zone.Contour.Min(p => p.Y);
                var maxY = zone.Contour.Max(p => p.Y);
                FitRectInsideSlabBounds(
                    ref minX, ref maxX, ref minY, ref maxY,
                    outline, slabEdgeInsetMm);
                if (!MeshBoundary.ClipRectToSlab(
                    ref minX, ref maxX, ref minY, ref maxY,
                    outline, slabEdgeInsetMm) ||
                    maxX - minX < 0.05 || maxY - minY < 0.05)
                {
                    zones.RemoveAt(i);
                    continue;
                }
                SetZoneBounds(zone, minX, maxX, minY, maxY);
            }
        }

        private static void NormalizeZoneLengths(
            List<AdditionalZone> zones, IList<Point3>? outline, double slabEdgeInsetMm)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var zone = zones[i];
                if (zone.Contour.Count < 3)
                {
                    zones.RemoveAt(i);
                    continue;
                }
                var minX = zone.Contour.Min(p => p.X);
                var maxX = zone.Contour.Max(p => p.X);
                var minY = zone.Contour.Min(p => p.Y);
                var maxY = zone.Contour.Max(p => p.Y);
                var availableMm = UnitConversion.MetersToMm(
                    zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY);
                if (availableMm < 1)
                {
                    zones.RemoveAt(i);
                    continue;
                }
                if (zone.FamilyKind == ZoneFamilyKind.Straight)
                {
                    // Только SUM-30 использует строгий ряд типовых длин вверх.
                    var requiredMm = Math.Max(1, Math.Ceiling(availableMm - 0.5));
                    var familyLengthMm = RebarTables.PickFamilyLength(requiredMm);
                    CenterAlongLength(
                        ref minX, ref maxX, ref minY, ref maxY,
                        zone.Direction, familyLengthMm);
                    FitRectInsideSlabBounds(
                        ref minX, ref maxX, ref minY, ref maxY,
                        outline, slabEdgeInsetMm);
                    zone.Contour = new List<Point3>
                    {
                        new Point3(minX, minY, zone.LevelZM),
                        new Point3(maxX, minY, zone.LevelZM),
                        new Point3(maxX, maxY, zone.LevelZM),
                        new Point3(minX, maxY, zone.LevelZM)
                    };
                    zone.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, zone.LevelZM);
                    zone.LengthMm = familyLengthMm;
                    zone.LengthM = UnitConversion.MmToMeters(familyLengthMm);
                }
                else
                {
                    // Для гнутой детали контур остаётся плановой частью, а параметр L
                    // содержит полную длину заготовки с вертикальными полками.
                    zone.LengthM = UnitConversion.MmToMeters(availableMm);
                    zone.LengthMm = RebarTables.BentBarTotalLengthMm(
                        availableMm, zone.VerticalLegMm, zone.FamilyKind);
                }
                zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
            }
        }

        private static void SplitOverlongStraightZones(
            List<AdditionalZone> zones, MosaicGrid mosaic, IList<Point3>? outline, double slabEdgeInsetMm)
        {
            const int maxLengthMm = 11700;
            for (var index = zones.Count - 1; index >= 0; index--)
            {
                var source = zones[index];
                if (source.FamilyKind != ZoneFamilyKind.Straight || source.Contour.Count < 3)
                    continue;
                var minX = source.Contour.Min(p => p.X);
                var maxX = source.Contour.Max(p => p.X);
                var minY = source.Contour.Min(p => p.Y);
                var maxY = source.Contour.Max(p => p.Y);
                var start = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X ? minX : minY);
                var end = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X ? maxX : maxY);
                if (end - start <= maxLengthMm + 1) continue;

                var lapMm = 2 * RebarTables.LapLenMm(source.ConcreteClass, source.DiameterMm);
                if (lapMm >= maxLengthMm) continue;
                var pieces = new List<(double Start, double End, int Length)>();
                var cursor = start;
                while (end - cursor > maxLengthMm + 1)
                {
                    pieces.Add((cursor, cursor + maxLengthMm, maxLengthMm));
                    cursor += maxLengthMm - lapMm;
                }
                var lastLength = RebarTables.PickFamilyLength(end - cursor);
                if (lastLength > maxLengthMm) continue;
                pieces.Add((end - lastLength, end, lastLength));

                var replacements = new List<AdditionalZone>();
                foreach (var piece in pieces)
                {
                    var zone = Newtonsoft.Json.JsonConvert.DeserializeObject<AdditionalZone>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(source))!;
                    var a = UnitConversion.MmToMeters(piece.Start);
                    var b = UnitConversion.MmToMeters(piece.End);
                    var pieceMinX = source.Direction == ZoneDirection.X ? a : minX;
                    var pieceMaxX = source.Direction == ZoneDirection.X ? b : maxX;
                    var pieceMinY = source.Direction == ZoneDirection.Y ? a : minY;
                    var pieceMaxY = source.Direction == ZoneDirection.Y ? b : maxY;
                    if (!MeshBoundary.ClipRectToSlab(ref pieceMinX, ref pieceMaxX,
                            ref pieceMinY, ref pieceMaxY, outline, slabEdgeInsetMm))
                        break;
                    var clippedLengthMm = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X
                        ? pieceMaxX - pieceMinX : pieceMaxY - pieceMinY);
                    if (Math.Abs(clippedLengthMm - piece.Length) > 1) break;
                    SetZoneBounds(zone,
                        pieceMinX, pieceMaxX, pieceMinY, pieceMaxY);
                    zone.LengthMm = UnitConversion.MetersToMm(zone.LengthM);
                    zone.NodeIds = source.NodeIds.Where(id =>
                        mosaic.PlateCentroids.TryGetValue(id, out var point) &&
                        point.X >= pieceMinX - 1e-6 && point.X <= pieceMaxX + 1e-6 &&
                        point.Y >= pieceMinY - 1e-6 && point.Y <= pieceMaxY + 1e-6).ToList();
                    zone.ElementId = zone.NodeIds.FirstOrDefault();
                    replacements.Add(zone);
                }
                var preservesIds = source.NodeIds.All(id => replacements.Any(z => z.NodeIds.Contains(id)));
                var validNeighbors = replacements.All(candidate => zones.Where(other =>
                    !ReferenceEquals(other, source) && other.Layer == candidate.Layer &&
                    other.Contour.Count >= 3).All(other =>
                {
                    var cMinX = candidate.Contour.Min(p => p.X);
                    var cMaxX = candidate.Contour.Max(p => p.X);
                    var cMinY = candidate.Contour.Min(p => p.Y);
                    var cMaxY = candidate.Contour.Max(p => p.Y);
                    var oMinX = other.Contour.Min(p => p.X);
                    var oMaxX = other.Contour.Max(p => p.X);
                    var oMinY = other.Contour.Min(p => p.Y);
                    var oMaxY = other.Contour.Max(p => p.Y);
                    return !RectanglesOverlapArea(cMinX, cMaxX, cMinY, cMaxY,
                               oMinX, oMaxX, oMinY, oMaxY) ||
                           IsAllowedLapOverlap(candidate, other, cMinX, cMaxX, cMinY, cMaxY,
                               oMinX, oMaxX, oMinY, oMaxY);
                }));
                if (replacements.Count != pieces.Count || !preservesIds || !validNeighbors)
                {
                    source.IsValid = false;
                    source.StatusColor = "warn";
                    source.Comment = "превышение 11700 мм: разделение конфликтует с контуром или соседними зонами";
                    continue;
                }
                zones.RemoveAt(index);
                zones.AddRange(replacements);
            }
        }

        private static void MergeCompatibleAlignedZones(List<AdditionalZone> zones)
        {
            const double toleranceM = 0.011;
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var a = zones[i];
                    var b = zones[j];
                    if (a.Layer != b.Layer || a.Direction != b.Direction ||
                        a.DiameterMm != b.DiameterMm || a.BarStepMm != b.BarStepMm ||
                        Math.Abs(a.LengthMm - b.LengthMm) > 1 ||
                        a.Contour.Count < 3 || b.Contour.Count < 3)
                        continue;

                    var aMinX = a.Contour.Min(p => p.X);
                    var aMaxX = a.Contour.Max(p => p.X);
                    var aMinY = a.Contour.Min(p => p.Y);
                    var aMaxY = a.Contour.Max(p => p.Y);
                    var bMinX = b.Contour.Min(p => p.X);
                    var bMaxX = b.Contour.Max(p => p.X);
                    var bMinY = b.Contour.Min(p => p.Y);
                    var bMaxY = b.Contour.Max(p => p.Y);
                    var aligned = a.Direction == ZoneDirection.X
                        ? Math.Abs(aMinX - bMinX) <= toleranceM && Math.Abs(aMaxX - bMaxX) <= toleranceM
                        : Math.Abs(aMinY - bMinY) <= toleranceM && Math.Abs(aMaxY - bMaxY) <= toleranceM;
                    if (!aligned) continue;

                    var perpGap = a.Direction == ZoneDirection.X
                        ? Math.Max(0, Math.Max(aMinY, bMinY) - Math.Min(aMaxY, bMaxY))
                        : Math.Max(0, Math.Max(aMinX, bMinX) - Math.Min(aMaxX, bMaxX));
                    if (perpGap > UnitConversion.MmToMeters(a.BarStepMm) + 1e-6)
                        continue;

                    var minX = Math.Min(aMinX, bMinX);
                    var maxX = Math.Max(aMaxX, bMaxX);
                    var minY = Math.Min(aMinY, bMinY);
                    var maxY = Math.Max(aMaxY, bMaxY);
                    a.Contour = new List<Point3>
                    {
                        new Point3(minX, minY, a.LevelZM),
                        new Point3(maxX, minY, a.LevelZM),
                        new Point3(maxX, maxY, a.LevelZM),
                        new Point3(minX, maxY, a.LevelZM)
                    };
                    a.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, a.LevelZM);
                    a.NodeIds = a.NodeIds.Concat(b.NodeIds).Distinct().ToList();
                    a.ElementId = a.NodeIds.FirstOrDefault();
                    a.AsRequired = Math.Max(a.AsRequired, b.AsRequired);
                    a.AsAdditional = Math.Max(a.AsAdditional, b.AsAdditional);
                    a.AsCoveredCm2PerM = Math.Max(a.AsCoveredCm2PerM, b.AsCoveredCm2PerM);
                    a.LengthM = a.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
                    a.WidthM = a.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
                    a.WidthMm = UnitConversion.MetersToMm(a.WidthM);
                    a.BarCount = Math.Max(2, (int)Math.Round(a.WidthMm / a.BarStepMm) + 1);
                    zones.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        private static void TrimZonesToActiveCells(
            List<AdditionalZone> zones, MosaicGrid mosaic, AnalysisSettings settings)
        {
            var cellM = mosaic.CellMm / 1000.0;
            for (var zoneIndex = zones.Count - 1; zoneIndex >= 0; zoneIndex--)
            {
                var zone = zones[zoneIndex];
                if (zone.Contour.Count < 3)
                {
                    zones.RemoveAt(zoneIndex);
                    continue;
                }
                var minX = zone.Contour.Min(p => p.X);
                var maxX = zone.Contour.Max(p => p.X);
                var minY = zone.Contour.Min(p => p.Y);
                var maxY = zone.Contour.Max(p => p.Y);
                var originalBounds = (MinX: minX, MaxX: maxX, MinY: minY, MaxY: maxY);
                var active = new List<(int Ix, int Iy)>();
                for (var iy = 0; iy < mosaic.Ny; iy++)
                for (var ix = 0; ix < mosaic.Nx; ix++)
                {
                    if (mosaic.Values[iy][ix] <= 0.01)
                        continue;
                    var cx = mosaic.OriginXM + (ix + 0.5) * cellM;
                    var cy = mosaic.OriginYM + (iy + 0.5) * cellM;
                    if (cx >= minX - 1e-6 && cx <= maxX + 1e-6 &&
                        cy >= minY - 1e-6 && cy <= maxY + 1e-6)
                        active.Add((ix, iy));
                }

                if (active.Count == 0)
                {
                    zones.RemoveAt(zoneIndex);
                    continue;
                }
                zone.NodeIds = active
                    .SelectMany(c => mosaic.PlateIds[c.Iy][c.Ix])
                    .Distinct()
                    .ToList();
                zone.ElementId = zone.NodeIds.FirstOrDefault();

                double coreMin, coreMax;
                if (zone.Direction == ZoneDirection.X)
                {
                    coreMin = mosaic.OriginYM + active.Min(c => c.Iy) * cellM;
                    coreMax = mosaic.OriginYM + (active.Max(c => c.Iy) + 1) * cellM;
                }
                else
                {
                    coreMin = mosaic.OriginXM + active.Min(c => c.Ix) * cellM;
                    coreMax = mosaic.OriginXM + (active.Max(c => c.Ix) + 1) * cellM;
                }

                var stepMm = Math.Max(1, zone.BarStepMm);
                var widthMm = Math.Ceiling(UnitConversion.MetersToMm(coreMax - coreMin) / stepMm) * stepMm;
                widthMm = Math.Max(widthMm, SettingToMm(settings.MinZoneWidthM));
                var widthM = UnitConversion.MmToMeters(widthMm);
                var mid = (coreMin + coreMax) / 2.0;
                var trimMin = mid - widthM / 2.0;
                var trimMax = mid + widthM / 2.0;
                if (zone.Direction == ZoneDirection.X)
                {
                    minY = Math.Max(minY, trimMin);
                    maxY = Math.Min(maxY, trimMax);
                }
                else
                {
                    minX = Math.Max(minX, trimMin);
                    maxX = Math.Min(maxX, trimMax);
                }
                if (maxX - minX < 0.05 || maxY - minY < 0.05)
                {
                    zones.RemoveAt(zoneIndex);
                    continue;
                }

                zone.Contour = new List<Point3>
                {
                    new Point3(minX, minY, zone.LevelZM),
                    new Point3(maxX, minY, zone.LevelZM),
                    new Point3(maxX, maxY, zone.LevelZM),
                    new Point3(minX, maxY, zone.LevelZM)
                };
                zone.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, zone.LevelZM);
                zone.LengthM = zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
                zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
                zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
                var minWidthMm = SettingToMm(settings.MinZoneWidthM);
                if (minWidthMm > 0 && zone.WidthMm + 1 < minWidthMm)
                {
                    minX = originalBounds.MinX;
                    maxX = originalBounds.MaxX;
                    minY = originalBounds.MinY;
                    maxY = originalBounds.MaxY;
                    zone.Contour = new List<Point3>
                    {
                        new Point3(minX, minY, zone.LevelZM),
                        new Point3(maxX, minY, zone.LevelZM),
                        new Point3(maxX, maxY, zone.LevelZM),
                        new Point3(minX, maxY, zone.LevelZM)
                    };
                    zone.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, zone.LevelZM);
                    zone.LengthM = zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
                    zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
                    zone.WidthMm = minWidthMm;
                }
                zone.BarCount = Math.Max(2, (int)Math.Round(zone.WidthMm / stepMm) + 1);
            }
        }

        private static void EnforceZoneGaps(List<AdditionalZone> zones, int gridCellMm)
        {
            foreach (var layerGroup in zones.GroupBy(z => z.Layer))
            {
                var direction = layerGroup.First().Direction;
                var ordered = layerGroup
                    .OrderBy(z => direction == ZoneDirection.X ? z.Placement.Y : z.Placement.X)
                    .ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var nearestHandled = false;
                    for (var j = i + 1; j < ordered.Count; j++)
                    {
                        var a = ordered[i];
                        var b = ordered[j];
                        if (a.Contour.Count < 3 || b.Contour.Count < 3)
                            continue;
                        var aMinX = a.Contour.Min(p => p.X);
                        var aMaxX = a.Contour.Max(p => p.X);
                        var aMinY = a.Contour.Min(p => p.Y);
                        var aMaxY = a.Contour.Max(p => p.Y);
                        var bMinX = b.Contour.Min(p => p.X);
                        var bMaxX = b.Contour.Max(p => p.X);
                        var bMinY = b.Contour.Min(p => p.Y);
                        var bMaxY = b.Contour.Max(p => p.Y);
                        var overlapX = Math.Min(aMaxX, bMaxX) - Math.Max(aMinX, bMinX);
                        var overlapY = Math.Min(aMaxY, bMaxY) - Math.Max(aMinY, bMinY);
                        var required = UnitConversion.MmToMeters(Math.Min(a.BarStepMm, b.BarStepMm));
                        if (IsAllowedLapOverlap(
                            a, b,
                            aMinX, aMaxX, aMinY, aMaxY,
                            bMinX, bMaxX, bMinY, bMaxY))
                            continue;
                        if (direction == ZoneDirection.X && overlapX <= 1e-6) continue;
                        if (direction == ZoneDirection.Y && overlapY <= 1e-6) continue;

                        double dx = 0, dy = 0;
                        if (direction == ZoneDirection.Y)
                        {
                            var gapX = bMinX - aMaxX;
                            if (gapX < required - 1e-6)
                                dx = aMaxX + required - bMinX;
                            else if (!nearestHandled && gapX > required + 1e-6 &&
                                     gapX <= required + UnitConversion.MmToMeters(gridCellMm) + 1e-6)
                            {
                                aMaxX = bMinX - required;
                                SetZoneBounds(a, aMinX, aMaxX, aMinY, aMaxY);
                            }
                        }
                        else
                        {
                            var gapY = bMinY - aMaxY;
                            if (gapY < required - 1e-6)
                                dy = aMaxY + required - bMinY;
                            else if (!nearestHandled && gapY > required + 1e-6 &&
                                     gapY <= required + UnitConversion.MmToMeters(gridCellMm) + 1e-6)
                            {
                                aMaxY = bMinY - required;
                                SetZoneBounds(a, aMinX, aMaxX, aMinY, aMaxY);
                            }
                        }
                        nearestHandled = true;
                        if (Math.Abs(dx) >= 1e-9 || Math.Abs(dy) >= 1e-9)
                        {
                            foreach (var p in b.Contour)
                            {
                                p.X += dx;
                                p.Y += dy;
                            }
                            b.Placement = new Point3(
                                b.Placement.X + dx, b.Placement.Y + dy, b.Placement.Z);
                        }
                    }
                }
            }
        }

        private static bool IsAllowedLapOverlap(
            AdditionalZone a, AdditionalZone b,
            double aMinX, double aMaxX, double aMinY, double aMaxY,
            double bMinX, double bMaxX, double bMinY, double bMaxY)
        {
            if (a.Layer != b.Layer || a.Direction != b.Direction) return false;
            var allowedMm = RebarTables.AllowedZoneOverlapMm(a, b);
            if (allowedMm <= 0) return false;
            var longitudinalOverlapM = a.Direction == ZoneDirection.X
                ? Math.Min(aMaxX, bMaxX) - Math.Max(aMinX, bMinX)
                : Math.Min(aMaxY, bMaxY) - Math.Max(aMinY, bMinY);
            var perpendicularOverlapM = a.Direction == ZoneDirection.X
                ? Math.Min(aMaxY, bMaxY) - Math.Max(aMinY, bMinY)
                : Math.Min(aMaxX, bMaxX) - Math.Max(aMinX, bMinX);
            return perpendicularOverlapM > 1e-6 &&
                   longitudinalOverlapM > 1e-6 &&
                   UnitConversion.MetersToMm(longitudinalOverlapM) + 1 >= allowedMm;
        }

        private static void SetZoneBounds(
            AdditionalZone zone, double minX, double maxX, double minY, double maxY)
        {
            zone.Contour = new List<Point3>
            {
                new Point3(minX, minY, zone.LevelZM),
                new Point3(maxX, minY, zone.LevelZM),
                new Point3(maxX, maxY, zone.LevelZM),
                new Point3(minX, maxY, zone.LevelZM)
            };
            zone.Placement = new Point3((minX + maxX) / 2.0, (minY + maxY) / 2.0, zone.LevelZM);
            zone.LengthM = zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
            zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(2, (int)Math.Ceiling(zone.WidthMm / zone.BarStepMm) + 1);
            zone.WidthMm = (zone.BarCount - 1) * zone.BarStepMm;
        }

        private static void FitRectInsideSlabBounds(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            IList<Point3>? outline, double insetMm)
        {
            if (outline == null || outline.Count < 3) return;
            var inset = Math.Max(0, insetMm) / 1000.0;
            var slabMinX = outline.Min(p => p.X) + inset;
            var slabMaxX = outline.Max(p => p.X) - inset;
            var slabMinY = outline.Min(p => p.Y) + inset;
            var slabMaxY = outline.Max(p => p.Y) - inset;

            if (maxXM - minXM <= slabMaxX - slabMinX)
            {
                var dx = minXM < slabMinX ? slabMinX - minXM
                    : maxXM > slabMaxX ? slabMaxX - maxXM
                    : 0;
                minXM += dx;
                maxXM += dx;
            }
            if (maxYM - minYM <= slabMaxY - slabMinY)
            {
                var dy = minYM < slabMinY ? slabMinY - minYM
                    : maxYM > slabMaxY ? slabMaxY - maxYM
                    : 0;
                minYM += dy;
                maxYM += dy;
            }
        }

        /// <summary>
        /// Зазор = шаг стержней перпендикулярно длине; сдвиг ограничен — иначе зона уезжает с пятна.
        /// </summary>
        private static bool ResolvePerpGap(
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            ZoneDirection direction, int currentStepMm,
            List<(double MinX, double MaxX, double MinY, double MaxY, int StepMm)> placed)
        {
            const int maxIter = 16;
            var origMinX = minXM;
            var origMaxX = maxXM;
            var origMinY = minYM;
            var origMaxY = maxYM;
            var maxShift = Math.Max(0.6, (direction == ZoneDirection.X ? (maxYM - minYM) : (maxXM - minXM)) * 2.5);

            for (var iter = 0; iter < maxIter; iter++)
            {
                var moved = false;
                foreach (var p in placed)
                {
                    var gapM = UnitConversion.MmToMeters(Math.Min(currentStepMm, p.StepMm));
                    var aMinX = minXM - (direction == ZoneDirection.Y ? gapM : 0);
                    var aMaxX = maxXM + (direction == ZoneDirection.Y ? gapM : 0);
                    var aMinY = minYM - (direction == ZoneDirection.X ? gapM : 0);
                    var aMaxY = maxYM + (direction == ZoneDirection.X ? gapM : 0);

                    if (aMaxX <= p.MinX || aMinX >= p.MaxX || aMaxY <= p.MinY || aMinY >= p.MaxY)
                        continue;

                    if (direction == ZoneDirection.X)
                    {
                        var cy = (minYM + maxYM) / 2.0;
                        var py = (p.MinY + p.MaxY) / 2.0;
                        var half = (maxYM - minYM) / 2.0;
                        var target = cy >= py ? p.MaxY + gapM + half : p.MinY - gapM - half;
                        var dy = target - cy;
                        minYM += dy;
                        maxYM += dy;
                        moved = true;
                    }
                    else
                    {
                        var cx = (minXM + maxXM) / 2.0;
                        var px = (p.MinX + p.MaxX) / 2.0;
                        var half = (maxXM - minXM) / 2.0;
                        var target = cx >= px ? p.MaxX + gapM + half : p.MinX - gapM - half;
                        var dx = target - cx;
                        minXM += dx;
                        maxXM += dx;
                        moved = true;
                    }
                }
                if (!moved) break;
            }

            var shift = direction == ZoneDirection.X
                ? Math.Abs(((minYM + maxYM) / 2.0) - ((origMinY + origMaxY) / 2.0))
                : Math.Abs(((minXM + maxXM) / 2.0) - ((origMinX + origMaxX) / 2.0));
            if (shift > maxShift)
            {
                minXM = origMinX; maxXM = origMaxX; minYM = origMinY; maxYM = origMaxY;
                return false;
            }

            foreach (var p in placed)
            {
                var gapM = UnitConversion.MmToMeters(Math.Min(currentStepMm, p.StepMm));
                var aMinX = minXM - (direction == ZoneDirection.Y ? gapM : 0);
                var aMaxX = maxXM + (direction == ZoneDirection.Y ? gapM : 0);
                var aMinY = minYM - (direction == ZoneDirection.X ? gapM : 0);
                var aMaxY = maxYM + (direction == ZoneDirection.X ? gapM : 0);
                if (!(aMaxX <= p.MinX || aMinX >= p.MaxX || aMaxY <= p.MinY || aMinY >= p.MaxY))
                {
                    minXM = origMinX; maxXM = origMaxX;
                    minYM = origMinY; maxYM = origMaxY;
                    return false;
                }
            }
            return true;
        }

        private static void ApplyHoleBent(
            AnalysisSettings settings,
            IList<OpeningInfo> openings,
            IList<Point3>? outline,
            RebarLayer layer,
            ZoneDirection direction,
            int dZone,
            string concrete,
            ref double minXM, ref double maxXM, ref double minYM, ref double maxYM,
            double coreCx, double coreCy,
            ref ZoneFamilyKind familyKind,
            ref double verticalLeg,
            ref string comment,
            ref bool countInSpec,
            ref bool countBars)
        {
            if (settings.ApplyHoleRules && openings.Count > 0)
            {
                foreach (var op in openings)
                {
                    if (HoleBentRules.ShouldIgnoreOpening(op, direction, settings.HoleIgnorePerpMm))
                        continue;
                    if (!HoleBentRules.RectIntersects(op, minXM, maxXM, minYM, maxYM))
                        continue;
                    var off = UnitConversion.MmToMeters(settings.EdgeOffsetMm);
                    if (direction == ZoneDirection.X)
                    {
                        if (coreCy < (op.MinYM + op.MaxYM) / 2) maxYM = Math.Min(maxYM, op.MinYM - off);
                        else minYM = Math.Max(minYM, op.MaxYM + off);
                    }
                    else
                    {
                        if (coreCx < (op.MinXM + op.MaxXM) / 2) maxXM = Math.Min(maxXM, op.MinXM - off);
                        else minXM = Math.Max(minXM, op.MaxXM + off);
                    }
                    if (settings.ApplyBentRules)
                    {
                        verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                            settings.SlabThicknessMm, settings.CoverTopMm, settings.CoverBottomMm, dZone);
                        familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, dZone);
                        countInSpec = false;
                        countBars = true;
                        comment = "отверстие: гнутая деталь";
                    }
                }
            }

            if (settings.ApplyBentRules && outline != null && outline.Count >= 3)
            {
                var oMinX = outline.Min(p => p.X);
                var oMaxX = outline.Max(p => p.X);
                var oMinY = outline.Min(p => p.Y);
                var oMaxY = outline.Max(p => p.Y);
                var off = UnitConversion.MmToMeters(settings.EdgeOffsetMm);
                // A zone can require a bent edge detail at either a longitudinal
                // or a transverse slab edge. Restricting this by bar direction
                // made vertical As2 zones ignore the left/right slab boundary.
                var nearEdge = minXM < oMinX + off || maxXM > oMaxX - off ||
                               minYM < oMinY + off || maxYM > oMaxY - off;
                if (nearEdge && familyKind == ZoneFamilyKind.Straight)
                {
                    verticalLeg = HoleBentRules.VerticalLegAvailableMm(
                        settings.SlabThicknessMm, settings.CoverTopMm, settings.CoverBottomMm, dZone);
                    familyKind = HoleBentRules.ChooseBentFamily(verticalLeg, dZone);
                    countInSpec = false;
                    countBars = true;
                    if (string.IsNullOrEmpty(comment)) comment = "торец: гнутая деталь";
                }
            }
        }

        private static double GetAsMain(AnalysisSettings s, RebarLayer layer) => layer switch
        {
            RebarLayer.As1 => s.AsMainAs1,
            RebarLayer.As2 => s.AsMainAs2,
            RebarLayer.As3 => s.AsMainAs3,
            _ => s.AsMainAs4
        };

        private static bool[][] LocalMaxima(double[][] area)
        {
            var ny = area.Length;
            var nx = area[0].Length;
            var mask = new bool[ny][];
            for (var iy = 0; iy < ny; iy++)
                mask[iy] = new bool[nx];

            for (var iy = 0; iy < ny; iy++)
            for (var ix = 0; ix < nx; ix++)
            {
                var v = area[iy][ix];
                if (v <= 0) continue;
                var ok = true;
                if (iy > 0 && v < area[iy - 1][ix]) ok = false;
                if (iy + 1 < ny && v < area[iy + 1][ix]) ok = false;
                if (ix > 0 && v < area[iy][ix - 1]) ok = false;
                if (ix + 1 < nx && v < area[iy][ix + 1]) ok = false;
                mask[iy][ix] = ok;
            }
            return mask;
        }

        private static List<(double Start, double End)> SplitByMaxLength(
            double longStartMm,
            double longEndMm,
            int diameterMm,
            string concreteClass,
            double alpha)
        {
            var lengths = RebarTables.Sum3FamilyLengthsMm;
            var maxL = lengths[lengths.Length - 1];
            if (longEndMm - longStartMm <= maxL)
                return new List<(double, double)> { (longStartMm, longEndMm) };

            var lap = RebarTables.LapLenMm(concreteClass, diameterMm);
            var overlap = 2.0 * alpha * lap;
            if (overlap >= maxL) overlap = maxL * 0.5;
            var advance = Math.Max(maxL - overlap, maxL * 0.5);

            var segs = new List<(double, double)>();
            var cur = longStartMm;
            while (cur < longEndMm - 1e-6)
            {
                var end = Math.Min(cur + maxL, longEndMm);
                segs.Add((cur, end));
                if (end >= longEndMm - 1e-6) break;
                cur += advance;
            }
            return segs;
        }
    }
}
