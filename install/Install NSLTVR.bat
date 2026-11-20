@echo off
rem NSLTVR installer (New Super Lucky's Tale VR). Double click it, or use: "Install NSLTVR.bat" install   or   "Install NSLTVR.bat" uninstall
rem It only touches the game folder. No game files are changed, and uninstall removes what it added.
setlocal
set "NSLTVR_HERE=%~dp0"
set "NSLTVR_ARG=%~1"
set "NSLTVR_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = Get-Content -LiteralPath $env:NSLTVR_SELF -Raw; $s = $t.Substring($t.IndexOf('#PS' + 'START') + 8); Invoke-Expression $s"
set "NSLTVR_RESULT=%ERRORLEVEL%"
if "%NSLTVR_ARG%"=="" pause
exit /b %NSLTVR_RESULT%

#PSSTART
$ErrorActionPreference = 'Stop'
$here = $env:NSLTVR_HERE
$files = Join-Path $here 'files'
$arg = $env:NSLTVR_ARG

function Find-Game {
    $roots = New-Object System.Collections.ArrayList
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            [void]$roots.Add($steam)
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    [void]$roots.Add(($m.Groups[1].Value -replace '\\\\', '\'))
                }
            }
        }
    } catch { }
    foreach ($r in $roots) {
        $g = Join-Path $r 'steamapps\common\New Super Luckys Tale'
        if (Test-Path -LiteralPath (Join-Path $g 'nslt.exe')) { return $g }
    }
    return $null
}

function Fail($text) {
    Write-Host ''
    Write-Host "NSLTVR: $text" -ForegroundColor Red
    exit 1
}

function Install-Mod($game) {
    Copy-Item -LiteralPath (Join-Path $files 'winhttp.dll') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files 'doorstop_config.ini') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files '.doorstop_version') -Destination $game -Force
    Copy-Item -LiteralPath (Join-Path $files 'BepInEx') -Destination $game -Recurse -Force

    $need = @('winhttp.dll', 'BepInEx\core\BepInEx.dll', 'BepInEx\plugins\NSLTVR\NSLTVR.dll', 'BepInEx\plugins\NSLTVR\NSLTVRNative.dll', 'BepInEx\plugins\NSLTVR\openxr_loader.dll')
    foreach ($n in $need) {
        if (-not (Test-Path -LiteralPath (Join-Path $game $n))) { Fail "$n did not copy." }
    }
    # Every copied file must match the zip's copy byte for byte.
    $root = (Get-Item -LiteralPath $files).FullName.TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $files -Recurse -File -Force) {
        $rel = $f.FullName.Substring($root.Length + 1)
        $dst = Join-Path $game $rel
        if (-not (Test-Path -LiteralPath $dst)) { Fail "$rel did not copy." }
        if ((Get-FileHash -LiteralPath $f.FullName).Hash -ne (Get-FileHash -LiteralPath $dst).Hash) { Fail "$rel did not copy correctly. Close anything using the game folder and run this again." }
    }
    Write-Host 'Every file checked.'

    # Files from a downloaded zip carry a "came from the internet" mark, which can stop the DLLs loading.
    Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx') -Recurse -File | Unblock-File
    Unblock-File -LiteralPath (Join-Path $game 'winhttp.dll')
    Write-Host ''
    Write-Host 'NSLTVR is installed.' -ForegroundColor Green
    Write-Host 'Start SteamVR or connect Virtual Desktop, then start New Super Lucky''s Tale from Steam. VR starts by itself.'
    Write-Host 'Hold both sticks down for half a second to open the settings menu, or press Insert on a keyboard.'
    $runtime = $null
    try { $runtime = (Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -ErrorAction Stop).ActiveRuntime } catch { }
    if (-not $runtime) {
        Write-Host ''
        Write-Host 'Warning: no OpenXR runtime is set on this PC.' -ForegroundColor Yellow
        Write-Host 'In SteamVR settings, under OpenXR, set SteamVR as the OpenXR runtime, or use Virtual Desktop with VDXR.'
        Write-Host 'Until then the game runs flat.'
    } else {
        Write-Host "OpenXR runtime: $runtime"
    }
    $sac = $null
    try { $sac = (Get-MpComputerStatus -ErrorAction Stop).SmartAppControlState } catch { }
    if ($sac -and $sac -ne 'Off') {
        Write-Host ''
        Write-Host "Warning: Windows Smart App Control is $sac on this PC." -ForegroundColor Yellow
        Write-Host 'It can block mod DLLs that are not signed, and this mod is not signed. If the game runs flat, check this first. See the README.'
    }
}

function Uninstall-Mod($game) {
    foreach ($n in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
        $p = Join-Path $game $n
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
    $plugin = Join-Path $game 'BepInEx\plugins\NSLTVR'
    if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Recurse -Force }
    $cfg = Join-Path $game 'BepInEx\config\com.scubasteve2365.nsltvr.cfg'
    if (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Force }
    $bep = Join-Path $game 'BepInEx'
    $plugins = Join-Path $bep 'plugins'
    $others = @()
    if (Test-Path -LiteralPath $plugins) { $others = @(Get-ChildItem -LiteralPath $plugins -Force) }
    if ($others.Count -eq 0) {
        if (Test-Path -LiteralPath $bep) { Remove-Item -LiteralPath $bep -Recurse -Force }
    } else {
        Write-Host 'Other BepInEx mods were found, so the BepInEx folder was left in place. They will not load without winhttp.dll.' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'NSLTVR is uninstalled. The game is back to how it was.' -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath (Join-Path $files 'BepInEx'))) { Fail 'the files folder is missing. Unzip everything before running this.' }
if (Get-Process -Name nslt -ErrorAction SilentlyContinue) { Fail 'close New Super Lucky''s Tale first, then run this again.' }

$game = Find-Game
if (-not $game) {
    Write-Host 'Could not find New Super Lucky''s Tale automatically.'
    if ($arg) { Fail 'game folder not found.' }
    $game = Read-Host 'Paste the game folder (the one holding nslt.exe)'
    $game = $game.Trim('"', ' ')
    if (-not (Test-Path -LiteralPath (Join-Path $game 'nslt.exe'))) { Fail 'nslt.exe is not in that folder.' }
}
Write-Host "Game folder: $game"

$choice = $arg
if (-not $choice) {
    Write-Host ''
    Write-Host '  1  Install or update NSLTVR'
    Write-Host '  2  Uninstall NSLTVR'
    $pick = Read-Host 'Type 1 or 2 and press Enter'
    if ($pick -eq '1') { $choice = 'install' }
    elseif ($pick -eq '2') { $choice = 'uninstall' }
    else { Fail 'nothing done.' }
}
if ($choice -eq 'install') { Install-Mod $game }
elseif ($choice -eq 'uninstall') { Uninstall-Mod $game }
else { Fail "unknown choice $choice" }
exit 0
