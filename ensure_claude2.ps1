# Claude2-Heal — relaunch the dashboard if it died (runs every 15 min via schtask).
# Opt out by creating a file named ".no-heal" in this folder.
$dir = "C:\trading_system\MNQ-Signal-System"
if (Test-Path (Join-Path $dir ".no-heal")) { exit 0 }
$listening = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if ($listening) { exit 0 }
$env:CLAUDE2_AUTO_ENGINE = '1'
Start-Process -FilePath "C:\Python314\python.exe" -ArgumentList "-X","utf8","claude2_app.py" -WorkingDirectory $dir -WindowStyle Hidden
