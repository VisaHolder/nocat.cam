# Renames OBS's virtual camera to "nocat.cam" (what apps like Discord list) and swaps its
# "no picture" image for the nocat card. Needs OBS Studio installed. Run as admin.
# Undo with: rename-vcam.ps1 -Undo
param([switch]$Undo)
$ErrorActionPreference = 'Stop'
$name = if ($Undo) { 'OBS Virtual Camera' } else { 'nocat.cam' }
$id = '{A3FCE0F5-3493-419F-958A-ABA1250EC20B}'
foreach ($root in 'HKLM:\SOFTWARE\Classes', 'HKLM:\SOFTWARE\WOW6432Node\Classes') {
    $inst = "$root\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\$id"
    if (-not (Test-Path $inst)) { throw "OBS's virtual camera isn't installed (install OBS Studio first)" }
    Set-ItemProperty $inst -Name FriendlyName -Value $name
    Set-ItemProperty "$root\CLSID\$id" -Name '(default)' -Value $name
}
$dir = 'C:\Program Files\obs-studio\data\obs-plugins\win-dshow'
$backup = "$dir\placeholder.obs-original.png"
$card = Join-Path $PSScriptRoot '..\assets\card_idle.png'
if (-not (Test-Path $card)) { $card = Join-Path $PSScriptRoot 'assets\card_idle.png' }
if ($Undo) {
    if (Test-Path $backup) { Copy-Item $backup "$dir\placeholder.png" -Force }
} elseif (Test-Path $dir) {
    if (-not (Test-Path $backup)) { Copy-Item "$dir\placeholder.png" $backup }
    Copy-Item $card "$dir\placeholder.png" -Force
}
"done: the virtual camera is now called '$name' (restart Discord to see it)"
