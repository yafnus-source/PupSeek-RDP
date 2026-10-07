; Pup Seek RDP — Inno Setup installer script.
; v3.9.3 — C#/.NET 8 self-contained single-file build.
; Per-user install (no admin required for installation itself — the app
; auto-elevates itself via UAC at runtime for shadow RDP).
; Compile: ISCC.exe PupSeekRDP.iss  →  produces C:\temp\Clo\prod\3.9.3.0\install\PupSeekRDP-3.9.3-Setup.exe

[Setup]
AppName=Pup Seek RDP
AppVersion=3.9.3
AppPublisher=M.A.D.
AppPublisherURL=https://claude.ai/claude-code
DefaultDirName={localappdata}\Programs\Pup Seek RDP
DefaultGroupName=Pup Seek RDP
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
OutputDir=C:\temp\Clo\prod\3.9.3.0\install
OutputBaseFilename=PupSeekRDP-3.9.3-Setup
DisableProgramGroupPage=yes
DisableDirPage=no
Uninstallable=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=C:\temp\Clo\cpro\app.ico
UninstallDisplayIcon={app}\PupSeekRDP.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "C:\temp\Clo\cpro\bin\Release\net8.0-windows\win-x64\publish\PupSeekRDP.exe"; DestDir: "{app}"; Flags: ignoreversion
; TODO.шная bundle: APK меняется простой заменой файла тем же именем, .iss трогать не надо
Source: "C:\temp\Clo\cpro\Bundle\Android\TODO-shnaya.apk"; DestDir: "{app}\Android"; Flags: ignoreversion
Source: "C:\temp\Clo\cpro\Bundle\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "C:\temp\Clo\cpro\Bundle\Docs\*"; DestDir: "{app}\Docs"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе";

[Icons]
Name: "{group}\Pup Seek RDP"; Filename: "{app}\PupSeekRDP.exe"; WorkingDir: "{app}"; Comment: "Запуск Pup Seek RDP"
Name: "{group}\Удалить Pup Seek RDP"; Filename: "{uninstallexe}"; Comment: "Удаление Pup Seek RDP"
Name: "{userdesktop}\Pup Seek RDP"; Filename: "{app}\PupSeekRDP.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Запуск Pup Seek RDP"

[Run]
Filename: "{app}\PupSeekRDP.exe"; Description: "Запустить Pup Seek RDP сейчас"; Flags: nowait postinstall skipifsilent
