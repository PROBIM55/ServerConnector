function Get-ConnectorLocalBuildPolicy {
    param([ulong]$AvailablePhysical, [ulong]$AvailableCommit)
    # The smaller aggregate cap is specific to the sequential Connector build.
    # The workstation reserve and the shared controller's early stop are retained.
    $budget = [ulong](2GB)
    $reserve = [ulong](6GB)
    $margin = [ulong](512MB)
    $minimumFree = $budget + $reserve + $margin
    $minimumCommit = $budget + [ulong](4GB)
    [pscustomobject]@{
        JobMemoryBytes = $budget
        ReserveBytes = $reserve
        EarlyStopFreeBytes = $reserve + $margin
        MinimumFreeBytes = $minimumFree
        MinimumCommitBytes = $minimumCommit
        CpuPercent = 25
        WaitSeconds = 0
        CanStart = $AvailablePhysical -ge $minimumFree -and $AvailableCommit -ge $minimumCommit
    }
}
