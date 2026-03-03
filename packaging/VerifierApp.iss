#define MyAppName "Inter-Knot Arena VerifierApp"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Inter-Knot Arena"
#define MyAppExeName "VerifierApp.exe"

[Setup]
AppId={{8E146545-B177-4E00-A6E8-7339DE5E8FA1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Inter-Knot Arena VerifierApp
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Inter-Knot-Arena-VerifierApp-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
