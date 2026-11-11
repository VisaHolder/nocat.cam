# Downloads libjpeg-turbo's turbojpeg.dll (BSD/IJG licence) from the official GitHub release into native\.
# nocat.cam uses it to decode the camera's MJPEG pictures fast (and straight to 1080p from 4K).
param([string]$Version = "3.2.1")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "native\turbojpeg.dll"
if (Test-Path $dest) { "already there: $dest"; return }

$name = "libjpeg-turbo-$Version-vc-x64.exe"
$url = "https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/$Version/$name"
$tmp = Join-Path $env:TEMP $name
"downloading $name ..."
Invoke-WebRequest $url -OutFile $tmp
$sig = Get-AuthenticodeSignature $tmp
if ($sig.Status -ne "Valid") { throw "the installer's signature isn't valid ($($sig.Status))" }

# it's an NSIS installer; 7-Zip can open it without running it
$seven = "C:\Program Files\7-Zip\7z.exe"
if (-not (Test-Path $seven)) { throw "needs 7-Zip to unpack the installer" }
$out = Join-Path $env:TEMP "ljt-$Version"
& $seven x $tmp "-o$out" -y | Out-Null
$dll = Get-ChildItem $out -Recurse -Filter turbojpeg.dll | Where-Object { $_.FullName -match '\\bin\\' } | Select-Object -First 1
if (-not $dll) { throw "turbojpeg.dll not found in the installer" }
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
Copy-Item $dll.FullName $dest
Remove-Item $tmp, $out -Recurse -Force
"saved $dest (signed by: $($sig.SignerCertificate.Subject))"
