function Test-ReleaseFilesByteIdentical {
    param(
        [Parameter(Mandatory = $true)] [string]$Left,
        [Parameter(Mandatory = $true)] [string]$Right
    )

    $leftStream = [IO.File]::OpenRead($Left)
    $rightStream = [IO.File]::OpenRead($Right)
    try {
        if ($leftStream.Length -ne $rightStream.Length) { return $false }
        $leftBuffer = [byte[]]::new(128KB)
        $rightBuffer = [byte[]]::new(128KB)
        while ($true) {
            $leftCount = $leftStream.Read($leftBuffer, 0, $leftBuffer.Length)
            $rightCount = $rightStream.Read($rightBuffer, 0, $rightBuffer.Length)
            if ($leftCount -ne $rightCount) { return $false }
            if ($leftCount -eq 0) { return $true }
            for ($index = 0; $index -lt $leftCount; $index++) {
                if ($leftBuffer[$index] -ne $rightBuffer[$index]) { return $false }
            }
        }
    }
    finally {
        $leftStream.Dispose()
        $rightStream.Dispose()
    }
}

function Add-ReleasePackageFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$SourceFile,
        [Parameter(Mandatory = $true)] [string]$RelativePath,
        [Parameter(Mandatory = $true)] [string]$PackageRoot,
        [Parameter(Mandatory = $true)] [System.Collections.Generic.Dictionary[string, string]]$Owners
    )

    if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) { throw "Publish file does not exist: $SourceFile" }
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains('\') -or $RelativePath.StartsWith('/') -or
        $RelativePath.Contains(':') -or $RelativePath.Split('/') -contains '..' -or $RelativePath.Split('/') -contains '.') {
        throw "Publish file has an unsafe relative path: $RelativePath"
    }

    $destination = Join-Path $PackageRoot ($RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if ($Owners.ContainsKey($RelativePath)) {
        if (-not (Test-ReleaseFilesByteIdentical $Owners[$RelativePath] $SourceFile)) {
            throw "Conflicting publish outputs share package path '$RelativePath'."
        }
        return
    }

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($SourceFile, $destination, $false)
    $Owners.Add($RelativePath, $destination)
}

Export-ModuleMember -Function Add-ReleasePackageFile, Test-ReleaseFilesByteIdentical
