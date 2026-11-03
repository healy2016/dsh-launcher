#requires -Version 5.1
<#
.SYNOPSIS
    Build DSHWebLauncher.exe (or the test variant) without .NET SDK or Visual Studio.
.DESCRIPTION
    Uses the Windows built-in .NET Framework csc.exe compiler.
    Outputs DSHWebLauncher.exe / DSHWebLauncher-Test.exe next to this script.
    A side-by-side settings file (settings.ini / settings.test.ini) is generated.
.PARAMETER Test
    Build the test variant DSHWebLauncher-Test.exe with settings.test.ini on port 9090.
.PARAMETER WorkDir
    DSH checkout root used in the generated settings file.
.PARAMETER NoSmoke
    Skip the --status smoke test.
.EXAMPLE
    .\build.ps1                       # production build on port 9000
    .\build.ps1 -Test                 # test build on port 9090
    .\build.ps1 -Test -WorkDir D:\code\deepseek-harness
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [string]$WorkDir = 'D:\openworkspace\deepseek-harness',
    [switch]$NoSmoke
)

$ErrorActionPreference = 'Stop'

function Find-CscCompiler {
    $candidates = @(
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
    )

    $fromPath = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates += $fromPath.Source }

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }

    throw @"
csc.exe not found. Make sure .NET Framework 4.x is installed.
Expected path:
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"@
}

function Test-Prerequisites {
    $required = @{
        Source = Join-Path $PSScriptRoot 'src\DshWebLauncher.cs'
        Icon   = Join-Path $PSScriptRoot 'assets\whale.ico'
        Manifest = Join-Path $PSScriptRoot 'src\app.manifest'
    }

    foreach ($item in $required.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $item.Value -PathType Leaf)) {
            throw "Missing $($item.Key): $($item.Value)"
        }
    }
}

function New-SettingsFile {
    param(
        [string]$Path,
        [string]$Port,
        # Do NOT name this $Profile: PowerShell's $PROFILE is an automatic variable and the assignment is lost.
        [string]$ProfileName,
        [string]$NodeExePath
    )
    $content = @"
# DSH Web Launcher configuration
# LaunchMode=global uses npm global dsh; LaunchMode=local uses local apps/cli
LaunchMode=local
NodePath=$NodeExePath
DshBin=
# Profile only affects local mode; empty means headless
Profile=$ProfileName
Port=$Port
WorkDir=$WorkDir
LaunchArgs=
NodeNoWarnings=1
AutoRestart=1
"@
    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
    Write-Host "Created $Path (Profile=$ProfileName, Port=$Port)" -ForegroundColor Cyan
}

# --- main ---

$exeName = if ($Test) { 'DSHWebLauncher-Test.exe' } else { 'DSHWebLauncher.exe' }
$iniName = if ($Test) { 'settings.test.ini' } else { 'settings.ini' }
$port    = if ($Test) { '9090' } else { '9000' }
# Test builds always use an isolated profile so the production profile's bundle list is never touched.
$profileName = if ($Test) { 'web-test' } else { 'web' }
$OutFile = Join-Path $PSScriptRoot $exeName

# Write node.exe's absolute path into the generated ini so it need not be filled in by hand.
$nodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $nodeExe) { $nodeExe = '' }

$csc = Find-CscCompiler
Test-Prerequisites

Write-Host "Compiler: $csc" -ForegroundColor Cyan
& $csc /nologo 2>&1 | Select-String -Pattern 'Visual C# Compiler' | Select-Object -First 1 | Write-Host

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    "/win32icon:$(Join-Path $PSScriptRoot 'assets\whale.ico')"
    "/win32manifest:$(Join-Path $PSScriptRoot 'src\app.manifest')"
    "/resource:$(Join-Path $PSScriptRoot 'assets\whale.ico'),whale.ico"
    "/out:$OutFile"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Core.dll'
    '/reference:System.Web.Extensions.dll'
    (Join-Path $PSScriptRoot 'src\DshWebLauncher.cs')
)

Write-Host "Building $exeName..." -ForegroundColor Cyan
$out = & $csc $cscArgs 2>&1
$code = $LASTEXITCODE

if ($out) { $out | ForEach-Object { Write-Host $_ } }

if ($code -ne 0) {
    throw "Build failed with csc exit code: $code"
}

if (-not (Test-Path -LiteralPath $OutFile -PathType Leaf)) {
    throw "csc returned 0 but $OutFile was not produced."
}

$item = Get-Item -LiteralPath $OutFile
Write-Host "Build succeeded: $($item.FullName) ($($item.Length) bytes)" -ForegroundColor Green

# Generate side-by-side settings file
$settingsPath = Join-Path $PSScriptRoot $iniName
New-SettingsFile -Path $settingsPath -Port $port -ProfileName $profileName -NodeExePath $nodeExe

# Smoke test
if (-not $NoSmoke) {
    Write-Host "Running --status smoke test..." -ForegroundColor Cyan
    $tmpOut = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $OutFile -ArgumentList '--status' `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput $tmpOut
        $statusText = Get-Content -LiteralPath $tmpOut -Raw
        Write-Host $statusText
        if ($proc.ExitCode -ne 0) {
            throw "--status exited with code $($proc.ExitCode)"
        }
        if ($statusText -notmatch 'port=') {
            throw "--status did not produce expected config output."
        }
        Write-Host "Smoke test passed." -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $tmpOut -ErrorAction SilentlyContinue
    }
}
