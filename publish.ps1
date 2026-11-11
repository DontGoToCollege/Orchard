# Builds Orchard as one portable exe.
# Usage:  .\publish.ps1        -> self-tests, then Orchard.exe (+ Versions\Archive\Orchard-<version>.exe)
#                                  A running Orchard is closed and restarted on the new version.
#         .\publish.ps1 -Run   -> same, and starts it even if it wasn't running
# Refuses a version that's already archived: raise <Version> in src\Orchard\Orchard.csproj first.
param([switch]$Run)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Orchard\Orchard.csproj"
$out  = Join-Path $root "dist"
# The x64 SDK; the dotnet on PATH can be the x86 one, which has no SDK on some PCs.
$dotnet = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { "dotnet" }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\Orchard-$version.exe"
if (Test-Path $archive) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Get-ChildItem $out -File | Where-Object { $_.Name -ne "Orchard.exe" } | Remove-Item -Force

# The built exe checks its own Swift writer and starter files before it is kept.
$exe = Join-Path $out "Orchard.exe"
$test = Start-Process $exe -ArgumentList "--selftest" -Wait -PassThru
if ($test.ExitCode -ne 0) {
    Get-Content (Join-Path $env:TEMP "orchard-selftest.log") | Write-Host -ForegroundColor Red
    throw "Self-test failed; nothing was archived."
}

New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Write-Host ("Built Orchard {0} ({1} MB), copy kept in Versions\Archive" -f $version, [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green

# A running Orchard is closed so the new version can replace it (the owner's standing wish), then started again.
# It closes the way its X button does, which offers to save unsaved work first.
$top = Join-Path $root "Orchard.exe"
$running = @(Get-Process Orchard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $top })
foreach ($p in $running) {
    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(60000)) { throw "Orchard didn't close within a minute, so it wasn't replaced." }
}
Copy-Item $exe $top
Write-Host "Orchard.exe is now $version." -ForegroundColor Green
if ($running.Count -gt 0 -or $Run) {
    # Started through Windows (not as this script's child) so it keeps running after the build.
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$top`""; CurrentDirectory = $root } | Out-Null
}
