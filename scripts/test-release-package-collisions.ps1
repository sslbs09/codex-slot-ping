$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'release-package-helpers.psm1') -Force

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "CodexSlotPing-collision-test-$([guid]::NewGuid().ToString('N'))"))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Assert-True ($tempRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) 'Collision test directory escaped the system temp directory.'
[void][IO.Directory]::CreateDirectory($tempRoot)

try {
    $packageRoot = Join-Path $tempRoot 'package'
    $appRoot = Join-Path $tempRoot 'app'
    $runnerRoot = Join-Path $tempRoot 'runner'
    [void][IO.Directory]::CreateDirectory($packageRoot)
    [void][IO.Directory]::CreateDirectory($appRoot)
    [void][IO.Directory]::CreateDirectory($runnerRoot)

    $sameBytes = [byte[]](1, 4, 9, 16, 25)
    $differentBytes = [byte[]](2, 3, 5, 7, 11)
    $appSame = Join-Path $appRoot 'shared.dll'
    $runnerSame = Join-Path $runnerRoot 'shared.dll'
    $runnerDifferent = Join-Path $runnerRoot 'conflict.dll'
    [IO.File]::WriteAllBytes($appSame, $sameBytes)
    [IO.File]::WriteAllBytes($runnerSame, $sameBytes)
    [IO.File]::WriteAllBytes($runnerDifferent, $differentBytes)

    $owners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    Add-ReleasePackageFile -SourceFile $appSame -RelativePath 'shared.dll' -PackageRoot $packageRoot -Owners $owners
    Add-ReleasePackageFile -SourceFile $runnerSame -RelativePath 'SHARED.DLL' -PackageRoot $packageRoot -Owners $owners
    Assert-True ($owners.Count -eq 1) 'Byte-identical case-insensitive collision was not deduplicated.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $packageRoot 'shared.dll'))) -ceq [Convert]::ToBase64String($sameBytes)) 'Deduplicated output bytes changed.'

    $conflictRejected = $false
    try {
        Add-ReleasePackageFile -SourceFile $runnerDifferent -RelativePath 'shared.dll' -PackageRoot $packageRoot -Owners $owners
    }
    catch { $conflictRejected = $true }
    Assert-True $conflictRejected 'Different-byte collision was not rejected.'
    Assert-True ($owners.Count -eq 1) 'Conflict changed the package ownership map.'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $packageRoot 'shared.dll'))) -ceq [Convert]::ToBase64String($sameBytes)) 'Conflict overwrote the first package file.'

    Write-Output 'Release package collision tests: PASS (identical deduplicated; conflict rejected without overwrite).'
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
