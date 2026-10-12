; Instalador PRIVADO do DriveFlow (C#). Gere com Instalador.bat na raiz do repositório.
; O DriveFlow.exe já leva o oauth_client.json embutido (login "Entrar com Google"),
; então este instalador nunca vai para o GitHub nem para releases públicas.

#define MyAppName "DriveFlow"
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif

[Setup]
; ID próprio: não mexe na instalação da versão Python.
AppId={{6A0C4E2B-5D7F-4C61-9B0E-2F9D3E7A8C51}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Nexotool
DefaultDirName={localappdata}\Programs\DriveFlowNet
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=DriveFlow-Setup-PRIVADO-v{#MyAppVersion}
SetupIconFile=..\src\DriveFlow.App\Assets\upload.ico
UninstallDisplayIcon={app}\DriveFlow.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"
Name: "startup"; Description: "Abrir o DriveFlow ao entrar no Windows"

[InstallDelete]
; Versão anterior: arquivos que não existem mais no pacote novo.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "..\publish\DriveFlow\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\DriveFlow.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\DriveFlow.exe"; Tasks: desktopicon

[Registry]
; Mesmo valor que o app grava em Configurações > Inicialização.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DriveFlowNet"; \
  ValueData: """{app}\DriveFlow.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\DriveFlow.exe"; Description: "Abrir o DriveFlow"; Flags: nowait postinstall skipifsilent
