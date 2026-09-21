<#
.SYNOPSIS
    Fallback launcher: does what Rcs.Launcher.exe does, using PowerShell instead of an unsigned executable.

.DESCRIPTION
    Some Windows laptops run Smart App Control or an Application Control (WDAC/AppLocker) policy that blocks
    unsigned executables. On such a machine the desktop icon simply does nothing — which is exactly the failure an
    employee cannot diagnose. This script is the documented way around it: PowerShell itself is signed by Microsoft
    and allowed, and it starts the same application in the same way.

    Point the desktop shortcut at:
        powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\Program Files\RCS\windows-pilot\start-rcs.ps1"

    The proper fix is to sign Rcs.Launcher.exe (and Rcs.Web.exe) with the organisation's code-signing certificate,
    or to have IT allow them. This script keeps the pilot moving until that happens.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = 'C:\Program Files\RCS',
    [string] $DataPath = 'C:\ProgramData\RCS\Pilot',
    [int] $Port = 5080,
    [int] $ReadyTimeoutSeconds = 90,
    [switch] $Stop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$url = "http://127.0.0.1:$Port"
$logDirectory = Join-Path $DataPath 'logs'
$pidFile = Join-Path $logDirectory 'rcs-web.pid'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Write-LauncherLog {
    param([string] $Message)
    $line = "{0} {1}" -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Message
    Add-Content -LiteralPath (Join-Path $logDirectory ("launcher-{0}.log" -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd'))) -Value $line
}

function Show-Problem {
    param([string] $Title, [string] $Message)
    Write-LauncherLog "problem: $Title"
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    if ('System.Windows.Forms.MessageBox' -as [type]) {
        [System.Windows.Forms.MessageBox]::Show($Message, "RCS — $Title", 'OK', 'Error') | Out-Null
    }
    else {
        Write-Host "$Title`n$Message"
    }
}

function Get-RunningApplication {
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
    $recorded = Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $recorded) { return $null }
    return Get-Process -Id ([int] $recorded) -ErrorAction SilentlyContinue
}

if ($Stop) {
    $running = Get-RunningApplication
    if ($running) { Stop-Process -Id $running.Id -Force; Write-LauncherLog 'stopped on request' }
    return
}

# Already running: open the browser again instead of starting a second copy.
if (Get-RunningApplication) {
    Write-LauncherLog 'already running; opening the browser'
    Start-Process "$url/login"
    return
}

$application = Join-Path $InstallPath 'Rcs.Web.exe'
if (-not (Test-Path -LiteralPath $application)) {
    Show-Problem -Title 'RCS tapılmadı' -Message "RCS proqramı tapılmadı:`n$application`n`nTexniki inzibatçıya müraciət edin."
    exit 2
}

$env:DOTNET_ENVIRONMENT = 'Pilot'
$env:ASPNETCORE_URLS = $url
$process = Start-Process -FilePath $application -WorkingDirectory $InstallPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $pidFile -Value $process.Id
Write-LauncherLog "application started (pid $($process.Id)); waiting for $url/health/ready"

$ready = $false
for ($waited = 0; $waited -lt $ReadyTimeoutSeconds -and -not $ready; $waited++) {
    if ($process.HasExited) {
        Show-Problem -Title 'RCS işə düşmədi' -Message "RCS başladı, lakin dayandı.`n`nƏn çox rast gəlinən səbəb: verilənlər bazası xidməti işləmir.`n`nQeydlər: $logDirectory"
        exit 3
    }

    Start-Sleep -Seconds 1
    try {
        $ready = (Invoke-WebRequest -Uri "$url/health/ready" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200
    }
    catch { }
}

if (-not $ready) {
    Show-Problem -Title 'RCS cavab vermir' -Message "RCS gözlənilən vaxtda işə hazır olmadı.`n`nQeydlər: $logDirectory`n`nTexniki inzibatçıya bu mesajı göstərin."
    exit 4
}

Write-LauncherLog 'healthy; opening the browser'
Start-Process "$url/login"
