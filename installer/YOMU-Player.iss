; YOMU Player installer (Inno Setup 6)
; Stable:   ISCC /DChannel=stable /DAppVersion=1.0.0 /DFileVersion=1.0.0.0 ...
; Nightly:  ISCC /DNightly=1 /DChannel=nightly ...
#ifndef Channel
  #define Channel "stable"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "1.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\YomuPlayer.Desktop\bin\Release\net8.0-windows10.0.19041.0\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef SetupIcon
  #define SetupIcon "..\src\YomuPlayer.Desktop\Assets\app.ico"
#endif
#ifndef SetupSmallImage
  #define SetupSmallImage "..\src\YomuPlayer.Desktop\Assets\app-small.bmp"
#endif

#ifdef Nightly
  #define AppName "YOMU Player Nightly"
  #define AppId "{{D2C6E8B3-8F7D-4B9C-A023-4F6E7B8C9D01}"
  #define Scheme "yomu-nightly"
  #define OutputBaseFilename "YOMU-Player-Nightly-Setup-" + AppVersion
#else
  #define AppName "YOMU Player"
  #define AppId "{{C1B5D7A2-7E6C-4A8B-9F12-3E5D6A7B8C90}"
  #define Scheme "yomu"
  #define OutputBaseFilename "YOMU-Player-Setup-" + AppVersion
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=YOMU Player
AppPublisherURL=https://github.com/kabouls/YOMU-Player
AppSupportURL=https://github.com/kabouls/YOMU-Player/issues
AppUpdatesURL=https://kabouls.github.io/YOMU-Player/{#Channel}.json
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#SetupIcon}
WizardSmallImageFile={#SetupSmallImage}
UninstallDisplayIcon={app}\YomuPlayer.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dark includetitlebar
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#FileVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#FileVersion}
VersionInfoDescription={#AppName} installer
VersionInfoCompany=YOMU Player

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.marker,data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\YomuPlayer.exe"; WorkingDir: "{app}"; Check: WantStartMenu
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\YomuPlayer.exe"; WorkingDir: "{app}"; Check: WantDesktop

[Registry]
Root: HKCU; Subkey: "Software\Classes\{#Scheme}"; ValueType: string; ValueName: ""; ValueData: "URL:{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#Scheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\YomuPlayer.exe,0"
Root: HKCU; Subkey: "Software\Classes\{#Scheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\YomuPlayer.exe"" ""%1"""

[Run]
Filename: "{app}\YomuPlayer.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\YomuPlayer.exe"; Flags: nowait; Check: ShouldRelaunchAfterSilentUpdate

[Code]
var
  OptionsPage: TInputDirWizardPage;
  StartMenuCheck: TNewCheckBox;
  DesktopCheck: TNewCheckBox;
  CreateStartMenu: Boolean;
  CreateDesktop: Boolean;

function WantStartMenu: Boolean;
begin
  Result := CreateStartMenu;
end;

function WantDesktop: Boolean;
begin
  Result := CreateDesktop;
end;

function ShouldRelaunchAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (Pos('/RELAUNCH', Uppercase(GetCmdTail)) > 0);
end;

procedure ApplyDarkTheme;
begin
  { Inno Setup's built-in dark style handles the wizard, edits, checkboxes and buttons. }
end;

procedure InitializeWizard;
begin
  OptionsPage := CreateInputDirPage(
    wpWelcome,
    'Install ' + ExpandConstant('{#AppName}'),
    'Choose where to install YOMU Player and which shortcuts to create.',
    '',
    False,
    '');
  OptionsPage.Add('Install folder:');
  OptionsPage.Values[0] := ExpandConstant('{localappdata}\Programs\{#AppName}');

  StartMenuCheck := TNewCheckBox.Create(OptionsPage.Surface);
  StartMenuCheck.Parent := OptionsPage.Surface;
  StartMenuCheck.Left := 0;
  StartMenuCheck.Top := OptionsPage.Surface.Height - 88;
  StartMenuCheck.Width := OptionsPage.SurfaceWidth;
  StartMenuCheck.Height := 24;
  StartMenuCheck.Caption := 'Create a Start Menu shortcut';
  StartMenuCheck.Checked := True;

  DesktopCheck := TNewCheckBox.Create(OptionsPage.Surface);
  DesktopCheck.Parent := OptionsPage.Surface;
  DesktopCheck.Left := 0;
  DesktopCheck.Top := OptionsPage.Surface.Height - 60;
  DesktopCheck.Width := OptionsPage.SurfaceWidth;
  DesktopCheck.Height := 24;
  DesktopCheck.Caption := 'Create a Desktop shortcut';
  DesktopCheck.Checked := True;

  CreateStartMenu := True;
  CreateDesktop := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = OptionsPage.ID then
  begin
    if Trim(OptionsPage.Values[0]) = '' then
    begin
      MsgBox('Choose an install folder.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    WizardForm.DirEdit.Text := OptionsPage.Values[0];
    CreateStartMenu := StartMenuCheck.Checked;
    CreateDesktop := DesktopCheck.Checked;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = OptionsPage.ID then
  begin
    WizardForm.NextButton.Caption := 'Install';
    WizardForm.BackButton.Visible := False;
  end;
end;
