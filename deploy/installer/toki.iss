; Toki setup (TK-242): ONE .exe with two roles chosen in the wizard.
;   PC principal: the store server as the "Toki" Windows service (deploy/install.ps1 + prepare-service), its own SQL Server Express
;                 instance TOKI when missing (R11 C1), firewall 5443 on private networks, keys folder closed to other users, and the register app.
;   Caja:         only the register app (Toki.Client.Wpf).
; R11 C6: the register app goes to Program Files for the whole PC (a cashier cannot change it) and both roles are updated by the
; "Toki\Actualizar" SYSTEM task (update-service.ps1), which installs only setups signed by Toki (R11 C3).
; Both check the WebView2 Evergreen runtime. Both apps are published self-contained: no .NET runtime to install.
; SQL Express and the WebView2 bootstrapper are downloaded from Microsoft at install time, on the store PC.
; Build with deploy/installer/build.ps1 (it publishes and passes the defines below). Silent: TokiSetup.exe /VERYSILENT /TYPE=server|register

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
; Microsoft links. shortcut: verify the Express 2025 SSEI link and its silent parameters before the first real build (docs/instalador-web.md).
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
#define SqlExpressUrl "https://go.microsoft.com/fwlink/p/?linkid=2216019"

[Setup]
AppId={{6E4C2B1A-5F0B-4C55-9C1E-7A0D2B9F4E21}
AppName=Toki
AppVersion={#AppVersion}
AppPublisher=Toki
DefaultDirName={autopf}\Toki
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=TokiSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Toki (punto de venta)
CloseApplications=yes
; Code signing (Authenticode, R11 C3): build.ps1 passes /Stoki=<TOKI_SIGNTOOL> $f and /DSigned when TOKI_SIGNTOOL is set
; (docs/instalador-web.md, "Firma de código"). The setup and its uninstaller are then signed.
#ifdef Signed
SignTool=toki
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
Name: "{commonprograms}\Toki"; Filename: "{app}\app\Toki.exe"; Components: app
Name: "{commondesktop}\Toki"; Filename: "{app}\app\Toki.exe"; Components: app

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install.ps1"" -Source ""{app}\server"" -Destination ""{app}\server"""; StatusMsg: "Instalando el servicio de Toki..."; Components: server; Flags: runhidden waituntilterminated
; Hourly as SYSTEM: the server updates when the service says it may (no shift open, or outside business hours); a register while its app is closed.
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""Toki\Actualizar"" /SC HOURLY /RU SYSTEM /RL HIGHEST /TR ""powershell.exe -NoProfile -ExecutionPolicy Bypass -File '{app}\scripts\update-service.ps1' -Role {code:Role}"""; Flags: runhidden waituntilterminated
Filename: "{app}\app\Toki.exe"; Description: "Abrir Toki"; Components: app; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""Toki\Actualizar"""; RunOnceId: "task"; Flags: runhidden
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall.ps1"" -Destination ""{app}\server"""; RunOnceId: "service"; Components: server; Flags: runhidden waituntilterminated

[Code]
var
  DownloadPage: TDownloadWizardPage;

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

{ R11 C1: the server's connection string uses its own instance .\TOKI (src/Hosts/Toki.Host.Local/appsettings.json). Another SQLEXPRESS on
  the PC is never reused: its administrators and network settings are someone else's. A TOKI instance is this setup's (reinstall or update). }
function HasTokiInstance: Boolean;
var
  Instance: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'TOKI', Instance);
end;

{ Output of a PowerShell expression (one line), or Fallback when it fails. }
function PowerShellValue(const Expression, Fallback: String): String;
var
  Code: Integer;
  Output: String;
  Lines: AnsiString;
begin
  Result := Fallback;
  Output := ExpandConstant('{tmp}\toki-value.txt');
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
    MsgBox('El archivo descargado ' + ExtractFileName(FileName) + ' no tiene una firma válida de Microsoft y no se ejecutó.' + #13#10 +
      'Intente de nuevo; si se repite, llame al técnico.', mbCriticalError, MB_OK);
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
    MsgBox('Toki necesita Windows 10 u 11 de 64 bits.', mbCriticalError, MB_OK);
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code, Missing: Integer;
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
  DownloadPage.Clear;
  Missing := 0;
  if not HasWebView2 then begin DownloadPage.Add('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', ''); Missing := Missing + 1; end;
  if IsServerRole and not HasTokiInstance then begin DownloadPage.Add('{#SqlExpressUrl}', 'SQLServerExpressSetup.exe', ''); Missing := Missing + 1; end;
  if Missing = 0 then exit;
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox('No se pudo descargar: ' + GetExceptionMessage + #13#10 + 'Revise la conexión a internet e intente de nuevo.', mbCriticalError, MB_OK);
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
      MsgBox('El componente WebView2 de Microsoft, que usa la app de caja, no se instaló (código ' + IntToStr(Code) + ').' + #13#10 +
        'Intente de nuevo; si se repite, llame al técnico.', mbCriticalError, MB_OK);
      Result := False;
      exit;
    end;
  end;

  if FileExists(ExpandConstant('{tmp}\SQLServerExpressSetup.exe')) then
  begin
    if not SignedByMicrosoft(ExpandConstant('{tmp}\SQLServerExpressSetup.exe')) then begin Result := False; exit; end;
    WizardForm.StatusLabel.Caption := 'Instalando SQL Server Express (puede tardar varios minutos)...';
    { The SSEI bootstrapper: Express only, its own instance TOKI, listening on this PC only (no TCP, no named pipes), Windows logins only,
      sysadmin = the PC's administrators (R11 C1: never the current user, who would become SYSTEM through SQL Server). }
    if not Exec(ExpandConstant('{tmp}\SQLServerExpressSetup.exe'),
      '/ACTION=Install /QUIET /IACCEPTSQLSERVERLICENSETERMS /HIDEPROGRESSBAR /ENU ' +
      '/INSTANCENAME=TOKI /TCPENABLED=0 /NPENABLED=0 /SQLSYSADMINACCOUNTS="' + AdministratorsGroup + '"', '', SW_HIDE, ewWaitUntilTerminated, Code)
      or ((Code <> 0) and (Code <> 3010)) then
    begin
      MsgBox('SQL Server Express no se instaló (código ' + IntToStr(Code) + '). Llame al técnico.', mbCriticalError, MB_OK);
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
  if IsServerRole then Exec('sc.exe', 'stop Toki', '', SW_HIDE, ewWaitUntilTerminated, Code);
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
    MsgBox('No se pudieron borrar todos los datos de Toki (la base de datos o la carpeta C:\ProgramData\Toki).' + #13#10 +
      'Llame al técnico para terminar de borrarlos.', mbError, MB_OK)
  else
    MsgBox('No se pudieron borrar los datos de Toki (código ' + IntToStr(Code) + '). Llame al técnico.', mbError, MB_OK);
end;

{ "Borrar datos": off by default. Without it the database and C:\ProgramData\Toki (sales, backups, license, keys) stay. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Script, Desktop, Saved: String;
begin
  if (CurUninstallStep = usUninstall) and not UninstallSilent and
     FileExists(ExpandConstant('{app}\scripts\remove-data.ps1')) and
     (MsgBox('¿Borrar también los datos de la tienda?' + #13#10 + #13#10 +
             'Se borran la base de datos Toki, los respaldos, la licencia y las llaves de C:\ProgramData\Toki. No se puede deshacer.' + #13#10 +
             'Si va a reinstalar o mudarse a otra PC, responda No.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    { R10 #9: the script first copies the last backup (from Backup:Folder in {app}\server's settings) to the Public Desktop, which stays
      whoever signs in next; exit 2 = no copy, nothing deleted; exit 3 = something could not be deleted. }
    Desktop := ExpandConstant('{commondesktop}');
    Saved := AddBackslash(Desktop) + 'Toki - ultimo respaldo';
    Script := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\remove-data.ps1') + '" -BackupTo "' + Desktop + '"' +
      ' -ServerDir "' + ExpandConstant('{app}\server') + '"';
    Code := RemoveData(Script);
    if Code = 0 then
      MsgBox('Datos borrados. El último respaldo quedó en ' + Saved + '. Se abre con la frase de los respaldos.', mbInformation, MB_OK)
    else if Code = 2 then
    begin
      if MsgBox('No hay un respaldo de Toki, o no se pudo copiar a ' + Saved + ', así que no se borró nada.' + #13#10 +
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
