; Inno Setup script for Spicetify Updater.
; Build with: "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer.iss

#define MyAppName "Spicetify Updater"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Spicetify Updater"
#define MyAppExeName "Spicetify-Updater.exe"
#define MyAppUrl "https://github.com/thecloudyy/SpicetifyUpdater"

[Setup]
AppId={{B7E4F0C2-5A3D-4E61-9F2B-7C8D1E5A6B94}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=Spicetify-Updater-{#MyAppVersion}-win-x64-setup
SetupIconFile=build\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "bin\Release\Spicetify-Updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Nothing outside {app} is removed: Spicetify's own data lives in %APPDATA%\spicetify
; and %LOCALAPPDATA%\spicetify and must survive an uninstall.
Type: filesandordirs; Name: "{app}"
