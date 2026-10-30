#define MyAppVersion "0.1.2"

[Setup]
AppId={{A7C2C5D1-9B9C-4D8A-A13E-61B7F0348D20}
AppName=HTTP FreeKit
AppVersion={#MyAppVersion}
AppPublisher=OpenSourcePremium
DefaultDirName={localappdata}\Programs\HTTP FreeKit
DisableDirPage=no
DefaultGroupName=HTTP FreeKit
DisableProgramGroupPage=yes
Uninstallable=yes
PrivilegesRequired=admin
OutputDir=dist\installer
OutputBaseFilename=HTTP-FreeKit-Setup-{#MyAppVersion}-win-x64
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\HTTP FreeKit.exe
LicenseFile=LICENSE
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "dist\HTTP FreeKit.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\WinDivert-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\HTTP FreeKit"; Filename: "{app}\HTTP FreeKit.exe"
Name: "{autoprograms}\Uninstall HTTP FreeKit"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HTTP FreeKit"; Filename: "{app}\HTTP FreeKit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HTTP FreeKit.exe"; Description: "Open HTTP FreeKit"; Flags: postinstall runascurrentuser
Filename: "{app}\HTTP FreeKit.exe"; Check: WizardSilent; Flags: nowait runascurrentuser

[UninstallRun]
Filename: "{app}\HTTP FreeKit.exe"; Parameters: "--cleanup"; Flags: waituntilterminated runascurrentuser; RunOnceId: "HTTPFreeKitCleanup"