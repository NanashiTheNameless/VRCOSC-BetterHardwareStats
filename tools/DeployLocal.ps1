param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [string]$Destination = (Join-Path $env:APPDATA 'VRCOSC/packages/local')
)
$ErrorActionPreference = 'Stop'

function Get-Digest([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$bundleRoot = Join-Path $destinationRoot 'lhm'
$backupRoot = Join-Path $bundleRoot 'root-backups'
$modules = @('BetterHardwareStats.Module.dll', 'BetterHardwareStats.Core.dll', 'BetterHardwareStats.Windows.dll')
$dependencies = @(
    'LibreHardwareMonitorLib.dll', 'HidSharp.dll', 'DiskInfoToolkit.dll', 'RAMSPDToolkit-NDD.dll',
    'BlackSharp.Core.dll', 'Mono.Posix.NETStandard.dll', 'MonoPosixHelper.dll', 'libMonoPosixHelper.dll',
    'System.Management.dll', 'System.IO.Ports.dll'
)

# Validate the complete build before copying any files into the package.
foreach ($name in $modules) {
    if (!(Test-Path -LiteralPath (Join-Path $buildRoot $name) -PathType Leaf)) { throw "Missing module assembly: $name" }
}
foreach ($name in $dependencies) {
    if (!(Test-Path -LiteralPath (Join-Path $buildRoot "lhm/$name") -PathType Leaf)) { throw "Missing sensor dependency: $name" }
}
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null
foreach ($name in $dependencies) {
    Copy-Item -LiteralPath (Join-Path $buildRoot "lhm/$name") -Destination (Join-Path $bundleRoot $name) -Force
}
foreach ($name in $modules) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination (Join-Path $destinationRoot $name) -Force
}

# Root DLLs are imported as managed assemblies. Keep matching sensor copies in the private bundle.
foreach ($name in $dependencies) {
    $rootFile = [IO.Path]::GetFullPath((Join-Path $destinationRoot $name))
    if (![string]::Equals([IO.Path]::GetDirectoryName($rootFile), $destinationRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Dependency path leaves the package directory: $rootFile"
    }
    if (!(Test-Path -LiteralPath $rootFile -PathType Leaf)) { continue }
    $rootHash = Get-Digest $rootFile
    $bundleHash = Get-Digest (Join-Path $bundleRoot $name)
    if ($rootHash -ne $bundleHash) { Write-Warning "Keeping a different root dependency: $name"; continue }
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backupFile = [IO.Path]::GetFullPath((Join-Path $backupRoot "$rootHash-$name"))
    if (!$backupFile.StartsWith($destinationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup path leaves the package directory: $backupFile"
    }
    Move-Item -LiteralPath $rootFile -Destination $backupFile -Force
    Write-Output "Moved duplicate root dependency into lhm/root-backups: $name"
}
