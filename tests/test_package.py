"""Offline integrity checks. These are NOT compilation or device tests."""
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
class SourceIntegrity(unittest.TestCase):
    def read(self, path): return (ROOT / path).read_text(encoding="utf-8")
    def test_android_manifest_is_xml_and_package_local(self):
        doc = ET.parse(ROOT / "android/app/src/main/AndroidManifest.xml")
        ns = "{http://schemas.android.com/apk/res/android}"
        app = doc.getroot().find("application")
        self.assertEqual(app.attrib[ns+"allowBackup"], "false")
        self.assertEqual(app.attrib[ns+"usesCleartextTraffic"], "false")
    def test_android_stable_identity(self):
        gradle = self.read("android/app/build.gradle.kts")
        self.assertIn('applicationId = "ir.raha.netmonitor"', gradle)
        self.assertIn('versionName = "0.6.0-rc1"', gradle)
        self.assertIn('versionCode = 6', gradle)
    def test_history_retains_database_schema(self):
        src = self.read("android/app/src/main/java/ir/raha/netmonitor/History.kt")
        self.assertIn('"raha-traffic.db", null, 1', src)
        self.assertIn('CREATE TABLE snapshots', src)
        self.assertIn('maxOf(capturedAt, latest + 1L)', src)
        self.assertIn('db.beginTransaction()', src)
    def test_no_unreviewed_network_permissions(self):
        xml = self.read("android/app/src/main/AndroidManifest.xml")
        self.assertNotIn('android.permission.INTERNET', xml)
        self.assertNotIn('QUERY_ALL_PACKAGES', xml)
        self.assertNotIn('BIND_VPN_SERVICE', xml)
    def test_windows_safe_window_capture(self):
        src = self.read("windows/RahaNetMonitor/ConnectionsWindow.cs")
        self.assertIn('64 * 1024 * 1024', src)
        self.assertIn('Invalid TCP table size', src)
        self.assertIn('"=+-@".Contains', src)
    def test_utc_and_local_rollover(self):
        src = self.read("windows/RahaNetMonitor/Program.cs")
        self.assertIn('FlushPendingHistory();', src)
        self.assertIn('_pendingLocalDay', src)
        self.assertIn('TimeZoneInfo.Local.GetUtcOffset', src)
    def test_ci_has_build_and_lint(self):
        src = self.read(".github/workflows/build.yml")
        for term in ('dotnet publish', 'assembleDebug', 'lintDebug', 'source-checks'):
            self.assertIn(term, src)
if __name__ == "__main__": unittest.main()
