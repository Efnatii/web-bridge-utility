[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$KompasKitConfigPath = "",
    [string]$UtilityExePath = "",
    [string]$DisplayName = "WebBridge Utility",
    [switch]$RemoveAllManaged,
    [switch]$WaitForKompasExit,
    [int]$WaitTimeoutSec = 600
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

function Resolve-KompasKitConfig {
    param(
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "KOMPAS kit config was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return $null
    }

    $kompasRoot = Join-Path $env:APPDATA "ASCON\KOMPAS-3D"
    if (-not (Test-Path -LiteralPath $kompasRoot)) {
        return $null
    }

    try {
        $runningKompas = Get-Process -Name KOMPAS -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $runningKompas -and -not [string]::IsNullOrWhiteSpace($runningKompas.Path)) {
            $installRoot = Split-Path -Parent (Split-Path -Parent $runningKompas.Path)
            if ($installRoot -match "KOMPAS-3D v(?<ver>\d+(?:\.\d+)?)$") {
                $candidate = Join-Path $kompasRoot "$($Matches['ver'])\KOMPAS.kit.config"
                if (Test-Path -LiteralPath $candidate) {
                    return $candidate
                }
            }
        }
    }
    catch {
    }

    $versionDirs = Get-ChildItem -LiteralPath $kompasRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object -Property {
            try {
                [version]$_.Name
            }
            catch {
                [version]"0.0"
            }
        } -Descending

    foreach ($dir in $versionDirs) {
        $candidate = Join-Path $dir.FullName "KOMPAS.kit.config"
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Wait-ForKompasProcessExit {
    param(
        [int]$TimeoutSec = 600
    )

    if ($TimeoutSec -lt 0) {
        $TimeoutSec = 0
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $running = @(Get-Process -Name KOMPAS -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) {
            return $true
        }

        if ($TimeoutSec -eq 0) {
            return $false
        }

        if ((Get-Date) -ge $deadline) {
            return $false
        }

        Write-Host "KOMPAS is running. Waiting for it to exit..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Resolve-UtilityMatchPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (Test-Path -LiteralPath $ExplicitPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $ExplicitPath).Path
        }

        return $ExplicitPath
    }

    $candidates = @(
        (Join-Path $RepoRootPath "artifacts\publish\utility\win-x64\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Release\net8.0\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Release\net8.0-windows\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Debug\net8.0\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Debug\net8.0-windows\win-x64\publish\WebBridge.Utility.exe")
    )

    return Resolve-ExistingPath -Candidates $candidates
}

function Remove-KompasUtilityBinding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$KitConfigPath,
        [string]$Program,
        [string]$Name,
        [switch]$RemoveAllManagedEntries
    )

    $descriptionPrefix = "Managed by web-bridge-utility"
    $managedDescription = if ([string]::IsNullOrWhiteSpace($Name)) {
        ""
    }
    else {
        "$descriptionPrefix ($Name)"
    }

    [xml]$xml = Get-Content -LiteralPath $KitConfigPath -Raw
    $group = $xml.SelectSingleNode("/Kit_Config/Groups/Group")
    if ($null -eq $group) {
        throw "Group node was not found in: $KitConfigPath"
    }

    $matchedNodes = New-Object System.Collections.Generic.List[System.Xml.XmlElement]
    foreach ($utility in @($group.SelectNodes("Utility"))) {
        $utilityPath = $utility.GetAttribute("path")
        $utilityDisplayName = $utility.GetAttribute("displayName")
        $utilityDescription = $utility.GetAttribute("description")
        $isManagedNode = -not [string]::IsNullOrWhiteSpace($utilityDescription) -and
            $utilityDescription.StartsWith($descriptionPrefix)

        $matches = $false
        if ($RemoveAllManagedEntries -and $isManagedNode) {
            $matches = $true
        }
        elseif (-not [string]::IsNullOrWhiteSpace($Program) -and $utilityPath -eq $Program) {
            $matches = $true
        }
        elseif (-not [string]::IsNullOrWhiteSpace($managedDescription) -and $utilityDescription -eq $managedDescription) {
            $matches = $true
        }
        elseif (-not [string]::IsNullOrWhiteSpace($Name) -and $isManagedNode -and $utilityDisplayName -eq $Name) {
            $matches = $true
        }

        if ($matches) {
            $matchedNodes.Add($utility) | Out-Null
        }
    }

    if ($matchedNodes.Count -eq 0) {
        return [pscustomobject]@{
            Status        = "NO_MATCH"
            BackupPath    = ""
            RemovedCount  = 0
            RemovedTitles = @()
        }
    }

    $removedTitles = @()
    foreach ($node in $matchedNodes) {
        $removedTitles += $node.GetAttribute("displayName")
    }

    $backupPath = "$KitConfigPath.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $KitConfigPath -Destination $backupPath -Force

    foreach ($node in $matchedNodes) {
        [void]$group.RemoveChild($node)
    }

    $xml.Save($KitConfigPath)

    return [pscustomobject]@{
        Status        = "UPDATED"
        BackupPath    = $backupPath
        RemovedCount  = $matchedNodes.Count
        RemovedTitles = $removedTitles
    }
}

