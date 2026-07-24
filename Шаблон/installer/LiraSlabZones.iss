; LiraSlabZones — установщик add-in Revit 2023
; Сборка: ISCC.exe installer\LiraSlabZones.iss

#define MyAppName "LiraSlabZones"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "SUM"
#define MyAppURL ""
#define MyAppExeName "LiraSlabZones.PreviewHost.exe"

[Setup]
AppId={{B7E6C2A1-4F3D-4A9E-9C11-8D2A6F0E5B21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\LiraSlabZones
DefaultGroupName=LiraSlabZones
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LiraSlabZones-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=LiraSlabZones (Revit 2023)
InfoBeforeFile=README-INSTALL.txt
SetupIconFile=
CloseApplications=no
DisableDirPage=no
DirExistsWarning=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык PreviewHost на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
; Данные приложения (config / families / output)
Source: "..\dist\stage\data\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; PreviewHost (опциональный просмотр без Revit)
Source: "..\dist\stage\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs

; Add-in → %APPDATA%\Autodesk\Revit\Addins\2023\LiraSlabZones
Source: "..\dist\stage\addin\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023\LiraSlabZones"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LiraSlabZones Preview"; Filename: "{app}\tools\{#MyAppExeName}"
Name: "{group}\Удалить LiraSlabZones"; Filename: "{uninstallexe}"
Name: "{autodesktop}\LiraSlabZones Preview"; Filename: "{app}\tools\{#MyAppExeName}"; Tasks: desktopicon

[Code]
function WriteAddinManifest: Boolean;
var
  AddinDir, AddinFile, DllPath, Xml: string;
begin
  AddinDir := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2023');
  ForceDirectories(AddinDir);
  DllPath := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2023\LiraSlabZones\LiraSlabZones.Revit2023.dll');
  AddinFile := AddinDir + '\LiraSlabZones.addin';
  Xml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>LiraSlabZones</Name>' + #13#10 +
    '    <Assembly>' + DllPath + '</Assembly>' + #13#10 +
    '    <AddInId>B7E6C2A1-4F3D-4A9E-9C11-8D2A6F0E5B21</AddInId>' + #13#10 +
    '    <FullClassName>LiraSlabZones.Revit2023.App</FullClassName>' + #13#10 +
    '    <VendorId>SUM</VendorId>' + #13#10 +
    '    <VendorDescription>LIRA to Revit slab additional rebar zones</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>' + #13#10;
  Result := SaveStringToFile(AddinFile, Xml, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ExpandConstant('{app}\output'));
    ForceDirectories(ExpandConstant('{app}\config'));
    ForceDirectories(ExpandConstant('{app}\families'));
    if not WriteAddinManifest then
      MsgBox('Не удалось записать LiraSlabZones.addin. Проверьте права на %APPDATA%.', mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AddinFile, AddinFolder: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AddinFile := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2023\LiraSlabZones.addin');
    AddinFolder := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\2023\LiraSlabZones');
    if FileExists(AddinFile) then DeleteFile(AddinFile);
    if DirExists(AddinFolder) then DelTree(AddinFolder, True, True, True);
  end;
end;

[Run]
Filename: "{app}\tools\{#MyAppExeName}"; Description: "Запустить PreviewHost"; Flags: nowait postinstall skipifsilent unchecked
