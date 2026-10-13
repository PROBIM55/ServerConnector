function Assert-ReleaseSigningDocument([byte[]]$Bytes, [string]$Purpose) {
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes))
    try {
        function ExactProperties($element, [string[]]$expected) {
            $actual = @($element.EnumerateObject() | ForEach-Object Name)
            if ($actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or
                @($actual | Where-Object { $_ -cnotin $expected }).Count) { throw 'Unexpected release document fields' }
        }
        function ValidAsset($element) {
            if ($element.GetProperty('size').GetInt64() -le 0 -or $element.GetProperty('size').GetInt64() -gt 1073741824 -or
                $element.GetProperty('sha256').GetString() -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid release asset pin' }
        }
        $d = $document.RootElement
        if ($Purpose -ceq 'updates') {
            ExactProperties $d @('schemaVersion','applicationId','channel','assets')
            $channel = $d.GetProperty('channel').GetString()
            if ($d.GetProperty('schemaVersion').GetInt32() -ne 1 -or
                $d.GetProperty('applicationId').GetString() -cne 'Structura.Connector.Desktop' -or
                $channel -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') { throw 'Unexpected update identity' }
            $assets = @($d.GetProperty('assets').EnumerateArray())
            if ($assets.Count -eq 0 -or $assets.Count -gt 20) { throw 'Invalid update asset count' }
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($asset in $assets) {
                ExactProperties $asset @('fileName','version','type','size','sha256'); ValidAsset $asset
                $version = $asset.GetProperty('version').GetString(); $name = $asset.GetProperty('fileName').GetString()
                $suffix = if ($asset.GetProperty('type').GetString() -ceq 'Full') { 'full' } else { 'delta(?:\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)?' }
                if ($version -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
                    $asset.GetProperty('type').GetString() -cnotin @('Full','Delta') -or -not $names.Add($name) -or
                    $name -cnotmatch ('^Structura\.Connector\.Desktop-' + [regex]::Escape($version) + '-' + [regex]::Escape($channel) + '-' + $suffix + '\.nupkg$')) {
                    throw 'Unexpected update asset identity'
                }
            }
        } elseif ($Purpose -ceq 'helper') {
            ExactProperties $d @('schemaVersion','version','helperRelativePath','size','sha256','velopackSetup','callerImage')
            ValidAsset $d
            $version = $d.GetProperty('version').GetString()
            if ($d.GetProperty('schemaVersion').GetInt32() -ne 4 -or $version -cnotmatch '^\d+\.\d+\.\d+$' -or
                $d.GetProperty('helperRelativePath').GetString() -cne 'Connector.Upgrade.MachineHelper.exe') { throw 'Unexpected helper identity' }
            $setup = $d.GetProperty('velopackSetup'); $caller = $d.GetProperty('callerImage')
            ExactProperties $setup @('relativePath','size','sha256','packageId','version'); ValidAsset $setup
            ExactProperties $caller @('relativePath','size','sha256'); ValidAsset $caller
            if ($setup.GetProperty('relativePath').GetString() -cne 'Setup.exe' -or
                $setup.GetProperty('packageId').GetString() -cne 'Structura.Connector.Desktop' -or
                $setup.GetProperty('version').GetString() -cne $version -or
                $caller.GetProperty('relativePath').GetString() -cne 'StructuraConnectorInstaller\Bootstrapper\Connector.Upgrade.Bootstrapper.exe') { throw 'Unexpected protected caller/setup identity' }
        } else { throw 'Unknown release signing purpose' }
    } finally { $document.Dispose() }
}
