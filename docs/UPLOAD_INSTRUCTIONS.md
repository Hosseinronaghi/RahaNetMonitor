# Upload the v0.6.0 RC1 source into this repository

This repository has been initialized. The full source archive is available as **RahaNetMonitor-v0.6.0-rc1-source.zip** from the ChatGPT project conversation, but it has **not** been uploaded here yet.

## PowerShell (Windows)

Download the ZIP from the conversation to your Downloads folder, then:

```powershell
git clone https://github.com/Hosseinronaghi/RahaNetMonitor.git
$root = Join-Path $PWD "RahaNetMonitor"
$archive = Join-Path $HOME "Downloads/RahaNetMonitor-v0.6.0-rc1-source.zip"
$temp = Join-Path $env:TEMP "raha-net-monitor-source"
New-Item -ItemType Directory -Force $temp | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $temp -Force
Copy-Item -Path (Join-Path $temp "RahaNetMonitor/*") -Destination $root -Recurse -Force
Set-Location $root
git add .
git commit -m "chore: import v0.6.0 RC1 Windows and Android source"
git push origin main
```

Use authenticated GitHub credentials when Git asks. Review the diff before pushing.

This is **source code only**, not a stable release; successful APK/EXE builds and on-device tests have not been confirmed.
