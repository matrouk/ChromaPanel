<#
    Builds ChromaPanel.exe.

    Uses the C# compiler that ships with the .NET Framework, so there is
    nothing to install: no SDK, no NuGet, no MSBuild. The result is a single
    self-contained executable that runs on any Windows 10/11 machine.

        .\build.ps1              build to .\build\ChromaPanel.exe
        .\build.ps1 -Run         build, then launch it
        .\build.ps1 -Clean       delete the build folder first
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Clean,
    [switch]$Tools,
    [switch]$Test,
    [string]$OutDir = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'

function Find-Csc {
    $roots = @(
        "$env:WINDIR\Microsoft.NET\Framework64",
        "$env:WINDIR\Microsoft.NET\Framework"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like 'v4*' } |
               Sort-Object Name -Descending |
               ForEach-Object { Join-Path $_.FullName 'csc.exe' } |
               Where-Object { Test-Path $_ } |
               Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

$csc = Find-Csc
if (-not $csc) {
    throw "Could not find csc.exe. The .NET Framework 4.x runtime is required (it ships with Windows 10 and 11)."
}
Write-Host "Compiler : $csc"

$srcDir = Join-Path $PSScriptRoot 'src'
$sources = @(Get-ChildItem $srcDir -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "No .cs files found in $srcDir" }
Write-Host "Sources  : $($sources.Count) file(s)"

if ($Clean -and (Test-Path $OutDir)) {
    Remove-Item $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$exe      = Join-Path $OutDir 'ChromaPanel.exe'
$manifest = Join-Path $srcDir 'app.manifest'

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/debug-'
    '/warn:3'
    '/langversion:5'
    "/out:$exe"
    "/win32manifest:$manifest"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
) + $sources

Write-Host "Output   : $exe"
Write-Host ''

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

$size = [Math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host ''
Write-Host "Built ChromaPanel.exe  ($size KB)" -ForegroundColor Green

if ($Tools) {
    # Read-only console dump of the display-identification report.
    $toolSrc = @(
        (Join-Path $srcDir 'Json.cs')
        (Join-Path $srcDir 'Model.cs')
        (Join-Path $srcDir 'Native.cs')
        (Join-Path $srcDir 'Report.cs')
        (Join-Path $srcDir 'Drs.cs')
        (Join-Path $srcDir 'Sharpening.cs')
        (Join-Path $PSScriptRoot 'tools\DumpDisplays.cs')
    )
    $toolExe = Join-Path $OutDir 'DumpDisplays.exe'
    $toolArgs = @(
        '/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/langversion:5'
        "/out:$toolExe"
        '/reference:System.dll'
        '/reference:System.Drawing.dll'
        '/reference:System.Windows.Forms.dll'
    ) + $toolSrc
    & $csc $toolArgs
    if ($LASTEXITCODE -ne 0) { throw "Tool compilation failed with exit code $LASTEXITCODE" }
    Write-Host "Built DumpDisplays.exe" -ForegroundColor Green
}

if ($Test) {
    # Pure-logic tests: colour maths, JSON, settings, shortcut parsing,
    # blackout targeting. None of them call into Windows display or NVIDIA
    # APIs, so they run anywhere, including on a CI runner with no display.
    $testExe = Join-Path $OutDir 'Tests.exe'
    $testArgs = @(
        '/nologo', '/target:exe', '/platform:anycpu', '/langversion:5'
        "/out:$testExe"
        '/reference:System.dll'
        '/reference:System.Drawing.dll'
        '/reference:System.Windows.Forms.dll'
        (Join-Path $srcDir 'Json.cs')
        (Join-Path $srcDir 'Model.cs')
        (Join-Path $srcDir 'Native.cs')
        (Join-Path $srcDir 'Hotkey.cs')
        (Join-Path $srcDir 'Blackout.cs')
        (Join-Path $PSScriptRoot 'tests\Tests.cs')
    )
    & $csc $testArgs
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE" }

    Write-Host ''
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

    # The sharpening shader, run through Direct3D and checked byte for byte
    # against a CPU version of the same maths. Uses the GPU when there is
    # one and Microsoft's WARP software rasteriser otherwise (CI has no GPU).
    $gpuExe = Join-Path $OutDir 'GpuTests.exe'
    $gpuArgs = @(
        '/nologo', '/target:exe', '/platform:anycpu', '/langversion:5'
        "/out:$gpuExe"
        '/reference:System.dll'
        (Join-Path $srcDir 'Json.cs')
        (Join-Path $srcDir 'Model.cs')
        (Join-Path $srcDir 'Dx.cs')
        (Join-Path $srcDir 'Sharpen.cs')
        (Join-Path $srcDir 'Anime4K.cs')
        (Join-Path $PSScriptRoot 'tests\GpuTests.cs')
    )
    & $csc $gpuArgs
    if ($LASTEXITCODE -ne 0) { throw "GPU test compilation failed with exit code $LASTEXITCODE" }

    Write-Host ''
    # The original GLSL is the reference the generated Anime4K shader is checked against.
    & $gpuExe (Join-Path $PSScriptRoot 'third_party\anime4k\Anime4K_Restore_CNN_M.glsl')
    if ($LASTEXITCODE -ne 0) { throw "GPU tests failed" }
}

if ($Run) {
    Write-Host 'Launching...'
    Start-Process $exe
}
