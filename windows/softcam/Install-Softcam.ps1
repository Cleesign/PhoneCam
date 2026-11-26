# Installs the "DirectShow Softcam" virtual camera used by PhoneCam.
# Copies softcam.dll to C:\Program Files\Softcam and registers it there (needs administrator rights).
# Run with -Uninstall to remove it again.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$target = Join-Path $env:ProgramFiles 'Softcam'
$dll = Join-Path $target 'softcam.dll'
$installer = Join-Path $target 'softcam_installer.exe'

try {
    if ($Uninstall) {
        if (Test-Path $installer) { & $installer unregister $dll }
        Write-Host "Softcam unregistered. You can delete $target." -ForegroundColor Green
    }
    else {
        New-Item -ItemType Directory -Force $target | Out-Null
        foreach ($f in 'softcam.dll', 'softcam_installer.exe', 'LICENSE') {
            Copy-Item (Join-Path $PSScriptRoot $f) $target -Force
        }
        & $installer register $dll
        if ($LASTEXITCODE -ne 0) { throw "softcam_installer.exe failed (exit code $LASTEXITCODE)." }
        Write-Host 'Done. The camera "DirectShow Softcam" is now available.' -ForegroundColor Green
    }
}
catch {
    Write-Host $_ -ForegroundColor Red
}
Read-Host 'Press Enter to close'
