#define MyAppName      "LLM Utilities"
#define MyAppVersion   "1.22.0"
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
  ClaudePage: TWizardPage;
  ChkSelectAll: TNewCheckBox;
  ChkRowster: TNewCheckBox;
  ChkFReader: TNewCheckBox;
  ChkCliSilentProxy: TNewCheckBox;
  ChkFWriter: TNewCheckBox;
  ChkContractGenerator: TNewCheckBox;
  ChkCodeNavigator: TNewCheckBox;
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
  ChkFWriter.Checked := ChkSelectAll.Checked;
  ChkContractGenerator.Checked := ChkSelectAll.Checked;
  ChkCodeNavigator.Checked := ChkSelectAll.Checked;
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

// ── Custom wizard page: MCP server checkboxes (opt-in) ─────────────────────────

procedure InitializeWizard;
begin
  ClaudePage := CreateCustomPage(wpSelectTasks,
    'MCP Integration',
    'Select which MCP servers to register.');

  ChkSelectAll := TNewCheckBox.Create(ClaudePage);
  ChkSelectAll.Parent := ClaudePage.Surface;
  ChkSelectAll.Left := 8;
  ChkSelectAll.Top := 16;
  ChkSelectAll.Width := ClaudePage.SurfaceWidth - 16;
  ChkSelectAll.Height := 17;
  ChkSelectAll.Caption := 'Select all MCP servers';
  ChkSelectAll.Checked := True;
  ChkSelectAll.OnClick := @MspSelectAllClick;

  ChkRowster := TNewCheckBox.Create(ClaudePage);
  ChkRowster.Parent := ClaudePage.Surface;
  ChkRowster.Left := 22;
  ChkRowster.Top := ChkSelectAll.Top + ChkSelectAll.Height + 8;
  ChkRowster.Width := ClaudePage.SurfaceWidth - 30;
  ChkRowster.Height := 17;
  ChkRowster.Caption := 'Register Rowster (MySQL database query tool)';
  ChkRowster.Checked := True;

  ChkFReader := TNewCheckBox.Create(ClaudePage);
  ChkFReader.Parent := ClaudePage.Surface;
  ChkFReader.Left := 22;
  ChkFReader.Top := ChkRowster.Top + ChkRowster.Height + 8;
  ChkFReader.Width := ClaudePage.SurfaceWidth - 30;
  ChkFReader.Height := 17;
  ChkFReader.Caption := 'Register FReader (file reader and text search)';
  ChkFReader.Checked := True;

  ChkCliSilentProxy := TNewCheckBox.Create(ClaudePage);
  ChkCliSilentProxy.Parent := ClaudePage.Surface;
  ChkCliSilentProxy.Left := 22;
  ChkCliSilentProxy.Top := ChkFReader.Top + ChkFReader.Height + 8;
  ChkCliSilentProxy.Width := ClaudePage.SurfaceWidth - 30;
  ChkCliSilentProxy.Height := 17;
  ChkCliSilentProxy.Caption := 'Register CliSilentProxy (shell command proxy)';
  ChkCliSilentProxy.Checked := True;

  ChkFWriter := TNewCheckBox.Create(ClaudePage);
  ChkFWriter.Parent := ClaudePage.Surface;
  ChkFWriter.Left := 22;
  ChkFWriter.Top := ChkCliSilentProxy.Top + ChkCliSilentProxy.Height + 8;
  ChkFWriter.Width := ClaudePage.SurfaceWidth - 30;
  ChkFWriter.Height := 17;
  ChkFWriter.Caption := 'Register FWriter (validated code editor)';
  ChkFWriter.Checked := True;

  ChkContractGenerator := TNewCheckBox.Create(ClaudePage);
  ChkContractGenerator.Parent := ClaudePage.Surface;
  ChkContractGenerator.Left := 22;
  ChkContractGenerator.Top := ChkFWriter.Top + ChkFWriter.Height + 8;
  ChkContractGenerator.Width := ClaudePage.SurfaceWidth - 30;
  ChkContractGenerator.Height := 17;
  ChkContractGenerator.Caption := 'Register ContractGenerator (C# to TypeScript interface generator)';
  ChkContractGenerator.Checked := True;

  ChkCodeNavigator := TNewCheckBox.Create(ClaudePage);
  ChkCodeNavigator.Parent := ClaudePage.Surface;
  ChkCodeNavigator.Left := 22;
  ChkCodeNavigator.Top := ChkContractGenerator.Top + ChkContractGenerator.Height + 8;
  ChkCodeNavigator.Width := ClaudePage.SurfaceWidth - 30;
  ChkCodeNavigator.Height := 17;
  ChkCodeNavigator.Caption := 'Register CodeNavigator (semantic code navigation for C# and TS)';
  ChkCodeNavigator.Checked := True;

  ChkLlmSelectAll := TNewCheckBox.Create(ClaudePage);
  ChkLlmSelectAll.Parent := ClaudePage.Surface;
  ChkLlmSelectAll.Left := 8;
  ChkLlmSelectAll.Top := ChkCodeNavigator.Top + ChkCodeNavigator.Height + 14;
  ChkLlmSelectAll.Width := ClaudePage.SurfaceWidth - 16;
  ChkLlmSelectAll.Height := 17;
  ChkLlmSelectAll.Caption := 'Register with all supported LLMs';
  ChkLlmSelectAll.Checked := True;
  ChkLlmSelectAll.OnClick := @LlmSelectAllClick;

  ChkClaudeCode := TNewCheckBox.Create(ClaudePage);
  ChkClaudeCode.Parent := ClaudePage.Surface;
  ChkClaudeCode.Left := 22;
  ChkClaudeCode.Top := ChkLlmSelectAll.Top + ChkLlmSelectAll.Height + 8;
  ChkClaudeCode.Width := ClaudePage.SurfaceWidth - 30;
  ChkClaudeCode.Height := 17;
  ChkClaudeCode.Caption := 'Claude Code (~/.claude.json)';
  ChkClaudeCode.Checked := True;

  ChkGemini := TNewCheckBox.Create(ClaudePage);
  ChkGemini.Parent := ClaudePage.Surface;
  ChkGemini.Left := 22;
  ChkGemini.Top := ChkClaudeCode.Top + ChkClaudeCode.Height + 8;
  ChkGemini.Width := ClaudePage.SurfaceWidth - 30;
  ChkGemini.Height := 17;
  ChkGemini.Caption := 'Gemini CLI (~/.gemini/settings.json)';
  ChkGemini.Checked := True;

  ChkOpenCode := TNewCheckBox.Create(ClaudePage);
  ChkOpenCode.Parent := ClaudePage.Surface;
  ChkOpenCode.Left := 22;
  ChkOpenCode.Top := ChkGemini.Top + ChkGemini.Height + 8;
  ChkOpenCode.Width := ClaudePage.SurfaceWidth - 30;
  ChkOpenCode.Height := 17;
  ChkOpenCode.Caption := 'OpenCode CLI (~/.config/opencode/opencode.json)';
  ChkOpenCode.Checked := True;

  ChkCursor := TNewCheckBox.Create(ClaudePage);
  ChkCursor.Parent := ClaudePage.Surface;
  ChkCursor.Left := 22;
  ChkCursor.Top := ChkOpenCode.Top + ChkOpenCode.Height + 8;
  ChkCursor.Width := ClaudePage.SurfaceWidth - 30;
  ChkCursor.Height := 17;
  ChkCursor.Caption := 'Cursor (~/.cursor/mcp.json)';
  ChkCursor.Checked := True;

  ChkWindsurf := TNewCheckBox.Create(ClaudePage);
  ChkWindsurf.Parent := ClaudePage.Surface;
  ChkWindsurf.Left := 22;
  ChkWindsurf.Top := ChkCursor.Top + ChkCursor.Height + 8;
  ChkWindsurf.Width := ClaudePage.SurfaceWidth - 30;
  ChkWindsurf.Height := 17;
  ChkWindsurf.Caption := 'Windsurf (~/.codeium/windsurf/mcp_config.json)';
  ChkWindsurf.Checked := True;

  ChkZed := TNewCheckBox.Create(ClaudePage);
  ChkZed.Parent := ClaudePage.Surface;
  ChkZed.Left := 22;
  ChkZed.Top := ChkWindsurf.Top + ChkWindsurf.Height + 8;
  ChkZed.Width := ClaudePage.SurfaceWidth - 30;
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
    SummaryLines.Lines.Add('✓ FWriter.exe installed');
    SummaryLines.Lines.Add('✓ ContractGenerator.exe installed');
    SummaryLines.Lines.Add('✓ CodeNavigator.exe installed');
    SummaryLines.Lines.Add('✓ McpRegistrar.exe installed');
    SummaryLines.Lines.Add('✓ LLMUtilities.Commons.dll installed');
    SummaryLines.Lines.Add('✓ LLMUtilities added to system PATH');

    // ── MCP registration (Claude Code + optionally Gemini CLI) ─────────────────
    if (not ChkRowster.Checked) and (not ChkFReader.Checked) and (not ChkCliSilentProxy.Checked) and (not ChkFWriter.Checked) and (not ChkContractGenerator.Checked) and (not ChkCodeNavigator.Checked) then
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
      if ChkFWriter.Checked then
        Args := Args + ' --register-fwriter --fwriter-path "' + ExpandConstant('{app}\FWriter.exe') + '"';
      if ChkContractGenerator.Checked then
        Args := Args + ' --register-contractgenerator --contractgenerator-path "' + ExpandConstant('{app}\ContractGenerator.exe') + '"';
      if ChkCodeNavigator.Checked then
        Args := Args + ' --register-codenavigator --codenavigator-path "' + ExpandConstant('{app}\CodeNavigator.exe') + '"';
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
          SummaryLines.Lines.Add('✓ Rowster registered with Claude Code');
        if ChkFReader.Checked then
          SummaryLines.Lines.Add('✓ FReader registered with Claude Code');
        if ChkCliSilentProxy.Checked then
          SummaryLines.Lines.Add('✓ CliSilentProxy registered with Claude Code');
        if ChkFWriter.Checked then
          SummaryLines.Lines.Add('✓ FWriter registered with Claude Code');
        if ChkContractGenerator.Checked then
          SummaryLines.Lines.Add('✓ ContractGenerator registered with Claude Code');
        if ChkCodeNavigator.Checked then
          SummaryLines.Lines.Add('✓ CodeNavigator registered with Claude Code');
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
