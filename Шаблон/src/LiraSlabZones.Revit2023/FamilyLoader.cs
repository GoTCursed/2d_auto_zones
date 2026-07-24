using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using LiraSlabZones.Core;

namespace LiraSlabZones.Revit2023
{
    internal static class PathResolver
    {
        public static string FindSolutionRoot() => SolutionPaths.FindRoot();

        public static string? PickJson(string defaultPath)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Выберите JSON зон (slab_zones.json)",
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = File.Exists(defaultPath) ? defaultPath : "slab_zones.json",
                InitialDirectory = File.Exists(defaultPath)
                    ? Path.GetDirectoryName(defaultPath)
                    : FindSolutionRoot()
            };
            return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
        }

        public static string? FindFamilyOnODrive(string familyFileName) =>
            SolutionPaths.FindFamilyOnODrive(familyFileName);
    }

    internal static class FamilyLoader
    {
        /// <summary>Типоразмер семейства, уже загруженного в проект (без .rfa).</summary>
        public static FamilySymbol? FindSymbol(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(familyName))
                return null;

            var name = NormalizeFamilyName(familyName);
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .ToList();

            var match = symbols.FirstOrDefault(s =>
                            s.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)
                            || s.Family.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? symbols.FirstOrDefault(s =>
                            s.FamilyName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf(s.FamilyName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (match == null) return null;
            if (!match.IsActive) match.Activate();
            return match;
        }

        /// <summary>Уникальные имена семейств в проекте (для выбора в UI).</summary>
        public static List<string> ListFamilyNames(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && !string.IsNullOrWhiteSpace(s.FamilyName))
                .Select(s => s.FamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Найти семейство по имени в проекте; если нет — диалог выбора из загруженных.
        /// </summary>
        public static FamilySymbol ResolveFromProject(Document doc, string? preferredFamilyName)
        {
            var preferred = NormalizeFamilyName(preferredFamilyName ?? "");
            if (!string.IsNullOrEmpty(preferred))
            {
                var found = FindSymbol(doc, preferred);
                if (found != null) return found;
            }

            // Частое имя SUM-30 (в т.ч. вариант _R22)
            var sum30 = FindSymbol(doc, "SUM-30")
                        ?? FindSymbol(doc, "Зона дополнительного армирования");
            if (sum30 != null) return sum30;

            var names = ListFamilyNames(doc);
            if (names.Count == 0)
                throw new InvalidOperationException(
                    "В проекте Revit нет загруженных семейств. Загрузите семейство зоны доп. армирования в проект.");

            var picked = PickFamilyName(names, preferred);
            if (string.IsNullOrEmpty(picked))
                throw new OperationCanceledException("Семейство не выбрано.");

            return FindSymbol(doc, picked!)
                   ?? throw new InvalidOperationException("Не удалось получить типоразмер семейства: " + picked);
        }

        /// <summary>Диалог выбора имени семейства из списка проекта.</summary>
        public static string? PickFamilyName(IReadOnlyList<string> familyNames, string? preferred = null)
        {
            if (familyNames == null || familyNames.Count == 0)
                return null;

            using var form = new System.Windows.Forms.Form
            {
                Text = "Семейство зоны доп. армирования",
                Width = 520,
                Height = 160,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var label = new System.Windows.Forms.Label
            {
                Text = "Выберите семейство из текущего проекта Revit:",
                Left = 12,
                Top = 12,
                Width = 480,
                AutoSize = false
            };

            var combo = new System.Windows.Forms.ComboBox
            {
                Left = 12,
                Top = 40,
                Width = 480,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var n in familyNames)
                combo.Items.Add(n);

            var prefer = NormalizeFamilyName(preferred ?? "");
            var idx = -1;
            if (!string.IsNullOrEmpty(prefer))
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    var item = combo.Items[i]?.ToString() ?? "";
                    if (item.Equals(prefer, StringComparison.OrdinalIgnoreCase)
                        || item.IndexOf("SUM-30", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        idx = i;
                        if (item.Equals(prefer, StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }
            if (idx < 0)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    var item = combo.Items[i]?.ToString() ?? "";
                    if (item.IndexOf("SUM-30", StringComparison.OrdinalIgnoreCase) >= 0
                        || item.IndexOf("дополнительного армирования", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        idx = i;
                        break;
                    }
                }
            }
            combo.SelectedIndex = idx >= 0 ? idx : 0;

            var ok = new System.Windows.Forms.Button { Text = "OK", DialogResult = DialogResult.OK, Left = 316, Top = 80, Width = 85 };
            var cancel = new System.Windows.Forms.Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 407, Top = 80, Width = 85 };
            form.Controls.Add(label);
            form.Controls.Add(combo);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            return form.ShowDialog() == DialogResult.OK
                ? combo.SelectedItem?.ToString()
                : null;
        }

        public static string NormalizeFamilyName(string nameOrFile)
        {
            var s = (nameOrFile ?? "").Trim();
            if (s.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                s = Path.GetFileNameWithoutExtension(s);
            return s;
        }

        /// <summary>Устарело: загрузка из файла. Оставлено для совместимости гнутых семейств при наличии .rfa.</summary>
        public static FamilySymbol EnsureSymbol(Document doc, string familyPath, string familyName)
        {
            var existing = FindSymbol(doc, familyName)
                           ?? FindSymbol(doc, Path.GetFileNameWithoutExtension(familyPath));
            if (existing != null)
                return existing;

            if (!File.Exists(familyPath))
                throw new FileNotFoundException(
                    "Семейство не найдено в проекте и файл отсутствует: " + familyName, familyPath);

            if (!doc.LoadFamily(familyPath, out var family) || family == null)
                throw new InvalidOperationException("Не удалось загрузить семейство: " + familyPath);

            var symbolIds = family.GetFamilySymbolIds();
            var symbol = symbolIds.Select(id => doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault()
                         ?? throw new InvalidOperationException("В семействе нет типоразмеров: " + family.Name);

            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }
    }
}
