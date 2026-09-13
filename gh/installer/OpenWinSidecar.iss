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
;
; FFmpeg: the HEVC encoder shells out to an external FFmpeg. If none is found
; on the machine, setup installs Gyan.FFmpeg via winget (the same build the
; service resolves at runtime); without winget it warns and continues — the
; app degrades to JPEG and tells the user what to install.

#define MyAppName "OpenWinSidecar"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "julianmb"
#define MyAppURL "https://github.com/julianmb/OpenWinSidecar"
#define MyAppExeName "OpenWinSidecar.exe"

; The MttVDD driver reads its resolution matrix from this exact path at init.
#define VddSettingsDir "C:\VirtualDisplayDriver"

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

[Dirs]
Name: "{#VddSettingsDir}"; Flags: uninsneveruninstall

[Files]
; Self-contained single-file publish output (exe + bundled native libs).
Source: "stage\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
; Virtual display driver package (inf/dll/cat + devcon + settings). The 163MB
; upstream control GUI and the redundant zips are deliberately excluded.
Source: "..\drivers\VDD\*"; DestDir: "{app}\drivers\VDD"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "VDD Control.exe,VDD Control.pdb,vdd_control.zip,vdd_x64.zip"
; Driver settings (iPad resolution matrix) at the path MttVDD.dll reads.
; uninsneveruninstall: a still-installed driver keeps a usable settings file.
Source: "..\drivers\VDD\vdd_settings.xml"; DestDir: "{#VddSettingsDir}"; Flags: ignoreversion uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
; Stage the driver package into the DriverStore (gives us the oemXX.inf name
; for clean uninstall). Idempotent.
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\drivers\VDD\MttVDD.inf"" /install"; Flags: runhidden; StatusMsg: "Installing virtual display driver..."
; Create the root-enumerated device node, but ONLY when none exists — see
; VddDeviceNodeAbsent. pnputil /add-driver alone only stages the package;
; without this step the virtual monitor never appears on a clean machine.
Filename: "{app}\drivers\VDD\control\Dependencies\devcon.exe"; Parameters: "install ""{app}\drivers\VDD\control\SignedDrivers\x86\VDD\MttVDD.inf"" Root\MttVDD"; Flags: runhidden; StatusMsg: "Creating virtual display device..."; Check: VddDeviceNodeAbsent
; Allow inbound streaming traffic on private networks.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""OpenWinSidecar"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=private"; Flags: runhidden; StatusMsg: "Configuring firewall..."
; Offer to launch (skipped on silent installs).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort cleanup; failures here never abort the uninstall.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ffmpeg.exe"; Flags: runhidden; RunOnceId: "KillFfmpeg"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""OpenWinSidecar"""; Flags: runhidden; RunOnceId: "DelFirewall"

[UninstallDelete]
Type: files; Name: "{app}\*.log"

[Code]
// ---------------------------------------------------------------------------
// FFmpeg detection + winget install. Mirrors the runtime resolution order in
// HevcStreamEncoder.ResolveFfmpegExecutable: PATH -> WinGet Packages tree ->
// WinGet Links alias.
// ---------------------------------------------------------------------------

