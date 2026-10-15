[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CommitSha,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'release-package-helpers.psm1') -Force

function Get-PackageRelativeFiles([string]$Root) {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        $files.Add([IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/'))
    }
    $files.Sort([StringComparer]::OrdinalIgnoreCase)
    return $files.ToArray()
}

function Invoke-DotNetPublish([string]$Project, [string]$Destination, [string]$AssemblyVersion, [string]$FileVersion, [string]$InformationalVersion, [string]$RuntimeOverrides) {
    $arguments = @(
        'publish', $Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '--nologo',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$AssemblyVersion",
        "-p:FileVersion=$FileVersion",
        "-p:InformationalVersion=$InformationalVersion",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:CspPackageSharedRuntime=true',
        "-p:CustomAfterMicrosoftCommonTargets=$RuntimeOverrides",
        '-o', $Destination
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project with exit code $LASTEXITCODE." }
}

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version must be a stable SemVer: $Version"
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'CommitSha must be a full 40-character Git SHA.' }
$CommitSha = $CommitSha.ToLowerInvariant()
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw "Output directory already exists; refusing to overwrite it: $outputRoot" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$stagingRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "CodexSlotPing-release-$([guid]::NewGuid().ToString('N'))"))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $stagingRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release staging escaped the system temp directory.' }
[void][IO.Directory]::CreateDirectory($stagingRoot)
$outputDirectoryCreated = $false
$publishedZip = $false
$publishedSidecar = $false

try {
    $packageRoot = Join-Path $stagingRoot 'package'
    [void][IO.Directory]::CreateDirectory($packageRoot)
    $publishRoots = @(
        [pscustomobject]@{ Name = 'App'; Project = 'src/CodexSlotPing.App/CodexSlotPing.App.csproj' },
        [pscustomobject]@{ Name = 'Runner'; Project = 'src/CodexSlotPing.Runner/CodexSlotPing.Runner.csproj' },
        [pscustomobject]@{ Name = 'Updater'; Project = 'src/CodexSlotPing.Updater/CodexSlotPing.Updater.csproj' }
    )
    $assemblyVersion = "$Version.0"
    $informationalVersion = "$Version+$CommitSha"
    $runtimeOverrides = Join-Path $PSScriptRoot 'release-package-runtime.targets'
    $owners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($item in $publishRoots) {
        $publishRoot = Join-Path $stagingRoot "publish-$($item.Name)"
        $projectPath = Join-Path $repoRoot $item.Project.Replace('/', [IO.Path]::DirectorySeparatorChar)
        Invoke-DotNetPublish $projectPath $publishRoot $assemblyVersion $assemblyVersion $informationalVersion $runtimeOverrides

        foreach ($publishedFile in Get-ChildItem -LiteralPath $publishRoot -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($publishRoot, $publishedFile.FullName).Replace('\', '/')
            Add-ReleasePackageFile -SourceFile $publishedFile.FullName `
                -RelativePath $relative -PackageRoot $packageRoot -Owners $owners
        }
    }

    foreach ($executable in @('CodexSlotPing.App.exe', 'CodexSlotPing.Runner.exe', 'CodexSlotPing.Updater.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $executable) -PathType Leaf)) {
            throw "Publish output is missing required root executable: $executable"
        }
    }

    $manifestPath = Join-Path $packageRoot 'codex-slot-ping.manifest.json'
    if (Test-Path -LiteralPath $manifestPath) { throw 'Publish output unexpectedly contains the package manifest.' }
    $manifestFiles = [Collections.Generic.List[string]]::new()
    foreach ($relative in Get-PackageRelativeFiles $packageRoot) { [void]$manifestFiles.Add($relative) }
    [void]$manifestFiles.Add('codex-slot-ping.manifest.json')
    $manifestFiles.Sort([StringComparer]::OrdinalIgnoreCase)
    $manifest = [ordered]@{
        schemaVersion = 1
        productId = 'CodexSlotPing'
        version = $Version
        files = $manifestFiles.ToArray()
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))

    $zipName = "CodexSlotPing-v$Version-win-x64.zip"
    $temporaryZipPath = Join-Path $stagingRoot $zipName
    $temporarySidecarPath = "$temporaryZipPath.sha256"
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot, $temporaryZipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $sha256 = (Get-FileHash -LiteralPath $temporaryZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($temporarySidecarPath, "$sha256  $zipName`n", [Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot 'verify-release-package.ps1') `
        -ArchivePath $temporaryZipPath -Version $Version -CommitSha $CommitSha | Out-Null

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputRoot))
    [void][IO.Directory]::CreateDirectory($outputRoot)
    $outputDirectoryCreated = $true
    $finalZipPath = Join-Path $outputRoot $zipName
    $finalSidecarPath = "$finalZipPath.sha256"
    $zipPartialPath = "$finalZipPath.partial-$([guid]::NewGuid().ToString('N'))"
    $sidecarPartialPath = "$finalSidecarPath.partial-$([guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::Copy($temporaryZipPath, $zipPartialPath, $false)
        [IO.File]::Copy($temporarySidecarPath, $sidecarPartialPath, $false)
        [IO.File]::Move($zipPartialPath, $finalZipPath)
        $publishedZip = $true
        [IO.File]::Move($sidecarPartialPath, $finalSidecarPath)
        $publishedSidecar = $true
        $summary = & (Join-Path $PSScriptRoot 'verify-release-package.ps1') `
            -ArchivePath $finalZipPath -Version $Version -CommitSha $CommitSha | ConvertFrom-Json
    }
    finally {
        foreach ($partial in @($zipPartialPath, $sidecarPartialPath)) {
            if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
        }
    }

    $summary | ConvertTo-Json -Compress
}
catch {
    if ($publishedZip -and (Test-Path -LiteralPath $finalZipPath -PathType Leaf)) { Remove-Item -LiteralPath $finalZipPath -Force }
    if ($publishedSidecar -and (Test-Path -LiteralPath $finalSidecarPath -PathType Leaf)) { Remove-Item -LiteralPath $finalSidecarPath -Force }
    if ($outputDirectoryCreated -and (Test-Path -LiteralPath $outputRoot -PathType Container) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $outputRoot -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
