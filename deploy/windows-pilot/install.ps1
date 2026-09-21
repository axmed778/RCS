<#
.SYNOPSIS
    Installs the RCS Windows single-laptop pilot: folders, database, accounts and a desktop icon.

.DESCRIPTION
    Run this once, as an administrator, on the employee's laptop, from the published pilot package:

        .\install.ps1 -PublishPath .\publish -PilotUser aad -PilotFullName "Ad Soyad"

    It creates the ProgramData folders, copies the release into Program Files, writes appsettings.Pilot.json with
    no secrets in it, creates the rcs_pilot database with the real migration runner, bootstraps the technical
    administrator and the employee's own account, records the department, and puts three shortcuts on the desktop.

    It never touches rcs_dev, never writes documents inside the release folder, and prints the two temporary
    passwords exactly once, for hand-over in person.

.NOTES
    Requires a local PostgreSQL 17+ (already installed) and its superuser password. The pilot package does not
    bundle a database engine.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishPath,
    [Parameter(Mandatory)][string] $PilotUser,
    [Parameter(Mandatory)][string] $PilotFullName,
    [string] $PilotJobTitle = '',
    [string] $DepartmentName = 'Şəhərsalma Şöbəsi',
    [string] $DepartmentType = 'MUNICIPAL_DEPARTMENT',
    [string] $AdminUser = 'tech.admin',
    [string] $AdminFullName = 'Texniki inzibatçı',
    [string] $InstallPath = 'C:\Program Files\RCS',
    [string] $DataPath = 'C:\ProgramData\RCS\Pilot',
    [string] $PostgresBin = '',
    [string] $PostgresSuperUser = 'postgres',
    [int] $Port = 5080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\RcsPilot.Common.ps1')

$Database = 'rcs_pilot'
Assert-SafeDatabaseName -Name $Database -Purpose Pilot | Out-Null
$DataPath = Assert-SafeDirectory -Path $DataPath
$InstallPath = Assert-SafeDirectory -Path $InstallPath -AllowProgramFiles
if (-not (Test-Path -LiteralPath $PublishPath)) { throw "Published files not found at '$PublishPath'." }
if (-not (Test-Path -LiteralPath (Join-Path $PublishPath 'Rcs.Web.exe'))) { throw "'$PublishPath' does not look like a published RCS release (Rcs.Web.exe is missing)." }

Write-Host "RCS — Windows single-laptop pilot installation" -ForegroundColor White
Write-Note "release:  $PublishPath"
Write-Note "program:  $InstallPath"
Write-Note "data:     $DataPath"

# ---------------------------------------------------------------- 1. folders

