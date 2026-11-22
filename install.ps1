#Requires -Version 5.1
<#
.SYNOPSIS
    One-click installer for JeekLauncher.

.DESCRIPTION
    irm https://raw.githubusercontent.com/tifish/JeekLauncher/main/install.ps1 | iex

    Downloads the latest release, installs it to %LOCALAPPDATA%\Programs\JeekLauncher,
    creates a Start Menu shortcut, enables start-with-Windows, and launches the app.

    Nothing is written to the uninstall registry: to remove JeekLauncher, close it and
    delete the install folder, the Start Menu shortcut, and its JeekLauncher-<user> scheduled task.

    The GitHub API is never called (api.github.com is rate-limited for anonymous users and
    unreachable from mainland China); the fixed releases/latest/download address is used
    instead, tried across the same mirrors JeekTools' GitHubMirrors uses.
#>

[CmdletBinding()]
param(
    # Skip enabling start-with-Windows.
    [switch] $NoAutoStart,
    # Install but do not launch afterwards.
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AppName = 'JeekLauncher'
$ExeName = "$AppName.exe"
$ReleaseUrl = "https://github.com/tifish/$AppName/releases/latest/download/$AppName.zip"
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$UserDataDirs = @('Config', 'Logs')

# Mirrors, in the same order as JeekTools' GitHubMirrors.GetMirrors.
function Get-Mirrors([string] $Url) {
    @(
        $Url
        $Url -replace '^https://github\.com/', 'https://ghfast.top/https://github.com/'
        $Url -replace '^https://github\.com/', 'https://gh-proxy.com/github.com/'
    )
}

# Abandon a mirror that stays below this for a full window, matching AutoUpdater's policy.
$MinBytesPerSecond = 512KB
$SlowWindowSeconds = 10
$IdleTimeoutSeconds = 30

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

<#
    Downloads $Url to $Destination, giving up on a mirror that stalls or stays slow so the
    caller can try the next one. Returns $true on success.
#>
function Invoke-MirrorDownload([string] $Url, [string] $Destination, [bool] $IsLastMirror) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.UserAgent = "$AppName-Installer/1.0"
    $request.Timeout = 15000
    $request.ReadWriteTimeout = ($IdleTimeoutSeconds * 1000)

    $response = $null
    $sourceStream = $null
    $output = $null
    try {
        $response = $request.GetResponse()
        $total = $response.ContentLength
        $sourceStream = $response.GetResponseStream()
        $output = [System.IO.File]::Create($Destination)

        $buffer = New-Object byte[] 81920
        $received = 0L
        $started = [Diagnostics.Stopwatch]::StartNew()
        $windowStart = $started.Elapsed
        $windowBytes = 0L
        $lastReport = -1

        while ($true) {
            $read = $sourceStream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }

            $output.Write($buffer, 0, $read)
            $received += $read
            $windowBytes += $read

            if ($total -gt 0) {
                $percent = [int](($received * 100) / $total)
                if ($percent -ne $lastReport -and $percent % 5 -eq 0) {
                    Write-Host "    $percent%  ($([int]($received / 1MB)) / $([int]($total / 1MB)) MB)"
                    $lastReport = $percent
                }
            }

            # The last mirror has nowhere to fall back to, so let it run at any speed.
            if ($IsLastMirror) { continue }

            $windowSeconds = ($started.Elapsed - $windowStart).TotalSeconds
            if ($windowSeconds -ge $SlowWindowSeconds) {
                $speed = $windowBytes / $windowSeconds
                if ($speed -lt $MinBytesPerSecond) {
                    Write-Host ("    too slow ({0:N0} KB/s), trying the next mirror" -f ($speed / 1KB)) -ForegroundColor Yellow
                    return $false
                }
                $windowStart = $started.Elapsed
                $windowBytes = 0L
            }
        }

        return ($received -gt 0)
    }
    catch {
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
    finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $sourceStream) { $sourceStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
    }
}

function Get-ReleaseZip([string] $Destination) {
    $mirrors = Get-Mirrors $ReleaseUrl
    for ($i = 0; $i -lt $mirrors.Count; $i++) {
        $mirror = $mirrors[$i]
        Write-Host "    $mirror"
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        if (Invoke-MirrorDownload $mirror $Destination ($i -eq $mirrors.Count - 1)) {
            return
        }
    }

    throw "Could not download $AppName from any mirror."
}

