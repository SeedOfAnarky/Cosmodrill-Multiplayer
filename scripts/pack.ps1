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
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $stage 'THIRD_PARTY_NOTICES.md')

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256
