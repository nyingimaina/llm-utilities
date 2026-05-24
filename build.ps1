param([switch]$SkipInstaller)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$publish = "$root\publish"
$iscc    = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

Write-Host "==> Cleaning publish dir..."
if (Test-Path $publish) { Remove-Item "$publish\*" -Recurse -Force }
else { New-Item -ItemType Directory -Path $publish | Out-Null }

$projects   = @("Rowster", "FReader", "CliSilentProxy", "FWriter", "ContractGenerator", "CodeNavigator", "Notifier", "McpRegistrar")
$ridProjects = @{
    "NotifierHelper" = "win-x64"
}

foreach ($proj in $projects) {
    Write-Host "==> Publishing $proj..."
    dotnet publish "$root\src\$proj" -c Release `
        -o "$publish" `
        --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $proj" }
}

foreach ($pair in $ridProjects.GetEnumerator()) {
    Write-Host "==> Publishing $($pair.Key) ($($pair.Value))..."
    dotnet publish "$root\src\$($pair.Key)" -c Release `
        -o "$publish" `
        -r $pair.Value `
        --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($pair.Key)" }
}

Write-Host "==> Published exes:"
Get-ChildItem "$publish\*.exe" | ForEach-Object { Write-Host "    $($_.Name)" }

if (-not $SkipInstaller) {
    if (-not (Test-Path $iscc)) { Write-Warning "ISCC.exe not found — skipping installer build."; exit 0 }
    Write-Host "==> Building installer..."
    & $iscc "$root\LLMUtilitiesSetup.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "==> Installer: $root\installer\LLMUtilitiesSetup.exe"
}

Write-Host "==> Done."
