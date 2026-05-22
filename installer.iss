[Setup]
AppId={{8A7B3C2D-4E5F-6G78-H9I0-J1K2L3M4N5O6}
AppName=LLM Utilities
AppVersion=1.0.0
AppPublisher=LLM Utilities
DefaultDirName={localappdata}\llm_utilities
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=llm_utilities_setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addpath"; Description: "Add to PATH"; GroupDescription: "System Integration:"; Flags: checkedonce

[Dirs]
Name: "{app}"; Flags: uninsalwaysuninstall

[Files]
Source: "publish\llm_tool_*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\*.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\*.deps.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\*.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "publish\index.json"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addpath; Flags: uninsdeletekey

[Code]
procedure BroadcastEnvChange;
var
  Hwnd: Integer;
begin
  Hwnd := $FFFF;
  SendMessage(Hwnd, $1A, 0, 'Environment');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    BroadcastEnvChange;
end;