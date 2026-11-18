[Setup]
AppId={{5EC3B50D-5396-4381-8E97-491EF6D3AC27}
AppName=Clario Presenter
AppVersion={#AppVersion}
AppPublisher=msaririzki
AppPublisherURL=https://github.com/msaririzki/Clario-Presenter
AppUpdatesURL=https://github.com/msaririzki/Clario-Presenter/releases/latest
DefaultDirName={localappdata}\Programs\Clario Presenter
DefaultGroupName=Clario Presenter
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Clario.Presenter.exe
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=msaririzki
OutputBaseFilename=Clario-Presenter-Setup-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
RestartApplications=no

[Languages]
Name: "id"; MessagesFile: "compiler:Default.isl,Indonesian.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat pintasan di desktop"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Clario Presenter"; Filename: "{app}\Clario.Presenter.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Clario Presenter"; Filename: "{app}\Clario.Presenter.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Use the same shell launch as the Start menu, including when Setup was opened by a packaged app.
Filename: "{win}\explorer.exe"; Parameters: """{userprograms}\Clario Presenter.lnk"""; Description: "Buka Clario Presenter"; Flags: nowait postinstall skipifsilent
