; OpenWinSidecar Windows installer (Inno Setup 6).
; Builds with:  iscc installer\OpenWinSidecar.iss   (from the repo root)
; Requires:     dotnet publish output in installer\stage\app (see below).
;
;   dotnet publish src/OpenWinSidecar/OpenWinSidecar.csproj -c Release -r win-x64 ^
;     --self-contained true -p:PublishSingleFile=true ^
;     -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
;     -o installer/stage/app
;
; Privileges: admin (driver install + firewall rule + Program Files). winget
; installs silently with: OpenWinSidecar-Setup-<ver>.exe /SILENT

#define MyAppName "OpenWinSidecar"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "julianmb"
#define MyAppURL "https://github.com/julianmb/OpenWinSidecar"
#define MyAppExeName "OpenWinSidecar.exe"

[Setup]
AppId={{0385E396-88A5-4DC6-8B84-2ED798EE2A71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\OpenWinSidecar\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=output
OutputBaseFilename=OpenWinSidecar-Setup-{#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Self-contained single-file publish output (exe + bundled native libs).
Source: "stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
; Virtual display driver package (inf/dll/cat + devcon + settings). The 163MB
; upstream control GUI and the redundant zips are deliberately excluded.
Source: "..\drivers\VDD\*"; DestDir: "{app}\drivers\VDD"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "VDD Control.exe,VDD Control.pdb,vdd_control.zip,vdd_x64.zip"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Register the virtual display driver (idempotent; harmless if already installed).
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\drivers\VDD\MttVDD.inf"" /install"; Flags: runhidden; StatusMsg: "Installing virtual display driver..."
; Allow inbound streaming traffic on private networks.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""OpenWinSidecar"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=private"; Flags: runhidden; StatusMsg: "Configuring firewall..."
; Offer to launch (skipped on silent installs).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort cleanup; failures here never abort the uninstall.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ffmpeg.exe"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OpenWinSidecar"""; Flags: runhidden

[UninstallDelete]
Type: files; Name: "{app}\*.log"

[Code]
{ Nothing custom: stock wizard + silent switches (/SILENT, /VERYSILENT,
  /SUPPRESSMSGBOXES, /NORESTART) come free with Inno and satisfy winget. }
