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
            zone.LengthMm = UnitConversion.MetersToMm(zone.LengthM);
            zone.WidthMm = UnitConversion.MetersToMm(zone.WidthM);
            zone.BarCount = Math.Max(2, (int)Math.Round(zone.WidthMm / Math.Max(1, zone.BarStepMm)) + 1);
        }
    }
}
