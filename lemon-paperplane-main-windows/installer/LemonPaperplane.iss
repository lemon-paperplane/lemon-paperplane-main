; 柠檬纸飞机 Windows 安装包（Inno Setup 6）。由 package.ps1 调用：
;   ISCC /DAppVersion=0.1.0 /DArch=x64|arm64 /DSourceDir=<已整理好的安装目录> /O<输出目录> LemonPaperplane.iss
; 装到当前用户（%LOCALAPPDATA%\Programs\Lemon Paperplane），不需要管理员权限。

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error 需要 /DSourceDir=<安装目录>
#endif

[Setup]
AppId={{6E0B8E2A-2F4C-4B53-9A8D-5C1E7F3A9B21}
AppName=Lemon Paperplane
AppVersion={#AppVersion}
AppVerName=Lemon Paperplane {#AppVersion}
AppPublisher=LemonIT
AppPublisherURL=https://github.com/lemon-paperplane
AppSupportURL=https://github.com/lemon-paperplane/lemon-paperplane-main
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Lemon Paperplane
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputBaseFilename=LemonPaperplane-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\LemonPaperplane\Resources\app.ico
UninstallDisplayIcon={app}\LemonPaperplane.exe
UninstallDisplayName={cm:AppDisplayName}
; ConPTY（终端）从 Windows 10 1809 起才有
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.AppDisplayName=Lemon Paperplane
zh.AppDisplayName=柠檬纸飞机

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; 内置插件、离线商店、编辑器网页每次整份替换（旧版本多出来的文件不能留下）
Type: filesandordirs; Name: "{app}\plugins"
Type: filesandordirs; Name: "{app}\store"
Type: filesandordirs; Name: "{app}\editor"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{cm:AppDisplayName}"; Filename: "{app}\LemonPaperplane.exe"
Name: "{autodesktop}\{cm:AppDisplayName}"; Filename: "{app}\LemonPaperplane.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LemonPaperplane.exe"; Description: "{cm:LaunchProgram,{cm:AppDisplayName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 先停掉后台服务和正在运行的工程，再关掉托盘里的界面
Filename: "{app}\paperplane.exe"; Parameters: "shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "StopDaemon"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM LemonPaperplane.exe"; Flags: runhidden waituntilterminated; RunOnceId: "CloseUI"

[Code]
// 升级前停掉旧版本的后台服务（它在用安装目录里的 paperplane.exe 和插件程序）
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\paperplane.exe');
  if FileExists(Exe) then
    Exec(Exe, 'shutdown', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
