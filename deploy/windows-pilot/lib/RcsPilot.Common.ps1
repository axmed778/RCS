<#
    Shared helpers for the Windows single-laptop pilot scripts (install.ps1, backup.ps1, restore-test.ps1).

    The guards here exist because these scripts run on a laptop that holds real case documents, typed by whoever is
    installing RCS that day. Every destructive-sounding name is checked against an explicit rule rather than trusted:
    a restore may only ever create a throw-away database, and no script may write to a drive root, a Windows
    directory or the source repository.
#>

Set-StrictMode -Version Latest

# The live pilot database. Nothing in these scripts may drop, overwrite or restore into it.
$script:RcsPilotDatabase = 'rcs_pilot'

function Write-Step {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "    OK   $Message" -ForegroundColor Green
}

function Write-Note {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "         $Message" -ForegroundColor DarkGray
}

<#
    A PostgreSQL database name this script is allowed to touch.

    Purpose decides the rule:
      Pilot     - exactly the live pilot database, used for reading and dumping only.
      Throwaway - a restore target. It MUST be named rcs_restore_test_*, so a mistyped or malicious value can never
                  name the live database, the development database, or anything else on the cluster.
#>
function Assert-SafeDatabaseName {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Name,
        [Parameter(Mandatory)][ValidateSet('Pilot', 'Throwaway')][string] $Purpose
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "A database name is required."
    }

    if ($Name -notmatch '^[a-z][a-z0-9_]{2,62}$') {
        throw "'$Name' is not a valid database name (lower-case letters, digits and underscores only)."
    }

    switch ($Purpose) {
        'Pilot' {
            if ($Name -ne $script:RcsPilotDatabase) {
                throw "This script only works with the '$script:RcsPilotDatabase' database, not '$Name'."
            }
        }
        'Throwaway' {
            if ($Name -notmatch '^rcs_restore_test_[a-z0-9_]{1,40}$') {
                throw "A restore target must be named rcs_restore_test_*, never '$Name'. The live pilot database is never restored into."
            }
            if ($Name -eq $script:RcsPilotDatabase -or $Name -eq 'rcs_dev' -or $Name -eq 'postgres' -or $Name -eq 'template1') {
                throw "'$Name' is a real database and must never be used as a restore target."
            }
        }
    }

    return $Name
}

<#
    A directory these scripts may create or write into. Refuses drive roots, the Windows and Program Files trees
    (except the installation folder the operator explicitly chose), and anything inside a git working copy — real
    documents must never land in the source repository.
#>
function Assert-SafeDirectory {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Path,
        [switch] $AllowProgramFiles
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "A directory is required."
    }

    $full = [System.IO.Path]::GetFullPath($Path)

    if ($full -match '^[A-Za-z]:\\?$') {
        throw "Refusing to use a drive root ('$full')."
    }

    $forbidden = @(
        [Environment]::GetFolderPath('Windows'),
        [Environment]::GetFolderPath('System')
    ) | Where-Object { $_ }

    foreach ($bad in $forbidden) {
        if ($full.TrimEnd('\') -eq $bad.TrimEnd('\') -or $full.StartsWith($bad.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to use a system directory ('$full')."
        }
    }

    if (-not $AllowProgramFiles) {
        $programFiles = @([Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')) | Where-Object { $_ }
        foreach ($bad in $programFiles) {
            if ($full.StartsWith($bad.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to store pilot data under Program Files ('$full'). Data belongs in ProgramData."
            }
        }
    }

    # Real documents must not live in a source checkout: a git pull, a branch switch or a clean would touch them.
    $probe = $full
    while ($probe) {
        if (Test-Path -LiteralPath (Join-Path $probe '.git')) {
            throw "Refusing to use '$full': it is inside a source repository. Pilot data belongs under ProgramData."
        }
        $parent = Split-Path -Path $probe -Parent
        if (-not $parent -or $parent -eq $probe) { break }
        $probe = $parent
    }

    return $full
}

<#
    Finds the local PostgreSQL command-line tools. A laptop install usually puts them in
    C:\Program Files\PostgreSQL\<version>\bin, which is not on PATH.
#>
function Get-PostgresTools {
    param([string] $BinPath)

    $candidates = @()
    if ($BinPath) { $candidates += $BinPath }
    $onPath = (Get-Command psql -ErrorAction SilentlyContinue)
    if ($onPath) { $candidates += (Split-Path -Path $onPath.Source -Parent) }
    $candidates += Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'bin' }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'psql.exe'))) {
            return [pscustomobject]@{
                Bin        = $candidate
                Psql       = Join-Path $candidate 'psql.exe'
                PgDump     = Join-Path $candidate 'pg_dump.exe'
                PgRestore  = Join-Path $candidate 'pg_restore.exe'
                CreateDb   = Join-Path $candidate 'createdb.exe'
                DropDb     = Join-Path $candidate 'dropdb.exe'
                PgIsReady  = Join-Path $candidate 'pg_isready.exe'
            }
        }
    }

    throw "PostgreSQL command-line tools were not found. Install PostgreSQL 17+ locally, or pass -PostgresBin 'C:\Program Files\PostgreSQL\17\bin'."
}

<# Runs a PostgreSQL tool and throws with its output when it fails, so no step fails silently. #>
function Invoke-Pg {
    param(
        [Parameter(Mandatory)][string] $Exe,
        [Parameter(Mandatory)][string[]] $Arguments,
        [string] $What = 'PostgreSQL command'
    )

    $output = & $Exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit $LASTEXITCODE): $($output -join ' ')"
    }

    return $output
}

<# The version and commit of the installed release, for a recovery point and for bug reports. #>
function Get-ReleaseIdentity {
    param([Parameter(Mandatory)][string] $InstallPath)

    $web = Join-Path $InstallPath 'Rcs.Web.dll'
    if (-not (Test-Path -LiteralPath $web)) { return 'unknown' }
    $info = (Get-Item -LiteralPath $web).VersionInfo
    $version = $info.ProductVersion
    if (-not $version) { $version = $info.FileVersion }
    return $version
}

<#
    A random password for a database role, from the system CSPRNG. Used once at installation and written only to
    the employee's own pgpass file; nothing prints it and nothing else stores it.
#>
function New-RcsPassword {
    param([int] $Length = 28)

    $alphabet = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $bytes = New-Object byte[] $Length
    # Create().GetBytes() exists in both Windows PowerShell 5.1 and PowerShell 7; the static Fill() does not.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}
