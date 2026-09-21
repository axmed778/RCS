<#
.SYNOPSIS
    Proves a recovery point is real by rebuilding it in a throw-away database and checking the document bytes.

.DESCRIPTION
        .\restore-test.ps1 -RecoveryPoint C:\ProgramData\RCS\Pilot\backups\20260921T101500Z

    The live rcs_pilot database and the live document store are only ever READ. The restore target is always a new
    database named rcs_restore_test_*, and that name is checked before anything is created — a mistyped or hostile
    value cannot name the pilot database.

    A pass means:
      1. the dump restores into an empty database;
      2. the application's own check-schema accepts it;
      3. every document version in it has its object present, of the recorded size and SHA-256;
      4. the case, user and document data is readable.

    Previews are not checked: they are derived and are regenerated after a restore.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RecoveryPoint,
    [string] $InstallPath = 'C:\Program Files\RCS',
    [string] $PostgresBin = '',
    [string] $PostgresSuperUser = 'postgres',
    [switch] $KeepDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\RcsPilot.Common.ps1')

$dump = Join-Path $RecoveryPoint 'database.dump'
$objects = Join-Path $RecoveryPoint 'objects'
if (-not (Test-Path -LiteralPath $dump)) { throw "No database.dump in '$RecoveryPoint'." }
if (-not (Test-Path -LiteralPath $objects)) { throw "No objects\ in '$RecoveryPoint'." }

# The one line that keeps this script safe: the target is a throw-away name or the script stops here.
$testDatabase = Assert-SafeDatabaseName -Purpose Throwaway -Name ("rcs_restore_test_" + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))
$pg = Get-PostgresTools -BinPath $PostgresBin

Write-Host "RCS — restore test" -ForegroundColor White
Write-Note "recovery point: $RecoveryPoint"
Write-Note "throw-away database: $testDatabase (the pilot database is never written to)"

$superPassword = Read-Host -Prompt "PostgreSQL '$PostgresSuperUser' password" -AsSecureString
$env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($superPassword))
try {
    Write-Step '1/4  Restoring the dump into the throw-away database'
    Invoke-Pg -Exe $pg.CreateDb -What 'creating the throw-away database' -Arguments @(
        '--host=127.0.0.1', "--username=$PostgresSuperUser", '--owner=rcs_migrate', '--template=template0',
        '--encoding=UTF8', '--locale-provider=builtin', '--builtin-locale=C.UTF-8', $testDatabase) | Out-Null
    Invoke-Pg -Exe $pg.PgRestore -What 'restoring the dump' -Arguments @(
        '--host=127.0.0.1', "--username=$PostgresSuperUser", "--dbname=$testDatabase", '--no-owner',
        '--role=rcs_migrate', '--exit-on-error', $dump) | Out-Null
    Write-Ok 'dump restored'

    Write-Step '2/4  Schema compatibility'
    $env:ConnectionStrings__Runtime = "Host=127.0.0.1;Port=5432;Database=$testDatabase;Username=rcs_app"
    $env:DOTNET_ENVIRONMENT = 'Pilot'
    & (Join-Path $InstallPath 'Rcs.Web.exe') check-schema
    if ($LASTEXITCODE -ne 0) { throw "check-schema refused the restored database (exit $LASTEXITCODE)." }
    Write-Ok 'the restored schema is exactly the one this release expects'

    Write-Step '3/4  Document objects: existence, size and SHA-256'
    $env:Rcs__Storage__RootPath = $objects
    $env:Rcs__Storage__TempPath = Join-Path $env:TEMP "rcs-restore-test-$PID"
    $env:Rcs__Preview__Enabled = 'false'
    New-Item -ItemType Directory -Path $env:Rcs__Storage__TempPath -Force | Out-Null
    & (Join-Path $InstallPath 'Rcs.Web.exe') verify-documents --rehash
    if ($LASTEXITCODE -ne 0) { throw "verify-documents reported a finding (exit $LASTEXITCODE): the recovery point is NOT valid." }
    Write-Ok 'every stored document matches its recorded size and hash'

    Write-Step '4/4  The data is readable'
    $counts = & $pg.Psql '--host=127.0.0.1' "--username=$PostgresSuperUser" "--dbname=$testDatabase" '--tuples-only' '--no-align' '--no-psqlrc' `
        "--command=SELECT 'cases=' || (SELECT count(*) FROM rcs.case_record) || ' documents=' || (SELECT count(*) FROM rcs.document_version) || ' users=' || (SELECT count(*) FROM rcs.app_user);"
    Write-Ok "$counts"

    Write-Host ''
    Write-Host "RESTORE TEST PASSED for $RecoveryPoint" -ForegroundColor Green
}
finally {
    $env:ConnectionStrings__Runtime = $null
    $env:DOTNET_ENVIRONMENT = $null
    $env:Rcs__Storage__RootPath = $null
    $env:Rcs__Storage__TempPath = $null
    $env:Rcs__Preview__Enabled = $null

    if (-not $KeepDatabase) {
        # Only ever the throw-away name this script created, re-checked before dropping.
        Assert-SafeDatabaseName -Name $testDatabase -Purpose Throwaway | Out-Null
        & $pg.DropDb '--host=127.0.0.1' "--username=$PostgresSuperUser" '--if-exists' $testDatabase | Out-Null
        Write-Note "throw-away database $testDatabase removed"
    }
    else {
        Write-Note "throw-away database kept: $testDatabase (drop it with dropdb when finished)"
    }

    $env:PGPASSWORD = $null
}
