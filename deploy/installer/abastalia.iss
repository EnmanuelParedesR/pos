; Abastalia setup (TK-242): ONE .exe with two roles chosen in the wizard.
;   PC principal: the store server as the "Abastalia" Windows service (deploy/install.ps1 + prepare-service), its own SQL Server Express
;                 instance ABASTALIA when missing (R11 C1), firewall 5443 on private networks, keys folder closed to other users, and the register app.
;   Caja:         only the register app (Abastalia.Client.Wpf).
; R11 C6: the register app goes to Program Files for the whole PC (a cashier cannot change it) and both roles are updated by the
; "Abastalia\Actualizar" SYSTEM task (update-service.ps1), which installs only setups signed by Abastalia (R11 C3).
; Both check the WebView2 Evergreen runtime. Both apps are published self-contained: no .NET runtime to install.
; SQL Express and the WebView2 bootstrapper are downloaded from Microsoft at install time, on the store PC.
; Build with deploy/installer/build.ps1 (it publishes and passes the defines below). Silent: AbastaliaSetup.exe /VERYSILENT /TYPE=server|register

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
; Microsoft links. The SQL Server 2025 Express SSEI (17.0) was checked on a clean VM (TK-160, docs/prueba-vm-2026-10-17.md); WebView2 was already there.
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
#define SqlExpressUrl "https://go.microsoft.com/fwlink/p/?linkid=2216019"
; TK-310: SQL Server setup's time limit. Only the VM test lowers it (ISCC /DSqlSetupMinutes=3) to cut an install off; releases keep 60.
#ifndef SqlSetupMinutes
  #define SqlSetupMinutes 60
#endif

