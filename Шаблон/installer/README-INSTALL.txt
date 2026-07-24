LiraSlabZones — установка

Что будет установлено:
1. Add-in Revit 2023 + DefaultSettings.cfg:
   %APPDATA%\Autodesk\Revit\Addins\2023\LiraSlabZones\
2. Пользовательские настройки (создаются при сохранении):
   %APPDATA%\Roaming\Autodesk\Revit\Addins\2023\LiraSlabZones.cfg
3. Данные (output):
   %LOCALAPPDATA%\LiraSlabZones\
4. PreviewHost:
   %LOCALAPPDATA%\LiraSlabZones\tools\

Семейства .rfa плагин НЕ подгружает.
Имена семейств берутся из проекта Revit по названию (DefaultSettings.cfg / LiraSlabZones.cfg).

Требования:
- Autodesk Revit 2023 (x64)
- .NET Framework 4.8
- Семейства зон уже загружены в шаблон/проект

После установки перезапустите Revit 2023.
