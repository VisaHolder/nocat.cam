# Downloads Google's MediaPipe engine (libmediapipe.dll, Apache 2.0) into native\.
# It's 68 MB, so it isn't kept in git. Taken from the official mediapipe wheel on PyPI.
param([string]$Version = "1.2.2")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "native\libmediapipe.dll"
if (Test-Path $dest) { "already there: $dest"; return }

$meta = Invoke-RestMethod "https://pypi.org/pypi/mediapipe/$Version/json"
$wheel = $meta.urls | Where-Object { $_.filename -match "win_amd64\.whl$" } | Select-Object -First 1
if (-not $wheel) { throw "no Windows wheel for mediapipe $Version" }

$tmp = Join-Path $env:TEMP "mediapipe-$Version.zip"
"downloading $($wheel.filename) ..."
Invoke-WebRequest $wheel.url -OutFile $tmp
if ((Get-FileHash $tmp -Algorithm SHA256).Hash -ne $wheel.digests.sha256.ToUpper()) { throw "download checksum mismatch" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($tmp)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq "mediapipe/tasks/c/libmediapipe.dll" }
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
} finally { $zip.Dispose(); Remove-Item $tmp }
"saved $dest"
