$ErrorActionPreference = "Stop"
$appName = "JeekLauncher"

# The app has already downloaded, extracted, and verified the update package
# into a staging folder before launching this script. All that remains here is
# the short critical window: wait for the app to exit, swap the files, restart.

if ($args.Count -eq 0) {
    Exit 1
}

$stageDir = [IO.Path]::GetFullPath($args[0])
$installDir = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $installDir "$appName.exe"
$components = @("Runtime", "Data", "Scripts", "$appName.exe")
$swap = Join-Path $installDir (".update-" + [guid]::NewGuid())
$incoming = Join-Path $swap "incoming"
$backup = Join-Path $swap "backup"
$saved = @()
$installed = @()
$keepBackup = $false

Write-Host "================================================================"
Write-Host " $appName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    foreach ($file in @("$appName.exe", "Runtime\$appName.dll", "Runtime\$appName.runtimeconfig.json", "Data\Languages.tab", "Scripts\AutoUpdate.ps1")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDir $file) -PathType Leaf)) {
            throw "Staged update package is missing $file"
        }
    }
    New-Item -ItemType Directory -Path $incoming, $backup -Force | Out-Null
    foreach ($name in $components) {
        Copy-Item -LiteralPath (Join-Path $stageDir $name) -Destination $incoming -Recurse
    }

    Write-Host "[1/3] Waiting for $appName to exit..."
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -and ($_.Path -ieq $exePath)) {
                $_.WaitForExit()
            }
        }
        catch {}
    }

    Write-Host "[2/3] Installing files..."
    foreach ($name in $components) {
        $target = Join-Path $installDir $name
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup
            $saved += $name
        }
        Move-Item -LiteralPath (Join-Path $incoming $name) -Destination $target
        $installed += $name
    }

    Write-Host "[3/3] Restarting $appName..."
    $previousGuiSetting = $env:DOTNET_DISABLE_GUI_ERRORS
    try {
        $env:DOTNET_DISABLE_GUI_ERRORS = '1'
        $probe = Start-Process -FilePath $exePath -WorkingDirectory $installDir -ArgumentList '--check-runtime --selftest lang' -Wait -PassThru
        if ($probe.ExitCode -ne 0) {
            throw "The updated launcher could not load its runtime (exit $($probe.ExitCode))."
        }
    }
    finally {
        $env:DOTNET_DISABLE_GUI_ERRORS = $previousGuiSetting
    }
    Start-Process -FilePath $exePath -WorkingDirectory $installDir
    # AutoUpdater clears its own stage on the next up-to-date check.

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    try {
        foreach ($name in $installed) {
            Move-Item -LiteralPath (Join-Path $installDir $name) -Destination $incoming
        }
        foreach ($name in $saved) {
            Move-Item -LiteralPath (Join-Path $backup $name) -Destination $installDir
        }
        if (Test-Path -LiteralPath $exePath) {
            Start-Process -FilePath $exePath -WorkingDirectory $installDir
        }
    }
    catch {
        $keepBackup = $true
        Write-Host "Recovery failed. Saved files: $backup. $($_.Exception.Message)" -ForegroundColor Red
    }
    Exit 1
}
finally {
    if (-not $keepBackup -and (Test-Path -LiteralPath $swap)) {
        Remove-Item -LiteralPath $swap -Recurse -Force -ErrorAction SilentlyContinue
    }
}
