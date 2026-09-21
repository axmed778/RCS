<#
.SYNOPSIS
    Creates one RCS recovery point on the pilot laptop: database + original documents + configuration + release identity.

.DESCRIPTION
    The employee can run this from the "RCS ehtiyat nüsxə" desktop shortcut; no arguments are needed.

    ORDER MATTERS, and it is why this is a script rather than a pg_dump line. The object store is append-only
    (ADR-043) and an object is published BEFORE the metadata that references it (DOCUMENT_MODEL.md 7.2), so
    dumping the database FIRST and copying the objects SECOND produces a recovery point where every object the
    dump names exists in the copy — the validity rule of ADR-021. The other order can produce a dump that names a
    document the copy does not contain.

    A database dump on its own is NOT a backup of RCS: the documents live on the filesystem.

    Previews are not copied: they are derived and are regenerated after a restore.
    This script never deletes anything. Removing old recovery points is a deliberate, separate decision.
#>
[CmdletBinding()]
param(
    [string] $DataPath = 'C:\ProgramData\RCS\Pilot',
    [string] $InstallPath = 'C:\Program Files\RCS',
    [string] $BackupRoot = '',
    [string] $PostgresBin = '',
    [string] $Database = 'rcs_pilot'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\RcsPilot.Common.ps1')

# Only the live pilot database is ever dumped, and only by that exact name.
Assert-SafeDatabaseName -Name $Database -Purpose Pilot | Out-Null
$DataPath = Assert-SafeDirectory -Path $DataPath
if (-not $BackupRoot) { $BackupRoot = Join-Path $DataPath 'backups' }
$BackupRoot = Assert-SafeDirectory -Path $BackupRoot

$objects = Join-Path $DataPath 'objects'
if (-not (Test-Path -LiteralPath $objects)) { throw "The document store '$objects' was not found." }

$pg = Get-PostgresTools -BinPath $PostgresBin
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd''T''HHmmss''Z''')
$target = Join-Path $BackupRoot $stamp
New-Item -ItemType Directory -Path $target -Force | Out-Null

Write-Host "RCS — ehtiyat nüsxə / backup" -ForegroundColor White
Write-Note "recovery point: $target"

# ---------------------------------------------------------------- 1. database first

Write-Step '1/4  Database'
Invoke-Pg -Exe $pg.PgDump -What 'dumping the pilot database' -Arguments @(
    '--host=127.0.0.1', '--port=5432', '--username=rcs_app', "--dbname=$Database",
    '--format=custom', '--compress=6', "--file=$(Join-Path $target 'database.dump')") | Out-Null
Write-Ok 'database.dump'

# ---------------------------------------------------------------- 2. originals second

Write-Step '2/4  Original documents'
$objectsTarget = Join-Path $target 'objects'
New-Item -ItemType Directory -Path $objectsTarget -Force | Out-Null
# /E subdirectories, /COPY:DAT data+attributes+timestamps, /R:1 /W:1 fail fast, /NFL /NDL quiet. No mirroring:
# robocopy /MIR would delete, and nothing here may ever delete.
$robocopy = Start-Process -FilePath 'robocopy.exe' -ArgumentList @($objects, $objectsTarget, '/E', '/COPY:DAT', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS') -Wait -PassThru -WindowStyle Hidden
if ($robocopy.ExitCode -ge 8) { throw "Copying the document objects failed (robocopy exit $($robocopy.ExitCode))." }
$copied = (Get-ChildItem -LiteralPath $objectsTarget -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Ok "$copied object file(s)"

# ---------------------------------------------------------------- 3. configuration, without secrets

Write-Step '3/4  Configuration needed for a restore (no secrets)'
$configTarget = Join-Path $target 'config'
New-Item -ItemType Directory -Path $configTarget -Force | Out-Null
foreach ($name in 'appsettings.json', 'appsettings.Pilot.json', 'launcher.json') {
    $source = Join-Path $InstallPath $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $configTarget -Force }
}
# pgpass.conf holds database passwords and is deliberately NOT copied into the recovery point.
Write-Ok 'appsettings and launcher settings'
Write-Note 'Database passwords are not in the backup: they live in the employee''s pgpass.conf only.'

# ---------------------------------------------------------------- 4. identity and manifest

Write-Step '4/4  Release identity and checksums'
@(
    "taken_at=$stamp"
    "database=$Database"
    "object_root=$objects"
    "computer=$env:COMPUTERNAME"
    "release=$(Get-ReleaseIdentity -InstallPath $InstallPath)"
    "mode=windows-single-laptop"
) | Set-Content -LiteralPath (Join-Path $target 'RECOVERY_POINT') -Encoding UTF8

$manifest = Join-Path $target 'objects.sha256'
Get-ChildItem -LiteralPath $objectsTarget -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($target.Length + 1))"
} | Set-Content -LiteralPath $manifest -Encoding ASCII
"$((Get-FileHash -LiteralPath (Join-Path $target 'database.dump') -Algorithm SHA256).Hash.ToLowerInvariant())  database.dump" |
    Add-Content -LiteralPath $manifest -Encoding ASCII
Write-Ok 'RECOVERY_POINT, objects.sha256'

Write-Host ''
Write-Host "Ehtiyat nüsxə hazırdır / backup complete:" -ForegroundColor Green
Write-Host "  $target" -ForegroundColor White
Write-Host ''
Write-Host '  A recovery point is only proven once restore-test.ps1 has passed against it.' -ForegroundColor Yellow
Write-Host '  Copy at least one recovery point onto separate media (USB disk) that this laptop cannot overwrite.' -ForegroundColor Yellow
