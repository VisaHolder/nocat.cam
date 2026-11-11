# Fetches OBS Studio's virtual camera module (obs-virtualcam-module32/64.dll, GPL-2.0, unmodified) into
# native\obs-vcam\. nocat.cam ships these two files so OBS doesn't have to be installed; they only need
# Windows' own DLLs. Uses an installed OBS if there is one, otherwise the official OBS release zip.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\native\obs-vcam'
New-Item -ItemType Directory -Force $out | Out-Null
$names = 'obs-virtualcam-module64.dll', 'obs-virtualcam-module32.dll'
$installed = 'C:\Program Files\obs-studio\data\obs-plugins\win-dshow'
if ((Test-Path "$installed\$($names[0])") -and (Test-Path "$installed\$($names[1])")) {
    foreach ($n in $names) { Copy-Item "$installed\$n" $out -Force }
    "copied from $installed"
    return
}
$rel = Invoke-RestMethod 'https://api.github.com/repos/obsproject/obs-studio/releases/latest' -Headers @{ 'User-Agent' = 'nocat.cam' }
$asset = $rel.assets | Where-Object { $_.name -match 'Windows-x64\.zip$' } | Select-Object -First 1
if (-not $asset) { throw "no Windows x64 zip in OBS $($rel.tag_name)" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) "obs-$($rel.tag_name)"
$zip = "$tmp.zip"
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($n in $names) {
        $e = $z.Entries | Where-Object { $_.FullName -like "*win-dshow/$n" } | Select-Object -First 1
        if (-not $e) { throw "$n not found in $($asset.name)" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $out $n), $true)
    }
} finally { $z.Dispose(); Remove-Item $zip -Force }
"extracted from OBS $($rel.tag_name)"
