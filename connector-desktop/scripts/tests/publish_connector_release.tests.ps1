#Requires -Version 7.4
# Network-free checks of publish-connector-release.yml: description bytes, the client's rules, channel rules, version
# immutability, notes, candidate artifact and the shared signing implementation. Every key is created here.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\connector_release_description.ps1')))

$root = Join-Path ([IO.Path]::GetTempPath()) ('connector-release-test-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$otherKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$publicPath = Join-Path $root 'test-public.pem'
[IO.File]::WriteAllText($publicPath, $key.ExportSubjectPublicKeyInfoPem())
$publicKey = Import-PinnedReleaseKey $publicPath
$keyVariable = 'P7_TEST_SIGN_KEY_PEM'
$passed = [Collections.Generic.List[string]]::new()

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "ASSERT: $Message" } }
function Expect-Code([scriptblock]$Action, [string]$Code, [string]$Label) {
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert ($null -ne $message) "$Label must fail"
    Assert ($message.StartsWith("${Code}:", [StringComparison]::Ordinal)) "$Label must fail with $Code, got: $message"
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern, [string]$Label) {
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    Assert ($null -ne $message -and $message -match $Pattern) "$Label must fail ($Pattern), got: $message"
}
function Pass([string]$Name) { $script:passed.Add($Name); Write-Output "ok  $Name" }
function At([string]$Text) { [DateTimeOffset]::Parse($Text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal) }
function New-Request([string]$Channel, [string]$Version, [string]$Paused = 'false', [string]$Notes = 'Ускорено подключение папок|Исправлен вход', [string]$Minimum = '1.1.15', [string]$Run = '0') {
    ConvertTo-ConnectorReleaseRequest -Channel $Channel -Version $Version -SourceRunId $Run -Paused $Paused -MinimumVersion $Minimum -Notes $Notes
}
function New-Installer([string]$Version, [string]$Payload) {
    $path = Join-Path $script:root ("installer-$Version-" + [guid]::NewGuid().ToString('N') + '.bin')
    [IO.File]::WriteAllBytes($path, [Text.Encoding]::UTF8.GetBytes($Payload))
    [pscustomobject]@{ FileName = "PlatformConnector-$Version.exe"; Size = [long](Get-Item $path).Length; Sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Sign([byte[]]$Bytes, [Security.Cryptography.ECDsa]$With = $script:key) {
    [Environment]::SetEnvironmentVariable($script:keyVariable, $With.ExportPkcs8PrivateKeyPem())
    $signatures = Invoke-PinnedReleaseSignature -Documents @(, $Bytes) -PublicKeyPemPath $script:publicPath -KeyVariable $script:keyVariable
    Assert ($null -eq [Environment]::GetEnvironmentVariable($script:keyVariable)) 'signing must clear the key variable'
    return , $signatures[0]
}
function Text([byte[]]$Bytes) { [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) }
function Bytes([string]$Text) { [Text.UTF8Encoding]::new($false).GetBytes($Text) }
function Remove-TestRoot([string]$Path) {
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '^connector-release-test-[0-9a-f]{32}$') { throw 'Refusing test cleanup outside its unique temp root.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

try {
    $installer16 = New-Installer '1.1.16' 'installer 1.1.16'
    $installer16Other = New-Installer '1.1.16' 'another build of 1.1.16'
    $installer15 = New-Installer '1.1.15' 'installer 1.1.15'
    $installer17 = New-Installer '1.1.17' 'installer 1.1.17'

    # --- notes and inputs ---
    $notes = ConvertFrom-ReleaseNotes '  Первая строка | Вторая строка  '
    Assert ($notes.Length -eq 2 -and $notes[0] -ceq 'Первая строка' -and $notes[1] -ceq 'Вторая строка') 'notes are split on | and trimmed'
    $eight = (1..8 | ForEach-Object { 'Ж' * 200 }) -join '|'
    Assert ((ConvertFrom-ReleaseNotes $eight).Length -eq 8) '8 lines of 200 characters are accepted'
    Expect-Code { ConvertFrom-ReleaseNotes ((1..9 | ForEach-Object { 'строка' }) -join '|') } 'NOTES_INVALID' '9 lines'
    Expect-Code { ConvertFrom-ReleaseNotes ('Ж' * 201) } 'NOTES_INVALID' '201 characters'
    Expect-Code { ConvertFrom-ReleaseNotes 'a||b' } 'NOTES_INVALID' 'empty line'
    Expect-Code { ConvertFrom-ReleaseNotes '   ' } 'NOTES_INVALID' 'no line'
    Expect-Code { ConvertFrom-ReleaseNotes "звонок`a" } 'NOTES_INVALID' 'control character'
    Expect-Code { ConvertFrom-ReleaseNotes "a`nb" } 'NOTES_INVALID' 'line feed inside a line'
    Expect-Code { ConvertFrom-ReleaseNotes "подмена$([char]0x202E)txt" } 'NOTES_INVALID' 'bidirectional override'
    Expect-Code { ConvertFrom-ReleaseNotes "обрыв$([char]0xD800)" } 'NOTES_INVALID' 'unpaired surrogate'
    Pass 'notes: 1..8 lines of 1..200 characters, no control/format characters'
    Expect-Code { New-Request 'Stable' '1.1.16' } 'CHANNEL_INVALID' 'channel case'
    Expect-Code { New-Request 'ci-1' '1.1.16' } 'CHANNEL_INVALID' 'foreign channel'
    Expect-Code { New-Request 'pilot' '1.01.16' } 'VERSION_INVALID' 'leading zero'
    Expect-Code { New-Request 'pilot' '1.1.16-beta' } 'VERSION_INVALID' 'suffix'
    Expect-Code { New-Request 'pilot' '1.1.65536' } 'VERSION_INVALID' 'part above 65535'
    Expect-Code { New-Request 'pilot' '1.1.16' 'True' } 'PAUSED_INVALID' 'paused spelling'
    Expect-Code { New-Request 'pilot' '1.1.16' -Minimum '1.1.17' } 'MINIMUM_VERSION_INVALID' 'minimum above version'
    Expect-Code { New-Request 'pilot' '1.1.16' -Minimum '' } 'MINIMUM_VERSION_INVALID' 'empty minimum'
    Expect-Code { New-Request 'pilot' '1.1.16' -Run '01' } 'SOURCE_RUN_INVALID' 'run id spelling'
    Pass 'inputs: channel, version, paused, minimum_version, source_run_id are strict'

    # --- first publication in pilot ---
    $t0 = At '2026-11-25T10:00:00Z'
    $pilot = New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Run '123') -Installer $installer16 -Now $t0.AddMilliseconds(730) -CurrentChannel $null -VersionDescriptions @() -InstallerAsset $null
    $expectedText = '{"schemaVersion":2,"applicationId":"Structura.PlatformConnector","channel":"pilot","version":"1.1.16","publishedAtUtc":"2026-11-25T10:00:00Z","paused":false,"minimumVersion":"1.1.15","installer":{"fileName":"PlatformConnector-1.1.16.exe","size":' +
        $installer16.Size + ',"sha256":"' + $installer16.Sha256 + '"},"notes":["\u0423\u0441\u043A\u043E\u0440\u0435\u043D\u043E \u043F\u043E\u0434\u043A\u043B\u044E\u0447\u0435\u043D\u0438\u0435 \u043F\u0430\u043F\u043E\u043A","\u0418\u0441\u043F\u0440\u0430\u0432\u043B\u0435\u043D \u0432\u0445\u043E\u0434"]}'
    Assert ((Text $pilot.Bytes) -ceq $expectedText) "exact description bytes, got: $(Text $pilot.Bytes)"
    Assert ($pilot.Description.Notes[0] -ceq 'Ускорено подключение папок') 'notes survive the escaping'
    $pilotSignature = Sign $pilot.Bytes
    Assert ($pilotSignature.Length -eq 64 -and (Test-ReleaseSignature -Bytes $pilot.Bytes -Signature $pilotSignature -PublicKey $publicKey)) 'pilot description signed and verified'
    Pass 'first publication in pilot: exact bytes, second precision, signed and verified'

    # --- the produced bytes satisfy every rule of ReleaseDescriptionVerifier.Verify ---
    $rules = [Collections.Generic.List[string]]::new()
    $b = $pilot.Bytes
    Assert ($b.Length -gt 0 -and $b.Length -le 65536) 'size'; $rules.Add('1..65536 bytes (DescriptionTooLarge)')
    Assert (-not ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) 'bom'; [void](Text $b); $rules.Add('UTF-8 without BOM, strictly decodable')
    Assert ($pilotSignature.Length -eq 64 -and $publicKey.VerifyData($b, $pilotSignature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) 'p1363'
    $rules.Add('signature: 64 bytes, ECDSA P-256 / SHA-256, IEEE P1363, over the exact bytes (SignatureInvalid)')
    $options = [System.Text.Json.JsonDocumentOptions]@{ AllowTrailingCommas = $false; CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow; MaxDepth = 4 }
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($b), $options)
    try {
        $r = $document.RootElement; $rules.Add('strict JSON: no comments, no trailing commas, depth <= 4')
        Assert ((@($r.EnumerateObject() | ForEach-Object Name) -join ',') -ceq 'schemaVersion,applicationId,channel,version,publishedAtUtc,paused,minimumVersion,installer,notes') 'root fields'
        $rules.Add('root: exactly the 9 fields of §3.5, in order, none unknown or repeated')
        Assert ($r.GetProperty('schemaVersion').GetRawText() -ceq '2') 'schema'; $rules.Add('schemaVersion = 2 spelled as plain digits')
        Assert ($r.GetProperty('applicationId').GetString() -ceq 'Structura.PlatformConnector') 'app'; $rules.Add('applicationId = Structura.PlatformConnector (ordinal)')
        Assert ($r.GetProperty('channel').GetString() -ceq 'pilot') 'channel'; $rules.Add('channel matches ^[a-z][a-z0-9-]{0,31}$ and equals the requested channel (ChannelMismatch)')
        Assert ($null -ne (Get-ConnectorReleaseVersion $r.GetProperty('version').GetString()) -and (Get-ConnectorReleaseVersion $r.GetProperty('minimumVersion').GetString()) -le (Get-ConnectorReleaseVersion $r.GetProperty('version').GetString())) 'versions'
        $rules.Add('version and minimumVersion are ReleaseVersion x.y.z (no leading zeros, parts <= 65535), minimumVersion <= version')
        Assert ($r.GetProperty('publishedAtUtc').GetString() -cmatch '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z') 'time'; $rules.Add("publishedAtUtc in format yyyy-MM-dd'T'HH:mm:ss'Z'")
        Assert ($r.GetProperty('paused').ValueKind -eq [System.Text.Json.JsonValueKind]::False) 'paused'; $rules.Add('paused is a JSON boolean')
        $i = $r.GetProperty('installer')
        Assert ((@($i.EnumerateObject() | ForEach-Object Name) -join ',') -ceq 'fileName,size,sha256') 'installer fields'; $rules.Add('installer: exactly fileName, size, sha256')
        Assert ($i.GetProperty('fileName').GetString() -ceq 'PlatformConnector-1.1.16.exe') 'file'; $rules.Add('fileName = PlatformConnector-<version>.exe')
        Assert ($i.GetProperty('size').GetRawText() -cmatch '^[1-9][0-9]*\z' -and $i.GetProperty('size').GetInt64() -le 2147483648) 'size'; $rules.Add('size: plain digits, 1..2 GiB')
        Assert ($i.GetProperty('sha256').GetString() -cmatch '^[0-9a-f]{64}\z') 'sha'; $rules.Add('sha256: 64 hexadecimal digits (lowercase)')
        $n = $r.GetProperty('notes')
        Assert ($n.ValueKind -eq [System.Text.Json.JsonValueKind]::Array -and $n.GetArrayLength() -le 20 -and
            @($n.EnumerateArray() | Where-Object { $_.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $_.GetString().Length -lt 1 -or $_.GetString().Length -gt 300 -or $_.GetString().ToCharArray().Where({ [char]::IsControl($_) }).Count }).Count -eq 0) 'notes'
        $rules.Add('notes: array of <= 20 strings, 1..300 characters, no control characters')
    } finally { $document.Dispose() }
    $client = ConvertFrom-ReleaseDescription -Bytes $b -Channel 'pilot'
    Assert ($client.Version -ceq '1.1.16' -and -not $client.Paused -and $client.Installer.Sha256 -ceq $installer16.Sha256) 'client mirror reads the description'
    Expect-Code { ConvertFrom-ReleaseDescription -Bytes $b -Channel 'stable' } 'CHANNEL_MISMATCH' 'pilot description requested as stable'
    $largest = New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Notes $eight) -Installer $installer16 -Now $t0 -CurrentChannel $null -VersionDescriptions @() -InstallerAsset $null
    Assert ($largest.Bytes.Length -le 65536) 'the largest allowed notes fit into 64 KiB'
    $rules.Add('largest allowed input (8 x 200 escaped Cyrillic characters) stays within 64 KiB')
    Pass "produced bytes satisfy the client's rules: $($rules.Count) rules checked"

    # --- strict own check of descriptions ---
    $good = Text $pilot.Bytes
    $installerJson = '{"fileName":"PlatformConnector-1.1.16.exe","size":' + $installer16.Size + ',"sha256":"' + $installer16.Sha256 + '"}'
    $mutations = [ordered]@{
        'extra root field'           = @(($good.Substring(0, $good.Length - 1) + ',"extra":1}'), 'client')
        'duplicate field'            = @($good.Replace('"paused":false', '"paused":false,"paused":true'), 'client')
        'extra installer field'      = @($good.Replace($installerJson, $installerJson.TrimEnd('}') + ',"url":"https://example.invalid"}'), 'client')
        'missing field'              = @($good.Replace(',"minimumVersion":"1.1.15"', ''), 'client')
        'schemaVersion 2.0'          = @($good.Replace('"schemaVersion":2', '"schemaVersion":2.0'), 'client')
        'schemaVersion 3'            = @($good.Replace('"schemaVersion":2', '"schemaVersion":3'), 'client')
        'other application'          = @($good.Replace('Structura.PlatformConnector', 'Structura.Connector.Desktop'), 'client')
        'installer name'             = @($good.Replace('PlatformConnector-1.1.16.exe', 'PlatformConnector-1.1.17.exe'), 'client')
        'size zero'                  = @($good.Replace('"size":' + $installer16.Size, '"size":0'), 'client')
        'size above 2 GiB'           = @($good.Replace('"size":' + $installer16.Size, '"size":2147483649'), 'client')
        'size as string'             = @($good.Replace('"size":' + $installer16.Size, '"size":"' + $installer16.Size + '"'), 'client')
        'short sha256'               = @($good.Replace($installer16.Sha256, $installer16.Sha256.Substring(1)), 'client')
        'minimum above version'      = @($good.Replace('"minimumVersion":"1.1.15"', '"minimumVersion":"1.1.17"'), 'client')
        'version with leading zero'  = @($good.Replace('"version":"1.1.16"', '"version":"1.01.16"'), 'client')
        'paused as string'           = @($good.Replace('"paused":false', '"paused":"false"'), 'client')
        'local time'                 = @($good.Replace('2026-11-25T10:00:00Z', '2026-11-25T10:00:00+04:00'), 'client')
        'trailing comma'             = @($good.Replace('"]}', '"],}'), 'client')
        'comment'                    = @(('/*x*/' + $good), 'client')
        'lone surrogate in a note'   = @($good.Replace('\u0418\u0441', '\uD800\u0441'), 'client')
        'control character in note'  = @($good.Replace('\u0418\u0441', '\u0007\u0441'), 'client')
        'notes not an array'         = @($good.Replace('"notes":[', '"notes":{"a":[').Replace('"]}', '"]}}'), 'client')
        'uppercase sha256'           = @($good.Replace($installer16.Sha256, $installer16.Sha256.ToUpperInvariant()), 'own')
        'fractional seconds'         = @($good.Replace('2026-11-25T10:00:00Z', '2026-11-25T10:00:00.5Z'), 'own')
        'pretty printed'             = @($good.Replace(',"', ', "'), 'own')
        'raw Cyrillic'               = @($good.Replace('\u0418\u0441\u043F\u0440\u0430\u0432\u043B\u0435\u043D \u0432\u0445\u043E\u0434', 'Исправлен вход'), 'own')
        'no notes'                   = @(($good.Substring(0, $good.IndexOf('"notes":')) + '"notes":[]}'), 'own')
        'channel ci-1'               = @($good.Replace('"channel":"pilot"', '"channel":"ci-1"'), 'own-ci')
    }
    foreach ($name in $mutations.Keys) {
        $mutated = Bytes $mutations[$name][0]
        Assert ([Convert]::ToHexString($mutated) -cne [Convert]::ToHexString($pilot.Bytes)) "mutation '$name' must change the bytes"
        switch ($mutations[$name][1]) {
            'client' {
                Expect-Code { ConvertFrom-ReleaseDescription -Bytes $mutated -Channel 'pilot' } 'DESCRIPTION_INVALID' "client rule: $name"
                Expect-Code { Assert-OwnReleaseDescription -Bytes $mutated -Channel 'pilot' } 'DESCRIPTION_INVALID' "own check: $name"
            }
            'own' {
                [void](ConvertFrom-ReleaseDescription -Bytes $mutated -Channel 'pilot')
                Expect-Code { Assert-OwnReleaseDescription -Bytes $mutated -Channel 'pilot' } 'DESCRIPTION_INVALID' "own check: $name"
            }
            'own-ci' {
                [void](ConvertFrom-ReleaseDescription -Bytes $mutated -Channel 'ci-1')
                Expect-Code { Assert-OwnReleaseDescription -Bytes $mutated -Channel 'ci-1' } 'DESCRIPTION_INVALID' "own check: $name"
            }
        }
    }
    $bom = [byte[]](@(0xEF, 0xBB, 0xBF) + $pilot.Bytes)
    Expect-Code { Assert-OwnReleaseDescription -Bytes $bom -Channel 'pilot' } 'DESCRIPTION_INVALID' 'own check: BOM'
    Expect-Code { ConvertFrom-ReleaseDescription -Bytes ([byte[]]::new(65537)) -Channel 'pilot' } 'DESCRIPTION_TOO_LARGE' 'client rule: above 64 KiB'
    Expect-Code { ConvertFrom-ReleaseDescription -Bytes $pilot.Bytes -Channel 'Pilot' } 'CHANNEL_INVALID' 'client rule: requested channel pattern'
    Pass "description with an extra field fails its own check; $($mutations.Count + 3) malformed variants refused"

    # --- channel rules ---
    $stableCurrent = (New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.15') -Installer $installer15 -Now (At '2026-11-20T09:00:00Z') `
        -CurrentChannel $null -VersionDescriptions @([pscustomobject]@{ Channel = 'pilot'; Version = '1.1.15'; Paused = $false; Installer = $installer15 }) -InstallerAsset $null).Description
    $pilotDescription = ConvertFrom-ReleaseDescription -Bytes $pilot.Bytes -Channel 'pilot'
    $asset16 = [pscustomobject]@{ Size = $installer16.Size; Digest = "sha256:$($installer16.Sha256)" }
    $stable = New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $pilotDescription.Installer -Now $t0.AddDays(2) `
        -CurrentChannel $stableCurrent -VersionDescriptions @($pilotDescription) -InstallerAsset $asset16
    Assert ($stable.Description.Channel -ceq 'stable' -and $stable.Description.Installer.Sha256 -ceq $installer16.Sha256) 'stable carries the pilot installer'
    $stableSignature = Sign $stable.Bytes
    Assert (Test-ReleaseSignature -Bytes $stable.Bytes -Signature $stableSignature -PublicKey $publicKey) 'stable signed'
    Expect-Code { ConvertFrom-ReleaseDescription -Bytes $stable.Bytes -Channel 'pilot' } 'CHANNEL_MISMATCH' 'stable description requested as pilot'
    Pass 'transfer pilot -> stable with the same installer'

    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $installer16 -Now $t0 -CurrentChannel $stableCurrent -VersionDescriptions @() -InstallerAsset $null } 'STABLE_REQUIRES_PILOT' 'stable without pilot'
    $stableOnly = (New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $installer16 -Now $t0.AddDays(2) -CurrentChannel $stableCurrent -VersionDescriptions @($pilotDescription) -InstallerAsset $null).Description
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $installer16 -Now $t0.AddDays(3) -CurrentChannel $stableCurrent -VersionDescriptions @($stableOnly) -InstallerAsset $null } 'STABLE_REQUIRES_PILOT' 'stable with only a stable description'
    Pass 'stable refused without a signed pilot description'

    $pilot17 = (New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.17') -Installer $installer17 -Now $t0.AddDays(1) -CurrentChannel $pilotDescription -VersionDescriptions @() -InstallerAsset $null).Description
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16') -Installer $installer16 -Now $t0.AddDays(2) -CurrentChannel $pilot17 -VersionDescriptions @($pilotDescription) -InstallerAsset $asset16 } 'VERSION_DOWNGRADE' 'pilot 1.1.17 -> 1.1.16'
    Pass 'version below the current version of the channel refused'

    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Run '124') -Installer $installer16Other -Now $t0.AddHours(1) -CurrentChannel $null -VersionDescriptions @($pilotDescription) -InstallerAsset $null } 'INSTALLER_CHANGED' 'other installer vs signed version description'
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Run '124') -Installer $installer16Other -Now $t0.AddHours(1) -CurrentChannel $pilotDescription -VersionDescriptions @() -InstallerAsset $null } 'INSTALLER_CHANGED' 'other installer vs current channel description'
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Run '124') -Installer $installer16Other -Now $t0.AddHours(1) -CurrentChannel $null -VersionDescriptions @() -InstallerAsset $asset16 } 'INSTALLER_CHANGED' 'other installer vs published installer asset'
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16') -Installer $installer16 -Now $t0.AddHours(1) -CurrentChannel $null -VersionDescriptions @($pilot17) -InstallerAsset $null } 'VERSION_RELEASE_INCONSISTENT' 'description of another version in the version release'
    Pass 'other installer for an already signed version refused'

    $paused = New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' 'true') -Installer $pilotDescription.Installer -Now $t0.AddHours(2) -CurrentChannel $pilotDescription -VersionDescriptions @($pilotDescription) -InstallerAsset $asset16
    Assert ($paused.Description.Paused -and (Text $paused.Bytes).Contains('"paused":true') -and $paused.Description.PublishedAtUtc -eq $t0.AddHours(2)) 'pause'
    $resumed = New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16') -Installer $paused.Description.Installer -Now $t0.AddHours(3) -CurrentChannel $paused.Description -VersionDescriptions @($paused.Description) -InstallerAsset $asset16
    Assert (-not $resumed.Description.Paused -and $resumed.Description.PublishedAtUtc -gt $paused.Description.PublishedAtUtc) 'resume'
    foreach ($plan in @($paused, $resumed)) { Assert (Test-ReleaseSignature -Bytes $plan.Bytes -Signature (Sign $plan.Bytes) -PublicKey $publicKey) 'pause/resume signed' }
    Pass 'pause and resume of the same version'

    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' 'true') -Installer $installer16 -Now $t0.AddMilliseconds(999) -CurrentChannel $pilotDescription -VersionDescriptions @($pilotDescription) -InstallerAsset $null } 'PUBLISHED_NOT_NEWER' 'same second'
    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.17') -Installer $installer17 -Now $t0.AddMinutes(-1) -CurrentChannel $pilotDescription -VersionDescriptions @() -InstallerAsset $null } 'PUBLISHED_NOT_NEWER' 'earlier time'
    Pass 'publishedAtUtc not later than the current description refused'

    # --- shared signing implementation ---
    [Environment]::SetEnvironmentVariable($keyVariable, $otherKey.ExportPkcs8PrivateKeyPem())
    Expect-Failure { Invoke-PinnedReleaseSignature -Documents @(, $pilot.Bytes) -PublicKeyPemPath $publicPath -KeyVariable $keyVariable } 'does not match the repository pin' 'foreign key'
    Assert ($null -eq [Environment]::GetEnvironmentVariable($keyVariable)) 'a refused key is cleared too'
    Expect-Failure { Invoke-PinnedReleaseSignature -Documents @(, $pilot.Bytes) -PublicKeyPemPath $publicPath -KeyVariable $keyVariable } 'not available' 'missing key'
    [Environment]::SetEnvironmentVariable($keyVariable, 'not a key')
    Expect-Failure { Invoke-PinnedReleaseSignature -Documents @(, $pilot.Bytes) -PublicKeyPemPath $publicPath -KeyVariable $keyVariable } 'could not be read' 'garbage key'
    Assert ($null -eq [Environment]::GetEnvironmentVariable($keyVariable)) 'an unreadable key is cleared too'
    $privatePath = Join-Path $root 'private-as-pin.pem'; [IO.File]::WriteAllText($privatePath, $key.ExportPkcs8PrivateKeyPem())
    Expect-Failure { Import-PinnedReleaseKey $privatePath } 'public key only' 'private key as the pin'
    $tampered = [byte[]]$pilotSignature.Clone(); $tampered[5] = $tampered[5] -bxor 1
    Assert (-not (Test-ReleaseSignature -Bytes $pilot.Bytes -Signature $tampered -PublicKey $publicKey)) 'tampered signature refused'
    Assert (-not (Test-ReleaseSignature -Bytes $paused.Bytes -Signature $pilotSignature -PublicKey $publicKey)) 'signature of other bytes refused'
    $der = $key.SignData($pilot.Bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    Assert (-not (Test-ReleaseSignature -Bytes $pilot.Bytes -Signature $der -PublicKey $publicKey)) 'DER signature refused'
    $pin = Import-PinnedReleaseKey (Get-PinnedReleaseKeyPath)
    try { Assert ($pin.KeySize -eq 256) 'repository pin is P-256' } finally { $pin.Dispose() }
    Pass 'signing: pin match, key variable cleared, P1363 only, repository pin loads'

    # --- candidate artifact and run of build-unified-connector-candidate.yml ---
    $headSha = 'a' * 40
    function New-Candidate([hashtable]$Change = @{}, [string[]]$Extra = @()) {
        $directory = Join-Path $script:root ('artifact-' + [guid]::NewGuid().ToString('N'))
        $release = Join-Path $directory 'clean-output/release'; [void][IO.Directory]::CreateDirectory($release)
        $exe = Join-Path $release 'PlatformConnector-1.1.16.exe'; [IO.File]::WriteAllBytes($exe, [Text.Encoding]::UTF8.GetBytes('installer 1.1.16'))
        $manifest = [ordered]@{ schemaVersion = 1; version = '1.1.16'; sourceSha = $script:headSha; sourceRun = '987654321'; fileName = 'PlatformConnector-1.1.16.exe'
            bytes = (Get-Item $exe).Length; sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant(); artifactKind = 'unsigned-cloud-candidate'; ciInjectedInstallFault = $false }
        foreach ($k in $Change.Keys) { $manifest[$k] = $Change[$k] }
        Set-Content -LiteralPath (Join-Path $release 'release.json') -Value ($manifest | ConvertTo-Json) -Encoding utf8
        foreach ($name in $Extra) { [IO.File]::WriteAllText((Join-Path $release $name), 'x') }
        [void][IO.Directory]::CreateDirectory((Join-Path $release 'evidence')); [IO.File]::WriteAllText((Join-Path $release 'evidence/clean-install.json'), '{}')
        return $directory
    }
    $candidate = Assert-CandidateArtifact -Directory (New-Candidate) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha
    Assert ($candidate.Sha256 -ceq $installer16.Sha256 -and $candidate.Size -eq $installer16.Size) 'candidate identity'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ sha256 = ('0' * 64) }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'release.json digest differs from the file'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ bytes = 1 }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'release.json size differs'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ ciInjectedInstallFault = $true; artifactKind = 'ci-injected-install-fault' }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'deliberately failing build'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ sourceRun = '1' }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'other run'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ sourceSha = ('b' * 40) }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'other commit'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate @{ version = '1.1.17' }) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'other version'
    Expect-Code { Assert-CandidateArtifact -Directory (New-Candidate -Extra @('PlatformConnector-1.1.15.exe')) -Version '1.1.16' -RunId '987654321' -HeadSha $headSha } 'CANDIDATE_INVALID' 'second installer'
    $goodRun = [pscustomobject]@{ id = 987654321; path = '.github/workflows/build-unified-connector-candidate.yml'; status = 'completed'; conclusion = 'success'; event = 'workflow_dispatch'
        head_branch = 'master'; head_sha = $headSha; head_repository = [pscustomobject]@{ full_name = 'PROBIM55/ServerConnector' } }
    Assert-CandidateRun -Run $goodRun -RunId '987654321'
    foreach ($change in @(@{ path = '.github/workflows/sign-connector-ci-release.yml' }, @{ conclusion = 'failure' }, @{ event = 'push' }, @{ head_branch = 'feature' }, @{ status = 'in_progress' }, @{ id = 1 })) {
        $run = $goodRun.PSObject.Copy(); foreach ($k in $change.Keys) { $run.$k = $change[$k] }
        Expect-Code { Assert-CandidateRun -Run $run -RunId '987654321' } 'SOURCE_RUN_INVALID' "candidate run with $($change.Keys)"
    }
    $forkRun = $goodRun.PSObject.Copy(); $forkRun.head_repository = [pscustomobject]@{ full_name = 'someone/ServerConnector' }
    Expect-Code { Assert-CandidateRun -Run $forkRun -RunId '987654321' } 'SOURCE_RUN_INVALID' 'candidate run from a fork'
    Assert-AncestorComparison ([pscustomobject]@{ status = 'identical'; behind_by = 0 }); Assert-AncestorComparison ([pscustomobject]@{ status = 'ahead'; behind_by = 0 })
    foreach ($status in 'behind', 'diverged') { Expect-Code { Assert-AncestorComparison ([pscustomobject]@{ status = $status; behind_by = 1 }) } 'SOURCE_RUN_INVALID' "comparison $status" }
    Pass 'candidate: release.json identity, no fault build, same run and commit, ancestor of the publishing commit'

    # --- review fixes: channel state, plan, tag, pinned key, candidate artifact and run ---
    Assert ($null -eq (Resolve-ChannelDescription -ReleaseExists $false -Bytes $null -Signature $null -Channel 'pilot' -PublicKey $publicKey)) 'no channel release = new channel'
    $firstInNewChannel = New-ConnectorReleasePlan -Request (New-Request 'pilot' '1.1.16' -Run '123') -Installer $installer16 -Now $t0 -CurrentChannel $null -VersionDescriptions @() -InstallerAsset $null
    Assert ($firstInNewChannel.Description.Channel -ceq 'pilot') 'first publication into a channel that does not exist yet'
    $resolved = Resolve-ChannelDescription -ReleaseExists $true -Bytes $pilot.Bytes -Signature $pilotSignature -Channel 'pilot' -PublicKey $publicKey
    Assert ($resolved.Version -ceq '1.1.16' -and [Convert]::ToHexString($resolved.Bytes) -ceq [Convert]::ToHexString($pilot.Bytes)) 'readable channel description'
    Expect-Code { Resolve-ChannelDescription -ReleaseExists $true -Bytes $null -Signature $null -Channel 'pilot' -PublicKey $publicKey } 'CHANNEL_DESCRIPTION_UNREADABLE' 'channel release without files'
    Expect-Code { Resolve-ChannelDescription -ReleaseExists $true -Bytes $pilot.Bytes -Signature $null -Channel 'pilot' -PublicKey $publicKey } 'CHANNEL_DESCRIPTION_UNREADABLE' 'channel release without signature (interrupted --clobber)'
    Expect-Code { Resolve-ChannelDescription -ReleaseExists $true -Bytes $pilot.Bytes -Signature $tampered -Channel 'pilot' -PublicKey $publicKey } 'CHANNEL_DESCRIPTION_UNREADABLE' 'channel description with a wrong signature'
    Expect-Code { Resolve-ChannelDescription -ReleaseExists $true -Bytes $paused.Bytes -Signature $pilotSignature -Channel 'pilot' -PublicKey $publicKey } 'CHANNEL_DESCRIPTION_UNREADABLE' 'new description with the old signature'
    Expect-Code { Resolve-ChannelDescription -ReleaseExists $true -Bytes $stable.Bytes -Signature $stableSignature -Channel 'pilot' -PublicKey $publicKey } 'CHANNEL_DESCRIPTION_UNREADABLE' 'genuine stable description replayed into pilot'
    Pass 'channel: no release = new channel; existing but unreadable channel refused'

    Expect-Code { New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $installer16 -Now $t0.AddDays(3) -CurrentChannel $stableCurrent -VersionDescriptions @($paused.Description) -InstallerAsset $null } 'STABLE_REQUIRES_PILOT' 'stable after a paused pilot'
    $afterResume = New-ConnectorReleasePlan -Request (New-Request 'stable' '1.1.16') -Installer $installer16 -Now $t0.AddDays(3) -CurrentChannel $stableCurrent -VersionDescriptions @($resumed.Description) -InstallerAsset $null
    Assert ($afterResume.Description.Channel -ceq 'stable') 'stable after the pilot was resumed'
    Pass 'stable counts only an unpaused pilot description'

    $planRequest = New-Request 'stable' '1.1.16'
    $facts = Get-ReleasePlanFacts -Request $planRequest -Installer $installer16 -Source 'signed pilot description of platform-connector-v1.1.16' -CurrentChannel $stableCurrent
    Assert ($facts.channel_current -ceq '1.1.15 paused=false publishedAtUtc=2026-11-20T09:00:00Z' -and $facts.installer_size -ceq [string]$installer16.Size) 'plan facts'
    $planned = @{}; foreach ($k in $facts.Keys) { $planned[$k] = [string]$facts[$k] }
    Assert-ReleasePlanMatch -Facts $facts -Planned $planned
    foreach ($k in 'installer_sha256', 'installer_size', 'version', 'channel', 'source', 'channel_current', 'paused') {
        $changed = $planned.Clone(); $changed[$k] = 'other'
        Expect-Code { Assert-ReleasePlanMatch -Facts $facts -Planned $changed } 'PLAN_MISMATCH' "plan differs in $k"
    }
    $missing = $planned.Clone(); $missing.Remove('installer_sha256')
    Expect-Code { Assert-ReleasePlanMatch -Facts $facts -Planned $missing } 'PLAN_MISMATCH' 'plan output missing'
    $movedChannel = Get-ReleasePlanFacts -Request $planRequest -Installer $installer16 -Source 'signed pilot description of platform-connector-v1.1.16' -CurrentChannel $null
    Expect-Code { Assert-ReleasePlanMatch -Facts $movedChannel -Planned $planned } 'PLAN_MISMATCH' 'channel changed between plan and sign'
    Pass 'sign refuses when its own result differs from what plan showed'

    $tagCommit = [pscustomobject]@{ type = 'commit'; sha = 'c' * 40 }
    $tagObject = Get-ReleaseTagObject ([pscustomobject]@{ ref = 'refs/tags/platform-connector-v1.1.16'; object = $tagCommit }) 'platform-connector-v1.1.16'
    Assert-ReleaseTagCommit $tagObject 'platform-connector-v1.1.16'
    Expect-Code { Get-ReleaseTagObject ([pscustomobject]@{ ref = 'refs/tags/platform-connector-v1.1.160'; object = $tagCommit }) 'platform-connector-v1.1.16' } 'TAG_INVALID' 'other tag reference'
    Expect-Code { Get-ReleaseTagObject @([pscustomobject]@{ ref = 'refs/tags/a'; object = $tagCommit }, [pscustomobject]@{ ref = 'refs/tags/b'; object = $tagCommit }) 'a' } 'TAG_INVALID' 'several references'
    Expect-Code { Assert-ReleaseTagCommit ([pscustomobject]@{ type = 'tree'; sha = 'c' * 40 }) 'x' } 'TAG_INVALID' 'tag on a tree'
    Expect-Code { Assert-ReleaseTagCommit ([pscustomobject]@{ type = 'tag'; sha = 'c' * 40 }) 'x' } 'TAG_INVALID' 'tag not resolved to a commit'
    Pass 'tag: exact reference, resolved to a commit (ancestry: comparison rule above)'

    Assert-SamePinnedKeyBlob ('e' * 40) ('e' * 40)
    Expect-Code { Assert-SamePinnedKeyBlob ('e' * 40) ('f' * 40) } 'SOURCE_RUN_INVALID' 'build commit with another pinned key'
    Expect-Code { Assert-SamePinnedKeyBlob '' ('f' * 40) } 'SOURCE_RUN_INVALID' 'pinned key missing on the build commit'
    $artifactName = 'platform-connector-1.1.16-cloud-tested-unsigned'
    $one = Select-CandidateArtifact ([pscustomobject]@{ artifacts = @([pscustomobject]@{ id = 42; name = $artifactName; expired = $false; digest = 'sha256:' + ('a' * 64) }) }) $artifactName
    Assert ($one.Id -ceq '42' -and $one.Digest -ceq ('sha256:' + ('a' * 64))) 'artifact by id with digest'
    $noDigest = Select-CandidateArtifact ([pscustomobject]@{ artifacts = @([pscustomobject]@{ id = 43; name = $artifactName; expired = $false }) }) $artifactName
    Assert ($noDigest.Id -ceq '43' -and $null -eq $noDigest.Digest) 'artifact without digest'
    Expect-Code { Select-CandidateArtifact ([pscustomobject]@{ artifacts = @([pscustomobject]@{ id = 42; name = $artifactName; expired = $true }) }) $artifactName } 'CANDIDATE_INVALID' 'expired artifact'
    Expect-Code { Select-CandidateArtifact ([pscustomobject]@{ artifacts = @([pscustomobject]@{ id = 42; name = $artifactName; expired = $false }, [pscustomobject]@{ id = 44; name = $artifactName; expired = $false }) }) $artifactName } 'CANDIDATE_INVALID' 'two artifacts of one name'
    Expect-Code { Select-CandidateArtifact ([pscustomobject]@{ artifacts = @() }) $artifactName } 'CANDIDATE_INVALID' 'no artifact'
    Expect-Code { Select-CandidateArtifact ([pscustomobject]@{ artifacts = @([pscustomobject]@{ id = 42; name = $artifactName; expired = $false; digest = 'md5:x' }) }) $artifactName } 'CANDIDATE_INVALID' 'unexpected digest'
    $runBefore = [pscustomobject]@{ id = 987654321; run_attempt = 1; status = 'completed'; conclusion = 'success'; head_sha = $headSha }
    Assert-SameRunState -Before $runBefore -After $runBefore.PSObject.Copy()
    foreach ($change in @(@{ run_attempt = 2 }, @{ conclusion = 'failure' }, @{ status = 'in_progress' }, @{ head_sha = ('b' * 40) })) {
        $runAfter = $runBefore.PSObject.Copy(); foreach ($k in $change.Keys) { $runAfter.$k = $change[$k] }
        Expect-Code { Assert-SameRunState -Before $runBefore -After $runAfter } 'SOURCE_RUN_CHANGED' "run changed: $($change.Keys)"
    }
    Pass 'candidate: same pinned key on the build commit, artifact by id and digest, same run attempt after download'

    Write-Output "PASS publish_connector_release: $($passed.Count) groups; client rules checked: $($rules -join '; ')"
} finally {
    [Environment]::SetEnvironmentVariable($keyVariable, [NullString]::Value)
    $publicKey.Dispose(); $key.Dispose(); $otherKey.Dispose()
    Remove-TestRoot $root
}
