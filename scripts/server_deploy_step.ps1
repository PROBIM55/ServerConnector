# Connector server-side deploy step.
#
# Запускается:
#   - GitHub Actions (.github/workflows/deploy-connector-server.yml) через SSH;
#   - либо вручную с VPS (`opwork_admin`) при необходимости.
#
# Делает:
#   1. clean-tree preflight + git fetch + fast-forward к точному SHA в C:\Connector\src
#   2. pip install -r requirements.txt в runtime venv
#   3. backup активной БД (verified pg_dump для PostgreSQL либо SQLite copy)
#   4. python run_migrations.py (применяет pending миграции)
#   5. перезапускает Scheduled Task ConnectorApi
#   6. smoke test: GET /health должен ответить 200 и вернуть нужный SHA
#
# При любой ошибке выше шагов 4–6 — auto-rollback: возвращает src на
# предыдущий SHA и перезапускает Task. БД из бэкапа НЕ восстанавливается
# автоматически (большинство миграций forward-only ALTER ADD; для
# destructive — восстанавливать руками из C:\Connector\backup\).

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$CommitSha
)

$ErrorActionPreference = 'Stop'

$src        = 'C:\Connector\src'
$runtime    = 'C:\Connector\runtime'
$backup     = 'C:\Connector\backup'
$venvPython = Join-Path $runtime '.venv\Scripts\python.exe'
$serverDir  = Join-Path $src 'connector\server'
$reqsFile   = Join-Path $serverDir 'requirements.txt'

function Assert-CleanSource([string]$Path) {
    $status = @(& git -C $Path status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the server source checkout.' }
    if ($status.Count -ne 0) {
        throw 'Server source has local changes; deployment preserves them and stops.'
    }
}

function Move-SourceForward([string]$Path, [string]$Target) {
    Assert-CleanSource $Path
    & git -C $Path fetch origin master
    if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch the approved release branch.' }
    & git -C $Path merge-base --is-ancestor $Target origin/master
    if ($LASTEXITCODE -ne 0) { throw 'Target SHA is not contained in origin/master.' }
    & git -C $Path merge-base --is-ancestor HEAD $Target
    if ($LASTEXITCODE -ne 0) { throw 'Target SHA is not a fast-forward from the deployed source.' }
    Assert-CleanSource $Path
    & git -C $Path merge --ff-only --no-edit $Target
    if ($LASTEXITCODE -ne 0) { throw 'Fast-forward to the approved SHA failed.' }
}

function Restore-CleanSource([string]$Path, [string]$ExpectedCurrent, [string]$Previous) {
    Assert-CleanSource $Path
    $current = (& git -C $Path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $current -cne $ExpectedCurrent) {
        throw 'Source changed after deployment; automatic rollback cannot replace it.'
    }
    # A detached checkout preserves branch history and refuses conflicting changes.
    # A later successful deployment can fast-forward this clean checkout normally.
    & git -C $Path checkout --detach $Previous
    if ($LASTEXITCODE -ne 0) { throw 'Safe source rollback failed.' }
}

function Invoke-Step([string]$Name, [scriptblock]$Block) {
    Write-Host "==> $Name"

    # Native-command stderr merged via 2>&1 в PowerShell с $ErrorActionPreference='Stop'
    # триггерит выкидывание исключения даже при успешном exit code (git fetch пишет
    # progress в stderr). Поэтому внутри блока временно ослабляем EAP и опираемся
    # ТОЛЬКО на $LASTEXITCODE для определения провала native-команд.
    $oldEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Block
    }
    finally {
        $ErrorActionPreference = $oldEAP
    }

    if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {
        throw "Step '$Name' exited with code $LASTEXITCODE"
    }
}

function Import-RuntimeEnv([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "Runtime env file not found: $Path"
        return
    }

    Get-Content -LiteralPath $Path | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith('#')) {
            return
        }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) {
            return
        }
        $key = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        Set-Item -Path "Env:$key" -Value $value
    }
}

# Запомнить текущий SHA для rollback
Assert-CleanSource $src
$prevSha = (& git -C $src rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the current server source SHA.' }
$sourceAdvanced = $false
$CommitSha = $CommitSha.ToLowerInvariant()
Write-Host "PREV_SHA=$prevSha"
Write-Host "TARGET_SHA=$CommitSha"

try {
    Move-SourceForward $src $CommitSha
    $sourceAdvanced = $true

    $newSha = (& git -C $src rev-parse HEAD).Trim()
    Write-Host "NEW_SHA=$newSha"

    if ($CommitSha -and ($newSha -ne $CommitSha)) {
        throw "Pulled SHA $newSha doesn't match expected $CommitSha"
    }

    Invoke-Step 'pip install requirements' {
        & $venvPython -m pip install --quiet -r $reqsFile 2>&1 | Out-Host
    }

    if (-not (Test-Path -LiteralPath $backup)) {
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
    }
    Import-RuntimeEnv -Path (Join-Path $runtime '.env')

    $ts = Get-Date -Format 'yyyy-MM-ddTHH-mm-ss'
    if ($env:CONNECTOR_DB_URL -and
        ($env:CONNECTOR_DB_URL.StartsWith('postgresql://', [StringComparison]::OrdinalIgnoreCase) -or
         $env:CONNECTOR_DB_URL.StartsWith('postgres://', [StringComparison]::OrdinalIgnoreCase))) {
        $postgresBackupScript = Join-Path $src 'scripts\backup_connector_postgres.ps1'
        Invoke-Step 'backup PostgreSQL' {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass `
                -File $postgresBackupScript `
                -Reason "predeploy-$prevSha" 2>&1 | Out-Host
        }
    } else {
        $backupFile = Join-Path $backup "connector.db.$prevSha-$ts"
        Invoke-Step "backup SQLite -> $backupFile" {
            Copy-Item (Join-Path $runtime 'connector.db') $backupFile -Force
        }
    }

    Invoke-Step 'run migrations' {
        $env:CONNECTOR_DB_PATH = Join-Path $runtime 'connector.db'
        & $venvPython (Join-Path $serverDir 'run_migrations.py') 2>&1 | Out-Host
    }

    Invoke-Step 'restart ConnectorApi' {
        schtasks /End /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 2
        schtasks /Run /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 5
    }

    Invoke-Step 'smoke test /health' {
        $resp = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 10
        if (-not $resp.ok) { throw "Health returned ok=false" }
        $expected = $newSha.Substring(0, 8)
        if ($resp.version -ne $expected) {
            throw "Deployed version mismatch: got '$($resp.version)', expected '$expected'"
        }
        Write-Host "HEALTH_OK version=$($resp.version)"
    }

    Write-Host "DEPLOY_OK $newSha"
}
catch {
    $err = $_
    Write-Warning "DEPLOY_FAIL: $err"
    if (-not $sourceAdvanced) {
        Write-Warning 'Source was not advanced; running service is preserved.'
        throw $err
    }
    Write-Warning "Rolling back clean source to $prevSha"

    try {
        Restore-CleanSource $src $CommitSha $prevSha
        schtasks /End /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 2
        schtasks /Run /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 5
        $resp = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 10
        if ($resp.ok) { Write-Warning "ROLLBACK_OK to $prevSha" }
        else { Write-Warning "ROLLBACK_HEALTH_BAD" }
    }
    catch {
        Write-Warning "ROLLBACK_ITSELF_FAILED: $_"
    }

    throw $err
}
