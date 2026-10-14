#define MyAppName "声屿 · 多音箱音乐空间"
#ifndef MyAppVersion
  #define MyAppVersion "0.4.0-preview.4"
#endif
#ifndef WindowsVersion
  #define WindowsVersion "0.4.0.4"
#endif
#ifndef AppBinary
  #define AppBinary "..\publish\PairUp.App.exe"
#endif
#ifndef AppWebRoot
  #define AppWebRoot "..\publish\Web"
#endif
#define MyAppPublisher "songyi141319；原作 Jamim Mehdi"
#define MyAppURL "https://github.com/songyi141319/pairup-zh-micsync"
#define MyAppExeName "PairUp.App.exe"

[Setup]
; Keep the fork separate from the original application's installer registration.
AppId={{DA759762-7011-40FC-825B-DCCA0B20BA17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#WindowsVersion}
AppCopyright=Copyright (c) 2026 Jamim Mehdi. MIT License.
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\PairUp-Chinese
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Shengyu-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\PairUp.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=no
DisableDirPage=no
LicenseFile=..\LICENSE
InfoBeforeFile=安装说明.txt
CloseApplications=no
RestartApplications=no
UninstallDisplayName={#MyAppName}
ShowLanguageDialog=no

; Audio state must be restored by the app's normal tray Exit action before an
; upgrade or uninstall. Never taskkill, auto-close, or silently launch the app.

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#AppBinary}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
Source: "{#AppWebRoot}\*"; DestDir: "{app}\Web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "安装说明.txt"; DestDir: "{app}"; DestName: "使用说明.txt"; Flags: ignoreversion
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\{#MyAppName}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即打开声屿"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked
