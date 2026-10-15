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
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
& (Join-Path $PSScriptRoot 'verify-release-package.ps1') -ArchivePath $ArchivePath -Version $Version -CommitSha $CommitSha | Out-Null

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "CodexSlotPing-e2e-$([guid]::NewGuid().ToString('N'))"))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'E2E temporary root escaped the system temp directory.' }
[void][IO.Directory]::CreateDirectory($tempRoot)

$oldCurrent = [Environment]::GetEnvironmentVariable('CSP_RELEASE_E2E_CURRENT_ARCHIVE', 'Process')
$oldTarget = [Environment]::GetEnvironmentVariable('CSP_RELEASE_E2E_TARGET_ARCHIVE', 'Process')
try {
    $currentArchivePath = $ArchivePath
    if ($Version -ne '0.3.0') {
        $currentDirectory = Join-Path $tempRoot 'current-v0.3.0'
        & (Join-Path $PSScriptRoot 'package-release.ps1') `
            -Version '0.3.0' -CommitSha $CommitSha -OutputDirectory $currentDirectory | Out-Null
        $currentArchivePath = Join-Path $currentDirectory 'CodexSlotPing-v0.3.0-win-x64.zip'
    }

    $targetDirectory = Join-Path $tempRoot 'test-target-v0.3.1'
    & (Join-Path $PSScriptRoot 'package-release.ps1') `
        -Version '0.3.1' -CommitSha $CommitSha -OutputDirectory $targetDirectory | Out-Null
    $targetArchivePath = Join-Path $targetDirectory 'CodexSlotPing-v0.3.1-win-x64.zip'

    [Environment]::SetEnvironmentVariable('CSP_RELEASE_E2E_CURRENT_ARCHIVE', $currentArchivePath, 'Process')
    [Environment]::SetEnvironmentVariable('CSP_RELEASE_E2E_TARGET_ARCHIVE', $targetArchivePath, 'Process')
    $testProject = Join-Path $repoRoot 'tests/CodexSlotPing.Updater.Tests/CodexSlotPing.Updater.Tests.csproj'
    & dotnet test $testProject -c Release `
        --filter 'Category=ReleaseArtifactE2E' `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "Release artifact E2E failed with exit code $LASTEXITCODE." }
    Write-Output 'Release artifact E2E: PASS'
}
finally {
    [Environment]::SetEnvironmentVariable('CSP_RELEASE_E2E_CURRENT_ARCHIVE', $oldCurrent, 'Process')
    [Environment]::SetEnvironmentVariable('CSP_RELEASE_E2E_TARGET_ARCHIVE', $oldTarget, 'Process')
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
