param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'CosmodrillMultiplayer.csproj'
$assemblyInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'AssemblyInfo.cs') -Raw
$versionMatch = [regex]::Match($assemblyInfo, 'Cosmodrill Multiplayer", "([^"]+)"')
if (!$versionMatch.Success) { throw 'Could not read the mod version from AssemblyInfo.cs.' }
$version = $versionMatch.Groups[1].Value

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$output = Join-Path $projectRoot "bin\$Configuration\net472"
$artifacts = Join-Path $projectRoot 'artifacts'
$stage = Join-Path $artifacts "Cosmodrill-Multiplayer-v$version"
$zip = Join-Path $artifacts "Cosmodrill-Multiplayer-v$version.zip"
$sourceStage = Join-Path $artifacts "Cosmodrill-Multiplayer-v$version-source"
$sourceProject = Join-Path $sourceStage 'Cosmodrill-Multiplayer'
$sourceZip = Join-Path $artifacts "Cosmodrill-Multiplayer-v$version-source.zip"
$checksums = Join-Path $artifacts "Cosmodrill-Multiplayer-v$version-SHA256SUMS.txt"

if (Test-Path -LiteralPath $stage)
{
    $resolvedStage = [System.IO.Path]::GetFullPath($stage)
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifacts) + [System.IO.Path]::DirectorySeparatorChar
    if (!$resolvedStage.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean staging path outside artifacts: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $stage 'Mods'), (Join-Path $stage 'UserLibs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'CosmodrillMultiplayer.dll') -Destination (Join-Path $stage 'Mods\CosmodrillMultiplayer.dll')
Copy-Item -LiteralPath (Join-Path $output 'Open.Nat.dll') -Destination (Join-Path $stage 'UserLibs\Open.Nat.dll')
Copy-Item -LiteralPath (Join-Path $projectRoot 'PACKAGE_README.txt') -Destination (Join-Path $stage 'README.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'RELEASE_NOTES.md') -Destination (Join-Path $stage 'RELEASE_NOTES.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $stage 'THIRD_PARTY_NOTICES.md')

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force

if (Test-Path -LiteralPath $sourceStage)
{
    $resolvedSourceStage = [System.IO.Path]::GetFullPath($sourceStage)
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifacts) + [System.IO.Path]::DirectorySeparatorChar
    if (!$resolvedSourceStage.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean source staging path outside artifacts: $resolvedSourceStage"
    }
    Remove-Item -LiteralPath $sourceStage -Recurse -Force
}

New-Item -ItemType Directory -Path $sourceProject, (Join-Path $sourceProject 'scripts') -Force | Out-Null
$sourceFiles = @(
    '.gitignore',
    'AssemblyInfo.cs',
    'AutoHost.cs',
    'CHANGELOG.md',
    'CosmodrillMultiplayer.csproj',
    'EconomySynchronization.cs',
    'EnemySynchronization.cs',
    'EnemyPositionSynchronization.cs',
    'JoinCode.cs',
    'MinedResourceSynchronization.cs',
    'MultiplayerMod.cs',
    'PACKAGE_README.txt',
    'PlayerSessionSynchronization.cs',
    'README.md',
    'RELEASE_NOTES.md',
    'RemotePlayerAvatar.cs',
    'ReplicationChannel.cs',
    'ResourceDropSynchronization.cs',
    'RescueRewardSynchronization.cs',
    'SavePathResolver.cs',
    'SaveWipeMenu.cs',
    'StationRepairSynchronization.cs',
    'TeammateLocator.cs',
    'THIRD_PARTY_NOTICES.md',
    'WorldDeviceSynchronization.cs',
    'WorldSynchronization.cs'
)
foreach ($file in $sourceFiles)
{
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $sourceProject $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pack.ps1') -Destination (Join-Path $sourceProject 'scripts\pack.ps1')
Compress-Archive -Path $sourceProject -DestinationPath $sourceZip -CompressionLevel Optimal -Force

$releaseFiles = @($zip, $sourceZip)
$checksumLines = foreach ($file in $releaseFiles)
{
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    $hash.Hash + '  ' + (Split-Path -Leaf $file)
}
[System.IO.File]::WriteAllLines($checksums, $checksumLines)

Get-FileHash -LiteralPath $zip, $sourceZip, $checksums -Algorithm SHA256
