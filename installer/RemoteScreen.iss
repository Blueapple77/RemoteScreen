; Instalátor klienta RemoteScreen pro Windows (Inno Setup 6.3+)
; Sestavení: build-installer.bat v kořeni projektu

#define AppName "RemoteScreen"
#define AppVersion "3.6.4"
#define AppExe "RemoteScreen.exe"
#define SourceDir "..\publish\client-installer"

[Setup]
AppId={{3E6B9D21-7C4A-4F58-A1E2-9D0B6C5F8A73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=RemoteScreen
AppComments=Ovládání Macu z Windows po místní síti
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; instalace bez práv správce (do profilu uživatele); v úvodu lze zvolit instalaci pro všechny
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\publish
OutputBaseFilename=RemoteScreen-Setup-{#AppVersion}
SetupIconFile=..\Client\RemoteScreen.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; běžící RemoteScreen se před aktualizací zavře
CloseApplications=force
RestartApplications=no

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Ovládání Macu z Windows"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ Předchozí název aplikace (MacRemote) – před instalací ho ukončit a odinstalovat.
  Nastavení (uložené počítače) si RemoteScreen převezme sám. }
const
  OldUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B7D-4E19-9C2B-5A1D3E7F9B42}_is1';

function GetOldUninstaller(var Cmd: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, OldUninstallKey, 'UninstallString', Cmd) or
            RegQueryStringValue(HKLM, OldUninstallKey, 'UninstallString', Cmd);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Cmd: String;
  Code: Integer;
begin
  if (CurStep = ssInstall) and GetOldUninstaller(Cmd) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM MacRemote.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(RemoveQuotes(Cmd), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;