Write-Step '1/10  Creating the data folders (outside the release, outside the repository)'
$folders = 'objects', 'tmp-uploads', 'previews', 'preview-tmp', 'logs', 'backups', 'data-protection-keys'
foreach ($folder in $folders) {
    $path = Join-Path $DataPath $folder
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
Write-Ok "$DataPath (objects, tmp-uploads, previews, preview-tmp, logs, backups, data-protection-keys)"
Write-Note 'Real documents live here for the life of the laptop; the release folder can be replaced without touching them.'

# ---------------------------------------------------------------- 2. PostgreSQL

Write-Step '2/10  Checking the local PostgreSQL'
$pg = Get-PostgresTools -BinPath $PostgresBin
Write-Ok "tools: $($pg.Bin)"
& $pg.PgIsReady --host=127.0.0.1 --port=5432 --quiet
if ($LASTEXITCODE -ne 0) {
    throw "PostgreSQL is not answering on 127.0.0.1:5432. Start the PostgreSQL service and run this script again."
}
Write-Ok 'PostgreSQL is running on 127.0.0.1:5432'

$superPassword = Read-Host -Prompt "PostgreSQL '$PostgresSuperUser' password" -AsSecureString
$env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($superPassword))
try {
    Invoke-Pg -Exe $pg.Psql -What 'connecting as the PostgreSQL superuser' -Arguments @(
        '--host=127.0.0.1', "--username=$PostgresSuperUser", '--dbname=postgres', '--quiet', '--no-psqlrc',
        '--command=SELECT 1') | Out-Null
    Write-Ok 'superuser credentials accepted'

    # ------------------------------------------------------------ 3. roles and database

    Write-Step '3/10  Creating the roles and the empty pilot database'
    $appPassword = New-RcsPassword
    $migratePassword = New-RcsPassword

    $rolesSql = Join-Path $PSScriptRoot '..\..\database\roles\roles.sql' | Resolve-Path
    Invoke-Pg -Exe $pg.Psql -What 'creating the cluster roles' -Arguments @(
        '--host=127.0.0.1', "--username=$PostgresSuperUser", '--dbname=postgres', '--quiet', '--no-psqlrc',
        "--file=$rolesSql") | Out-Null

    # Passwords are set here and written only to the employee's own pgpass file, never into appsettings.
    foreach ($role in @(@{ Name = 'rcs_app'; Password = $appPassword }, @{ Name = 'rcs_migrate'; Password = $migratePassword })) {
        Invoke-Pg -Exe $pg.Psql -What "setting the $($role.Name) password" -Arguments @(
            '--host=127.0.0.1', "--username=$PostgresSuperUser", '--dbname=postgres', '--quiet', '--no-psqlrc',
            "--command=ALTER ROLE $($role.Name) WITH LOGIN PASSWORD '$($role.Password -replace "'", "''")'") | Out-Null
    }

    $exists = & $pg.Psql '--host=127.0.0.1' "--username=$PostgresSuperUser" '--dbname=postgres' '--tuples-only' '--no-align' '--no-psqlrc' `
        "--command=SELECT 1 FROM pg_database WHERE datname='$Database'"
    if ("$exists".Trim() -eq '1') {
        Write-Ok "$Database already exists — left exactly as it is"
        Write-Note 'An existing pilot database is never recreated: it may already hold real cases.'
    }
    else {
        $createSql = Join-Path $PSScriptRoot '..\..\database\pilot\create_pilot_database.sql' | Resolve-Path
        Invoke-Pg -Exe $pg.Psql -What 'creating the pilot database' -Arguments @(
            '--host=127.0.0.1', "--username=$PostgresSuperUser", '--dbname=postgres', '--quiet', '--no-psqlrc',
            "--file=$createSql") | Out-Null
        Write-Ok "$Database created (empty; no demonstration data)"
    }
}
finally {
    $env:PGPASSWORD = $null
}

# ---------------------------------------------------------------- 4. pgpass

Write-Step '4/10  Storing the database passwords in the employee''s own password file'
$pgpassDirectory = Join-Path $env:APPDATA 'postgresql'
New-Item -ItemType Directory -Path $pgpassDirectory -Force | Out-Null
$pgpass = Join-Path $pgpassDirectory 'pgpass.conf'
$entries = @("127.0.0.1:5432:${Database}:rcs_app:$appPassword", "127.0.0.1:5432:${Database}:rcs_migrate:$migratePassword")
$existing = if (Test-Path -LiteralPath $pgpass) { Get-Content -LiteralPath $pgpass } else { @() }
$kept = $existing | Where-Object { $_ -notmatch "^127\.0\.0\.1:5432:${Database}:" }
Set-Content -LiteralPath $pgpass -Value (@($kept) + $entries | Where-Object { $_ }) -Encoding ASCII
Write-Ok "$pgpass (readable only by this Windows account)"
Write-Note 'The application reads it automatically; no password is written into any configuration file.'

# ---------------------------------------------------------------- 5. files and configuration

Write-Step '5/10  Copying the release and writing the configuration'
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
Copy-Item -Path (Join-Path $PublishPath '*') -Destination $InstallPath -Recurse -Force
# The maintenance scripts travel with the release, so the backup shortcut and the restore test are on the laptop.
Copy-Item -Path $PSScriptRoot -Destination (Join-Path $InstallPath 'windows-pilot') -Recurse -Force
Write-Ok "release copied to $InstallPath"

$template = Join-Path $PSScriptRoot '..\config-templates\appsettings.WindowsPilot.example.json' | Resolve-Path
$settings = Get-Content -LiteralPath $template -Raw | ConvertFrom-Json
$settings.Urls = "http://127.0.0.1:$Port"
$settings.ConnectionStrings.Runtime = "Host=127.0.0.1;Port=5432;Database=$Database;Username=rcs_app"
$settings.ConnectionStrings.Migration = "Host=127.0.0.1;Port=5432;Database=$Database;Username=rcs_migrate"
$settings.Rcs.Storage.RootPath = Join-Path $DataPath 'objects'
$settings.Rcs.Storage.TempPath = Join-Path $DataPath 'tmp-uploads'
$settings.Rcs.Preview.StorageRoot = Join-Path $DataPath 'previews'
$settings.Rcs.Preview.TempRoot = Join-Path $DataPath 'preview-tmp'
$settings.Rcs.Hosting.BaseUrl = "http://127.0.0.1:$Port"
$settings.Rcs.Hosting.DataProtectionKeysPath = Join-Path $DataPath 'data-protection-keys'
$settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $InstallPath 'appsettings.Pilot.json') -Encoding UTF8
Write-Ok 'appsettings.Pilot.json written (no secrets)'

$launcherSettings = [ordered]@{
    applicationPath     = 'Rcs.Web.exe'
    url                 = "http://127.0.0.1:$Port"
    environmentName     = 'Pilot'
    logDirectory        = (Join-Path $DataPath 'logs')
    readyTimeoutSeconds = 90
    startPath           = '/login'
}
$launcherSettings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $InstallPath 'launcher.json') -Encoding UTF8
Write-Ok 'launcher.json written'

# ---------------------------------------------------------------- 6. schema

Write-Step '6/10  Applying the database schema with the real migration runner'
Push-Location $InstallPath
try {
    $env:DOTNET_ENVIRONMENT = 'Pilot'
    & (Join-Path $InstallPath 'Rcs.Web.exe') migrate
    if ($LASTEXITCODE -ne 0) { throw "Migration failed (exit $LASTEXITCODE)." }
    Write-Ok 'migrations applied'

    & (Join-Path $InstallPath 'Rcs.Web.exe') check-schema
    if ($LASTEXITCODE -ne 0) { throw "check-schema refused the database (exit $LASTEXITCODE)." }
    Write-Ok 'schema matches this release'

    # ------------------------------------------------------------ 7. accounts

    Write-Step '7/10  Creating the first accounts (PERMISSIONS.md 25.2)'
    $adminOutput = & (Join-Path $InstallPath 'Rcs.Web.exe') user bootstrap-admin "--username=$AdminUser" "--full-name=$AdminFullName"
    $adminOutput | ForEach-Object { Write-Note $_ }
    if ($LASTEXITCODE -ne 0) { throw "Creating the technical administrator failed (exit $LASTEXITCODE)." }

    $userArguments = @("--username=$PilotUser", "--full-name=$PilotFullName", "--by=$AdminUser")
    if ($PilotJobTitle) { $userArguments += "--job-title=$PilotJobTitle" }
    $userOutput = & (Join-Path $InstallPath 'Rcs.Web.exe') user create @userArguments
    $userOutput | ForEach-Object { Write-Note $_ }
    if ($LASTEXITCODE -ne 0) { throw "Creating the employee's account failed (exit $LASTEXITCODE)." }

    Write-Step '8/10  Recording the department and granting the pilot roles'
    & (Join-Path $InstallPath 'Rcs.Web.exe') user set-department "--name=$DepartmentName" "--type=$DepartmentType" "--by=$AdminUser" | ForEach-Object { Write-Note $_ }
    # The first Head grant is the documented installation exception; the Head grants the rest.
    & (Join-Path $InstallPath 'Rcs.Web.exe') user grant "--username=$PilotUser" '--role=HEAD' "--by=$AdminUser" | ForEach-Object { Write-Note $_ }
    & (Join-Path $InstallPath 'Rcs.Web.exe') user grant "--username=$PilotUser" '--role=CHIEF' "--by=$PilotUser" | ForEach-Object { Write-Note $_ }
    & (Join-Path $InstallPath 'Rcs.Web.exe') user grant "--username=$PilotUser" '--role=WORKER' "--by=$PilotUser" | ForEach-Object { Write-Note $_ }
    Write-Ok "$PilotUser holds WORKER + CHIEF + HEAD for the pilot"
}
finally {
    Pop-Location
    $env:DOTNET_ENVIRONMENT = $null
}

# ---------------------------------------------------------------- 9. shortcuts

Write-Step '9/10  Creating the desktop shortcuts'
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
if (-not $desktop) { $desktop = [Environment]::GetFolderPath('Desktop') }
$shell = New-Object -ComObject WScript.Shell

function New-Shortcut {
    param([string] $Name, [string] $Target, [string] $Arguments, [string] $Description)
    $link = $shell.CreateShortcut((Join-Path $desktop "$Name.lnk"))
    $link.TargetPath = $Target
    $link.Arguments = $Arguments
    $link.WorkingDirectory = Split-Path -Path $Target -Parent
    $link.Description = $Description
    $link.Save()
    Write-Ok "$Name"
}

New-Shortcut -Name 'RCS' -Target (Join-Path $InstallPath 'Rcs.Launcher.exe') -Arguments '' -Description 'RCS — iş və yazışma sistemi'
New-Shortcut -Name 'RCS-i dayandır' -Target (Join-Path $InstallPath 'Rcs.Launcher.exe') -Arguments '--stop' -Description 'RCS proqramını dayandırır'
New-Shortcut -Name 'RCS ehtiyat nüsxə' -Target 'powershell.exe' `
    -Arguments "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallPath 'windows-pilot\backup.ps1')`"" `
    -Description 'RCS məlumatlarının ehtiyat nüsxəsini yaradır'

# ---------------------------------------------------------------- 10. verify

Write-Step '10/10  Verifying that the application starts'
# Windows PowerShell 5.1 has no -Environment on Start-Process, so the child inherits these.
$env:DOTNET_ENVIRONMENT = 'Pilot'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
$process = Start-Process -FilePath (Join-Path $InstallPath 'Rcs.Web.exe') -PassThru -WindowStyle Hidden

$ready = $false
for ($attempt = 0; $attempt -lt 60 -and -not $ready; $attempt++) {
    Start-Sleep -Seconds 1
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health/ready" -UseBasicParsing -TimeoutSec 5
        $ready = $response.StatusCode -eq 200
    }
    catch { }
}
if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
$env:DOTNET_ENVIRONMENT = $null
$env:ASPNETCORE_URLS = $null

if (-not $ready) { throw "RCS did not report healthy on http://127.0.0.1:$Port/health/ready. Check $DataPath\logs." }
Write-Ok 'RCS started and reported healthy'

Write-Host ''
Write-Host 'Installation finished.' -ForegroundColor Green
Write-Host ''
Write-Host '  The employee double-clicks the RCS icon on the desktop.' -ForegroundColor White
Write-Host "  Address:            http://127.0.0.1:$Port" -ForegroundColor White
Write-Host "  Sign-in name:       $PilotUser" -ForegroundColor White
Write-Host '  Temporary password: printed above — hand it over in person; it must be changed at first sign-in.' -ForegroundColor White
Write-Host ''
Write-Host '  Documents:  ' -NoNewline; Write-Host (Join-Path $DataPath 'objects') -ForegroundColor White
Write-Host '  Backups:    ' -NoNewline; Write-Host (Join-Path $DataPath 'backups') -ForegroundColor White
Write-Host '  Logs:       ' -NoNewline; Write-Host (Join-Path $DataPath 'logs') -ForegroundColor White
Write-Host ''
Write-Host '  Run a backup today, then run restore-test.ps1 against it BEFORE any real document is entered.' -ForegroundColor Yellow
