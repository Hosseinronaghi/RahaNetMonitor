"""Offline source-pack consistency checks; NOT compilation or runtime tests."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
read = lambda path: (root / path).read_text(encoding="utf-8")
assert 'versionName = "0.6.0-rc1"' in read('android/app/build.gradle.kts')
assert 'versionCode = 6' in read('android/app/build.gradle.kts')
assert '۰.۶.۰' in read('android/app/src/main/java/ir/raha/netmonitor/MainActivity.kt')
assert '۰.۶.۰' in read('windows/RahaNetMonitor/Program.cs')
assert 'class AlertThrottle' in read('android/app/src/main/java/ir/raha/netmonitor/BackgroundMonitor.kt') or 'object AlertThrottle' in read('android/app/src/main/java/ir/raha/netmonitor/BackgroundMonitor.kt')
assert 'TotalRxDelta = _pendingRx' in read('windows/RahaNetMonitor/Program.cs')
assert 'ConnectionKey' in read('windows/RahaNetMonitor/ConnectionsWindow.cs')
assert 'firstOrNull()' in read('android/app/src/main/java/ir/raha/netmonitor/History.kt')
assert 'android' in read('.github/workflows/build.yml')
assert 'windows' in read('.github/workflows/build.yml')
assert (root / 'README.md').exists()
print('PASS: static release consistency checks (not build/runtime tests)')

assert 'FlushPendingHistory()' in read('windows/RahaNetMonitor/Program.cs')
assert 'Unexpected TCP table allocation size' in read('windows/RahaNetMonitor/ConnectionsWindow.cs')
assert 'maxOf(capturedAt, latest + 1L)' in read('android/app/src/main/java/ir/raha/netmonitor/History.kt')