<#
    Stops only instances running from the install directory, so a developer build running
    from a source tree is never killed.
#>
function Stop-InstalledInstance {
    $installedExe = Join-Path $InstallDir $ExeName
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -and ($_.Path -ieq $installedExe)) {
                Write-Host "    stopping pid $($_.Id)"
                $_.CloseMainWindow() | Out-Null
                if (-not $_.WaitForExit(5000)) {
                    $_.Kill()
                    $_.WaitForExit(5000) | Out-Null
                }
            }
        }
        catch {
            # Access denied or already gone; the file replace below will report a real problem.
        }
    }
}

function Test-DotnetRuntime {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $false }

    try {
        $runtimes = & dotnet --list-runtimes 2>$null
    }
    catch {
        return $false
    }

    # The app targets .NET 10 and Avalonia needs only the base runtime.
    return [bool]($runtimes | Select-String -SimpleMatch 'Microsoft.NETCore.App 10.')
}

function New-StartMenuShortcut {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $shortcut = Join-Path $startMenu "$AppName.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $InstallDir $ExeName
    $link.WorkingDirectory = $InstallDir
    $link.Description = 'Launch apps by typing their initials'
    $link.Save()
    return $shortcut
}

function Enable-AutoStart {
    # Same per-user logon task the app's own "Start with Windows" checkbox writes (see
    # AutoStart.cs), so the two never disagree. A logon task starts right away, unlike the
    # HKCU Run key whose entries Explorer runs one at a time. No admin rights needed: the
    # trigger is limited to this user. The security descriptor keeps the task editable by
    # the app even if this script ran elevated; priority 4 is normal (the default 7 is not).
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $exe = [System.Security.SecurityElement]::Escape((Join-Path $InstallDir $ExeName))
    $dir = [System.Security.SecurityElement]::Escape($InstallDir)
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts $AppName at logon.</Description>
    <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;$sid)</SecurityDescriptor>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$sid</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$sid</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exe</Command>
      <WorkingDirectory>$dir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
    Register-ScheduledTask -TaskName "$AppName-$env:USERNAME" -Xml $xml -Force | Out-Null

    # Drop the Run value older versions used, or the app would be started twice.
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $AppName -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "  $AppName installer" -ForegroundColor Green
Write-Host "  target: $InstallDir"
Write-Host ""

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "$AppName-install-$PID"
$zipPath = Join-Path $stage "$AppName.zip"
$unpackDir = Join-Path $stage 'package'

try {
    New-Item -ItemType Directory -Force -Path $unpackDir | Out-Null

    Write-Step 'Downloading the latest release'
    Get-ReleaseZip $zipPath

    Write-Step 'Extracting'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $unpackDir -Force
    if (-not (Test-Path -LiteralPath (Join-Path $unpackDir $ExeName))) {
        throw "The downloaded package does not contain $ExeName."
    }

    Write-Step 'Stopping any installed instance'
    Stop-InstalledInstance

    Write-Step 'Installing files'
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    # /MIR clears files left by an older version. User data is excluded so settings and
    # logs survive, matching AutoUpdate.ps1's preserve list.
    $excludes = $UserDataDirs | ForEach-Object { Join-Path $InstallDir $_ }
    robocopy $unpackDir $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 /XD @excludes | Out-Null
    # robocopy exit codes below 8 are success (files copied / extra files removed).
    if ($LASTEXITCODE -ge 8) {
        throw "Copying files failed (robocopy exit code $LASTEXITCODE)."
    }

    Write-Step 'Creating the Start Menu shortcut'
    $shortcut = New-StartMenuShortcut
    Write-Host "    $shortcut"

    if (-not $NoAutoStart) {
        Write-Step 'Enabling start with Windows'
        Enable-AutoStart
    }

    $exePath = Join-Path $InstallDir $ExeName
    if (-not (Test-DotnetRuntime)) {
        Write-Step '.NET 10 runtime not found — running Setup.cmd'
        # Setup.cmd installs the runtime (elevating if needed) and then starts the app.
        Start-Process -FilePath (Join-Path $InstallDir 'Setup.cmd') -WorkingDirectory $InstallDir
    }
    elseif (-not $NoLaunch) {
        Write-Step "Starting $AppName"
        Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
    }

    Write-Host ""
    Write-Host "  $AppName is installed." -ForegroundColor Green
    Write-Host "  Press Alt+Space to open the launcher; it lives in the system tray."
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $stage -ErrorAction SilentlyContinue
}
