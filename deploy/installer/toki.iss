; Toki setup (TK-242): ONE .exe with two roles chosen in the wizard.
;   PC principal: the store server as the "Toki" Windows service (deploy/install.ps1 + prepare-service), SQL Server Express when
;                 missing, firewall 5443 on private networks, keys folder closed to other users, and the register app.
;   Caja:         only the register app (Toki.Client.Wpf, installed by its Velopack setup so it can update itself).
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
; Code signing (Authenticode): the owner buys the certificate. Then, in Inno Setup: Tools > Configure Sign Tools, name "toki" =
;   signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f "C:\ruta\certificado.pfx" /p $p $f
; and uncomment the next line (build.ps1 passes /Stoki=... when TOKI_SIGNTOOL is set).
;SignTool=toki

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
Source: "update-service.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "remove-data.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
; The register app's own Velopack setup (vpk pack): installs per user in %LocalAppData%\Toki and updates itself.
Source: "{#PublishDir}\app-setup\Toki-Setup.exe"; DestDir: "{tmp}"; Components: app; Flags: deleteafterinstall

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install.ps1"" -Source ""{app}\server"" -Destination ""{app}\server"""; StatusMsg: "Instalando el servicio de Toki..."; Components: server; Flags: runhidden waituntilterminated
; Hourly: applies a server update when the service says it may (no shift open, or outside business hours).
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""Toki\Actualizar servidor"" /SC HOURLY /RU SYSTEM /RL HIGHEST /TR ""powershell.exe -NoProfile -ExecutionPolicy Bypass -File '{app}\scripts\update-service.ps1'"""; Components: server; Flags: runhidden waituntilterminated
; Per user, as the person who ran the setup (not the elevated account), so the app lands in their profile.
Filename: "{tmp}\Toki-Setup.exe"; Parameters: "--silent"; StatusMsg: "Instalando la app de caja..."; Components: app; Flags: runasoriginaluser waituntilterminated

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""Toki\Actualizar servidor"""; RunOnceId: "task"; Components: server; Flags: runhidden
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall.ps1"" -Destination ""{app}\server"""; RunOnceId: "service"; Components: server; Flags: runhidden waituntilterminated

[Code]
var
  DownloadPage: TDownloadWizardPage;

function IsServerRole: Boolean;
begin
  Result := WizardIsComponentSelected('server');
end;

function HasWebView2: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
             or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version))
            and (Version <> '') and (Version <> '0.0.0.0');
end;

{ The server's connection string uses .\SQLEXPRESS (src/Hosts/Toki.Host.Local/appsettings.json): an existing instance is reused. }
function HasSqlExpress: Boolean;
var
  Instance: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', 'SQLEXPRESS', Instance);
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
  DownloadPage.Clear;
  Missing := 0;
  if not HasWebView2 then begin DownloadPage.Add('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', ''); Missing := Missing + 1; end;
  if IsServerRole and not HasSqlExpress then begin DownloadPage.Add('{#SqlExpressUrl}', 'SQLServerExpressSetup.exe', ''); Missing := Missing + 1; end;
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
    { The SSEI bootstrapper: Express only, listening on this PC only (no TCP), the instance the connection string names. }
    if not Exec(ExpandConstant('{tmp}\SQLServerExpressSetup.exe'),
      '/ACTION=Install /QUIET /IACCEPTSQLSERVERLICENSETERMS /HIDEPROGRESSBAR /ENU ' +
      '/INSTANCENAME=SQLEXPRESS /TCPENABLED=0 /ADDCURRENTUSERASSQLADMIN=True', '', SW_HIDE, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010)) then
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
