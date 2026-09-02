; Inno Setup script for the Vigil installer.
;
; Built in CI, where the Inno compiler is already present:
;     iscc /DAppVersion=0.7.0 packaging\vigil.iss
;
; It installs per-user by default - into %LOCALAPPDATA%\Programs\Vigil - so
; there is no elevation prompt on a machine someone has just bought this for.
; Nothing is written outside that folder except the shortcuts you choose.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "Vigil"
#define Publisher "BOF Studios"
#define AppExe "Vigil.exe"

[Setup]
; Stable, so an upgrade replaces the last install instead of sitting beside it.
AppId={{92402474-3863-5106-B00D-7CEC5A9B081C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/BofStudios/vigil
AppSupportURL=https://github.com/BofStudios/vigil/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
; per-user, so buying and installing this never needs an administrator
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Vigil-{#AppVersion}-windows-setup
SetupIconFile=..\vigil\assets\vigil.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start Vigil when I log in"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\Vigil\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open Vigil"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The application only. Settings, sessions and memory live in the user's
; .vigil folder and are deliberately left alone - uninstalling should not
; throw away someone's API key and history.
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
