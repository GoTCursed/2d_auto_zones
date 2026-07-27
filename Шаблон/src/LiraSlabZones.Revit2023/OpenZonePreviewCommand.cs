using System;
using System.IO;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiraSlabZones.Core;
using LiraSlabZones.Revit2023.UI;

namespace LiraSlabZones.Revit2023
{
    [Transaction(TransactionMode.Manual)]
    public sealed class OpenZonePreviewCommand : IExternalCommand
    {
        private static ZonePreviewWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiApp = commandData.Application;
                var uiDoc = uiApp.ActiveUIDocument;
                var settings = AppConfig.LoadEffectiveSettings();

                if (_window != null)
                {
                    if (uiDoc != null)
                        _window.SetProjectFamilies(FamilyLoader.ListFamilyNames(uiDoc.Document),
                            settings.FamilyStraight);
                    _window.Activate();
                    return Result.Succeeded;
                }

                _window = new ZonePreviewWindow();
                new WindowInteropHelper(_window)
                {
                    Owner = uiApp.MainWindowHandle
                };

                if (uiDoc != null)
                {
                    var doc = uiDoc.Document;
                    _window.SetProjectFamilies(FamilyLoader.ListFamilyNames(doc), settings.FamilyStraight);
                    _window.SetPlaceCallback(result => PlaceIntoRevit(doc, result));
                }

                _window.Closed += (_, __) => _window = null;

                var demo = DemoSlabFactory.Create(settings);
                _window.LoadResult(demo);
                _window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("LiraSlabZones", ex.ToString());
                return Result.Failed;
            }
        }

        private static void PlaceIntoRevit(Document doc, AnalysisResult analysis)
        {
            AppConfig.SaveUserSettings(analysis.Settings);

            var familyName = analysis.Settings.FamilyStraight;
            var symbol = FamilyLoader.FindSymbol(doc, familyName);
            if (symbol == null)
            {
                try
                {
                    symbol = FamilyLoader.ResolveFromProject(doc, familyName);
                    analysis.Settings.FamilyStraight = symbol.FamilyName;
                    AppConfig.SaveUserSettings(analysis.Settings);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var root = SolutionPaths.FindRoot();
            var outPath = Path.Combine(root, "output", "slab_zones.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            SlabZoneAnalyzer.SaveJson(analysis, outPath);

            int placed;
            using (var tx = new Transaction(doc, "LiraSlabZones: зоны из превью"))
            {
                tx.Start();
                if (!symbol.IsActive) symbol.Activate();
                placed = ZonePlacer.Place(doc, symbol, analysis);
                tx.Commit();
            }

            TaskDialog.Show("LiraSlabZones",
                $"Размещено: {placed} из {analysis.Zones.Count}\n" +
                $"Семейство (из проекта): {symbol.FamilyName}\n" +
                $"User cfg: {AppConfig.UserConfigPath}\n" +
                $"JSON: {outPath}");
        }
    }
}
