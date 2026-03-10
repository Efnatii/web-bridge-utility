[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$KompasKitConfigPath = "",
    [string]$UtilityExePath = "",
    [string]$UtilityArguments = "",
    [string]$DisplayName = "WebBridge Utility",
    [string]$IconPath = "",
    [switch]$SkipBuild,
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

function Publish-Utility {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath
    )

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "dotnet CLI was not found. Build the utility manually or pass -UtilityExePath."
    }

    $projectPath = Join-Path $RepoRootPath "src\WebBridge.Utility\WebBridge.Utility.csproj"
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Project file was not found: $projectPath"
    }

    $publishDir = Join-Path $RepoRootPath "artifacts\publish\utility\win-x64"
    if (-not (Test-Path -LiteralPath $publishDir)) {
        New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
    }

    Write-Host "Publishing WebBridge.Utility to $publishDir" -ForegroundColor Cyan
    & $dotnet.Source publish $projectPath `
        -c Release `
        -r win-x64 `
        -p:PublishSingleFile=true `
        -p:SelfContained=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedExe = Join-Path $publishDir "WebBridge.Utility.exe"
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "Published EXE was not found after dotnet publish: $publishedExe"
    }

    return (Resolve-Path -LiteralPath $publishedExe).Path
}

function Resolve-UtilityExePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [string]$ExplicitPath,
        [switch]$SkipPublish
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Utility EXE was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $candidates = @(
        (Join-Path $RepoRootPath "artifacts\publish\utility\win-x64\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Release\net8.0\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Release\net8.0-windows\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Debug\net8.0\win-x64\publish\WebBridge.Utility.exe"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin\Debug\net8.0-windows\win-x64\publish\WebBridge.Utility.exe")
    )

    $resolved = Resolve-ExistingPath -Candidates $candidates
    if ($null -ne $resolved) {
        return $resolved
    }

    $searchRoots = @(
        (Join-Path $RepoRootPath "artifacts"),
        (Join-Path $RepoRootPath "src\WebBridge.Utility\bin")
    )

    foreach ($searchRoot in $searchRoots) {
        if (-not (Test-Path -LiteralPath $searchRoot)) {
            continue
        }

        $found = Get-ChildItem -LiteralPath $searchRoot -Recurse -Filter WebBridge.Utility.exe -File -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1
        if ($null -ne $found) {
            return $found.FullName
        }
    }

    if ($SkipPublish) {
        throw "WebBridge.Utility.exe was not found. Build it first or rerun without -SkipBuild."
    }

    return Publish-Utility -RepoRootPath $RepoRootPath
}

function Resolve-DefaultArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [string]$ExplicitArguments
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitArguments)) {
        return $ExplicitArguments
    }

    $defaultConfig = Join-Path $RepoRootPath "configs\config.production.sample.json"
    if (Test-Path -LiteralPath $defaultConfig -PathType Leaf) {
        return ('--config "' + (Resolve-Path -LiteralPath $defaultConfig).Path + '"')
    }

    return ""
}

function Resolve-IconPathValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Icon file was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $defaultIcon = Join-Path $RepoRootPath "src\WebBridge.Utility\Assets\utility-icon.ico"
    if (Test-Path -LiteralPath $defaultIcon -PathType Leaf) {
        return (Resolve-Path -LiteralPath $defaultIcon).Path
    }

    return ""
}

function Set-XmlAttributeValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlElement]$Element,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        $Value = ""
    }

    $hasAttribute = $Element.HasAttribute($Name)
    $currentValue = $Element.GetAttribute($Name)
    if ($hasAttribute -and $currentValue -eq $Value) {
        return $false
    }

    $Element.SetAttribute($Name, $Value)
    return $true
}

