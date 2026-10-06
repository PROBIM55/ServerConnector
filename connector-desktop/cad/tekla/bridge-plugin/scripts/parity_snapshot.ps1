# Phase F snapshot harness: вставляет canonical сценарий через
# bridge-desktop API, читает обратно состояние компонента, сохраняет
# JSON-snapshot для сравнения before/after.
#
# Workflow:
#   # 1. На прежнем плагине:
#   .\parity_snapshot.ps1 -Scenario 01_topflange_constant -Stage before
#   # 2. Подменить плагин через redeploy_plugin.ps1.
#   # 3. На новом плагине:
#   .\parity_snapshot.ps1 -Scenario 01_topflange_constant -Stage after
#   # 4. Diff:
#   .\parity_diff.ps1 -Scenario 01_topflange_constant
#
# Автоматически находит порт и токен:
#   - Порт через Get-CimInstance Win32_Process по командной строке
#     Platform.Bridge.Desktop.Tekla.exe (--port N).
#   - Токен через DPAPI расшифровку %LOCALAPPDATA%\Platform\Bridge\token.dat
#     (CurrentUser scope — работает только под тем же Windows-логином).
#   - Override через -BaseUrl и -BridgeToken.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][ValidateSet("before", "after")][string]$Stage,
    [string]$BaseUrl,
    [string]$BridgeToken,
    [string]$FixturesDir = (Join-Path $PSScriptRoot ".." | Join-Path -ChildPath "tests\fixtures\parity")
)

$ErrorActionPreference = "Stop"

# --- Port discovery -------------------------------------------------
if (-not $BaseUrl) {
    $proc = Get-CimInstance Win32_Process -Filter "Name='Platform.Bridge.Desktop.Tekla.exe'" `
        | Select-Object -First 1
    if (-not $proc) {
        throw "Platform.Bridge.Desktop.Tekla.exe не запущен. Запустите Platform Connector tray app или вручную через %LOCALAPPDATA%\Platform\Bridge\app\."
    }
    $match = [regex]::Match($proc.CommandLine, '--port\s+(\d+)')
    if (-not $match.Success) {
        throw "Не нашёл --port в командной строке Bridge.Desktop (PID $($proc.ProcessId)): $($proc.CommandLine)"
    }
    $port = [int]$match.Groups[1].Value
    $BaseUrl = "http://127.0.0.1:$port"
    Write-Host "[parity_snapshot] обнаружен Bridge.Desktop на $BaseUrl (PID $($proc.ProcessId))"
}

# --- Token discovery ------------------------------------------------
if (-not $BridgeToken) {
    $tokenPath = Join-Path $env:LOCALAPPDATA "Platform\Bridge\token.dat"
    if (-not (Test-Path $tokenPath)) {
        throw "token.dat не найден: $tokenPath"
    }
    Add-Type -AssemblyName System.Security
    $enc = [IO.File]::ReadAllBytes($tokenPath)
    $dec = [Security.Cryptography.ProtectedData]::Unprotect($enc, $null, 'CurrentUser')
    $BridgeToken = [Text.Encoding]::UTF8.GetString($dec)
}

# --- Health check ---------------------------------------------------
try {
    $health = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health" -TimeoutSec 5
    Write-Host "[parity_snapshot] /health ok: $($health | ConvertTo-Json -Compress)"
} catch {
    throw "Bridge.Desktop не отвечает на $BaseUrl/health: $($_.Exception.Message). Возможно процесс zombie — перезапустите Connector tray app."
}

# --- Load scenario --------------------------------------------------
$scenarioPath = Join-Path $FixturesDir "$Scenario.json"
if (-not (Test-Path $scenarioPath)) {
    $available = (Get-ChildItem $FixturesDir -Filter "*.json" | ForEach-Object { "  " + $_.BaseName }) -join "`n"
    throw "Сценарий не найден: $scenarioPath. Доступные:`n$available"
}

# Имя $scn НЕ $scenario — PowerShell case-insensitive, и param([string]$Scenario)
# имеет typed string. Присвоение PSCustomObject обратно перекастится в "@{...}".
$raw = [IO.File]::ReadAllText($scenarioPath, [Text.Encoding]::UTF8)
$scn = $raw | ConvertFrom-Json
Write-Host "[parity_snapshot] scenario: $($scn.id) — $($scn.description)"
Write-Host "[parity_snapshot] stage:    $Stage"

# --- Build request --------------------------------------------------
$externalObjectId = "parity-{0}-{1}-{2}" -f $scn.id, $Stage, (Get-Date -Format "yyyyMMddHHmmss")
$idempotencyKey = [Guid]::NewGuid().ToString()

