; Inno Setup Script for DocX MCP Server
; Professional installer for Windows x64/arm64

#define MyAppName "DocX MCP Server"
#define MyAppPublisher "DocX MCP Team"
#define MyAppURL "https://github.com/valdo404/docx-mcp"
#define MyAppExeName "docx-mcp.exe"
#define MyCliExeName "docx-cli.exe"

; Version will be passed via command line: /DMyAppVersion=1.0.0
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; Architecture will be passed via command line: /DMyAppArch=x64
#ifndef MyAppArch
  #define MyAppArch "x64"
#endif

[Setup]
AppId={{B8F4E3A2-7C91-4D5E-A6B3-9E8F1C2D4A5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\DocxMcp
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir=..\..\dist\installers
OutputBaseFilename=docx-mcp-{#MyAppVersion}-{#MyAppArch}-setup
SetupIconFile=docx-mcp.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed={#MyAppArch}compatible
ArchitecturesInstallIn64BitMode={#MyAppArch}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=MCP Server for Microsoft Word Document Manipulation
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "addtopath"; Description: "Add to system PATH (recommended for CLI usage)"; GroupDescription: "Additional options:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\dist\windows-{#MyAppArch}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\dist\windows-{#MyAppArch}\{#MyCliExeName}"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion; DestName: "README.txt"
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\DocX CLI"; Filename: "{app}\{#MyCliExeName}"
Name: "{group}\Documentation"; Filename: "{app}\README.txt"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register application path for easy command-line access
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\docx-mcp.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\docx-cli.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyCliExeName}"; Flags: uninsdeletekey

[Code]
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure AddToPath();
var
  OldPath: string;
  NewPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', OldPath) then
    OldPath := '';

  if Pos(ExpandConstant('{app}'), OldPath) = 0 then
  begin
    if OldPath <> '' then
      NewPath := OldPath + ';' + ExpandConstant('{app}')
    else
      NewPath := ExpandConstant('{app}');

    RegWriteStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', NewPath);
  end;
end;

procedure RemoveFromPath();
var
  OldPath: string;
  NewPath: string;
  AppPath: string;
  P: Integer;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', OldPath) then
  begin
    AppPath := ExpandConstant('{app}');
    NewPath := OldPath;

    P := Pos(';' + AppPath, NewPath);
    if P > 0 then
      Delete(NewPath, P, Length(AppPath) + 1)
    else
    begin
      P := Pos(AppPath + ';', NewPath);
      if P > 0 then
        Delete(NewPath, P, Length(AppPath) + 1)
      else
      begin
        P := Pos(AppPath, NewPath);
        if P > 0 then
          Delete(NewPath, P, Length(AppPath));
      end;
    end;

    if NewPath <> OldPath then
      RegWriteStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', NewPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddToPath();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DocX MCP Server"; Flags: nowait postinstall skipifsilent unchecked
