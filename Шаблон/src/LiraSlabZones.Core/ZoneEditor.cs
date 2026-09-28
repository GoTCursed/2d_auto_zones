using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace LiraSlabZones.Core
{
    public static class ZoneEditor
    {
        private const double Scale = 1000000.0;

        public static bool Move(AdditionalZone zone, double dxM, double dyM, IList<Point3> slab)
        {
            var moved = zone.Contour
                .Select(p => new Point3(p.X + dxM, p.Y + dyM, p.Z))
                .ToList();
            return ApplyClipped(zone, moved, slab);
        }

        public static bool Resize(
            AdditionalZone zone, double minX, double maxX, double minY, double maxY,
            IList<Point3> slab)
        {
            return ApplyClipped(zone, Rectangle(minX, maxX, minY, maxY, zone.LevelZM), slab);
        }

        public static void SetDiameter(AdditionalZone zone, int diameterMm)
        {
            if (diameterMm <= 0) throw new ArgumentOutOfRangeException(nameof(diameterMm));
            zone.DiameterMm = diameterMm;
            zone.AsCoveredCm2PerM = BarCapacity.AsCm2PerM(diameterMm, Math.Max(1, zone.BarStepMm));
            zone.Comment = "диаметр изменён в предпросмотре";
        }

        public static void SetStep(AdditionalZone zone, int stepMm)
        {
            if (stepMm != 100 && stepMm != 200) throw new ArgumentOutOfRangeException(nameof(stepMm));
            zone.BarStepMm = stepMm;
            zone.BarCount = Math.Max(2, (int)Math.Ceiling(zone.WidthMm / stepMm) + 1);
            zone.AsCoveredCm2PerM = BarCapacity.AsCm2PerM(zone.DiameterMm, stepMm);
            zone.Comment = "шаг изменён в предпросмотре";
        }

        public static List<AdditionalZone> SplitPerpendicularToEdge(
            AdditionalZone zone, double xM, double yM, bool verticalEdge, IList<Point3> slab)
        {
            // A vertical edge determines a horizontal cut, and vice versa.
            return Split(zone, verticalEdge ? yM : xM, !verticalEdge, slab);
        }

        public static bool ResizeByDimensions(AdditionalZone zone, double lengthMm, double widthMm, IList<Point3> slab)
        {
            if (lengthMm <= 50 || widthMm <= 50) return false;
            var halfX = (zone.Direction == ZoneDirection.X ? lengthMm : widthMm) / 2000.0;
            var halfY = (zone.Direction == ZoneDirection.Y ? lengthMm : widthMm) / 2000.0;
            var candidate = Copy(zone);
            if (!Resize(candidate, zone.Placement.X - halfX, zone.Placement.X + halfX,
                    zone.Placement.Y - halfY, zone.Placement.Y + halfY, slab)) return false;
            SetContour(zone, candidate.Contour.ToList());
            zone.Comment = "габариты изменены в предпросмотре";
            return true;
        }

        public static void SetFamily(AdditionalZone zone, ZoneFamilyKind kind, string familyName)
        {
            var name = AppConfig.StripRfa(familyName);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Имя семейства не задано.", nameof(familyName));
            zone.FamilyKind = kind;
            zone.FamilyFileName = name;
            SetContour(zone, zone.Contour.ToList());
            zone.Comment = "семейство изменено в предпросмотре";
        }

        public static bool CreateGap(AdditionalZone moving, AdditionalZone reference, IList<Point3> slab)
        {
            if (ReferenceEquals(moving, reference)) return false;
            var gap = Math.Min(moving.BarStepMm, reference.BarStepMm) / 1000.0;
            var a = Bounds(moving.Contour);
            var b = Bounds(reference.Contour);

            var candidates = new[]
            {
                (Dx: b.MinX - gap - a.MaxX, Dy: 0.0),
                (Dx: b.MaxX + gap - a.MinX, Dy: 0.0),
                (Dx: 0.0, Dy: b.MinY - gap - a.MaxY),
                (Dx: 0.0, Dy: b.MaxY + gap - a.MinY)
            }.OrderBy(v => Math.Abs(v.Dx) + Math.Abs(v.Dy));

            foreach (var candidate in candidates)
            {
                var copy = Copy(moving);
                var shifted = moving.Contour
                    .Select(p => new Point3(p.X + candidate.Dx, p.Y + candidate.Dy, p.Z))
                    .ToList();
                if (!ApplyClipped(copy, shifted, slab)) continue;
                if (!HasRequiredGap(copy.Contour, reference.Contour, gap)) continue;
                SetContour(moving, copy.Contour.ToList());
                moving.Comment = $"зазор {gap * 1000:0} мм создан в предпросмотре";
                return true;
            }
            return false;
        }

        private static bool HasRequiredGap(IList<Point3> first, IList<Point3> second, double required)
        {
            var a = Bounds(first);
            var b = Bounds(second);
            var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            var gapX = Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            var gapY = Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY));
            return (overlapY > 1e-6 && gapX + 1e-6 >= required) ||
                   (overlapX > 1e-6 && gapY + 1e-6 >= required);
        }

        public static bool HasPlacementConflict(AdditionalZone first, AdditionalZone second)
        {
            if (first.Layer != second.Layer || first.Contour.Count < 3 || second.Contour.Count < 3)
                return false;
            var a = Bounds(first.Contour);
            var b = Bounds(second.Contour);
            var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (overlapX > 1e-6 && overlapY > 1e-6)
            {
                if (IsLocalBentRecovery(first) || IsLocalBentRecovery(second))
                    return false;
                var allowedMm = RebarTables.AllowedZoneOverlapMm(first, second);
                var longitudinalMm = UnitConversion.MetersToMm(first.Direction == ZoneDirection.X
                    ? overlapX : overlapY);
                return allowedMm <= 0 || longitudinalMm + 1 < allowedMm;
            }
            var requiredGap = Math.Min(first.BarStepMm, second.BarStepMm) / 1000.0;
            var gapX = Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            var gapY = Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY));
            return (overlapY > 1e-6 && gapX < requiredGap - 1e-6) ||
                   (overlapX > 1e-6 && gapY < requiredGap - 1e-6);
        }

        public static void EnforceRequiredGaps(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates)
        {
            var centroids = plates.ToDictionary(p => p.Id, p => p.Centroid);
            foreach (var layerGroup in zones.GroupBy(z => z.Layer))
            {
                var layerZones = layerGroup.Where(z => z.Contour.Count >= 3).ToList();
                for (var i = 0; i < layerZones.Count; i++)
                for (var j = i + 1; j < layerZones.Count; j++)
                {
                    var first = layerZones[i];
                    var second = layerZones[j];
                    if (first.Direction != second.Direction) continue;
                    var a = Bounds(first.Contour);
                    var b = Bounds(second.Contour);
                    var overlapX = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    var overlapY = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    var required = UnitConversion.MmToMeters(
                        Math.Min(first.BarStepMm, second.BarStepMm));

                    if (overlapX > 1e-6 && overlapY > 1e-6)
                    {
                        var longitudinalOverlap = first.Direction == ZoneDirection.X
                            ? overlapX : overlapY;
                        var transverseOverlap = first.Direction == ZoneDirection.X
                            ? overlapY : overlapX;
                        var allowed = UnitConversion.MmToMeters(
                            RebarTables.AllowedZoneOverlapMm(first, second));
                        if (allowed > 0 && longitudinalOverlap + 0.001 >= allowed)
                            continue;

                        // A short overlap in the bar direction is an end joint,
                        // not side-by-side placement. Resolve it along the bars.
                        if (longitudinalOverlap <= transverseOverlap)
                        {
                            if (first.Direction == ZoneDirection.X)
                            {
                                var left = first.Placement.X <= second.Placement.X ? first : second;
                                var right = ReferenceEquals(left, first) ? second : first;
                                EnsureAxisGap(left, right, 0, true, centroids);
                            }
                            else
                            {
                                var bottom = first.Placement.Y <= second.Placement.Y ? first : second;
                                var top = ReferenceEquals(bottom, first) ? second : first;
                                EnsureAxisGap(bottom, top, 0, false, centroids);
                            }
                            continue;
                        }
                    }

                    // Clearance is transverse to the bars. Reversed layers must
                    // therefore use their resolved direction here as well.
                    if (first.Direction == ZoneDirection.Y && overlapY > 1e-6)
                    {
                        var left = first.Placement.X <= second.Placement.X ? first : second;
                        var right = ReferenceEquals(left, first) ? second : first;
                        EnsureAxisGap(left, right, required, true, centroids);
                    }
                    else if (first.Direction == ZoneDirection.X && overlapX > 1e-6)
                    {
                        var bottom = first.Placement.Y <= second.Placement.Y ? first : second;
                        var top = ReferenceEquals(bottom, first) ? second : first;
                        EnsureAxisGap(bottom, top, required, false, centroids);
                    }
                }
            }
        }

        public static void MergeLongitudinalConflicts(
            IList<AdditionalZone> zones, IList<Point3> slab)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < zones.Count && !changed; i++)
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var first = zones[i];
                    var second = zones[j];
                    if (first.Layer != second.Layer || first.Direction != second.Direction ||
                        first.DiameterMm != second.DiameterMm || first.BarStepMm != second.BarStepMm ||
                        first.FamilyKind != ZoneFamilyKind.Straight ||
                        second.FamilyKind != ZoneFamilyKind.Straight)
                        continue;
                    var a = Bounds(first.Contour);
                    var b = Bounds(second.Contour);
                    var longitudinalOverlap = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                        : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    var transverseOverlap = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY)
                        : Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    var minTransverse = first.Direction == ZoneDirection.X
                        ? Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
                        : Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX);
                    if (longitudinalOverlap <= 1e-6 ||
                        transverseOverlap < minTransverse * 0.8)
                        continue;
                    var merged = Merge(first, second, slab);
                    if (merged == null) continue;
                    SetContour(merged, Rectangle(
                        Math.Min(a.MinX, b.MinX), Math.Max(a.MaxX, b.MaxX),
                        Math.Min(a.MinY, b.MinY), Math.Max(a.MaxY, b.MaxY),
                        merged.LevelZM));
                    merged.Comment = "объединено перед обходом отверстия";
                    zones[i] = merged;
                    zones.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        public static void AbsorbNarrowSplitParts(
            IList<AdditionalZone> parts, double minimumWidthM)
        {
            if (minimumWidthM <= 0) return;
            var narrow = parts.Where(part => part.Contour.Count >= 3 &&
                part.FamilyKind == ZoneFamilyKind.Straight &&
                part.WidthM + 1e-6 < minimumWidthM).ToList();
            foreach (var sliver in narrow)
            {
                if (!parts.Contains(sliver)) continue;
                var a = Bounds(sliver.Contour);
                var target = parts.Where(candidate => !ReferenceEquals(candidate, sliver) &&
                        candidate.Layer == sliver.Layer &&
                        candidate.Direction == sliver.Direction &&
                        candidate.DiameterMm == sliver.DiameterMm &&
                        candidate.BarStepMm == sliver.BarStepMm &&
                        candidate.FamilyKind == ZoneFamilyKind.Straight)
                    .Select(candidate =>
                    {
                        var b = Bounds(candidate.Contour);
                        var longitudinalOverlap = sliver.Direction == ZoneDirection.X
                            ? Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX)
                            : Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                        var transverseGap = sliver.Direction == ZoneDirection.X
                            ? Math.Max(0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY))
                            : Math.Max(0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
                        return new { Zone = candidate, Bounds = b, longitudinalOverlap, transverseGap };
                    })
                    .Where(item => item.longitudinalOverlap > 1e-6 &&
                        item.transverseGap <= UnitConversion.MmToMeters(sliver.BarStepMm) + 1e-6)
                    .OrderBy(item => item.transverseGap)
                    .FirstOrDefault();
                if (target == null) continue;
                SetContour(target.Zone, Rectangle(
                    Math.Min(a.MinX, target.Bounds.MinX), Math.Max(a.MaxX, target.Bounds.MaxX),
                    Math.Min(a.MinY, target.Bounds.MinY), Math.Max(a.MaxY, target.Bounds.MaxY),
                    target.Zone.LevelZM));
                target.Zone.NodeIds = target.Zone.NodeIds.Concat(sliver.NodeIds).Distinct().ToList();
                target.Zone.ElementId = target.Zone.NodeIds.FirstOrDefault();
                target.Zone.AsRequired = Math.Max(target.Zone.AsRequired, sliver.AsRequired);
                target.Zone.AsAdditional = Math.Max(target.Zone.AsAdditional, sliver.AsAdditional);
                parts.Remove(sliver);
            }
        }

        public static void EnforceMaximumDetailLength(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline,
            int maximumLengthMm = 11700)
        {
            var centroids = plates.ToDictionary(plate => plate.Id, plate => plate.Centroid);
            for (var index = zones.Count - 1; index >= 0; index--)
            {
                var source = zones[index];
                if (source.Contour.Count < 3 || source.LengthMm <= maximumLengthMm + 1)
                    continue;
                var bounds = Bounds(source.Contour);
                var bent = source.FamilyKind != ZoneFamilyKind.Straight;
                var legCount = source.FamilyKind is ZoneFamilyKind.PEqual or ZoneFamilyKind.PDiff ? 2 : 1;
                var maxPlanMm = maximumLengthMm -
                    (bent ? legCount * Math.Max(0, source.VerticalLegMm) : 0);
                if (maxPlanMm <= 50)
                {
                    source.IsValid = false;
                    source.StatusColor = "error";
                    source.Comment = "деталь превышает 11700 мм: вертикальные участки слишком велики";
                    continue;
                }

                var startMm = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X
                    ? bounds.MinX : bounds.MinY);
                var endMm = UnitConversion.MetersToMm(source.Direction == ZoneDirection.X
                    ? bounds.MaxX : bounds.MaxY);
                var lapMm = Math.Min(maxPlanMm - 1,
                    2 * RebarTables.LapLenMm(source.ConcreteClass, source.DiameterMm));
                var piecePlanLimitMm = bent ? maxPlanMm - 1 : maxPlanMm;
                var ranges = new List<(double Start, double End)>();
                if (source.LengthMm - maximumLengthMm <= 50)
                {
                    var centerMm = (startMm + endMm) / 2.0;
                    ranges.Add((centerMm - piecePlanLimitMm / 2.0,
                        centerMm + piecePlanLimitMm / 2.0));
                }
                else
                {
                    var cursor = startMm;
                    while (endMm - cursor > piecePlanLimitMm + 1)
                    {
                        ranges.Add((cursor, cursor + piecePlanLimitMm));
                        cursor += piecePlanLimitMm - lapMm;
                    }
                    var remainingMm = endMm - cursor;
                    var lastPlanMm = bent
                        ? remainingMm
                        : Math.Min(piecePlanLimitMm, RebarTables.PickFamilyLength(remainingMm));
                    ranges.Add((endMm - lastPlanMm, endMm));
                }

                var replacements = new List<AdditionalZone>();
                foreach (var range in ranges)
                {
                    var copy = Copy(source);
                    var a = UnitConversion.MmToMeters(range.Start);
                    var b = UnitConversion.MmToMeters(range.End);
                    var minX = source.Direction == ZoneDirection.X ? a : bounds.MinX;
                    var maxX = source.Direction == ZoneDirection.X ? b : bounds.MaxX;
                    var minY = source.Direction == ZoneDirection.Y ? a : bounds.MinY;
                    var maxY = source.Direction == ZoneDirection.Y ? b : bounds.MaxY;
                    if (!RectangleInsideOutline(minX, maxX, minY, maxY, outline))
                    {
                        var sourcePoints = source.NodeIds
                            .Where(centroids.ContainsKey)
                            .Select(id => centroids[id]).ToList();
                        TryShiftRectangleInsideOutline(ref minX, ref maxX, ref minY, ref maxY,
                            source.Direction, outline, sourcePoints);
                    }
                    SetContour(copy, Rectangle(minX, maxX, minY, maxY, source.LevelZM));
                    if (copy.LengthMm > maximumLengthMm + 1)
                        continue;
                    copy.NodeIds = source.NodeIds.Where(id => centroids.TryGetValue(id, out var point) &&
                        point.X >= copy.Contour.Min(p => p.X) - 1e-6 &&
                        point.X <= copy.Contour.Max(p => p.X) + 1e-6 &&
                        point.Y >= copy.Contour.Min(p => p.Y) - 1e-6 &&
                        point.Y <= copy.Contour.Max(p => p.Y) + 1e-6).ToList();
                    copy.ElementId = copy.NodeIds.FirstOrDefault();
                    copy.Comment = "разделено по максимальной длине детали 11700 мм";
                    replacements.Add(copy);
                }
                if (replacements.Count == 0)
                {
                    source.IsValid = false;
                    source.StatusColor = "warn";
                    source.Comment = "превышение 11700 мм: не удалось построить части внутри плиты";
                    continue;
                }
                zones.RemoveAt(index);
                foreach (var replacement in replacements)
                    zones.Insert(index++, replacement);
                index -= replacements.Count;
            }
        }

        public static void MergeDominatedOpeningExtensions(IList<AdditionalZone> zones)
        {
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var weaker = zones[i];
                if (weaker.FamilyKind != ZoneFamilyKind.Straight || weaker.NodeIds.Count > 3)
                    continue;
                var weakBounds = Bounds(weaker.Contour);
                var candidate = zones.Where((stronger, index) => index != i &&
                        stronger.Layer == weaker.Layer && stronger.Direction == weaker.Direction &&
                        stronger.FamilyKind == ZoneFamilyKind.Straight &&
                        stronger.DiameterMm > weaker.DiameterMm &&
                        stronger.Comment?.IndexOf("обход отверстия", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(stronger =>
                    {
                        var bounds = Bounds(stronger.Contour);
                        var transverseOverlap = stronger.Direction == ZoneDirection.X
                            ? Math.Min(bounds.MaxY, weakBounds.MaxY) - Math.Max(bounds.MinY, weakBounds.MinY)
                            : Math.Min(bounds.MaxX, weakBounds.MaxX) - Math.Max(bounds.MinX, weakBounds.MinX);
                        var weakWidth = weaker.Direction == ZoneDirection.X
                            ? weakBounds.MaxY - weakBounds.MinY : weakBounds.MaxX - weakBounds.MinX;
                        var longitudinalGap = stronger.Direction == ZoneDirection.X
                            ? Math.Max(0, Math.Max(bounds.MinX, weakBounds.MinX) - Math.Min(bounds.MaxX, weakBounds.MaxX))
                            : Math.Max(0, Math.Max(bounds.MinY, weakBounds.MinY) - Math.Min(bounds.MaxY, weakBounds.MaxY));
                        return new { Zone = stronger, Bounds = bounds, transverseOverlap, weakWidth, longitudinalGap };
                    })
                    .Where(item => item.transverseOverlap >= item.weakWidth * 0.8 &&
                        item.longitudinalGap <= UnitConversion.MmToMeters(weaker.BarStepMm) + 1e-6)
                    .OrderBy(item => item.longitudinalGap)
                    .FirstOrDefault();
                if (candidate == null) continue;

                var strong = candidate.Zone;
                var longitudinalMin = strong.Direction == ZoneDirection.X
                    ? Math.Min(candidate.Bounds.MinX, weakBounds.MinX)
                    : Math.Min(candidate.Bounds.MinY, weakBounds.MinY);
                var longitudinalMax = strong.Direction == ZoneDirection.X
                    ? Math.Max(candidate.Bounds.MaxX, weakBounds.MaxX)
                    : Math.Max(candidate.Bounds.MaxY, weakBounds.MaxY);
                var familyMm = RebarTables.PickFamilyLength(
                    UnitConversion.MetersToMm(longitudinalMax - longitudinalMin));
                if (familyMm > 11700) continue;
                var center = (longitudinalMin + longitudinalMax) / 2.0;
                var half = UnitConversion.MmToMeters(familyMm) / 2.0;
                SetContour(strong, strong.Direction == ZoneDirection.X
                    ? Rectangle(center - half, center + half,
                        candidate.Bounds.MinY, candidate.Bounds.MaxY, strong.LevelZM)
                    : Rectangle(candidate.Bounds.MinX, candidate.Bounds.MaxX,
                        center - half, center + half, strong.LevelZM));
                strong.NodeIds = strong.NodeIds.Concat(weaker.NodeIds).Distinct().ToList();
                strong.ElementId = strong.NodeIds.FirstOrDefault();
                strong.Comment = "переходная зона объединена у отверстия";
                zones.RemoveAt(i);
            }
        }

        public static void NormalizeWidthsToBarStep(
            IList<AdditionalZone> zones, IList<LiraPlateElement> plates,
            IList<Point3> outline)
        {
            var centroids = plates.ToDictionary(plate => plate.Id, plate => plate.Centroid);
            foreach (var zone in zones.Where(zone => zone.Contour.Count >= 3 &&
                zone.BarStepMm > 0 && zone.FamilyKind == ZoneFamilyKind.Straight))
            {
                var bounds = Bounds(zone.Contour);
                var widthMm = UnitConversion.MetersToMm(zone.Direction == ZoneDirection.X
                    ? bounds.MaxY - bounds.MinY : bounds.MaxX - bounds.MinX);
                var modules = Math.Max(1, (int)Math.Floor((widthMm + 1) / zone.BarStepMm));
                var targetWidthM = UnitConversion.MmToMeters(modules * zone.BarStepMm);
                var minX = bounds.MinX;
                var maxX = bounds.MaxX;
                var minY = bounds.MinY;
                var maxY = bounds.MaxY;
                if (zone.Direction == ZoneDirection.X)
                {
                    var center = (minY + maxY) / 2.0;
                    minY = center - targetWidthM / 2.0;
                    maxY = center + targetWidthM / 2.0;
                }
                else
                {
                    var center = (minX + maxX) / 2.0;
                    minX = center - targetWidthM / 2.0;
                    maxX = center + targetWidthM / 2.0;
                }
                if (!RectangleInsideOutline(minX, maxX, minY, maxY, outline))
                {
                    var assigned = zone.NodeIds.Where(centroids.ContainsKey)
                        .Select(id => centroids[id]).ToList();
                    TryShiftRectangleInsideOutline(ref minX, ref maxX, ref minY, ref maxY,
                        zone.Direction, outline, assigned);
                }
                SetContour(zone, Rectangle(minX, maxX, minY, maxY, zone.LevelZM));
            }
        }

        private static bool RectangleInsideOutline(
            double minX, double maxX, double minY, double maxY,
            IList<Point3> outline)
        {
            if (outline == null || outline.Count < 3) return true;
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            return Rectangle(minX, maxX, minY, maxY, 0).All(point =>
            {
                var x = point.X + (centerX - point.X) * 0.001;
                var y = point.Y + (centerY - point.Y) * 0.001;
                return MeshBoundary.PointInPolygon(x, y, outline);
            });
        }

        private static bool TryShiftRectangleInsideOutline(
            ref double minX, ref double maxX, ref double minY, ref double maxY,
            ZoneDirection direction, IList<Point3> outline, IList<Point3> assigned)
        {
            var bestShift = double.NaN;
            var bestCovered = -1;
            const double stepM = 0.05;
            const int maxSteps = 40;
            for (var step = 0; step <= maxSteps; step++)
            foreach (var sign in step == 0 ? new[] { 0 } : new[] { -1, 1 })
            {
                var shift = step * stepM * sign;
                var x1 = minX + (direction == ZoneDirection.Y ? shift : 0);
                var x2 = maxX + (direction == ZoneDirection.Y ? shift : 0);
                var y1 = minY + (direction == ZoneDirection.X ? shift : 0);
                var y2 = maxY + (direction == ZoneDirection.X ? shift : 0);
                if (!RectangleInsideOutline(x1, x2, y1, y2, outline)) continue;
                var covered = assigned.Count(point =>
                    point.X >= x1 - 1e-6 && point.X <= x2 + 1e-6 &&
                    point.Y >= y1 - 1e-6 && point.Y <= y2 + 1e-6);
                if (covered < bestCovered) continue;
                if (covered == bestCovered && !double.IsNaN(bestShift) &&
                    Math.Abs(shift) >= Math.Abs(bestShift)) continue;
                bestCovered = covered;
                bestShift = shift;
            }
            if (double.IsNaN(bestShift)) return false;
            if (direction == ZoneDirection.Y) { minX += bestShift; maxX += bestShift; }
            else { minY += bestShift; maxY += bestShift; }
            return true;
        }

        private static void EnsureAxisGap(
            AdditionalZone before, AdditionalZone after, double required, bool alongX,
            IReadOnlyDictionary<int, Point3> centroids)
        {
            var a = Bounds(before.Contour);
            var b = Bounds(after.Contour);
            var current = alongX ? b.MinX - a.MaxX : b.MinY - a.MaxY;
            var shortage = required - current;
            if (shortage <= 1e-6) return;

            const double coverToleranceM = 0.001;
            var beforePoints = before.NodeIds.Where(centroids.ContainsKey).Select(id => centroids[id]).ToList();
            var afterPoints = after.NodeIds.Where(centroids.ContainsKey).Select(id => centroids[id]).ToList();
            var beforeLimit = beforePoints.Count > 0
                ? (alongX ? beforePoints.Max(p => p.X) : beforePoints.Max(p => p.Y))
                : (alongX ? a.MaxX : a.MaxY);
            var afterLimit = afterPoints.Count > 0
                ? (alongX ? afterPoints.Min(p => p.X) : afterPoints.Min(p => p.Y))
                : (alongX ? b.MinX : b.MinY);
            var trimAfter = Math.Min(shortage, Math.Max(0,
                (alongX ? afterLimit - b.MinX : afterLimit - b.MinY) - coverToleranceM));
            if (trimAfter > 0)
            {
                if (alongX) b.MinX += trimAfter; else b.MinY += trimAfter;
                shortage -= trimAfter;
            }
            var trimBefore = Math.Min(shortage,
                Math.Max(0, (alongX ? a.MaxX - beforeLimit : a.MaxY - beforeLimit) - coverToleranceM));
            if (trimBefore > 0)
            {
                if (alongX) a.MaxX -= trimBefore; else a.MaxY -= trimBefore;
                shortage -= trimBefore;
            }
            // When contours merely touch, their FE ownership can still contain
            // stale shared ids after a split. Create the clearance symmetrically;
            // do not use this fallback for a real area overlap.
            if (shortage > 1e-6 && (current >= -1e-5 || required <= 1e-6))
            {
                var half = shortage / 2.0;
                if (alongX)
                {
                    a.MaxX -= half;
                    b.MinX += shortage - half;
                }
                else
                {
                    a.MaxY -= half;
                    b.MinY += shortage - half;
                }
                shortage = 0;
            }
            if (shortage > 1e-6) return;

            SetContour(before, Rectangle(a.MinX, a.MaxX, a.MinY, a.MaxY, before.LevelZM));
            SetContour(after, Rectangle(b.MinX, b.MaxX, b.MinY, b.MaxY, after.LevelZM));
        }

        private static bool IsLocalBentRecovery(AdditionalZone zone) =>
            zone.FamilyKind != ZoneFamilyKind.Straight &&
            zone.Comment?.IndexOf("локальная гнутая деталь", StringComparison.OrdinalIgnoreCase) >= 0;

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(IList<Point3> contour) =>
            (contour.Min(p => p.X), contour.Max(p => p.X),
             contour.Min(p => p.Y), contour.Max(p => p.Y));

        public static bool IntersectsOpening(AdditionalZone zone, IList<OpeningInfo> openings)
        {
            if (zone.Contour.Count < 3) return false;
            foreach (var opening in openings)
            {
                var overlap = Clipper.Intersect(new Paths64 { ToPath(zone.Contour) },
                    new Paths64 { ToPath(Rectangle(opening.MinXM, opening.MaxXM,
                        opening.MinYM, opening.MaxYM, zone.LevelZM)) }, FillRule.NonZero);
                if (overlap.Any(path => Math.Abs(Clipper.Area(path)) > 1)) return true;
            }
            return false;
        }

        public static List<AdditionalZone> ExcludeOpenings(AdditionalZone zone, IList<OpeningInfo> openings)
        {
            var parts = new List<AdditionalZone> { zone };
            foreach (var opening in openings)
            {
                var next = new List<AdditionalZone>();
                foreach (var part in parts)
                {
                    var bounds = Bounds(part.Contour);
                    var left = Math.Max(bounds.MinX, opening.MinXM);
                    var right = Math.Min(bounds.MaxX, opening.MaxXM);
                    var bottom = Math.Max(bounds.MinY, opening.MinYM);
                    var top = Math.Min(bounds.MaxY, opening.MaxYM);
                    if (right - left <= 1e-6 || top - bottom <= 1e-6)
                    {
                        next.Add(part);
                        continue;
                    }
                    void AddPiece(double x0, double x1, double y0, double y1)
                    {
                        if (x1 - x0 <= 0.05 || y1 - y0 <= 0.05) return;
                        var clipped = Clipper.Intersect(new Paths64 { ToPath(part.Contour) },
                            new Paths64 { ToPath(Rectangle(x0, x1, y0, y1, part.LevelZM)) }, FillRule.NonZero);
                        foreach (var path in clipped.Where(p => p.Count >= 3))
                        {
                            var copy = Copy(part);
                            SetContour(copy, FromPath(path, copy.LevelZM));
                            if (copy.LengthM <= 0.05 || copy.WidthM <= 0.05) continue;
                            copy.Comment = "подрезано по отверстию";
                            next.Add(copy);
                        }
                    }
                    AddPiece(bounds.MinX, left, bounds.MinY, bounds.MaxY);
                    AddPiece(right, bounds.MaxX, bounds.MinY, bounds.MaxY);
                    AddPiece(left, right, bounds.MinY, bottom);
                    AddPiece(left, right, top, bounds.MaxY);
                }
                parts = next;
            }
            return parts;
        }

        public static List<AdditionalZone> SplitAtOpenings(
            AdditionalZone zone, IList<OpeningInfo> openings, AnalysisSettings settings,
            IList<LiraPlateElement>? plates = null)
        {
            // Direction is a layer setting, not persistent geometry. Re-resolve it here
            // because imported/edited zones may still carry the pre-reverse direction.
            var effectiveDirection = RebarTables.DirectionForLayer(
                zone.Layer, settings.ReverseZoneDirections);
            zone.Direction = effectiveDirection;
            var zoneBounds = Bounds(zone.Contour);
            // A straight family is centred and rounded up only after opening
            // processing. Include a nearby opening that the final anchored bar can
            // reach; checking only the preliminary rectangle misses openings beside
            // a reversed X/Y layer.
            var influenceMm = Math.Max(0, settings.EdgeOffsetMm);
            if (settings.ReverseZoneDirections && effectiveDirection == ZoneDirection.X)
                influenceMm += RebarTables.AnchorageLenMm(
                    settings.ConcreteClass, zone.DiameterMm) + Math.Max(0, settings.GridCellMm);
            var influenceGap = UnitConversion.MmToMeters(influenceMm) + 1e-5;
            var relevant = openings.Where(op => !HoleBentRules.ShouldIgnoreOpening(
                    op, effectiveDirection, settings.HoleIgnorePerpMm))
                .Where(op => HoleBentRules.RectIntersects(op,
                        zoneBounds.MinX, zoneBounds.MaxX, zoneBounds.MinY, zoneBounds.MaxY) ||
                    (effectiveDirection == ZoneDirection.X
                        ? Math.Min(zoneBounds.MaxY, op.MaxYM) - Math.Max(zoneBounds.MinY, op.MinYM) > 1e-6 &&
                          (Math.Abs(zoneBounds.MinX - op.MaxXM) <= influenceGap ||
                           Math.Abs(zoneBounds.MaxX - op.MinXM) <= influenceGap)
                        : Math.Min(zoneBounds.MaxX, op.MaxXM) - Math.Max(zoneBounds.MinX, op.MinXM) > 1e-6 &&
                          (Math.Abs(zoneBounds.MinY - op.MaxYM) <= influenceGap ||
                           Math.Abs(zoneBounds.MaxY - op.MinYM) <= influenceGap)))
                .ToList();
            if (relevant.Count == 0)
                return new List<AdditionalZone> { zone };

            var parts = IntersectsOpening(zone, relevant)
                ? ExcludeOpenings(zone, relevant)
                : new List<AdditionalZone> { zone };
            if (plates != null && plates.Count > 0)
            {
                var transitionCellMm = settings.ReverseZoneDirections &&
                    zone.Layer == RebarLayer.As2 && effectiveDirection == ZoneDirection.X
                        ? settings.GridCellMm
                        : 0;
                parts = SplitAtTransverseOpeningEdges(
                    parts, relevant, effectiveDirection, transitionCellMm);
                parts = MergeSameTransverseBands(parts, effectiveDirection);
            }
            foreach (var part in parts)
            {
                part.Direction = effectiveDirection;
                var bounds = Bounds(part.Contour);
                var endsAtOpening = relevant.Any(op => effectiveDirection == ZoneDirection.X
                    ? Math.Min(bounds.MaxY, op.MaxYM) - Math.Max(bounds.MinY, op.MinYM) > 1e-6 &&
                      (Math.Abs(bounds.MaxX - op.MinXM) < 1e-5 ||
                       Math.Abs(bounds.MinX - op.MaxXM) < 1e-5)
                    : Math.Min(bounds.MaxX, op.MaxXM) - Math.Max(bounds.MinX, op.MinXM) > 1e-6 &&
                      (Math.Abs(bounds.MaxY - op.MinYM) < 1e-5 ||
                       Math.Abs(bounds.MinY - op.MaxYM) < 1e-5));
                if (endsAtOpening && settings.ApplyBentRules)
                {
                    var gapM = UnitConversion.MmToMeters(Math.Max(0, settings.EdgeOffsetMm));
                    var minX = bounds.MinX;
                    var maxX = bounds.MaxX;
                    var minY = bounds.MinY;
                    var maxY = bounds.MaxY;
                    foreach (var op in relevant)
                    {
                        if (effectiveDirection == ZoneDirection.X)
                        {
                            if (Math.Abs(maxX - op.MinXM) < 1e-5) maxX -= gapM;
                            if (Math.Abs(minX - op.MaxXM) < 1e-5) minX += gapM;
                        }
                        else
                        {
                            if (Math.Abs(maxY - op.MinYM) < 1e-5) maxY -= gapM;
                            if (Math.Abs(minY - op.MaxYM) < 1e-5) minY += gapM;
                        }
                    }
                    if (maxX - minX > 0.05 && maxY - minY > 0.05)
                    {
                        SetContour(part, Rectangle(minX, maxX, minY, maxY, part.LevelZM));
                        bounds = Bounds(part.Contour);
                    }
                    part.VerticalLegMm = HoleBentRules.VerticalLegAvailableMm(
                        settings.SlabThicknessMm, settings.CoverTopMm,
                        settings.CoverBottomMm, part.DiameterMm);
                    part.FamilyKind = HoleBentRules.ChooseBentFamily(part.VerticalLegMm, part.DiameterMm);
                    part.FamilyFileName = settings.GetFamilyName(part.FamilyKind);
                    part.CountBars = true;
                    part.CountInSpec = false;
                    part.Comment = "отверстие: гнутая деталь";
                }
                else if (zone.FamilyKind == ZoneFamilyKind.Straight)
                {
                    part.FamilyKind = ZoneFamilyKind.Straight;
                    part.FamilyFileName = settings.GetFamilyName(ZoneFamilyKind.Straight);
                    part.VerticalLegMm = 0;
                    part.Comment = "обход отверстия: прямой стержень";
                }
                SetContour(part, part.Contour.ToList());
                if (plates != null)
                {
                    part.NodeIds = plates.Where(plate => zone.NodeIds.Contains(plate.Id) &&
                        plate.Centroid.X >= bounds.MinX - 1e-6 && plate.Centroid.X <= bounds.MaxX + 1e-6 &&
                        plate.Centroid.Y >= bounds.MinY - 1e-6 && plate.Centroid.Y <= bounds.MaxY + 1e-6)
                        .Select(plate => plate.Id).Distinct().ToList();
                    part.ElementId = part.NodeIds.FirstOrDefault();
                }
            }
            if (plates != null && plates.Count > 0)
            {
                foreach (var part in parts.Where(part => part.NodeIds.Count > 0))
                    RestoreLongitudinalPlacement(part, relevant, settings, plates);
            }
            return parts;
        }

        private static List<AdditionalZone> MergeSameTransverseBands(
            List<AdditionalZone> source, ZoneDirection direction)
        {
            const double toleranceM = 0.001;
            var result = new List<AdditionalZone>();
            foreach (var part in source)
            {
                var bounds = Bounds(part.Contour);
                var existing = result.FirstOrDefault(candidate =>
                {
                    var other = Bounds(candidate.Contour);
                    return direction == ZoneDirection.X
                        ? Math.Abs(bounds.MinY - other.MinY) <= toleranceM &&
                          Math.Abs(bounds.MaxY - other.MaxY) <= toleranceM
                        : Math.Abs(bounds.MinX - other.MinX) <= toleranceM &&
                          Math.Abs(bounds.MaxX - other.MaxX) <= toleranceM;
                });
                if (existing == null)
                {
                    result.Add(part);
                    continue;
                }
                var otherBounds = Bounds(existing.Contour);
                SetContour(existing, Rectangle(
                    Math.Min(bounds.MinX, otherBounds.MinX), Math.Max(bounds.MaxX, otherBounds.MaxX),
                    Math.Min(bounds.MinY, otherBounds.MinY), Math.Max(bounds.MaxY, otherBounds.MaxY),
                    existing.LevelZM));
                existing.NodeIds = existing.NodeIds.Concat(part.NodeIds).Distinct().ToList();
                existing.ElementId = existing.NodeIds.FirstOrDefault();
            }
            return result;
        }

        private static void RestoreLongitudinalPlacement(
            AdditionalZone zone, IList<OpeningInfo> openings, AnalysisSettings settings,
            IList<LiraPlateElement> plates)
        {
            var assigned = plates.Where(plate => zone.NodeIds.Contains(plate.Id)).ToList();
            if (assigned.Count == 0 || zone.Contour.Count < 3) return;
            var bounds = Bounds(zone.Contour);
            var coreMin = zone.Direction == ZoneDirection.X
                ? assigned.Min(plate => plate.Contour.Min(point => point.X))
                : assigned.Min(plate => plate.Contour.Min(point => point.Y));
            var coreMax = zone.Direction == ZoneDirection.X
                ? assigned.Max(plate => plate.Contour.Max(point => point.X))
                : assigned.Max(plate => plate.Contour.Max(point => point.Y));
            var anchorageM = UnitConversion.MmToMeters(
                RebarTables.AnchorageLenMm(settings.ConcreteClass, zone.DiameterMm));
            // Near an opening the preliminary FE envelope is narrower than the
            // final bar. Keep two mesh modules in addition to concrete anchorage;
            // this matches the two-FE opening rule and prevents undersized 1950 /
            // 2900 families after a reversed-direction split.
            var placementReserveM = settings.ReverseZoneDirections &&
                                    zone.FamilyKind == ZoneFamilyKind.Straight &&
                                    zone.Direction == ZoneDirection.X
                ? UnitConversion.MmToMeters(Math.Max(0, settings.GridCellMm))
                : 0;
            var start = coreMin - anchorageM - placementReserveM;
            var end = coreMax + anchorageM + placementReserveM;
            if (end - start <= 0.05) return;

            if (zone.FamilyKind == ZoneFamilyKind.Straight)
            {
                var requiredMm = UnitConversion.MetersToMm(end - start);
                var familyM = UnitConversion.MmToMeters(RebarTables.PickFamilyLength(requiredMm));
                var center = (coreMin + coreMax) / 2.0;
                var familyStart = center - familyM / 2.0;
                var familyEnd = center + familyM / 2.0;
                start = familyStart;
                end = familyEnd;
            }

            if (zone.Direction == ZoneDirection.X)
                SetContour(zone, Rectangle(start, end, bounds.MinY, bounds.MaxY, zone.LevelZM));
            else
                SetContour(zone, Rectangle(bounds.MinX, bounds.MaxX, start, end, zone.LevelZM));
        }

        private static List<AdditionalZone> SplitAtTransverseOpeningEdges(
            List<AdditionalZone> source, IList<OpeningInfo> openings,
            ZoneDirection direction, int transitionCellMm)
        {
            var result = new List<AdditionalZone>();
            foreach (var part in source)
            {
                var bounds = Bounds(part.Contour);
                var cuts = openings
                    .SelectMany(op => direction == ZoneDirection.X
                        ? transitionCellMm > 0
                            ? new[]
                            {
                                op.MinYM - 2 * UnitConversion.MmToMeters(transitionCellMm),
                                op.MinYM,
                                op.MaxYM
                            }
                            : new[] { op.MinYM, op.MaxYM }
                        : new[] { op.MinXM, op.MaxXM })
                    .Where(value => value > (direction == ZoneDirection.X ? bounds.MinY : bounds.MinX) + 1e-5 &&
                                    value < (direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX) - 1e-5)
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
                if (cuts.Count == 0)
                {
                    result.Add(part);
                    continue;
                }

                var edges = new List<double>
                {
                    direction == ZoneDirection.X ? bounds.MinY : bounds.MinX
                };
                edges.AddRange(cuts);
                edges.Add(direction == ZoneDirection.X ? bounds.MaxY : bounds.MaxX);
                for (var i = 0; i < edges.Count - 1; i++)
                {
                    if (edges[i + 1] - edges[i] <= 0.05) continue;
                    var copy = Copy(part);
                    var rectangle = direction == ZoneDirection.X
                        ? Rectangle(bounds.MinX, bounds.MaxX, edges[i], edges[i + 1], part.LevelZM)
                        : Rectangle(edges[i], edges[i + 1], bounds.MinY, bounds.MaxY, part.LevelZM);
                    var clipped = Clipper.Intersect(new Paths64 { ToPath(part.Contour) },
                        new Paths64 { ToPath(rectangle) }, FillRule.NonZero);
                    foreach (var path in clipped.Where(path => path.Count >= 3))
                    {
                        var band = Copy(copy);
                        SetContour(band, FromPath(path, band.LevelZM));
                        result.Add(band);
                    }
                }
            }
            return result;
        }

        public static AdditionalZone? Create(
            AdditionalZone template, double minX, double maxX, double minY, double maxY,
            IList<Point3> slab)
        {
            var zone = Copy(template);
            zone.NodeIds.Clear();
            zone.ElementId = 0;
            zone.Comment = "создано в предпросмотре";
            return ApplyClipped(zone, Rectangle(minX, maxX, minY, maxY, template.LevelZM), slab)
                ? zone
                : null;
        }

        public static AdditionalZone? Merge(AdditionalZone first, AdditionalZone second, IList<Point3> slab)
        {
            if (first.Layer != second.Layer || first.Direction != second.Direction)
                return null;
            var paths = new Paths64 { ToPath(first.Contour), ToPath(second.Contour) };
            var union = Clipper.Union(paths, FillRule.NonZero);
            var clipped = IntersectWithSlab(union, slab);
            if (clipped.Count != 1) return null;
            var path = Largest(clipped);
            if (path == null) return null;

            var governing = first.AsCoveredCm2PerM >= second.AsCoveredCm2PerM ? first : second;
            var merged = Copy(governing);
            merged.NodeIds = first.NodeIds.Concat(second.NodeIds).Distinct().ToList();
            merged.ElementId = merged.NodeIds.FirstOrDefault();
            merged.AsRequired = Math.Max(first.AsRequired, second.AsRequired);
            merged.AsAdditional = Math.Max(first.AsAdditional, second.AsAdditional);
            merged.Comment = "объединено в предпросмотре";
            SetContour(merged, FromPath(path, merged.LevelZM));
            return merged;
        }

        public static List<AdditionalZone> Split(
            AdditionalZone zone, double coordinateM, bool verticalCut, IList<Point3> slab)
        {
            var minX = zone.Contour.Min(p => p.X);
            var maxX = zone.Contour.Max(p => p.X);
            var minY = zone.Contour.Min(p => p.Y);
            var maxY = zone.Contour.Max(p => p.Y);
            var margin = Math.Max(maxX - minX, maxY - minY) + 1.0;
            var cutters = verticalCut
                ? new[]
                {
                    Rectangle(minX - margin, coordinateM, minY - margin, maxY + margin, zone.LevelZM),
                    Rectangle(coordinateM, maxX + margin, minY - margin, maxY + margin, zone.LevelZM)
                }
                : new[]
                {
                    Rectangle(minX - margin, maxX + margin, minY - margin, coordinateM, zone.LevelZM),
                    Rectangle(minX - margin, maxX + margin, coordinateM, maxY + margin, zone.LevelZM)
                };

            var result = new List<AdditionalZone>();
            foreach (var cutter in cutters)
            {
                var pieces = Clipper.Intersect(
                    new Paths64 { ToPath(zone.Contour) },
                    new Paths64 { ToPath(cutter) }, FillRule.NonZero);
                var path = Largest(IntersectWithSlab(pieces, slab));
                if (path == null) continue;
                var copy = Copy(zone);
                copy.Comment = "разделено в предпросмотре";
                SetContour(copy, FromPath(path, copy.LevelZM));
                if (copy.LengthM > 0.05 && copy.WidthM > 0.05)
                    result.Add(copy);
            }
            return result;
        }

        private static bool ApplyClipped(
            AdditionalZone zone, IList<Point3> contour, IList<Point3> slab)
        {
            var clipped = IntersectWithSlab(new Paths64 { ToPath(contour) }, slab);
            var path = Largest(clipped);
            if (path == null) return false;
            SetContour(zone, FromPath(path, zone.LevelZM));
            return zone.LengthM > 0.05 && zone.WidthM > 0.05;
        }

        private static Paths64 IntersectWithSlab(Paths64 subject, IList<Point3> slab)
        {
            if (slab == null || slab.Count < 3) return subject;
            return Clipper.Intersect(subject, new Paths64 { ToPath(slab) }, FillRule.NonZero);
        }

        private static Path64? Largest(Paths64 paths) => paths
            .Where(p => p.Count >= 3)
            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
            .FirstOrDefault();

        private static Path64 ToPath(IEnumerable<Point3> points) =>
            new Path64(points.Select(p => new Point64(
                (long)Math.Round(p.X * Scale),
                (long)Math.Round(p.Y * Scale))));

        private static List<Point3> FromPath(Path64 path, double z) => path
            .Select(p => new Point3(p.X / Scale, p.Y / Scale, z))
            .ToList();

        private static List<Point3> Rectangle(
            double minX, double maxX, double minY, double maxY, double z) =>
            new List<Point3>
            {
                new Point3(Math.Min(minX, maxX), Math.Min(minY, maxY), z),
                new Point3(Math.Max(minX, maxX), Math.Min(minY, maxY), z),
                new Point3(Math.Max(minX, maxX), Math.Max(minY, maxY), z),
                new Point3(Math.Min(minX, maxX), Math.Max(minY, maxY), z)
            };

        private static AdditionalZone Copy(AdditionalZone source) => new AdditionalZone
        {
            ZoneId = source.ZoneId,
            Layer = source.Layer,
            NodeIds = source.NodeIds.ToList(),
            ElementId = source.ElementId,
            LevelZM = source.LevelZM,
            Direction = source.Direction,
            DiameterMm = source.DiameterMm,
            BarStepMm = source.BarStepMm,
            BarCount = source.BarCount,
            WidthMm = source.WidthMm,
            WidthM = source.WidthM,
            LengthMm = source.LengthMm,
            LengthM = source.LengthM,
            AsAdditional = source.AsAdditional,
            AsRequired = source.AsRequired,
            AsCoveredCm2PerM = source.AsCoveredCm2PerM,
            ConcreteClass = source.ConcreteClass,
            FamilyKind = source.FamilyKind,
            FamilyFileName = source.FamilyFileName,
            CountInSpec = source.CountInSpec,
            CountBars = source.CountBars,
            VerticalLegMm = source.VerticalLegMm,
            RotationDeg = source.RotationDeg,
            AlphaCoef = source.AlphaCoef,
            Rebar = source.Rebar,
            AxisNameX = source.AxisNameX,
            AxisNameY = source.AxisNameY,
            AxisPosXM = source.AxisPosXM,
            AxisPosYM = source.AxisPosYM,
            OffsetFromAxisXMm = source.OffsetFromAxisXMm,
            OffsetFromAxisYMm = source.OffsetFromAxisYMm,
            AxisTieLabel = source.AxisTieLabel,
            Comment = source.Comment,
            IsValid = source.IsValid,
            StatusColor = source.StatusColor
        };

        private static void SetContour(AdditionalZone zone, List<Point3> contour)
        {
            zone.Contour = contour;
            var minX = contour.Min(p => p.X);
            var maxX = contour.Max(p => p.X);
            var minY = contour.Min(p => p.Y);
            var maxY = contour.Max(p => p.Y);
            zone.Placement = new Point3((minX + maxX) / 2, (minY + maxY) / 2, zone.LevelZM);
            zone.LengthM = zone.Direction == ZoneDirection.X ? maxX - minX : maxY - minY;
            zone.WidthM = zone.Direction == ZoneDirection.X ? maxY - minY : maxX - minX;
            var planLengthMm = UnitConversion.MetersToMm(zone.LengthM);
            zone.LengthMm = zone.FamilyKind == ZoneFamilyKind.Straight
                ? planLengthMm
                : RebarTables.BentBarTotalLengthMm(planLengthMm, zone.VerticalLegMm, zone.FamilyKind);
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(2, (int)Math.Round(zone.WidthMm / Math.Max(1, zone.BarStepMm)) + 1);
        }
    }
}
