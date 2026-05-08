$ErrorActionPreference = 'Stop'
$ts = Get-Date -Format 'yyyy-MM-ddTHH-mm-ss'

Write-Host "CUTOVER_START_$ts"

# 1. Backup current Scheduled Task XML
schtasks /Query /TN ConnectorApi /XML > "C:\Connector\backup\ConnectorApi.old.xml.$ts"
Write-Host "TASK_BACKUP_OK -> C:\Connector\backup\ConnectorApi.old.xml.$ts"

# 2. Stop Task and any running uvicorn
schtasks /End /TN ConnectorApi 2>&1 | Out-Null
Start-Sleep -Seconds 2
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'python.exe' -and $_.CommandLine -like '*uvicorn app:app*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
Write-Host "OLD_PROCESSES_STOPPED"

# 3. Copy live DB and config to runtime\, also snapshot to backup\
Copy-Item 'C:\Connector\server\connector.db' 'C:\Connector\runtime\connector.db' -Force
Copy-Item 'C:\Connector\server\config.json'  'C:\Connector\runtime\config.json'  -Force
Copy-Item 'C:\Connector\server\connector.db' "C:\Connector\backup\connector.db.pre-restructure-$ts" -Force
Write-Host "DATA_COPIED"

# 4. Recreate Scheduled Task pointing to runtime_launch.ps1
schtasks /Delete /TN ConnectorApi /F | Out-Null
$tr = "powershell -NoProfile -ExecutionPolicy Bypass -File C:\Connector\src\connector\server\runtime_launch.ps1"
schtasks /Create /SC ONSTART /TN ConnectorApi /TR $tr /RU SYSTEM /RL HIGHEST /F | Out-Null
Write-Host "TASK_RECREATED"

# 5. Start
schtasks /Run /TN ConnectorApi | Out-Null
Start-Sleep -Seconds 5

# 6. Verify
$pyProc = Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'python.exe' -and $_.CommandLine -like '*uvicorn app:app*' } |
    Select-Object -First 1

if ($pyProc) {
    $cmdLine = $pyProc.CommandLine
    Write-Host "UVICORN_RUNNING pid=$($pyProc.ProcessId)"
    Write-Host "CMDLINE: $cmdLine"
} else {
    Write-Host "ERROR: uvicorn did NOT start. Check C:\Connector\runtime\logs\runner.log"
    Get-Content 'C:\Connector\runtime\logs\runner.log' -Tail 5 -ErrorAction SilentlyContinue
    Get-Content 'C:\Connector\runtime\logs\uvicorn.err.log' -Tail 20 -ErrorAction SilentlyContinue
    throw "uvicorn did not start after cutover"
}

# 7. Quick localhost smoke test (server-side)
try {
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 5
    Write-Host "HEALTH_OK: $($health | ConvertTo-Json -Compress)"
} catch {
    Write-Host "HEALTH_FAIL: $_"
    Get-Content 'C:\Connector\runtime\logs\uvicorn.err.log' -Tail 20 -ErrorAction SilentlyContinue
    throw
}

Write-Host "CUTOVER_END_$ts"
