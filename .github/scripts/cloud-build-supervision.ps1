function Stop-PlatformCloudBuilderTree {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process,[Parameter(Mandatory)][string]$TerminationLog)
    $Process.Refresh()
    if($Process.HasExited){return}
    # The builder owns private-build.log. Opening that file for redirection can
    # fail before taskkill is started, so termination has its own log.
    & "$env:WINDIR\System32\taskkill.exe" /PID $Process.Id /T /F *> $TerminationLog
    $buildStopExit=$LASTEXITCODE
    if(!$Process.WaitForExit(30000) -or $buildStopExit -ne 0){throw 'Cloud builder tree termination was not confirmed'}
}

function Read-PlatformCloudLog {
    param([string]$Path)
    if([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Leaf)){return ''}
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try {
        $count=[int][Math]::Min($stream.Length,8MB)
        if($stream.Length -gt $count){[void]$stream.Seek(-$count,[IO.SeekOrigin]::End)}
        $bytes=[byte[]]::new($count)
        $read=0
        while($read -lt $count){$next=$stream.Read($bytes,$read,$count-$read);if(!$next){break};$read+=$next}
        return [Text.Encoding]::UTF8.GetString($bytes,0,$read)
    } finally {$stream.Dispose()}
}