function Update-KompasUtilityBinding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$KitConfigPath,
        [Parameter(Mandatory = $true)]
        [string]$Program,
        [Parameter(Mandatory = $true)]
        [string]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $descriptionPrefix = "Managed by web-bridge-utility"
    $description = "$descriptionPrefix ($Name)"

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

        if (
            $utilityPath -eq $Program -or
            ($utilityDescription -eq $description) -or
            (
                -not [string]::IsNullOrWhiteSpace($utilityDescription) -and
                $utilityDescription.StartsWith($descriptionPrefix) -and
                $utilityDisplayName -eq $Name
            )
        ) {
            $matchedNodes.Add($utility) | Out-Null
        }
    }

    $existingNode = $null
    if ($matchedNodes.Count -gt 0) {
        $existingNode = $matchedNodes[0]
    }

    $added = 0
    $updated = 0
    $removed = 0
    $changed = $false
    $isNewNode = $false

    if ($null -eq $existingNode) {
        $existingNode = $xml.CreateElement("Utility")
        [void]$group.AppendChild($existingNode)
        $added = 1
        $changed = $true
        $isNewNode = $true
    }

    $entryChanged = $false
    $entryChanged = (Set-XmlAttributeValue -Element $existingNode -Name "path" -Value $Program) -or $entryChanged
    $entryChanged = (Set-XmlAttributeValue -Element $existingNode -Name "displayName" -Value $Name) -or $entryChanged
    $entryChanged = (Set-XmlAttributeValue -Element $existingNode -Name "params" -Value $Arguments) -or $entryChanged
    $entryChanged = (Set-XmlAttributeValue -Element $existingNode -Name "description" -Value $description) -or $entryChanged

    if ((-not $isNewNode) -and $entryChanged) {
        $updated = 1
    }
    if ($entryChanged) {
        $changed = $true
    }

    if ($matchedNodes.Count -gt 1) {
        for ($index = 1; $index -lt $matchedNodes.Count; $index++) {
            [void]$group.RemoveChild($matchedNodes[$index])
            $removed++
            $changed = $true
        }
    }

    if (-not $changed) {
        return [pscustomobject]@{
            Status     = "NO_CHANGES"
            BackupPath = ""
            Added      = 0
            Updated    = 0
            Removed    = 0
        }
    }

    $backupPath = "$KitConfigPath.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $KitConfigPath -Destination $backupPath -Force
    $xml.Save($KitConfigPath)

    return [pscustomobject]@{
        Status     = "UPDATED"
        BackupPath = $backupPath
        Added      = $added
        Updated    = $updated
        Removed    = $removed
    }
}

function Write-BindingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRootPath,
        [Parameter(Mandatory = $true)]
        [string]$Program,
        [Parameter(Mandatory = $true)]
        [string]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Icon,
        [Parameter(Mandatory = $true)]
        [string]$KitConfigPath
    )

    $outDir = Join-Path $RepoRootPath "out"
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -Path $outDir -ItemType Directory -Force | Out-Null
    }

    $bindingFile = Join-Path $outDir "kompas_webbridge_utility_binding.txt"
    $content = @"
KOMPAS-3D utility binding
-------------------------
Program   : $Program
Arguments : $Arguments
Name      : $Name
Icon      : $Icon
Config    : $KitConfigPath

Notes:
- The utility entry was written to KOMPAS.kit.config.
- KOMPAS usually uses the EXE icon automatically for Utility entries.
- If you create a manual external toolbar button, use the Icon path above.
"@

    $content | Set-Content -LiteralPath $bindingFile -Encoding UTF8
    return $bindingFile
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

$resolvedUtilityExePath = Resolve-UtilityExePath -RepoRootPath $resolvedRepoRoot -ExplicitPath $UtilityExePath -SkipPublish:$SkipBuild
$resolvedUtilityArguments = Resolve-DefaultArguments -RepoRootPath $resolvedRepoRoot -ExplicitArguments $UtilityArguments
$resolvedIconPath = Resolve-IconPathValue -RepoRootPath $resolvedRepoRoot -ExplicitPath $IconPath

$updateResult = Update-KompasUtilityBinding `
    -KitConfigPath $resolvedKompasKitConfig `
    -Program $resolvedUtilityExePath `
    -Arguments $resolvedUtilityArguments `
    -Name $DisplayName

$bindingFile = Write-BindingFile `
    -RepoRootPath $resolvedRepoRoot `
    -Program $resolvedUtilityExePath `
    -Arguments $resolvedUtilityArguments `
    -Name $DisplayName `
    -Icon $resolvedIconPath `
    -KitConfigPath $resolvedKompasKitConfig

Write-Host ""
Write-Host "=== WEBBRIDGE UTILITY -> KOMPAS ===" -ForegroundColor Cyan
Write-Host "KOMPAS config : $resolvedKompasKitConfig"
Write-Host "Program       : $resolvedUtilityExePath"
Write-Host "Arguments     : $resolvedUtilityArguments"
Write-Host "Display name  : $DisplayName"
Write-Host "Icon path     : $resolvedIconPath"
Write-Host "Binding file  : $bindingFile"
Write-Host "Update status : $($updateResult.Status)"
if (-not [string]::IsNullOrWhiteSpace($updateResult.BackupPath)) {
    Write-Host "Backup        : $($updateResult.BackupPath)"
}
if ($updateResult.Added -gt 0 -or $updateResult.Updated -gt 0 -or $updateResult.Removed -gt 0) {
    Write-Host "Delta         : +$($updateResult.Added) / ~$($updateResult.Updated) / -$($updateResult.Removed)"
}
Write-Host ""
