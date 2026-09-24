using System;
using System.Collections.Generic;
using System.Linq;

namespace LiraSlabZones.Core
{
    public enum ZoneIssueKind
    {
        UncoveredElement,
        EmptyZone,
        InvalidZone,
        OverlongBar,
        PlacementConflict
    }

    public sealed class ZoneIssue
    {
        public ZoneIssueKind Kind { get; set; }
        public RebarLayer Layer { get; set; }
        public int ZoneId { get; set; }
        public int ElementId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class ZoneDiagnostics
    {
        public List<ZoneIssue> Issues { get; set; } = new List<ZoneIssue>();
        public int LayoutIterations { get; set; }
        public bool LayoutConverged { get; set; }
        public int UncoveredCount => Issues.Count(i => i.Kind == ZoneIssueKind.UncoveredElement);
        public int EmptyZoneCount => Issues.Count(i => i.Kind == ZoneIssueKind.EmptyZone);
        public int ConflictCount => Issues.Count(i => i.Kind == ZoneIssueKind.PlacementConflict);
        public bool HasErrors => Issues.Count > 0;
    }

    public sealed class ZoneSpatialIndex
    {
        private readonly double _cellM;
        private readonly Dictionary<(int X, int Y), List<int>> _cells =
            new Dictionary<(int X, int Y), List<int>>();

        public ZoneSpatialIndex(double cellM = 2.0) => _cellM = Math.Max(0.1, cellM);

        public void Add(int index, double minX, double maxX, double minY, double maxY)
        {
            foreach (var key in Keys(minX, maxX, minY, maxY))
            {
                if (!_cells.TryGetValue(key, out var values))
                    _cells[key] = values = new List<int>();
                values.Add(index);
            }
        }

        public IEnumerable<int> Query(double minX, double maxX, double minY, double maxY) =>
            Keys(minX, maxX, minY, maxY)
                .Where(_cells.ContainsKey).SelectMany(key => _cells[key]).Distinct();

        private IEnumerable<(int X, int Y)> Keys(double minX, double maxX, double minY, double maxY)
        {
            var x0 = (int)Math.Floor(minX / _cellM);
            var x1 = (int)Math.Floor(maxX / _cellM);
            var y0 = (int)Math.Floor(minY / _cellM);
            var y1 = (int)Math.Floor(maxY / _cellM);
            for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                yield return (x, y);
        }
    }

    public static class ZoneLayoutDiagnostics
    {
        public static ZoneDiagnostics Evaluate(
            IList<LiraPlateElement> plates, IList<AdditionalZone> zones, AnalysisSettings settings,
            int iterations = 0, bool converged = true)
        {
            var result = new ZoneDiagnostics { LayoutIterations = iterations, LayoutConverged = converged };
            var byLayer = zones.GroupBy(z => z.Layer).ToDictionary(g => g.Key, g => g.ToList());
            foreach (RebarLayer layer in Enum.GetValues(typeof(RebarLayer)))
            {
                if (!LayerEnabled(layer, settings)) continue;
                var background = Background(layer, settings);
                var layerZones = byLayer.TryGetValue(layer, out var found) ? found : new List<AdditionalZone>();
                foreach (var plate in plates.Where(p => p.Rebar.Ok && p.Rebar.Get(layer) - background > 0.01))
                {
                    if (layerZones.Any(zone => Covers(zone, plate.Centroid))) continue;
                    result.Issues.Add(new ZoneIssue { Kind = ZoneIssueKind.UncoveredElement,
                        Layer = layer, ElementId = plate.Id, Message = "КЭ не покрыт зоной" });
                }
            }

            var plateById = plates.ToDictionary(p => p.Id);
            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                if (!zone.IsValid)
                    result.Issues.Add(Issue(ZoneIssueKind.InvalidZone, zone, zone.Comment));
                if (RebarTables.ExceedsMaxBarLength(zone))
                    result.Issues.Add(Issue(ZoneIssueKind.OverlongBar, zone, "Полная длина больше 11700 мм"));
                var useful = zone.NodeIds.Any(id => plateById.TryGetValue(id, out var plate) &&
                    plate.Rebar.Ok && plate.Rebar.Get(zone.Layer) - Background(zone.Layer, settings) > 0.01 &&
                    Covers(zone, plate.Centroid));
                if (!useful && zone.NodeIds.Count == 0)
                    useful = plates.Any(plate => plate.Rebar.Ok &&
                        plate.Rebar.Get(zone.Layer) - Background(zone.Layer, settings) > 0.01 &&
                        Covers(zone, plate.Centroid));
                if (!useful)
                    result.Issues.Add(Issue(ZoneIssueKind.EmptyZone, zone, "Зона не покрывает расчётный КЭ"));
            }

            var index = BuildIndex(zones);
            for (var i = 0; i < zones.Count; i++)
            {
                var bounds = Bounds(zones[i]);
                var padding = Math.Max(0.1, zones[i].BarStepMm / 1000.0);
                foreach (var j in index.Query(bounds.MinX - padding, bounds.MaxX + padding,
                             bounds.MinY - padding, bounds.MaxY + padding).Where(j => j > i))
                {
                    if (!ZoneEditor.HasPlacementConflict(zones[i], zones[j])) continue;
                    result.Issues.Add(Issue(ZoneIssueKind.PlacementConflict, zones[i],
                        $"Конфликт с зоной №{zones[j].ZoneId}"));
                }
            }
            return result;
        }

        public static ZoneSpatialIndex BuildIndex(IList<AdditionalZone> zones)
        {
            var index = new ZoneSpatialIndex();
            for (var i = 0; i < zones.Count; i++)
            {
                var b = Bounds(zones[i]);
                index.Add(i, b.MinX, b.MaxX, b.MinY, b.MaxY);
            }
            return index;
        }

        private static ZoneIssue Issue(ZoneIssueKind kind, AdditionalZone zone, string message) =>
            new ZoneIssue { Kind = kind, Layer = zone.Layer, ZoneId = zone.ZoneId, Message = message ?? string.Empty };

        private static bool Covers(AdditionalZone zone, Point3 point) => zone.Contour.Count >= 3 &&
            point.X >= zone.Contour.Min(p => p.X) - 1e-6 && point.X <= zone.Contour.Max(p => p.X) + 1e-6 &&
            point.Y >= zone.Contour.Min(p => p.Y) - 1e-6 && point.Y <= zone.Contour.Max(p => p.Y) + 1e-6;

        private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(AdditionalZone zone) =>
            (zone.Contour.Min(p => p.X), zone.Contour.Max(p => p.X),
             zone.Contour.Min(p => p.Y), zone.Contour.Max(p => p.Y));

        private static bool LayerEnabled(RebarLayer layer, AnalysisSettings s) => layer switch
        { RebarLayer.As1 => s.ShowAs1, RebarLayer.As2 => s.ShowAs2,
          RebarLayer.As3 => s.ShowAs3, RebarLayer.As4 => s.ShowAs4, _ => false };

        private static double Background(RebarLayer layer, AnalysisSettings s) => layer switch
        { RebarLayer.As1 => s.AsMainAs1, RebarLayer.As2 => s.AsMainAs2,
          RebarLayer.As3 => s.AsMainAs3, RebarLayer.As4 => s.AsMainAs4, _ => 0 };
    }
}
