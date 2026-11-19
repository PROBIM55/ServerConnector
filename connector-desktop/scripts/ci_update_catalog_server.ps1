#Requires -Version 7.4
# Disposable update catalog for the cloud update check only: serves <Root>/<channel>/connector-release.json(.sig) and
# files/<name> under the production URL layout, with Range/If-Range/ETag and, once, the catalog's
# 503 connector_download_busy answer. Runs only on a disposable hosted VM whose hosts file points bimplatforma.ru at
# 127.0.0.1 and whose machine root store alone trusts a root created and destroyed there.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root, [switch]$BusyOnce)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable hosted VM only.' }
$prefix = 'https://bimplatforma.ru:443/api/platform/connectors/unified/'
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
$listener.Start()
$busyAnswered = -not $BusyOnce
$log = Join-Path $rootFull 'requests.log'
while ($listener.IsListening) {
    $context = $listener.GetContext()
    $request = $context.Request; $response = $context.Response
    try {
        $relative = $request.Url.AbsolutePath.Substring('/api/platform/connectors/unified/'.Length)
        if ($relative -cnotmatch '^ci-[0-9]+/(connector-release\.json(\.sig)?|files/PlatformConnector-\d+\.\d+\.\d+\.exe)$') { $response.StatusCode = 404; continue }
        $path = [IO.Path]::GetFullPath((Join-Path $rootFull $relative.Replace('/','\')))
        if (!$path.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf)) { $response.StatusCode = 404; continue }
        $response.Headers['Cache-Control'] = 'no-store'
        if ($relative -notmatch '/files/') {
            $bytes = [IO.File]::ReadAllBytes($path)
            $response.ContentLength64 = $bytes.Length
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
            continue
        }
        if (!$busyAnswered) {
            $busyAnswered = $true
            $body = [Text.Encoding]::UTF8.GetBytes('{"ok":false,"error":"connector_download_busy"}')
            $response.StatusCode = 503; $response.Headers['Retry-After'] = '30'; $response.ContentType = 'application/json'
            $response.ContentLength64 = $body.Length; $response.OutputStream.Write($body, 0, $body.Length)
            continue
        }
        $file = [IO.File]::OpenRead($path)
        try {
            $length = $file.Length
            $etag = '"' + (Split-Path $path -Leaf) + '-' + $length + '"'
            $response.Headers['ETag'] = $etag
            $response.Headers['Accept-Ranges'] = 'bytes'
            $from = 0L
            $range = $request.Headers['Range']
            $ifRange = $request.Headers['If-Range']
            if ($range -match '^bytes=(\d+)-$' -and (!$ifRange -or $ifRange -ceq $etag)) {
                $from = [long]$Matches[1]
                if ($from -ge $length) { $response.StatusCode = 416; continue }
                $response.StatusCode = 206
                $response.Headers['Content-Range'] = "bytes $from-$($length - 1)/$length"
            }
            $response.ContentLength64 = $length - $from
            $file.Position = $from
            $file.CopyTo($response.OutputStream)
        } finally { $file.Dispose() }
    } catch {
        try { $response.StatusCode = 500 } catch { }
    } finally {
        Add-Content -LiteralPath $log -Value ("{0:o} {1} {2} -> {3}" -f [DateTimeOffset]::UtcNow, $request.HttpMethod, $request.Url.AbsolutePath, $response.StatusCode)
        try { $response.Close() } catch { }
    }
}