[Setup]
AppId={{6E4C2B1A-5F0B-4C55-9C1E-7A0D2B9F4E21}
AppName=Abastalia
AppVersion={#AppVersion}
AppPublisher=Abastalia
DefaultDirName={autopf}\Abastalia
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=AbastaliaSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Abastalia (punto de venta)
CloseApplications=yes
; Code signing (Authenticode, R11 C3): build.ps1 passes /Sabastalia=<ABASTALIA_SIGNTOOL> $f and /DSigned when ABASTALIA_SIGNTOOL is set
; (docs/instalador-web.md, "Firma de código"). The setup and its uninstaller are then signed.
#ifdef Signed
SignTool=abastalia
SignedUninstaller=yes
#endif

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Types]
Name: "server"; Description: "PC principal (servidor de la tienda y caja)"
Name: "register"; Description: "Caja (solo la app de caja; el servidor está en otra PC)"

[Components]
Name: "server"; Description: "Servidor de la tienda (servicio de Windows y base de datos)"; Types: server; Flags: fixed
Name: "app"; Description: "App de caja"; Types: server register; Flags: fixed

[Files]
Source: "{#PublishDir}\server\*"; DestDir: "{app}\server"; Components: server; Flags: recursesubdirs ignoreversion
Source: "..\install.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "..\uninstall.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "update-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "remove-tree.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "remove-data.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
; R11 C6: the register app for the whole PC, in Program Files (only administrators and the SYSTEM update task can change it).
Source: "{#PublishDir}\app\*"; DestDir: "{app}\app"; Components: app; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{commonprograms}\Abastalia"; Filename: "{app}\app\Abastalia.exe"; Components: app
Name: "{commondesktop}\Abastalia"; Filename: "{app}\app\Abastalia.exe"; Components: app

[Run]
; The service (install.ps1) is installed from CurStepChanged in [Code]: [Run] ignores exit codes, and a failed service must fail the setup.
; Hourly as SYSTEM: the server updates when the service says it may (no shift open, or outside business hours); a register while its app is closed.
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""Abastalia\Actualizar"" /SC HOURLY /RU SYSTEM /RL HIGHEST /TR ""powershell.exe -NoProfile -ExecutionPolicy Bypass -File '{app}\scripts\update-service.ps1' -Role {code:Role}"""; Flags: runhidden waituntilterminated
Filename: "{app}\app\Abastalia.exe"; Description: "Abrir Abastalia"; Components: app; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""Abastalia\Actualizar"""; RunOnceId: "task"; Flags: runhidden
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall.ps1"" -Destination ""{app}\server"""; RunOnceId: "service"; Components: server; Flags: runhidden waituntilterminated

[Code]
var
  DownloadPage: TDownloadWizardPage;
  ServiceFailed: Boolean;

function IsServerRole: Boolean;
begin
  Result := WizardIsComponentSelected('server');
end;

{ update-service.ps1 -Role: what this PC is. }
function Role(Param: String): String;
begin
  if IsServerRole then Result := 'server' else Result := 'register';
end;

function HasWebView2: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
             or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version))
            and (Version <> '') and (Version <> '0.0.0.0');
end;

{ R11 C1: the server's connection string uses its own instance .\ABASTALIA (src/Hosts/Abastalia.Host.Local/appsettings.json). Another SQLEXPRESS on
  the PC is never reused: its administrators and network settings are someone else's. A ABASTALIA instance is this setup's (reinstall or update). }
function HasAbastaliaInstance: Boolean;
var
  Instance: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'ABASTALIA', Instance);
end;

{ TK-310 (VM test): a SQL Server setup cut off by its time limit (or a power cut) leaves ABASTALIA in Instance Names with its files but
  no service, and ConfigurationState SQL_Engine_Core_Inst = 0. Setup writes 1 there only when the engine is configured. Such an instance
  is not ready: the setup removes it and installs SQL Express again, instead of skipping SQL and failing on the service. }
function AbastaliaInstanceReady: Boolean;
var
  Id: String;
  State: Cardinal;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'ABASTALIA', Id) and
    RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\' + Id + '\ConfigurationState', 'SQL_Engine_Core_Inst', State) and (State = 1);
end;

{ TK-310: a store's database in the half-installed instance (its default folder; the setup always names it Abastalia). Then the instance
  is never removed by the setup: that is the technician's call. }
function HalfInstanceHasStoreData: Boolean;
var
  Id, Dir: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'ABASTALIA', Id) and
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\' + Id + '\Setup', 'SqlProgramDir', Dir) and
    FileExists(AddBackslash(Dir) + Id + '\MSSQL\DATA\Abastalia.mdf');
end;

{ TK-305: SQL Server's usage and error reports off too, not only its telemetry service: CustomerFeedback and EnableErrorReporting = 0 under
  the instance's CPE key (MSSQL17.ABASTALIA, from Instance Names) and its version's shared key (170). Every install, so older ones get it. }
procedure SqlTelemetryOff;
var
  Id, Key, Shared: String;
begin
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'ABASTALIA', Id) then exit;
  Key := 'SOFTWARE\Microsoft\Microsoft SQL Server\' + Id + '\CPE';
  Shared := 'SOFTWARE\Microsoft\Microsoft SQL Server\' + Copy(Id, 6, Pos('.', Id) - 6) + '0';
  if not (RegWriteDWordValue(HKLM, Key, 'CustomerFeedback', 0) and RegWriteDWordValue(HKLM, Key, 'EnableErrorReporting', 0) and
          RegWriteDWordValue(HKLM, Shared, 'CustomerFeedback', 0) and RegWriteDWordValue(HKLM, Shared, 'EnableErrorReporting', 0)) then
    Log('SQL Server telemetry: could not write ' + Key + ' or ' + Shared);
end;

{ Output of a PowerShell expression (one line), or Fallback when it fails. }
function PowerShellValue(const Expression, Fallback: String): String;
var
  Code: Integer;
  Output: String;
  Lines: AnsiString;
begin
  Result := Fallback;
  Output := ExpandConstant('{tmp}\abastalia-value.txt');
  if Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "' + Expression + ' | Set-Content -Encoding Default -LiteralPath ''' + Output + '''"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) and LoadStringFromFile(Output, Lines) and (Trim(String(Lines)) <> '') then
    Result := Trim(String(Lines));
end;

{ R11 C1: SQL Server's administrators are the PC's administrators, not the person who runs the setup day to day. The group's name is
  translated (BUILTIN\Administradores on a Spanish Windows): it is looked up by its SID. }
function AdministratorsGroup: String;
begin
  Result := PowerShellValue('(New-Object Security.Principal.SecurityIdentifier ''S-1-5-32-544'').Translate([Security.Principal.NTAccount]).Value',
    'BUILTIN\Administrators');
end;

{ R11 C2: whether the system drive is protected by BitLocker or Device Encryption (ProtectionStatus 1; Win32_EncryptableVolume needs the
  administrator rights the setup has). }
function SystemDriveEncrypted: Boolean;
begin
  Result := PowerShellValue('(Get-CimInstance -Namespace root/cimv2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume ' +
    '-ErrorAction SilentlyContinue | Where-Object DriveLetter -eq $env:SystemDrive).ProtectionStatus', '0') = '1';
end;

{ R10 #8: a downloaded installer runs as administrator only with a valid Authenticode signature (Status Valid: trusted chain, file untouched)
  whose certificate names exactly O=Microsoft Corporation, as a whole part of the subject. }
function SignedByMicrosoft(const FileName: String): Boolean;
var
  Code: Integer;
  Literal: String;
begin
  { The path goes inside a single-quoted PowerShell string: a quote in it is doubled. }
  Literal := FileName;
  StringChangeEx(Literal, '''', '''''', True);
  Result := Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature -LiteralPath ''' + Literal + '''; ' +
    'if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''(^|, )O=Microsoft Corporation(,|$)'') { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then
    SuppressibleMsgBox('El archivo descargado ' + ExtractFileName(FileName) + ' no tiene una firma válida de Microsoft y no se ejecutó.' + #13#10 +
      'Intente de nuevo; si se repite, llame al técnico.', mbCriticalError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Descargando lo que falta de Microsoft...', nil);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    SuppressibleMsgBox('Abastalia necesita Windows 10 u 11 de 64 bits.', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ Suppressible: a silent install (the update task, /VERYSILENT /SUPPRESSMSGBOXES) must never wait on a box nobody sees. }
procedure SqlNotInstalled(const Code: String);
begin
  SuppressibleMsgBox('SQL Server Express no se instaló (código ' + Code + '). Llame al técnico.', mbCriticalError, MB_OK, IDOK);
end;

{ A PowerShell single-quoted literal. }
function PsLiteral(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

{ TK-291: Exec has no time limit (the SSEI download once hung the setup forever). The program runs from a PowerShell script that waits at
  most Minutes and then kills it with its child processes; Code is then 1460 (ERROR_TIMEOUT). }
function ExecWithTimeout(const FileName, Params: String; Minutes: Integer; var Code: Integer): Boolean;
var
  Script: String;
begin
  Script := ExpandConstant('{tmp}\abastalia-run.ps1');
  Result := SaveStringToFile(Script,
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$p = Start-Process -FilePath ' + PsLiteral(FileName) + ' -ArgumentList ' + PsLiteral(Params) + ' -WindowStyle Hidden -PassThru' + #13#10 +
    '$null = $p.Handle' + #13#10 +
    'if (-not $p.WaitForExit(' + IntToStr(Minutes * 60000) + ')) { taskkill.exe /T /F /PID $p.Id | Out-Null; exit 1460 }' + #13#10 +
    'exit $p.ExitCode' + #13#10, False)
    and Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ TK-291: a SQL Express step (download or extraction) with a time limit and one retry. Clean is emptied before each try; the step worked
  when the program ends with 0 and Expected exists. What names the step in the error the store sees. }
function RunSqlStep(const FileName, Params, Clean, Expected, What: String; Minutes: Integer): Boolean;
var
  Attempt, Code: Integer;
begin
  Result := False;
  Code := -1;
  for Attempt := 1 to 2 do
  begin
    DelTree(Clean, True, True, True);
    Code := -1;
    if ExecWithTimeout(FileName, Params, Minutes, Code) and (Code = 0) and FileExists(Expected) then
    begin
      Result := True;
      exit;
    end;
    Log('SQL Express ' + What + ', attempt ' + IntToStr(Attempt) + ': code ' + IntToStr(Code));
  end;
  if Code = 1460 then
    SuppressibleMsgBox('SQL Server Express no se instaló: la ' + What + ' no terminó en ' + IntToStr(Minutes) + ' minutos (se intentó dos veces).' + #13#10 +
      'Revise la conexión a internet e intente de nuevo; si se repite, llame al técnico.', mbCriticalError, MB_OK, IDOK)
  else
    SqlNotInstalled(What + ' ' + IntToStr(Code) + ', se intentó dos veces');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code, Missing: Integer;
  Media: String;
begin
  Result := True;
  if CurPageID <> wpReady then exit;
  { R11 C2: a stolen main PC without disk encryption gives away the e-CF key, the store CA and the database. A warning, not a stop. }
  if IsServerRole and not SystemDriveEncrypted and
     (SuppressibleMsgBox('El disco de esta PC no está cifrado (BitLocker o Cifrado de dispositivo de Windows).' + #13#10 + #13#10 +
       'Si se la roban, se pueden leer la base de datos, los respaldos y la llave de los comprobantes electrónicos. Active el cifrado en ' +
       'Configuración de Windows → Privacidad y seguridad → Cifrado de dispositivo (o BitLocker) y guarde la clave de recuperación fuera de la PC.' + #13#10 + #13#10 +
       '¿Seguir con la instalación de todos modos?', mbConfirmation, MB_YESNO or MB_DEFBUTTON1, IDYES) = IDNO) then
  begin
    Result := False;
    exit;
  end;
  { TK-292 (second PC on the VM): the firewall rule opens 5443 on private networks only. Windows 11 marks a new network Public, and then a
    register on another PC never reaches this one. A warning: a store with a single PC does not need it. }
  if IsServerRole and (PowerShellValue('@(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object NetworkCategory -eq ''Public'').Count', '0') <> '0') then
    SuppressibleMsgBox('Windows tiene la red de esta PC como Pública, así que las cajas de otras PCs no se van a poder conectar a ella.' + #13#10 + #13#10 +
      'Si la tienda tiene más de una caja, cambie la red a Privada: Configuración de Windows → Red e Internet → Ethernet o Wi-Fi (su red) → ' +
      'Tipo de perfil de red → Red privada. Hágalo solo en la red de la tienda.', mbInformation, MB_OK, IDOK);
  if IsServerRole and HasAbastaliaInstance and not AbastaliaInstanceReady and HalfInstanceHasStoreData then
  begin
    SuppressibleMsgBox('SQL Server Express (instancia ABASTALIA) está dañado y tiene la base de datos de la tienda, así que el instalador no lo toca.' + #13#10 +
      'Llame al técnico.', mbCriticalError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  DownloadPage.Clear;
  Missing := 0;
  if not HasWebView2 then begin DownloadPage.Add('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', ''); Missing := Missing + 1; end;
  if IsServerRole and not AbastaliaInstanceReady then begin DownloadPage.Add('{#SqlExpressUrl}', 'SQLServerExpressSetup.exe', ''); Missing := Missing + 1; end;
  if Missing = 0 then exit;
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      SuppressibleMsgBox('No se pudo descargar: ' + GetExceptionMessage + #13#10 + 'Revise la conexión a internet e intente de nuevo.', mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  if FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe')) then
  begin
    if not SignedByMicrosoft(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe')) then begin Result := False; exit; end;
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    begin
      SuppressibleMsgBox('El componente WebView2 de Microsoft, que usa la app de caja, no se instaló (código ' + IntToStr(Code) + ').' + #13#10 +
        'Intente de nuevo; si se repite, llame al técnico.', mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  end;

  if FileExists(ExpandConstant('{tmp}\SQLServerExpressSetup.exe')) then
  begin
    if not SignedByMicrosoft(ExpandConstant('{tmp}\SQLServerExpressSetup.exe')) then begin Result := False; exit; end;
    WizardForm.StatusLabel.Caption := 'Instalando SQL Server Express (puede tardar varios minutos)...';
    { TK-160 (VM test): the 2025 SSEI bootstrapper ends with -1 on /ACTION=Install /QUIET. It only downloads the Express package (/ACTION=Download),
      which is extracted and its setup.exe run with SQL Server's own parameters. TK-291: both steps have a time limit and one retry. }
    Media := ExpandConstant('{tmp}\sql');
    if not RunSqlStep(ExpandConstant('{tmp}\SQLServerExpressSetup.exe'), '/ACTION=Download /MEDIATYPE=Core /LANGUAGE=en-US /QUIET /MEDIAPATH="' + Media + '"',
         Media, Media + '\SQLEXPR_x64_ENU.exe', 'descarga', 60) then begin Result := False; exit; end;
    if not SignedByMicrosoft(Media + '\SQLEXPR_x64_ENU.exe') then begin Result := False; exit; end;
    if not RunSqlStep(Media + '\SQLEXPR_x64_ENU.exe', '/q /x:"' + Media + '\setup"', Media + '\setup', Media + '\setup\setup.exe', 'extracción', 20) then
    begin
      Result := False;
      exit;
    end;
    { TK-310: a half-installed ABASTALIA (an earlier setup cut off) is removed first, with the same setup.exe. Cut off before its
      configuration (as on the VM) it has no instance name yet, only its id: /INSTANCENAME says it does not exist and /INSTANCEID
      removes it. Cut off later it has its name. }
    if HasAbastaliaInstance then
    begin
      WizardForm.StatusLabel.Caption := 'Quitando una instalación de SQL Server Express que quedó a medias...';
      Log('SQL Server: ABASTALIA is half-installed, uninstalling it');
      if not ExecWithTimeout(Media + '\setup\setup.exe', '/Q /ACTION=Uninstall /FEATURES=SQLEngine /INSTANCEID=ABASTALIA /ENU', {#SqlSetupMinutes}, Code) then Code := -1;
      Log('SQL Server uninstall by id: code ' + IntToStr(Code));
      if HasAbastaliaInstance then
      begin
        if not ExecWithTimeout(Media + '\setup\setup.exe', '/Q /ACTION=Uninstall /FEATURES=SQLEngine /INSTANCENAME=ABASTALIA /ENU', {#SqlSetupMinutes}, Code) then Code := -1;
        Log('SQL Server uninstall by name: code ' + IntToStr(Code));
      end;
      if HasAbastaliaInstance then
      begin
        SuppressibleMsgBox('Una instalación anterior de SQL Server Express quedó a medias y no se pudo quitar (código ' + IntToStr(Code) + ').' + #13#10 +
          'Reinicie la PC y vuelva a abrir el instalador de Abastalia; si se repite, llame al técnico.', mbCriticalError, MB_OK, IDOK);
        Result := False;
        exit;
      end;
    end;
    { Express only, its own instance ABASTALIA, listening on this PC only (no TCP, no named pipes), Windows logins only,
      sysadmin = the PC's administrators (R11 C1: never the current user, who would become SYSTEM through SQL Server),
      no telemetry service (TK-291: SQLTELEMETRY$ABASTALIA disabled). TK-305: with a time limit too (a slow PC takes 10-20 minutes). }
    if not ExecWithTimeout(Media + '\setup\setup.exe',
      '/Q /ACTION=Install /FEATURES=SQLEngine /IACCEPTSQLSERVERLICENSETERMS /ENU /UPDATEENABLED=0 /SQLSVCSTARTUPTYPE=Automatic ' +
      '/INSTANCENAME=ABASTALIA /TCPENABLED=0 /NPENABLED=0 /SQLTELSVCSTARTUPTYPE=Disabled /ADDCURRENTUSERASSQLADMIN=False /SQLSYSADMINACCOUNTS="' + AdministratorsGroup + '"', {#SqlSetupMinutes}, Code)
      or ((Code <> 0) and (Code <> 3010)) then
    begin
      { TK-292 (VM test): with a restart pending (Windows Update, another uninstall) SQL Server setup refuses to start: facility 1214,
        error 3010 = 0x84BE0BC2. The store can fix that one itself. }
      if Code = -2067919934 then
        SuppressibleMsgBox('Windows pide reiniciar la PC antes de instalar SQL Server Express.' + #13#10 +
          'Reinicie la PC y vuelva a abrir el instalador de Abastalia.', mbCriticalError, MB_OK, IDOK)
      else if Code = 1460 then
        SuppressibleMsgBox('SQL Server Express no se instaló: la instalación no terminó en {#SqlSetupMinutes} minutos.' + #13#10 +
          'Reinicie la PC y vuelva a abrir el instalador de Abastalia; si se repite, llame al técnico.', mbCriticalError, MB_OK, IDOK)
      else
        SqlNotInstalled(IntToStr(Code));
      Result := False;
    end;
  end;
end;

{ The service holds its files open: stop it before replacing them (an update over an installed server). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if IsServerRole then Exec('sc.exe', 'stop Abastalia', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ The service, once the files are in place (install.ps1: service, prepare-service, firewall, start). TK-160: from [Run] a failure went
  unnoticed (the setup ended with 0 and no service); now the setup ends with 10, which update-service.ps1 reports. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep <> ssPostInstall) or not IsServerRole then exit;
  SqlTelemetryOff;
  WizardForm.StatusLabel.Caption := 'Instalando el servicio de Abastalia...';
  if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\install.ps1') + '" -Source "' +
       ExpandConstant('{app}\server') + '" -Destination "' + ExpandConstant('{app}\server') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
  begin
    Log('install.ps1 failed: ' + IntToStr(Code));
    ServiceFailed := True;
    SuppressibleMsgBox('El servicio de Abastalia no se instaló (código ' + IntToStr(Code) + '). Llame al técnico.', mbCriticalError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ServiceFailed then Result := 10 else Result := 0;
end;

{ remove-data.ps1's exit code; -1 when PowerShell could not even start. }
function RemoveData(const Params: String): Integer;
var
  Code: Integer;
begin
  if Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then Result := Code else Result := -1;
end;

procedure DataNotRemoved(Code: Integer);
begin
  if Code = 3 then
    MsgBox('No se pudieron borrar todos los datos de Abastalia (la base de datos o la carpeta C:\ProgramData\Abastalia).' + #13#10 +
      'Llame al técnico para terminar de borrarlos.', mbError, MB_OK)
  else
    MsgBox('No se pudieron borrar los datos de Abastalia (código ' + IntToStr(Code) + '). Llame al técnico.', mbError, MB_OK);
end;

{ "Borrar datos": off by default. Without it the database and C:\ProgramData\Abastalia (sales, backups, license, keys) stay. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Script, Desktop, Saved: String;
begin
  if (CurUninstallStep = usUninstall) and not UninstallSilent and
     FileExists(ExpandConstant('{app}\scripts\remove-data.ps1')) and
     (MsgBox('¿Borrar también los datos de la tienda?' + #13#10 + #13#10 +
             'Se borran la base de datos Abastalia, los respaldos, la licencia y las llaves de C:\ProgramData\Abastalia. No se puede deshacer.' + #13#10 +
             'Si va a reinstalar o mudarse a otra PC, responda No.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    { R10 #9: the script first copies the last backup (from Backup:Folder in the installed server's settings) to the Public Desktop, which stays
      whoever signs in next; exit 2 = no copy, nothing deleted; exit 3 = something could not be deleted. }
    Desktop := ExpandConstant('{commondesktop}');
    Saved := AddBackslash(Desktop) + 'Abastalia - ultimo respaldo';
    Script := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\remove-data.ps1') + '" -BackupTo "' + Desktop + '"' +
      ' -ServerDir "' + ExpandConstant('{app}\server') + '"';
    Code := RemoveData(Script);
    if Code = 0 then
      MsgBox('Datos borrados. El último respaldo quedó en ' + Saved + '. Se abre con la frase de los respaldos.', mbInformation, MB_OK)
    else if Code = 2 then
    begin
      if MsgBox('No hay un respaldo de Abastalia, o no se pudo copiar a ' + Saved + ', así que no se borró nada.' + #13#10 +
                '¿Borrar los datos de todos modos, sin respaldo?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        Code := RemoveData(Script + ' -NoFinalBackup');
        if Code = 0 then MsgBox('Datos borrados, sin respaldo.', mbInformation, MB_OK) else DataNotRemoved(Code);
      end;
    end
    else
      DataNotRemoved(Code);
  end;
end;
