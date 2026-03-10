[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$DotnetExePath = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Repo root was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-UtilityVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath
    )

    $json = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $json.Versions -or [string]::IsNullOrWhiteSpace($json.Versions.UtilityVersion)) {
        return "0.0.0"
    }

    return [string]$json.Versions.UtilityVersion
}

function Resolve-DotnetExe {
    param(
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "dotnet executable was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "dotnet CLI was not found."
    }

    return $dotnet.Source
}

function Assert-DotnetSdkAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DotnetPath
    )

    $sdks = & $DotnetPath --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $sdks -or @($sdks).Count -eq 0) {
        throw "dotnet SDK was not found for $DotnetPath. Install an SDK or pass -DotnetExePath."
    }
}

function Publish-Utility {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [Parameter(Mandatory = $true)]
        [string]$RuntimeId,
        [Parameter(Mandatory = $true)]
        [string]$DotnetPath
    )

    $projectPath = Join-Path $RepoRootPath "src\WebBridge.Utility\WebBridge.Utility.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Project file was not found: $projectPath"
    }

    $publishDir = Join-Path $RepoRootPath ("artifacts\publish\utility\" + $RuntimeId)
    if (-not (Test-Path -LiteralPath $publishDir)) {
        New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
    }

    Assert-DotnetSdkAvailable -DotnetPath $DotnetPath
    Write-Host "Publishing WebBridge.Utility for $RuntimeId" -ForegroundColor Cyan
    & $DotnetPath publish $projectPath `
        -c Release `
        -r $RuntimeId `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $exePath = Join-Path $publishDir "WebBridge.Utility.exe"
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Published EXE was not found: $exePath"
    }

    return (Resolve-Path -LiteralPath $exePath).Path
}

function Resolve-PublishedExe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [Parameter(Mandatory = $true)]
        [string]$RuntimeId,
        [Parameter(Mandatory = $true)]
        [string]$DotnetPath,
        [switch]$SkipBuild
    )

    $candidates = @(
        (Join-Path $RepoRootPath ("artifacts\publish\utility\" + $RuntimeId + "\WebBridge.Utility.exe")),
        (Join-Path $RepoRootPath ("src\WebBridge.Utility\bin\Release\net8.0\" + $RuntimeId + "\publish\WebBridge.Utility.exe")),
        (Join-Path $RepoRootPath ("src\WebBridge.Utility\bin\Release\net8.0-windows\" + $RuntimeId + "\publish\WebBridge.Utility.exe"))
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    if ($SkipBuild) {
        throw "Published EXE was not found. Rerun without -SkipPublish or publish it manually."
    }

    return Publish-Utility -RepoRootPath $RepoRootPath -RuntimeId $RuntimeId -DotnetPath $DotnetPath
}

$resolvedRepoRoot = Resolve-RepoRoot -Path $RepoRoot
$sourceConfig = Join-Path $resolvedRepoRoot "configs\config.production.sample.json"
$sourceBindBat = Join-Path $resolvedRepoRoot "scripts\bind_release_package_to_kompas3d.bat"
$resolvedDotnetExe = Resolve-DotnetExe -ExplicitPath $DotnetExePath

if (-not (Test-Path -LiteralPath $sourceConfig -PathType Leaf)) {
    throw "Production config source was not found: $sourceConfig"
}

if (-not (Test-Path -LiteralPath $sourceBindBat -PathType Leaf)) {
    throw "Release bind BAT was not found: $sourceBindBat"
}

$utilityVersion = Get-UtilityVersion -ConfigPath $sourceConfig
$publishedExe = Resolve-PublishedExe -RepoRootPath $resolvedRepoRoot -RuntimeId $Runtime -DotnetPath $resolvedDotnetExe -SkipBuild:$SkipPublish

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $resolvedRepoRoot "artifacts\release"
}

if (-not (Test-Path -LiteralPath $OutputRoot)) {
    New-Item -Path $OutputRoot -ItemType Directory -Force | Out-Null
}

$packageName = "web-bridge-utility-$utilityVersion-$Runtime"
$packageDir = Join-Path $OutputRoot $packageName
$archivePath = Join-Path $OutputRoot ($packageName + ".zip")

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

New-Item -Path $packageDir -ItemType Directory -Force | Out-Null

$releaseExePath = Join-Path $packageDir "WebBridge.Utility.exe"
$releaseConfigPath = Join-Path $packageDir "config.production.json"
$releaseBatPath = Join-Path $packageDir "bind_utility_to_kompas3d.bat"

Copy-Item -LiteralPath $publishedExe -Destination $releaseExePath -Force
Copy-Item -LiteralPath $sourceConfig -Destination $releaseConfigPath -Force
Copy-Item -LiteralPath $sourceBindBat -Destination $releaseBatPath -Force

Compress-Archive -LiteralPath $releaseExePath, $releaseConfigPath, $releaseBatPath -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host ""
Write-Host "=== RELEASE PACKAGE READY ===" -ForegroundColor Cyan
Write-Host "Version     : $utilityVersion"
Write-Host "Runtime     : $Runtime"
Write-Host "Package dir : $packageDir"
Write-Host "Archive     : $archivePath"
Write-Host ""
Write-Host "Archive contents:" -ForegroundColor Cyan
Write-Host "  WebBridge.Utility.exe"
Write-Host "  config.production.json"
Write-Host "  bind_utility_to_kompas3d.bat"
Write-Host ""