$startParts = $scn.pluginData.tb_start -split ","
$endParts = $scn.pluginData.tb_end -split ","

# tb_* поля → Parameters{name}. Bridge.Desktop adapter использует schema-имена
# (без префикса tb_) для большинства полей, кроме двух special cases:
#   tb_mat  → material
#   tb_name → bridgeName
# Они задаются explicitly через TeklaAttribute в BridgeGirderSchemaV1.
$tbToSchemaName = @{
    "tb_mat" = "material"
    "tb_name" = "bridgeName"
}
$parameters = @{}
foreach ($prop in $scn.pluginData.PSObject.Properties) {
    # nb: this loop iterates 27 tb_* fields
    $key = if ($tbToSchemaName.ContainsKey($prop.Name)) { $tbToSchemaName[$prop.Name] }
           else { $prop.Name -replace "^tb_", "" }
    $parameters[$key] = $prop.Value
}

$request = @{
    operation = "upsert"
    provider = "tekla"
    componentType = "BridgeGirder"
    schemaVersion = 1
    externalObjectId = $externalObjectId
    idempotencyKey = $idempotencyKey
    placement = @{
        kind = "axis"
        start = @{ x = [double]$startParts[0]; y = [double]$startParts[1]; z = [double]$startParts[2] }
        end   = @{ x = [double]$endParts[0];   y = [double]$endParts[1];   z = [double]$endParts[2] }
    }
    parameters = $parameters
}

$body = $request | ConvertTo-Json -Depth 10

# Используем curl.exe вместо Invoke-RestMethod — последний по неизвестной
# причине теряет parameters{} dictionary при сериализации (даже с UTF-8
# bytes), а curl с тем же телом работает корректно.
function Invoke-BridgeApi {
    param([string]$Url, [string]$JsonBody)
    $tempFile = [IO.Path]::GetTempFileName()
    try {
        # UTF-8 без BOM — PS5 Set-Content -Encoding UTF8 ставит BOM, .NET JSON
        # parser сервера на BOM падает в pre-validation. Записываем через
        # System.IO.File с явной UTF8Encoding(false).
        $utf8NoBom = New-Object Text.UTF8Encoding $false
        [IO.File]::WriteAllText($tempFile, $JsonBody, $utf8NoBom)
        $raw = & curl.exe -sS -X POST $Url `
            -H "X-Bridge-Token: $BridgeToken" `
            -H "Content-Type: application/json" `
            -d "@$tempFile"
        if ($LASTEXITCODE -ne 0) { throw "curl failed: $raw" }
        return $raw | ConvertFrom-Json
    } finally { Remove-Item $tempFile -Force -ErrorAction SilentlyContinue }
}

Write-Host "[parity_snapshot] POST $BaseUrl/component/upsert ..."
$upsertResponse = Invoke-BridgeApi -Url "$BaseUrl/component/upsert" -JsonBody $body

if (-not $upsertResponse.ok) {
    throw "Upsert failed: $($upsertResponse.errorCode) — $($upsertResponse.message)"
}
$componentGuid = $upsertResponse.teklaComponentGuid
if (-not $componentGuid) {
    Write-Warning "Upsert ответ не содержит teklaComponentGuid. Полный ответ:`n$($upsertResponse | ConvertTo-Json -Depth 5)"
}
Write-Host "[parity_snapshot] inserted teklaComponentId=$($upsertResponse.teklaComponentId) GUID=$componentGuid"

# --- Read snapshot --------------------------------------------------
$readRequest = @{
    operation = "read"
    provider = "tekla"
    componentType = "BridgeGirder"
    schemaVersion = 1
    externalObjectId = $externalObjectId
    idempotencyKey = [Guid]::NewGuid().ToString()
    target = @{ teklaComponentGuid = $componentGuid }
    parameters = @{}
} | ConvertTo-Json -Depth 10

Write-Host "[parity_snapshot] POST $BaseUrl/component/read ..."
$readResponse = Invoke-BridgeApi -Url "$BaseUrl/component/read" -JsonBody $readRequest

$snapshotDir = Join-Path $FixturesDir $Stage
New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
$snapshotPath = Join-Path $snapshotDir "$($scn.id).json"
$readResponse | ConvertTo-Json -Depth 20 | Set-Content $snapshotPath -Encoding UTF8

Write-Host "[parity_snapshot] saved: $snapshotPath"
Write-Host "[parity_snapshot] next steps:"
Write-Host "  - повторите с другим -Stage (before|after) после swap'а плагина"
Write-Host "  - запустите parity_diff.ps1 -Scenario $($scn.id)"
