@echo off
setlocal

set "PACKAGE_DIR=%~dp0"
set "UTILITY_EXE=%PACKAGE_DIR%WebBridge.Utility.exe"
set "UTILITY_CONFIG=%PACKAGE_DIR%config.production.json"
set "DISPLAY_NAME=WebBridge Utility"

if not exist "%UTILITY_EXE%" (
  echo ERROR: File was not found:
  echo   %UTILITY_EXE%
  exit /b 2
)

if not exist "%UTILITY_CONFIG%" (
  echo ERROR: File was not found:
  echo   %UTILITY_CONFIG%
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$pkg=[IO.Path]::GetFullPath('%PACKAGE_DIR%');" ^
  "$exe=[IO.Path]::GetFullPath('%UTILITY_EXE%');" ^
  "$cfg=[IO.Path]::GetFullPath('%UTILITY_CONFIG%');" ^
  "$name='%DISPLAY_NAME%';" ^
  "$descPrefix='Managed by web-bridge-utility';" ^
  "$desc=$descPrefix+' ('+$name+')';" ^
  "function GetKit { if([string]::IsNullOrWhiteSpace($env:APPDATA)){ throw 'APPDATA is not available.' }; $root=Join-Path $env:APPDATA 'ASCON\KOMPAS-3D'; if(!(Test-Path -LiteralPath $root)){ throw 'KOMPAS profile directory was not found under APPDATA.' }; try { $k=Get-Process -Name KOMPAS -ErrorAction SilentlyContinue | Select-Object -First 1; if($null -ne $k -and -not [string]::IsNullOrWhiteSpace($k.Path)){ $installRoot=Split-Path -Parent (Split-Path -Parent $k.Path); if($installRoot -match 'KOMPAS-3D v(?<ver>\d+(?:\.\d+)?)$'){ $candidate=Join-Path $root ($Matches['ver'] + '\KOMPAS.kit.config'); if(Test-Path -LiteralPath $candidate){ return (Resolve-Path -LiteralPath $candidate).Path } } } } catch {} ; $dirs=Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Sort-Object -Property { try { [version]$_.Name } catch { [version]'0.0' } } -Descending; foreach($dir in $dirs){ $candidate=Join-Path $dir.FullName 'KOMPAS.kit.config'; if(Test-Path -LiteralPath $candidate){ return (Resolve-Path -LiteralPath $candidate).Path } }; throw 'KOMPAS.kit.config was not found.' };" ^
  "$running=@(Get-Process -Name KOMPAS -ErrorAction SilentlyContinue);" ^
  "if($running.Count -gt 0){ throw 'KOMPAS is running. Close it and rerun this BAT file.' };" ^
  "$kit=GetKit;" ^
  "[xml]$xml=Get-Content -LiteralPath $kit -Raw;" ^
  "$group=$xml.SelectSingleNode('/Kit_Config/Groups/Group');" ^
  "if($null -eq $group){ throw ('Group node was not found in: ' + $kit) };" ^
  "$matched=New-Object 'System.Collections.Generic.List[System.Xml.XmlElement]';" ^
  "foreach($u in @($group.SelectNodes('Utility'))){ $up=$u.GetAttribute('path'); $un=$u.GetAttribute('displayName'); $ud=$u.GetAttribute('description'); if($up -eq $exe -or $ud -eq $desc -or ((-not [string]::IsNullOrWhiteSpace($ud)) -and $ud.StartsWith($descPrefix) -and $un -eq $name)){ $matched.Add($u) | Out-Null } };" ^
  "$node=$null; if($matched.Count -gt 0){ $node=$matched[0] };" ^
  "$added=0; $updated=0; $removed=0; $changed=$false; $isNew=$false;" ^
  "if($null -eq $node){ $node=$xml.CreateElement('Utility'); [void]$group.AppendChild($node); $added=1; $changed=$true; $isNew=$true };" ^
  "function SetAttr([System.Xml.XmlElement]$e,[string]$n,[string]$v){ if($null -eq $v){ $v='' }; if($e.HasAttribute($n) -and $e.GetAttribute($n) -eq $v){ return $false }; $e.SetAttribute($n,$v); return $true };" ^
  "$args='--config ' + [char]34 + $cfg + [char]34;" ^
  "$entryChanged=$false;" ^
  "$entryChanged=(SetAttr $node 'path' $exe) -or $entryChanged;" ^
  "$entryChanged=(SetAttr $node 'displayName' $name) -or $entryChanged;" ^
  "$entryChanged=(SetAttr $node 'params' $args) -or $entryChanged;" ^
  "$entryChanged=(SetAttr $node 'description' $desc) -or $entryChanged;" ^
  "if(((-not $isNew)) -and $entryChanged){ $updated=1 };" ^
  "if($entryChanged){ $changed=$true };" ^
  "if($matched.Count -gt 1){ for($i=1; $i -lt $matched.Count; $i++){ [void]$group.RemoveChild($matched[$i]); $removed++; $changed=$true } };" ^
  "$backup='';" ^
  "if($changed){ $backup=$kit + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss'); Copy-Item -LiteralPath $kit -Destination $backup -Force; $xml.Save($kit) };" ^
  "Write-Host '';" ^
  "Write-Host '=== WEBBRIDGE UTILITY -> KOMPAS ===' -ForegroundColor Cyan;" ^
  "Write-Host ('Package dir  : ' + $pkg);" ^
  "Write-Host ('KOMPAS config: ' + $kit);" ^
  "Write-Host ('Program      : ' + $exe);" ^
  "Write-Host ('Arguments    : ' + $args);" ^
  "Write-Host ('Display name : ' + $name);" ^
  "if([string]::IsNullOrWhiteSpace($backup)){ Write-Host 'Backup       : not needed (no changes)' } else { Write-Host ('Backup       : ' + $backup) };" ^
  "Write-Host ('Delta        : +' + $added + ' / ~' + $updated + ' / -' + $removed);" ^
  "Write-Host 'Icon         : KOMPAS should use the icon embedded in WebBridge.Utility.exe';" ^
  "Write-Host '';"

set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
  echo.
  echo Binding failed with exit code %EXIT_CODE%.
)

exit /b %EXIT_CODE%
