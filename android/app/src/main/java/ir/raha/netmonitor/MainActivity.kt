package ir.raha.netmonitor

import android.content.Intent
import android.Manifest
import android.os.Build
import android.content.pm.PackageManager
import androidx.activity.result.contract.ActivityResultContracts
import java.text.DateFormat
import java.util.Date
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.lifecycle.lifecycleScope
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.unit.LayoutDirection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.DecimalFormat

private val bg = Color(0xFF121722)
private val surface = Color(0xFF1E2634)
private val mint = Color(0xFF62D9B0)
private fun bytes(n: Long): String {
    var v = n.toDouble(); val u = arrayOf("B", "KB", "MB", "GB", "TB"); var i = 0
    while (v >= 1024 && i < u.size-1) { v /= 1024; i++ }
    return "${DecimalFormat("0.##").format(v)} ${u[i]}"
}

class MainActivity: ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val stats = Stats(this)
        val history = History(this)
        val exporter = registerForActivityResult(ActivityResultContracts.CreateDocument("text/csv")) { uri ->
            if (uri != null) {
                lifecycleScope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            contentResolver.openOutputStream(uri)?.bufferedWriter(Charsets.UTF_8)?.use { writer ->
                                writer.write("\uFEFF")
                                writer.write(history.csv())
                            } ?: throw java.io.IOException("خروجی قابل نوشتن نیست")
                        }
                    } catch (e: Exception) {
                        android.widget.Toast.makeText(this@MainActivity, "خطا در ذخیرهٔ گزارش: ${e.localizedMessage}", android.widget.Toast.LENGTH_LONG).show()
                    }
                }
            }
        }
        val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* notification delivery depends on user approval */ }
        setContent {
            var allowed by remember { mutableStateOf(stats.hasAccess()) }
            var periodic by remember { mutableStateOf(BackgroundMonitor.enabled(this@MainActivity)) }
            var limit by remember { mutableIntStateOf(BackgroundMonitor.thresholdMiB(this@MainActivity)) }
            var apps by remember { mutableStateOf<List<AppTraffic>>(emptyList()) }
            var loading by remember { mutableStateOf(false) }
            var error by remember { mutableStateOf<String?>(null) }
            var count by remember { mutableLongStateOf(0L) }
            var recent by remember { mutableStateOf<List<SavedSnapshot>>(emptyList()) }
            var changes by remember { mutableStateOf<Map<Int, TrafficChange>?>(null) }
            var alert by remember { mutableStateOf<String?>(null) }
            var showHistory by remember { mutableStateOf(false) }
            var query by remember { mutableStateOf("") }
            var sortByUpload by remember { mutableStateOf(false) }
            val scope = rememberCoroutineScope()
            fun refresh() {
                allowed = stats.hasAccess()
                if (!allowed) return
                scope.launch {
                    loading = true; error = null
                    try {
                        val result = withContext(Dispatchers.IO) {
                            val list = stats.today()
                            val delta = history.changes(list)
                            history.save(list)
                            Pair(Triple(list, history.snapshotCount(), history.recent()), delta)
                        }
                        apps = result.first.first; count = result.first.second; recent = result.first.third
                        changes = result.second
                        val largest = result.second?.maxByOrNull { it.value.rx + it.value.tx }
                        alert = largest?.takeIf { it.value.rx + it.value.tx >= 100L * 1024 * 1024 }
                            ?.let { (uid, traffic) -> "مصرف بالای ۱۰۰ مگابایت از برداشت قبلی: " +
                                (apps.firstOrNull { it.uid == uid }?.name ?: "UID $uid") +
                                " (${bytes(traffic.rx + traffic.tx)})" }
                    } catch (e: Exception) { error = e.localizedMessage ?: "خطا در خواندن اطلاعات" }
                    finally { loading = false }
                }
            }
            LaunchedEffect(allowed) { if (allowed) refresh() }
            CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Rtl) {
                MaterialTheme(colorScheme = darkColorScheme(primary = mint, background = bg, surface = surface)) {
                    Column(Modifier.fillMaxSize().background(bg).padding(18.dp)) {
                        Text("رها | پایش اینترنت", color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.Bold)
                        Spacer(Modifier.height(5.dp))
                        Text("نسخهٔ نامزد انتشار ۰.۶.۰ • آمار امروز • پایش دوره‌ای اختیاری", color = Color.LightGray, fontSize = 12.sp)
                        Spacer(Modifier.height(20.dp))
                        if (!allowed) {
                            Text("برای نمایش مصرف برنامه‌ها، دسترسی به آمار مصرف را فعال کنید.", color = Color.White)
                            Spacer(Modifier.height(12.dp))
                            Button(onClick = { startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS)) }) { Text("بازکردن تنظیمات مجوز") }
                            Button(onClick = { allowed = stats.hasAccess() }) { Text("بررسی دوبارهٔ مجوز") }
                        } else {
                            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                StatCard("دریافت امروز", bytes(apps.sumOf { it.rx }), Modifier.weight(1f))
                                StatCard("ارسال امروز", bytes(apps.sumOf { it.tx }), Modifier.weight(1f))
                            }
                            Spacer(Modifier.height(14.dp))
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text("ثبت دوره‌ای در پس‌زمینه", color = Color.White, modifier = Modifier.weight(1f), fontSize = 13.sp)
                                Switch(checked = periodic, onCheckedChange = { value ->
                                    periodic = value
                                    BackgroundMonitor.configure(this@MainActivity, value)
                                    if (value && Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                                        notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                                    }
                                })
                            }
                            Text("برداشت‌ها با WorkManager، حداقل فاصلهٔ درخواست ۱۵ دقیقه؛ زمان واقعی ممکن است عقب بیفتد. بدون VPN و بدون پایش ثانیه‌ای.", color = Color.LightGray, fontSize = 11.sp)
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text("هشدار از هر برداشت: $limit MiB", color = Color.White, fontSize = 12.sp, modifier = Modifier.weight(1f))
                                OutlinedButton(onClick = { limit = if (limit == 100) 500 else if (limit == 500) 1000 else 100; BackgroundMonitor.setThreshold(this@MainActivity, limit) }) { Text("تغییر آستانه") }
                            }
                            Spacer(Modifier.height(14.dp))
                            Text("مجموع بالا مربوط به UIDهای گزارش‌شده است و ممکن است با مصرف کل صورتحساب برابر نباشد.", color = Color.LightGray, fontSize = 11.sp)
                            Spacer(Modifier.height(10.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Button(onClick = { refresh() }, enabled = !loading) { Text(if (loading) "در حال خواندن…" else "به‌روزرسانی") }
                                OutlinedButton(onClick = { showHistory = !showHistory }) { Text(if (showHistory) "برنامه‌ها" else "تاریخچه") }
                            }
                            Text("تعداد برداشت‌های ذخیره‌شده: $count", color = Color.LightGray, fontSize = 11.sp)
                            error?.let { Text(it, color = Color(0xFFFFA0A0)) }
                            alert?.let { Text(it, color = Color(0xFFFFD580), fontSize = 12.sp) }
                            Text(if (changes == null) "هنوز برداشت قبلی برای محاسبهٔ تغییرات امروز وجود ندارد."
                                else "تغییرات نسبت به آخرین برداشت امروز (نه سرعت لحظه‌ای)", color = Color.LightGray, fontSize = 11.sp)
                            Spacer(Modifier.height(10.dp))
                            if (showHistory) {
                                Text("برداشت‌های ثبت‌شده — آمار تجمعی هر برداشت", color = Color.White, fontSize = 15.sp)
                                Text("ردیف‌های تاریخچه را با هم جمع نکنید؛ هر برداشت بازهٔ امروز را تا لحظهٔ ثبت نشان می‌دهد.", color = Color.LightGray, fontSize = 11.sp)
                                OutlinedButton(onClick = { exporter.launch("raha-android-history.csv") }) { Text("خروجی CSV تاریخچه") }
                                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    items(recent, key = { it.ts }) { r ->
                                        Column(Modifier.fillMaxWidth().background(surface, RoundedCornerShape(12.dp)).padding(12.dp)) {
                                            Text(DateFormat.getDateTimeInstance().format(Date(r.ts)), color = Color.White)
                                            Text("↓ ${bytes(r.totalRx)}   ↑ ${bytes(r.totalTx)}   •   ${r.apps} UID", color = mint, fontSize = 12.sp)
                                        }
                                    }
                                }
                            } else {
                                OutlinedTextField(value = query, onValueChange = { query = it }, label = { Text("جستجوی نام برنامه یا UID") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text("مرتب‌سازی بر اساس آپلود", color = Color.White, fontSize = 12.sp)
                                    Switch(checked = sortByUpload, onCheckedChange = { sortByUpload = it })
                                }
                                val filtered = apps.filter { it.name.contains(query, true) || it.uid.toString().contains(query) }
                                    .let { list -> if (sortByUpload) list.sortedByDescending { it.tx } else list.sortedByDescending { it.rx + it.tx } }
                                Text("برنامه‌ها (${filtered.size})", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    items(filtered, key = { it.uid }) { a ->
                                        Column(Modifier.fillMaxWidth().background(surface, RoundedCornerShape(12.dp)).padding(14.dp)) {
                                            Text(a.name, color = Color.White, fontWeight = FontWeight.SemiBold)
                                            Text("UID: ${a.uid}", color = Color.LightGray, fontSize = 11.sp)
                                            Spacer(Modifier.height(5.dp))
                                            Text("↓ ${bytes(a.rx)}     ↑ ${bytes(a.tx)}", color = mint, fontSize = 13.sp)
                                            changes?.get(a.uid)?.let { d ->
                                                Text("از برداشت قبل: ↓ ${bytes(d.rx)}  ↑ ${bytes(d.tx)}", color = Color(0xFFFFD580), fontSize = 11.sp)
                                            }
                                            Text("Wi-Fi: ↓ ${bytes(a.wifiRx)}  ↑ ${bytes(a.wifiTx)}", color = Color.LightGray, fontSize = 11.sp)
                                            Text("دادهٔ موبایل: ↓ ${bytes(a.mobileRx)}  ↑ ${bytes(a.mobileTx)}", color = Color.LightGray, fontSize = 11.sp)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
@Composable private fun StatCard(title: String, value: String, modifier: Modifier = Modifier) {
    Column(modifier.background(surface, RoundedCornerShape(14.dp)).padding(14.dp)) {
        Text(title, color = Color.LightGray, fontSize = 12.sp)
        Spacer(Modifier.height(8.dp))
        Text(value, color = mint, fontSize = 19.sp, fontWeight = FontWeight.Bold)
    }
}