function FileExistsInDir(const ADir, AName: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(ADir) then Exit;
  if not FindFirst(ADir + '\' + AName, FindRec) then Exit;
  try
    Result := (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0;
  finally
    FindClose(FindRec);
  end;
end;

// Gyan.FFmpeg via winget is a portable zip laid out as:
//   {localappdata}\Microsoft\WinGet\Packages\Gyan.FFmpeg_*\<version>\bin\ffmpeg.exe
function WinGetPackagesFfmpeg(): String;
var
  Base, Sub, Candidate: String;
  FindRec, SubRec: TFindRec;
begin
  Result := '';
  Base := ExpandConstant('{localappdata}\Microsoft\WinGet\Packages');
  if not DirExists(Base) then Exit;
  if not FindFirst(Base + '\Gyan.FFmpeg*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Sub := Base + '\' + FindRec.Name;
        if FindFirst(Sub + '\*', SubRec) then
        begin
          try
            repeat
              if (SubRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
              begin
                Candidate := Sub + '\' + SubRec.Name + '\bin\ffmpeg.exe';
                if FileExists(Candidate) then
                begin
                  Result := Candidate;
                  Exit;
                end;
              end;
            until not FindNext(SubRec);
          finally
            FindClose(SubRec);
          end;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function FfmpegOnPath(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\where.exe'), 'ffmpeg.exe', '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0);
end;

function FfmpegPresent(): Boolean;
begin
  Result := FfmpegOnPath() or
    FileExistsInDir(ExpandConstant('{localappdata}\Microsoft\WinGet\Links'), 'ffmpeg.exe') or
    (WinGetPackagesFfmpeg() <> '');
end;

function FindWingetExe(): String;
var
  P: String;
  Code: Integer;
begin
  P := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
  if FileExists(P) then
    Result := P
  else if Exec('winget.exe', '--version', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Result := 'winget.exe'
  else
    Result := '';
end;

procedure WarnFfmpegMissing(const Reason: String);
begin
  // Silent installs (winget, deployment tools, validation VMs) must never block on a
  // dialog — log instead; the app repeats the instruction at runtime.
  if WizardSilent() then
  begin
    Log('FFmpeg missing (' + Reason + ') — silent install: not showing a prompt. ' +
      'Install with: winget install --id Gyan.FFmpeg -e');
    Exit;
  end;
  MsgBox('OpenWinSidecar streams video as HEVC using FFmpeg, which was not found on this computer. ' + Reason + #13#10#13#10 +
    'The installation will continue, but streaming will fall back to lower-quality JPEG until FFmpeg is installed. Run:' + #13#10#13#10 +
    '    winget install --id Gyan.FFmpeg -e' + #13#10#13#10 +
    'then restart OpenWinSidecar.', mbInformation, MB_OK);
end;

procedure InstallFfmpegViaWinget();
var
  Winget, Params: String;
  Code: Integer;
begin
  Winget := FindWingetExe();
  if Winget = '' then
  begin
    Log('FFmpeg not found and winget is unavailable; skipping automatic FFmpeg install.');
    WarnFfmpegMissing('winget is not available to install it automatically.');
    Exit;
  end;
  Log('FFmpeg not found; installing Gyan.FFmpeg via winget (download is ~170MB, this can take a few minutes)...');
  Params := 'install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements --disable-interactivity --silent';
  if Exec(Winget, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Log('winget install of Gyan.FFmpeg succeeded.')
  else
  begin
    Log(Format('winget install of Gyan.FFmpeg failed (exit code %d).', [Code]));
    WarnFfmpegMissing(Format('The automatic winget install failed (exit code %d).', [Code]));
  end;
end;

// ---------------------------------------------------------------------------
// Virtual display driver: backup settings before overwrite; remove device +
// driver package on uninstall (best effort — every failure is ignored).
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------

procedure RunHiddenLogged(const Exe, Params: String);
var
  Code: Integer;
begin
  if Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log(Format('%s %s -> exit %d', [Exe, Params, Code]))
  else
    Log(Format('%s could not be launched.', [Exe]));
end;

// devcon install unconditionally CREATES a new device node, so re-running it on a
// machine that already has one adds a ghost monitor (ROOT\DISPLAY\0001, 0002, ...).
// Gate the [Run] entry on "no virtual display instance exists yet" — pnputil lists
// the instance IDs, whose ROOT\DISPLAY\ prefix is locale-independent.
function VddDeviceNodeAbsent(): Boolean;
var
  TmpFile: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  TmpFile := ExpandConstant('{tmp}\ows_vdd_enum.txt');
  RunHiddenLogged(ExpandConstant('{cmd}'), '/c pnputil /enum-devices /class Display > "' + TmpFile + '"');
  Result := True;
  if not LoadStringsFromFile(TmpFile, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos('ROOT\DISPLAY\', Uppercase(Lines[I])) > 0 then
    begin
      Log('Virtual display device node already present; skipping devcon install.');
      Result := False;
      Exit;
    end;
end;

// pnputil /delete-driver needs the published oemXX.inf name, so enumerate and
// match against the original INF name (works on any OS language).
procedure DeleteMttVddDriverPackages();
var
  TmpFile: String;
  Lines: TArrayOfString;
  I, Colon: Integer;
  Line, Oem: String;
begin
  TmpFile := ExpandConstant('{tmp}\ows_pnputil_enum.txt');
  RunHiddenLogged(ExpandConstant('{cmd}'), '/c pnputil /enum-drivers > "' + TmpFile + '"');
  if not LoadStringsFromFile(TmpFile, Lines) then Exit;
  Oem := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    if Pos('Published Name', Line) > 0 then
    begin
      Colon := Pos(':', Line);
      if Colon > 0 then
        Oem := Trim(Copy(Line, Colon + 1, MaxInt));
    end
    else if (Pos('MTTVDD.INF', Uppercase(Line)) > 0) and (Oem <> '') then
    begin
      RunHiddenLogged(ExpandConstant('{sys}\pnputil.exe'), '/delete-driver ' + Oem + ' /uninstall /force');
      Oem := '';
    end;
  end;
end;

procedure RemoveVirtualDisplayDevice();
var
  Devcon: String;
begin
  RunHiddenLogged(ExpandConstant('{sys}\taskkill.exe'), '/F /IM OpenWinSidecar.exe');
  RunHiddenLogged(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ffmpeg.exe');
  Devcon := ExpandConstant('{app}\drivers\VDD\control\Dependencies\devcon.exe');
  if FileExists(Devcon) then
  begin
    // Wildcard: repeated driver installs can leave ROOT\DISPLAY\0001.. behind, and the
    // uninstaller must not orphan them (they reappear as ghost monitors in Settings).
    RunHiddenLogged(Devcon, 'remove "@ROOT\DISPLAY\*"');
  end;
  RunHiddenLogged(ExpandConstant('{sys}\pnputil.exe'), '/remove-device "ROOT\DISPLAY\0000"');
  RunHiddenLogged(ExpandConstant('{sys}\pnputil.exe'), '/remove-device "ROOT\DISPLAY\0001"');
  DeleteMttVddDriverPackages();
end;

// ---------------------------------------------------------------------------

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dest: String;
begin
  Result := '';
  // Back up any existing driver settings before [Files] overwrites it.
  Dest := ExpandConstant('{#VddSettingsDir}\vdd_settings.xml');
  if FileExists(Dest) then
  begin
    if CopyFile(Dest, Dest + '.bak', False) then
      Log('Backed up existing vdd_settings.xml to vdd_settings.xml.bak.')
    else
      Log('Could not back up existing vdd_settings.xml (continuing).');
  end;
  if FfmpegPresent() then
    Log('FFmpeg already present; skipping winget install.')
  else if WizardSilent() then
    // Silent installs (incl. winget validation) must stay fully non-interactive:
    // no winget download inside the install, no dialogs. Winget-driven installs get
    // FFmpeg from the manifest's PackageDependencies; everyone else gets the
    // actionable log line (and the same instruction at app runtime).
    Log('FFmpeg not found; silent install skips the winget attempt. ' +
      'Install with: winget install --id Gyan.FFmpeg -e (or rely on the winget package dependency).')
  else
    InstallFfmpegViaWinget();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveVirtualDisplayDevice();
end;
