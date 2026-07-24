using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LiraSlabZones.Core
{
    public static class SolutionPaths
    {
        /// <summary>Каталог данных после установки: %LOCALAPPDATA%\LiraSlabZones</summary>
        public static string InstalledDataRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiraSlabZones");

        public static string FindRoot()
        {
            // 1) Рядом с DLL / вверх по дереву (dev)
            var candidates = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""
            };

            foreach (var start in candidates.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (LooksLikeRoot(dir.FullName))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }

            // 2) Установленный каталог данных
            if (LooksLikeRoot(InstalledDataRoot))
                return InstalledDataRoot;

            // 3) Dev-fallback
            var fallback = @"C:\Users\Filippov_G\Pictures\Test\Шаблон";
            if (Directory.Exists(fallback) && LooksLikeRoot(fallback))
                return fallback;

            // 4) Создать минимальный корень в LocalAppData
            EnsureInstalledLayout(InstalledDataRoot);
            return InstalledDataRoot;
        }

        public static bool LooksLikeRoot(string path) =>
            !string.IsNullOrWhiteSpace(path)
            && Directory.Exists(Path.Combine(path, "config"))
            && (Directory.Exists(Path.Combine(path, "families")) || Directory.Exists(Path.Combine(path, "output")));

        public static void EnsureInstalledLayout(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "families"));
            Directory.CreateDirectory(Path.Combine(root, "output"));
            var settings = Path.Combine(root, "config", "settings.json");
            if (!File.Exists(settings))
            {
                File.WriteAllText(settings, "{\n  \"ConcreteClass\": \"B25\",\n  \"AutoLayout\": true,\n  \"GridCellMm\": 300\n}\n");
            }
        }

        public static string? FindFamilyOnODrive(string familyFileName)
        {
            try
            {
                var filippov = Directory.GetDirectories(@"O:\")
                    .FirstOrDefault(d => d.IndexOf("Филиппов", StringComparison.OrdinalIgnoreCase) >= 0);
                if (filippov == null) return null;

                var detach = Directory.GetDirectories(filippov)
                    .FirstOrDefault(d => d.IndexOf("отсоедин", StringComparison.OrdinalIgnoreCase) >= 0);
                if (detach == null) return null;

                var path = Path.Combine(detach, familyFileName);
                if (File.Exists(path)) return path;

                return Directory.GetFiles(detach, "*.rfa")
                    .FirstOrDefault(f => Path.GetFileName(f).IndexOf("SUM-30", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return null;
            }
        }
    }
}
