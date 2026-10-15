[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CommitSha
)

$ErrorActionPreference = 'Stop'

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-SafeRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or $Path.StartsWith('/') -or
        $Path.Contains(':') -or $Path.Contains([char]0)) { return $false }

    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..') -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
        $stem = $segment.Split('.')[0]
        if ($stem -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { return $false }
    }
    return $true
}

function Assert-X64Pe([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        Assert-Condition ($stream.Length -ge 256) "Executable is too short to be a PE file: $Path"
        Assert-Condition ($reader.ReadUInt16() -eq 0x5A4D) "Executable is missing the MZ header: $Path"
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        Assert-Condition ($peOffset -ge 0x40 -and $peOffset -le ($stream.Length - 26)) "Executable has an invalid PE header offset: $Path"
        $stream.Position = $peOffset
        Assert-Condition ($reader.ReadUInt32() -eq 0x00004550) "Executable is missing the PE signature: $Path"
        Assert-Condition ($reader.ReadUInt16() -eq 0x8664) "Executable is not Windows x64/AMD64: $Path"
        $stream.Position = $peOffset + 24
        Assert-Condition ($reader.ReadUInt16() -eq 0x20B) "Executable is not a 64-bit PE image: $Path"
    }
    finally {
        $stream.Dispose()
    }
}