function Write-UnbindReport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [Parameter(Mandatory = $true)]
        [string]$KitConfigPath,
        [Parameter(Mandatory = $true)]
        [string]$Program,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Result
    )

    $outDir = Join-Path $RepoRootPath "out"
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -Path $outDir -ItemType Directory -Force | Out-Null
    }

    $reportPath = Join-Path $outDir "kompas_webbridge_utility_unbind.txt"
    $content = @"
KOMPAS-3D utility unbind
------------------------
Config         : $KitConfigPath
Program match  : $Program
Display name   : $Name
Status         : $($Result.Status)
Removed count  : $($Result.RemovedCount)
Removed titles : $($Result.RemovedTitles -join ', ')
Backup         : $($Result.BackupPath)
"@

    $content | Set-Content -LiteralPath $reportPath -Encoding UTF8
    return $reportPath
}

$resolvedRepoRoot = Resolve-RepoRoot -Path $RepoRoot
$resolvedKompasKitConfig = Resolve-KompasKitConfig -ExplicitPath $KompasKitConfigPath
if ([string]::IsNullOrWhiteSpace($resolvedKompasKitConfig)) {
    throw "KOMPAS.kit.config was not found under APPDATA. Start KOMPAS once or pass -KompasKitConfigPath."
}

$runningKompas = @(Get-Process -Name KOMPAS -ErrorAction SilentlyContinue)
if ($runningKompas.Count -gt 0) {
    if ($WaitForKompasExit) {
        $closed = Wait-ForKompasProcessExit -TimeoutSec $WaitTimeoutSec
        if (-not $closed) {
            throw "KOMPAS is still running after waiting $WaitTimeoutSec seconds."
        }
    }
    else {
        throw "KOMPAS is running. Close it or rerun with -WaitForKompasExit."
    }
}

$resolvedUtilityMatchPath = Resolve-UtilityMatchPath -RepoRootPath $resolvedRepoRoot -ExplicitPath $UtilityExePath
$removeResult = Remove-KompasUtilityBinding `
    -KitConfigPath $resolvedKompasKitConfig `
    -Program $resolvedUtilityMatchPath `
    -Name $DisplayName `
    -RemoveAllManagedEntries:$RemoveAllManaged

$reportPath = Write-UnbindReport `
    -RepoRootPath $resolvedRepoRoot `
    -KitConfigPath $resolvedKompasKitConfig `
    -Program $resolvedUtilityMatchPath `
    -Name $DisplayName `
    -Result $removeResult

Write-Host ""
Write-Host "=== WEBBRIDGE UTILITY UNBIND ===" -ForegroundColor Cyan
Write-Host "KOMPAS config : $resolvedKompasKitConfig"
Write-Host "Program match : $resolvedUtilityMatchPath"
Write-Host "Display name  : $DisplayName"
Write-Host "Report        : $reportPath"
Write-Host "Update status : $($removeResult.Status)"
if (-not [string]::IsNullOrWhiteSpace($removeResult.BackupPath)) {
    Write-Host "Backup        : $($removeResult.BackupPath)"
}
if ($removeResult.RemovedCount -gt 0) {
    Write-Host "Removed count : $($removeResult.RemovedCount)"
    Write-Host "Removed titles: $($removeResult.RemovedTitles -join ', ')"
}
Write-Host ""
