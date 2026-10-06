param([string]$ConnectorRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$canonicalRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ConnectorRoot).Path).TrimEnd('\')
$serverRoot = Split-Path -Parent $canonicalRoot
$accessRoot = Join-Path $serverRoot 'connector-access'
if (-not (Test-Path -LiteralPath $accessRoot -PathType Container)) { throw "Approved sibling root was not found: $accessRoot" }
$canonicalAccessRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $accessRoot).Path).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $canonicalAccessRoot 'src\Connector.Access.Client\Connector.Access.Client.csproj') -PathType Leaf)) { throw 'Approved Connector.Access.Client contract project was not found.' }
$allowedRoots = @($canonicalRoot, $canonicalAccessRoot)
function Is-AllowedSourcePath([string]$Path) {
    foreach ($allowed in $allowedRoots) {
        if ($Path -eq $allowed -or $Path.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
$excluded = @('bin','obj','.git','.tools','artifacts','publish','publish-package-update','node_modules','.venv','venv','__pycache__','target','dist')
$pending = [System.Collections.Generic.Stack[string]]::new()
foreach ($allowed in $allowedRoots) { $pending.Push($allowed) }
$projects = [System.Collections.Generic.List[string]]::new()
$sourceScripts = [System.Collections.Generic.List[string]]::new()
while ($pending.Count) {
    $directory = $pending.Pop()
    foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
        if ($child.PSIsContainer) { if ($child.Name -notin $excluded) { $pending.Push($child.FullName) }; continue }
        if ($child.Extension -eq '.csproj') { $projects.Add($child.FullName) }
        if ($child.Extension -in @('.cs','.ps1','.py') -and $child.FullName -ne $PSCommandPath) { $sourceScripts.Add($child.FullName) }
    }
}
$checked = 0
foreach ($project in $projects) {
    $projectDirectory = Split-Path -Parent $project
    [xml]$xml = [IO.File]::ReadAllText($project)
    foreach ($item in $xml.SelectNodes('//*[@Include]')) {
        if ($item.LocalName -notin @('ProjectReference','Compile','Content','EmbeddedResource')) { continue }
        $include = [string]$item.Include
        if ($include.Contains('$(')) {
            if ($item.LocalName -eq 'ProjectReference') { throw "Dynamic source dependency is not allowed: $project : $include" }
            continue
        }
        foreach ($entry in $include.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
            $wildcardIndex = $entry.IndexOfAny([char[]]'*?')
            $pathPart = if ($wildcardIndex -ge 0) { $entry.Substring(0,$wildcardIndex) } else { $entry }
            if (-not $pathPart) { $pathPart = '.' }
            $resolved = [IO.Path]::GetFullPath((Join-Path $projectDirectory $pathPart))
            if (-not (Is-AllowedSourcePath $resolved)) {
                throw "Source dependency escapes connector root: $project : $entry"
            }
            if ($wildcardIndex -lt 0 -and -not (Test-Path -LiteralPath $resolved)) { throw "Missing local source/content: $project : $entry" }
            $checked++
        }
    }
}
$legacyBindingPattern = 'PlatformSourceRoot|apps[\\/]server[\\/].*appsettings|appsettings\.Local\.json|["''][A-Za-z]:[\\/][^\r\n"'']*(13_Платформа|Structura_Main)'
function Remove-SourceComments([string]$SourceText, [string]$Extension) {
    if ($Extension -eq '.ps1') {
        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseInput($SourceText, [ref]$tokens, [ref]$parseErrors) | Out-Null
        $clean = [Text.StringBuilder]::new($SourceText)
        foreach ($token in $tokens) {
            if ($token.Kind -ne [System.Management.Automation.Language.TokenKind]::Comment) { continue }
            $length = $token.Extent.EndOffset - $token.Extent.StartOffset
            $clean.Remove($token.Extent.StartOffset, $length).Insert($token.Extent.StartOffset, (' ' * $length)) | Out-Null
        }
        return $clean.ToString()
    }
    if ($Extension -eq '.cs') {
        $tokenPattern = '(?s)(?<raw>"{3,}).*?\k<raw>|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|(?<comment>/\*.*?\*/|//[^\r\n]*)'
    } else {
        $tokenPattern = @'
(?s)(?<triple>"""|''').*?\k<triple>|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?<comment>#[^\r\n]*)
'@
    }
    return [regex]::Replace($SourceText, $tokenPattern, {
        param($match)
        if ($match.Groups['comment'].Success) { return ' ' * $match.Length }
        return $match.Value
    })
}
foreach ($source in $sourceScripts) {
    $code = Remove-SourceComments ([IO.File]::ReadAllText($source)) ([IO.Path]::GetExtension($source))
    if ($code -match $legacyBindingPattern) {
        throw "Legacy checkout/config binding is not allowed in connector sources: $source"
    }
}
"Source boundary check passed: $($projects.Count) projects, $checked local source/content references, $($sourceScripts.Count) source scripts checked for legacy checkout bindings."
