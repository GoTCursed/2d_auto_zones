using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023
{
    internal static class ZonePlacer
    {
        private const double MetersToFeet = UnitConversion.MetersToFeet;

        public static int Place(Document doc, FamilySymbol? defaultSymbol, AnalysisResult analysis)
        {
            if (analysis.Zones == null || analysis.Zones.Count == 0)
                return 0;

            var settings = analysis.Settings ?? AppConfig.LoadEffectiveSettings();
            var level = FindNearestLevel(doc, analysis.Zones.First().LevelZM);
            int count = 0;
            var cache = new Dictionary<string, FamilySymbol?>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in analysis.Zones.GroupBy(z => settings.GetFamilyName(z.FamilyKind)))
            {
                var familyName = group.Key;
                if (!cache.TryGetValue(familyName, out var symbol))
                {
                    symbol = FamilyLoader.FindSymbol(doc, familyName);
                    if (symbol == null
                        && defaultSymbol != null
                        && familyName.Equals(settings.GetFamilyName(ZoneFamilyKind.Straight), StringComparison.OrdinalIgnoreCase))
                        symbol = defaultSymbol;
                    cache[familyName] = symbol;
                }

                if (symbol == null)
                    throw new InvalidOperationException(
                        "Семейство «" + familyName + "» не найдено в проекте Revit.\n" +
                        "Плагин не подгружает .rfa — загрузите семейство в проект вручную.\n" +
                        "Имя настраивается в " + AppConfig.UserConfigPath);

                if (!symbol.IsActive) symbol.Activate();

                foreach (var zone in group)
                {
                    if (PlaceOne(doc, symbol, level, zone, settings))
                        count++;
                }
            }

            return count;
        }

        private static bool PlaceOne(
            Document doc,
            FamilySymbol symbol,
            Level level,
            AdditionalZone zone,
            AnalysisSettings settings)
        {
            var ox = settings.OffsetXM;
            var oy = settings.OffsetYM;
            var px = zone.Placement.X + ox;
            var py = zone.Placement.Y + oy;
            if (Math.Abs(settings.RotationDeg) > 1e-9)
            {
                var rad = settings.RotationDeg * Math.PI / 180.0;
                var cos = Math.Cos(rad);
                var sin = Math.Sin(rad);
                var x0 = zone.Placement.X;
                var y0 = zone.Placement.Y;
                px = x0 * cos - y0 * sin + ox;
                py = x0 * sin + y0 * cos + oy;
            }

            var pt = new XYZ(px * MetersToFeet, py * MetersToFeet, zone.LevelZM * MetersToFeet);
            FamilyInstance? inst = null;
            try
            {
                inst = doc.Create.NewFamilyInstance(pt, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            }
            catch
            {
                try
                {
                    inst = doc.Create.NewFamilyInstance(pt, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                }
                catch
                {
                    return false;
                }
            }

            if (inst == null) return false;

            var zoneRot = zone.Direction == ZoneDirection.Y ? 90.0 : 0.0;
            var totalRot = (settings.RotationDeg + zoneRot) * Math.PI / 180.0;
            if (Math.Abs(totalRot) > 1e-9)
            {
                try
                {
                    ElementTransformUtils.RotateElement(
                        doc,
                        inst.Id,
                        Line.CreateBound(pt, pt + XYZ.BasisZ),
                        totalRot);
                }
                catch
                {
                    // ignore rotate failures
                }
            }

            var lengthFeet = zone.LengthMm > 0
                ? UnitConversion.MmToFeet(zone.LengthMm)
                : zone.LengthM * MetersToFeet;
            var widthFeet = zone.WidthMm > 0
                ? UnitConversion.MmToFeet(zone.WidthMm)
                : zone.WidthM * MetersToFeet;

            TrySetDoubleParam(inst, new[] { "Длина", "Длинна", "Length", "L", "l" }, lengthFeet);
            TrySetDoubleParam(inst, new[] { "Ширина", "Width", "B", "b" }, widthFeet);
            TrySetDoubleParam(inst, new[] { "Шаг", "Step", "Spacing" }, UnitConversion.MmToFeet(zone.BarStepMm));
            TrySetDoubleParam(inst, new[] { "Ø", "Диаметр", "Diameter", "d" }, UnitConversion.MmToFeet(zone.DiameterMm));
            TrySetIntParam(inst, new[] { "RNF.Количество элементов", "Количество", "Count", "N" }, zone.BarCount);
            TrySetStringParam(inst, new[] { "Класс бетона", "Concrete", "Бетон" }, zone.ConcreteClass);
            TrySetDoubleParam(inst, new[] { "Коэф. α", "Alpha", "α" }, zone.AlphaCoef);
            TrySetStringParam(inst, new[] { "RNF.Раздел" }, zone.RnfSection);
            TrySetStringParam(inst, new[] { "RNF.Марка конструкции" }, zone.RnfMarkConstruction);
            TrySetStringParam(inst, new[] { "RNF.Марка сборки" }, zone.RnfMarkAssembly);
            TrySetStringParam(inst, new[] { "RNF.Марка элемента" }, zone.RnfMarkElement);
            TrySetStringParam(inst, new[] { "Комментарии", "Comments", "Mark", "Марка" },
                string.IsNullOrEmpty(zone.Comment)
                    ? $"As+={zone.AsAdditional:F2}; Ø{zone.DiameterMm}/{zone.BarStepMm}×{zone.BarCount}; {zone.Direction}"
                    : zone.Comment);
            TrySetBoolParam(inst, new[] { "Учет в спецификации", "Учёт в спецификации" }, zone.CountInSpec);
            TrySetBoolParam(inst, new[] { "подсчет стержней", "Подсчет стержней", "подсчёт стержней" }, zone.CountBars);

            return true;
        }

        private static Level FindNearestLevel(Document doc, double zMeters)
        {
            var z = zMeters * MetersToFeet;
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z))
                .ToList();

            return levels.FirstOrDefault()
                   ?? throw new InvalidOperationException("В проекте нет уровней.");
        }

        private static void TrySetDoubleParam(Element e, string[] names, double valueFeet)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                {
                    p.Set(valueFeet);
                    return;
                }
            }
        }

        private static void TrySetStringParam(Element e, string[] names, string value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                {
                    p.Set(value);
                    return;
                }
            }
        }

        private static void TrySetIntParam(Element e, string[] names, int value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p == null || p.IsReadOnly) continue;
                if (p.StorageType == StorageType.Integer) { p.Set(value); return; }
                if (p.StorageType == StorageType.Double) { p.Set((double)value); return; }
            }
        }

        private static void TrySetBoolParam(Element e, string[] names, bool value)
        {
            foreach (var name in names)
            {
                var p = e.LookupParameter(name) ?? e.GetParameters(name).FirstOrDefault();
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Integer)
                {
                    p.Set(value ? 1 : 0);
                    return;
                }
            }
        }
    }
}
