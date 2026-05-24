#define MyAppName      "LLM Utilities"
#define MyAppVersion   "1.32.0"
#define MyAppPublisher "Savanna HerdIQ"
#define PublishDir     "publish"
#define ReadmeFile     "README.md"

[Setup]
AppId={{B7C2E4F1-9D3A-4B0E-8F2C-3A7D5E9B1C4F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LLMUtilities
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=LLMUtilitiesSetup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*.exe";                          DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll";                          DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*.json";                         DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\runtimes\*";                     DestDir: "{app}\runtimes"; Flags: ignoreversion skipifsourcedoesntexist recursesubdirs createallsubdirs
Source: "{#ReadmeFile}";                                DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKLM; \
  Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
  ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; \
  Check: NeedsAddPath(ExpandConstant('{app}'))

[Icons]
Name: "{group}\LLM Utilities README"; Filename: "{app}\README.md"

[Code]

var
  McpPage: TWizardPage;
  LlmPage: TWizardPage;
  ChkSelectAll: TNewCheckBox;
  ChkRowster: TNewCheckBox;
  ChkFReader: TNewCheckBox;
  ChkCliSilentProxy: TNewCheckBox;
  ChkNotifier: TNewCheckBox;
  ChkLlmSelectAll: TNewCheckBox;
  ChkClaudeCode: TNewCheckBox;
  ChkGemini: TNewCheckBox;
  ChkOpenCode: TNewCheckBox;
  ChkCursor: TNewCheckBox;
  ChkWindsurf: TNewCheckBox;
  ChkZed: TNewCheckBox;
  SummaryPage: TWizardPage;
  SummaryLines: TNewMemo;

// ── .NET 9 detection ─────────────────────────────────────────────────────────

function IsDotNet9Installed(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\9.*'), FindRec);
  if Result then FindClose(FindRec);
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  Response: Integer;
begin
  Result := True;
  if IsDotNet9Installed() then Exit;

  Response := MsgBox(
    '.NET 9 Runtime is required but was not found on this system.' + #13#10 + #13#10 +
    'Click Yes to install it now (requires internet connection).' + #13#10 +
    'Click No to cancel setup.',
    mbConfirmation, MB_YESNO);

  if Response = IDNO then
  begin
    Result := False;
    Exit;
  end;

  Exec('powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -Command "' +
       'Invoke-WebRequest -Uri ''https://dot.net/v1/dotnet-install.ps1'' ' +
       '-OutFile ''$env:TEMP\dotnet-install.ps1''; ' +
       '& $env:TEMP\dotnet-install.ps1 -Channel 9.0 -Runtime dotnet ' +
       '-InstallDir ''$env:ProgramFiles\dotnet''"',
       '', SW_SHOW, ewWaitUntilTerminated, ResultCode);

  if not IsDotNet9Installed() then
  begin
    MsgBox(
      '.NET 9 installation may not have completed. Please install .NET 9 Runtime ' +
      'manually from https://dotnet.microsoft.com/download/dotnet/9.0, then re-run this installer.',
      mbError, MB_OK);
    Result := False;
  end;
end;

// ── PATH helpers ──────────────────────────────────────────────────────────────

function NeedsAddPath(Path: string): Boolean;
var
  Current: string;
begin
  if not RegQueryStringValue(HKLM,
      'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
      'Path', Current)
  then begin Result := True; Exit; end;
  Result := Pos(';' + Lowercase(Path) + ';',
                ';' + Lowercase(Current) + ';') = 0;
end;

procedure RemovePath(Path: string);
var
  Current: string;
  Lower:   string;
  LowerC:  string;
begin
  if not RegQueryStringValue(HKLM,
      'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
      'Path', Current)
  then Exit;

  Lower  := Lowercase(Path);
  LowerC := Lowercase(Current);

  if Pos(';' + Lower, LowerC) > 0 then
    StringChangeEx(Current, ';' + Path, '', False)
  else if Pos(Lower + ';', LowerC) > 0 then
    StringChangeEx(Current, Path + ';', '', False)
  else if Lowercase(Current) = Lower then
    Current := '';

  RegWriteExpandStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', Current);
end;

// ── Select All click handlers ─────────────────────────────────────────────────

procedure MspSelectAllClick(Sender: TObject);
begin
  ChkRowster.Checked := ChkSelectAll.Checked;
  ChkFReader.Checked := ChkSelectAll.Checked;
  ChkCliSilentProxy.Checked := ChkSelectAll.Checked;
  ChkNotifier.Checked := ChkSelectAll.Checked;
end;

procedure LlmSelectAllClick(Sender: TObject);
begin
  ChkClaudeCode.Checked := ChkLlmSelectAll.Checked;
  ChkGemini.Checked := ChkLlmSelectAll.Checked;
  ChkOpenCode.Checked := ChkLlmSelectAll.Checked;
  ChkCursor.Checked := ChkLlmSelectAll.Checked;
  ChkWindsurf.Checked := ChkLlmSelectAll.Checked;
  ChkZed.Checked := ChkLlmSelectAll.Checked;
end;

// ── Custom wizard page: MCP server checkboxes ────────────────────────────────

procedure InitializeWizard;
begin
  McpPage := CreateCustomPage(wpSelectTasks,
    'MCP Servers',
    'Select which MCP servers to install and register.');

  ChkSelectAll := TNewCheckBox.Create(McpPage);
  ChkSelectAll.Parent := McpPage.Surface;
  ChkSelectAll.Left := 8;
  ChkSelectAll.Top := 16;
  ChkSelectAll.Width := McpPage.SurfaceWidth - 16;
  ChkSelectAll.Height := 17;
  ChkSelectAll.Caption := 'Select all MCP servers';
  ChkSelectAll.Checked := True;
  ChkSelectAll.OnClick := @MspSelectAllClick;

  ChkRowster := TNewCheckBox.Create(McpPage);
  ChkRowster.Parent := McpPage.Surface;
  ChkRowster.Left := 22;
  ChkRowster.Top := ChkSelectAll.Top + ChkSelectAll.Height + 8;
  ChkRowster.Width := McpPage.SurfaceWidth - 30;
  ChkRowster.Height := 17;
  ChkRowster.Caption := 'Register Rowster (MySQL database query tool)';
  ChkRowster.Checked := True;

  ChkFReader := TNewCheckBox.Create(McpPage);
  ChkFReader.Parent := McpPage.Surface;
  ChkFReader.Left := 22;
  ChkFReader.Top := ChkRowster.Top + ChkRowster.Height + 8;
  ChkFReader.Width := McpPage.SurfaceWidth - 30;
  ChkFReader.Height := 17;
  ChkFReader.Caption := 'Register FReader (file reader and text search)';
  ChkFReader.Checked := True;

  ChkCliSilentProxy := TNewCheckBox.Create(McpPage);
  ChkCliSilentProxy.Parent := McpPage.Surface;
  ChkCliSilentProxy.Left := 22;
  ChkCliSilentProxy.Top := ChkFReader.Top + ChkFReader.Height + 8;
  ChkCliSilentProxy.Width := McpPage.SurfaceWidth - 30;
  ChkCliSilentProxy.Height := 17;
  ChkCliSilentProxy.Caption := 'Register CliSilentProxy (shell command proxy)';
  ChkCliSilentProxy.Checked := True;

  ChkNotifier := TNewCheckBox.Create(McpPage);
  ChkNotifier.Parent := McpPage.Surface;
  ChkNotifier.Left := 22;
  ChkNotifier.Top := ChkCliSilentProxy.Top + ChkCliSilentProxy.Height + 8;
  ChkNotifier.Width := McpPage.SurfaceWidth - 30;
  ChkNotifier.Height := 17;
  ChkNotifier.Caption := 'Register Notifier (desktop notification service)';
  ChkNotifier.Checked := True;

  // ── Custom wizard page: LLM target checkboxes ────────────────────────────────

  LlmPage := CreateCustomPage(McpPage.ID,
    'LLM Integration',
    'Select which LLM CLI tools to register MCP servers with.');

  ChkLlmSelectAll := TNewCheckBox.Create(LlmPage);
  ChkLlmSelectAll.Parent := LlmPage.Surface;
  ChkLlmSelectAll.Left := 8;
  ChkLlmSelectAll.Top := 16;
  ChkLlmSelectAll.Width := LlmPage.SurfaceWidth - 16;
  ChkLlmSelectAll.Height := 17;
  ChkLlmSelectAll.Caption := 'Register with all supported LLMs';
  ChkLlmSelectAll.Checked := True;
  ChkLlmSelectAll.OnClick := @LlmSelectAllClick;

  ChkClaudeCode := TNewCheckBox.Create(LlmPage);
  ChkClaudeCode.Parent := LlmPage.Surface;
  ChkClaudeCode.Left := 22;
  ChkClaudeCode.Top := ChkLlmSelectAll.Top + ChkLlmSelectAll.Height + 8;
  ChkClaudeCode.Width := LlmPage.SurfaceWidth - 30;
  ChkClaudeCode.Height := 17;
  ChkClaudeCode.Caption := 'Claude Code (~/.claude.json)';
  ChkClaudeCode.Checked := True;

  ChkGemini := TNewCheckBox.Create(LlmPage);
  ChkGemini.Parent := LlmPage.Surface;
  ChkGemini.Left := 22;
  ChkGemini.Top := ChkClaudeCode.Top + ChkClaudeCode.Height + 8;
  ChkGemini.Width := LlmPage.SurfaceWidth - 30;
  ChkGemini.Height := 17;
  ChkGemini.Caption := 'Gemini CLI (~/.gemini/settings.json)';
  ChkGemini.Checked := True;

  ChkOpenCode := TNewCheckBox.Create(LlmPage);
  ChkOpenCode.Parent := LlmPage.Surface;
  ChkOpenCode.Left := 22;
  ChkOpenCode.Top := ChkGemini.Top + ChkGemini.Height + 8;
  ChkOpenCode.Width := LlmPage.SurfaceWidth - 30;
  ChkOpenCode.Height := 17;
  ChkOpenCode.Caption := 'OpenCode CLI (~/.config/opencode/opencode.json)';
  ChkOpenCode.Checked := True;

  ChkCursor := TNewCheckBox.Create(LlmPage);
  ChkCursor.Parent := LlmPage.Surface;
  ChkCursor.Left := 22;
  ChkCursor.Top := ChkOpenCode.Top + ChkOpenCode.Height + 8;
  ChkCursor.Width := LlmPage.SurfaceWidth - 30;
  ChkCursor.Height := 17;
  ChkCursor.Caption := 'Cursor (~/.cursor/mcp.json)';
  ChkCursor.Checked := True;

  ChkWindsurf := TNewCheckBox.Create(LlmPage);
  ChkWindsurf.Parent := LlmPage.Surface;
  ChkWindsurf.Left := 22;
  ChkWindsurf.Top := ChkCursor.Top + ChkCursor.Height + 8;
  ChkWindsurf.Width := LlmPage.SurfaceWidth - 30;
  ChkWindsurf.Height := 17;
  ChkWindsurf.Caption := 'Windsurf (~/.codeium/windsurf/mcp_config.json)';
  ChkWindsurf.Checked := True;

  ChkZed := TNewCheckBox.Create(LlmPage);
  ChkZed.Parent := LlmPage.Surface;
  ChkZed.Left := 22;
  ChkZed.Top := ChkWindsurf.Top + ChkWindsurf.Height + 8;
  ChkZed.Width := LlmPage.SurfaceWidth - 30;
  ChkZed.Height := 17;
  ChkZed.Caption := 'Zed (~/.config/zed/settings.json)';
  ChkZed.Checked := True;

  // ── Summary page (appears after install, before finish) ──────────────────────

  SummaryPage := CreateCustomPage(wpInfoAfter,
    'Installation Summary',
    'LLM Utilities installation completed.');

  SummaryLines := TNewMemo.Create(SummaryPage);
  SummaryLines.Parent := SummaryPage.Surface;
  SummaryLines.Left := 8;
  SummaryLines.Top := 8;
  SummaryLines.Width := SummaryPage.SurfaceWidth - 16;
  SummaryLines.Height := SummaryPage.SurfaceHeight - 16;
  SummaryLines.ReadOnly := True;
  SummaryLines.ScrollBars := ssVertical;

  SummaryLines.Lines.Add('Installing...');
  SummaryLines.Lines.Add('');
  SummaryLines.Lines.Add('Results will appear after installation.');
end;

// ── Post-install: register MCP servers with Claude Code (if checked) ────────

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ExePath: string;
  Args: string;
begin
  if CurStep = ssPostInstall then
  begin
    // ── Build summary ──────────────────────────────────────────────────────────
    SummaryLines.Lines.Clear();
    SummaryLines.Lines.Add('✓ Rowster.exe installed');
    SummaryLines.Lines.Add('✓ FReader.exe installed');
    SummaryLines.Lines.Add('✓ CliSilentProxy.exe installed');
    SummaryLines.Lines.Add('✓ Notifier.exe installed');
    SummaryLines.Lines.Add('✓ NotifierHelper.exe installed');
    SummaryLines.Lines.Add('✓ McpRegistrar.exe installed');
    SummaryLines.Lines.Add('✓ LLMUtilities.Commons.dll installed');
    SummaryLines.Lines.Add('✓ LLMUtilities added to system PATH');

    // ── MCP registration (Claude Code + optionally Gemini CLI) ─────────────────
    if (not ChkRowster.Checked) and (not ChkFReader.Checked) and (not ChkCliSilentProxy.Checked) and (not ChkNotifier.Checked) then
    begin
      SummaryLines.Lines.Add('');
      SummaryLines.Lines.Add('○ MCP registration skipped');
    end
    else
    begin
      ExePath := ExpandConstant('{app}\McpRegistrar.exe');
      Args := '';
      if ChkRowster.Checked then
        Args := Args + ' --register-rowster --rowster-path "' + ExpandConstant('{app}\Rowster.exe') + '"';
      if ChkFReader.Checked then
        Args := Args + ' --register-freader --freader-path "' + ExpandConstant('{app}\FReader.exe') + '"';
      if ChkCliSilentProxy.Checked then
        Args := Args + ' --register-clisilentproxy --clisilentproxy-path "' + ExpandConstant('{app}\CliSilentProxy.exe') + '"';
      if ChkNotifier.Checked then
        Args := Args + ' --register-notifier --notifier-path "' + ExpandConstant('{app}\Notifier.exe') + '"';
      if not ChkClaudeCode.Checked then
        Args := Args + ' --skip-claude';
      if ChkGemini.Checked then
        Args := Args + ' --register-gemini';
      if ChkOpenCode.Checked then
        Args := Args + ' --register-opencode';
      if ChkCursor.Checked then
        Args := Args + ' --register-cursor';
      if ChkWindsurf.Checked then
        Args := Args + ' --register-windsurf';
      if ChkZed.Checked then
        Args := Args + ' --register-zed';

      if Exec(ExePath, Args, '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
      and (ResultCode = 0) then
      begin
        SummaryLines.Lines.Add('');
        if ChkRowster.Checked then
          SummaryLines.Lines.Add('✓ Rowster registered with LLMs');
        if ChkFReader.Checked then
          SummaryLines.Lines.Add('✓ FReader registered with LLMs');
        if ChkCliSilentProxy.Checked then
          SummaryLines.Lines.Add('✓ CliSilentProxy registered with LLMs');
        if ChkNotifier.Checked then
          SummaryLines.Lines.Add('✓ Notifier registered with LLMs');
        SummaryLines.Lines.Add('');
        SummaryLines.Lines.Add('  Also registered:');
        if ChkGemini.Checked then
          SummaryLines.Lines.Add('  ✓ Gemini CLI');
        if ChkOpenCode.Checked then
          SummaryLines.Lines.Add('  ✓ OpenCode CLI');
        if ChkCursor.Checked then
          SummaryLines.Lines.Add('  ✓ Cursor');
        if ChkWindsurf.Checked then
          SummaryLines.Lines.Add('  ✓ Windsurf');
        if ChkZed.Checked then
          SummaryLines.Lines.Add('  ✓ Zed');
      end
      else
      begin
        SummaryLines.Lines.Add('');
        SummaryLines.Lines.Add('✗ MCP registration failed (exit code ' +
          IntToStr(ResultCode) + '). Check the console output for details.');
      end;
    end;
  end;
end;

// ── Uninstall helpers ─────────────────────────────────────────────────────────

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ExePath: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExePath := ExpandConstant('{app}\McpRegistrar.exe');
    if FileExists(ExePath) then
      Exec(ExePath, '--uninstall', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
  end;

  if CurUninstallStep = usPostUninstall then
    RemovePath(ExpandConstant('{app}'));
end;