Assert-Condition ($Version -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') "Version must be a stable SemVer: $Version"
Assert-Condition ($CommitSha -match '^[0-9a-fA-F]{40}$') 'CommitSha must be a full 40-character Git SHA.'
$CommitSha = $CommitSha.ToLowerInvariant()
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
Assert-Condition (Test-Path -LiteralPath $ArchivePath -PathType Leaf) "ZIP does not exist: $ArchivePath"
$zipName = "CodexSlotPing-v$Version-win-x64.zip"
Assert-Condition ([IO.Path]::GetFileName($ArchivePath) -ceq $zipName) "ZIP filename must be $zipName"
$sidecarPath = "$ArchivePath.sha256"
Assert-Condition (Test-Path -LiteralPath $sidecarPath -PathType Leaf) "SHA-256 sidecar does not exist: $sidecarPath"

$sidecarText = [IO.File]::ReadAllText($sidecarPath)
Assert-Condition ($sidecarText -cmatch '^(?<hash>[0-9a-fA-F]{64})  (?<name>[^\r\n]+)\r?\n?$') 'SHA-256 sidecar must contain exactly one hash, two spaces, and the ZIP filename.'
Assert-Condition ($Matches.name -ceq $zipName) 'SHA-256 sidecar names a different ZIP.'
$sidecarHash = $Matches.hash.ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($actualHash -ceq $sidecarHash) 'SHA-256 sidecar does not match the ZIP.'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "CodexSlotPing-verify-$([guid]::NewGuid().ToString('N'))"
$tempRoot = [IO.Path]::GetFullPath($tempRoot)
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Assert-Condition ($tempRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) 'Verifier temporary root escaped the system temp directory.'
[void][IO.Directory]::CreateDirectory($tempRoot)

try {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $actualFiles = [Collections.Generic.List[string]]::new()
        $actualFileSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $entrySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $totalExpanded = [long]0
        foreach ($entry in $archive.Entries) {
            $isDirectory = $entry.FullName.EndsWith('/', [StringComparison]::Ordinal)
            $relative = if ($isDirectory) { $entry.FullName.TrimEnd('/') } else { $entry.FullName }
            Assert-Condition (Test-SafeRelativePath $relative) "ZIP contains an unsafe path: $relative"
            if ($entrySet.Contains($relative)) { throw "ZIP contains a duplicate/case-colliding entry: $relative" }
            [void]$entrySet.Add($relative)
            if ($isDirectory) { continue }

            $unixKind = ($entry.ExternalAttributes -shr 16) -band 0xF000
            Assert-Condition ($unixKind -ne 0xA000) "ZIP contains a symbolic link: $relative"
            Assert-Condition ($actualFileSet.Add($relative)) "ZIP contains a duplicate/case-colliding file: $relative"
            Assert-Condition ($entry.Length -le 1GB) "ZIP entry exceeds the 1 GiB file limit: $relative"
            $totalExpanded += $entry.Length
            Assert-Condition ($totalExpanded -le 2GB) 'ZIP expanded size exceeds the 2 GiB package limit.'
            [void]$actualFiles.Add($relative)

            $destination = [IO.Path]::GetFullPath((Join-Path $tempRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            $tempRootPrefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-Condition ($destination.StartsWith($tempRootPrefix, [StringComparison]::OrdinalIgnoreCase)) "ZIP entry escaped extraction root: $relative"
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            $sourceStream = $entry.Open()
            $destinationStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $sourceStream.CopyTo($destinationStream) }
            finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    foreach ($relative in $actualFiles) {
        $normalized = $relative.Replace('\', '/')
        $segments = $normalized.Split('/')
        Assert-Condition (-not ($segments | Where-Object { $_ -match '^(tests?|fixtures?|bin|obj|update-control|logs?)$' })) "ZIP contains a test, build, control, or user-log directory: $relative"
        Assert-Condition ($normalized -notmatch '(?i)(^|/)[^/]*\.part$') "ZIP contains a partial file: $relative"
        Assert-Condition ($normalized -notmatch '(?i)\.(pdb|cs|xaml|csproj|sln|props|targets|ps1|psm1|cmd|bat)$') "ZIP contains a debug, source, or shell file: $relative"
        Assert-Condition ($normalized -notmatch '(?i)(^|/)(user-config|user-state|state|config|logs?)(/|$)|(^|/)[^/]*(update-intent|journal|handoff-ready|startup-ack)[^/]*$') "ZIP contains update-control or user config/state/log data: $relative"
    }

    $manifestPath = Join-Path $tempRoot 'codex-slot-ping.manifest.json'
    Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'ZIP is missing the root package manifest.'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $manifestJson = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
    $manifestDoc = [System.Text.Json.JsonDocument]::Parse([string]$manifestJson)
    try {
        $root = $manifestDoc.RootElement
        Assert-Condition ($root.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) 'Package manifest root must be an object.'
        $manifestProps = [Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            Assert-Condition (-not $manifestProps.ContainsKey($property.Name)) "Package manifest has duplicate property: $($property.Name)"
            $manifestProps.Add($property.Name, $property.Value)
        }
        $expectedProperties = @('schemaVersion', 'productId', 'version', 'files')
        Assert-Condition ($manifestProps.Count -eq $expectedProperties.Count -and @($expectedProperties | Where-Object { -not $manifestProps.ContainsKey($_) }).Count -eq 0) 'Package manifest properties do not match schema v1.'
        Assert-Condition ($manifestProps['schemaVersion'].ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $manifestProps['schemaVersion'].GetInt32() -eq 1) 'Package manifest schemaVersion must equal 1.'
        Assert-Condition ($manifestProps['productId'].GetString() -ceq 'CodexSlotPing') 'Package manifest productId is invalid.'
        Assert-Condition ($manifestProps['version'].GetString() -ceq $Version) 'Package manifest version does not match the requested version.'
        Assert-Condition ($manifestProps['files'].ValueKind -eq [System.Text.Json.JsonValueKind]::Array) 'Package manifest files must be an array.'

        $manifestFiles = [Collections.Generic.List[string]]::new()
        $manifestFileSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $manifestProps['files'].EnumerateArray()) {
            Assert-Condition ($file.ValueKind -eq [System.Text.Json.JsonValueKind]::String) 'Package manifest files entries must be strings.'
            $relative = $file.GetString()
            Assert-Condition (Test-SafeRelativePath $relative) "Package manifest contains an unsafe path: $relative"
            Assert-Condition ($manifestFileSet.Add($relative)) "Package manifest contains a duplicate/case-colliding path: $relative"
            [void]$manifestFiles.Add($relative)
        }
    }
    finally { $manifestDoc.Dispose() }

    Assert-Condition ($manifestFileSet.SetEquals($actualFileSet)) 'Manifest file set does not exactly match ZIP regular files.'
    Assert-Condition ($manifestFileSet.Contains('codex-slot-ping.manifest.json')) 'Manifest does not own itself.'
    foreach ($name in @('CodexSlotPing.App.exe', 'CodexSlotPing.Runner.exe', 'CodexSlotPing.Updater.exe')) {
        Assert-Condition ($actualFileSet.Contains($name)) "ZIP is missing root executable: $name"
        $exePath = Join-Path $tempRoot $name
        Assert-X64Pe $exePath
        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
        Assert-Condition ($versionInfo.FileVersion -ceq "$Version.0") "$name FileVersion '$($versionInfo.FileVersion)' does not equal '$Version.0'."
        Assert-Condition ($versionInfo.ProductVersion -ceq "$Version+$CommitSha") "$name ProductVersion '$($versionInfo.ProductVersion)' does not equal '$Version+$CommitSha'."
    }

    foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        Assert-Condition ($actualFileSet.Contains($runtimeFile)) "Self-contained .NET runtime dependency is missing: $runtimeFile"
    }

    $pdbCount = @($actualFiles | Where-Object { $_ -match '(?i)\.pdb$' }).Count
    Assert-Condition ($pdbCount -eq 0) "ZIP contains $pdbCount PDB files."

    $summary = [ordered]@{
        archive = $ArchivePath
        version = $Version
        commitSha = $CommitSha
        sha256 = $actualHash
        size = (Get-Item -LiteralPath $ArchivePath).Length
        fileCount = $actualFiles.Count
        manifestFileCount = $manifestFiles.Count
        appFileVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tempRoot 'CodexSlotPing.App.exe'))).FileVersion
        runnerFileVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tempRoot 'CodexSlotPing.Runner.exe'))).FileVersion
        updaterFileVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tempRoot 'CodexSlotPing.Updater.exe'))).FileVersion
    }
    $summary | ConvertTo-Json -Compress
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "package-version=$Version`npackage-sha256=$actualHash`npackage-file-count=$($actualFiles.Count)`nmanifest-file-count=$($manifestFiles.Count)`n")
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
