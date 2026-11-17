package ir.raha.netmonitor

import android.app.AppOpsManager
import android.app.usage.NetworkStats
import android.app.usage.NetworkStatsManager
import android.content.Context
import android.net.ConnectivityManager
import android.os.Process
import android.content.pm.PackageManager
import android.os.Build
import java.util.Calendar

data class AppTraffic(val uid: Int, val name: String, val rx: Long, val tx: Long, val wifiRx: Long, val mobileRx: Long,
                      val wifiTx: Long = 0, val mobileTx: Long = 0)

class Stats(private val context: Context) {
    fun hasAccess(): Boolean {
        val ops = context.getSystemService(Context.APP_OPS_SERVICE) as AppOpsManager
        return ops.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), context.packageName) == AppOpsManager.MODE_ALLOWED
    }
    fun today(): List<AppTraffic> {
        check(hasAccess()) { "ابتدا مجوز دسترسی به آمار مصرف را فعال کنید" }
        val start = Calendar.getInstance().apply { set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }.timeInMillis
        val end = System.currentTimeMillis()
        val network = context.getSystemService(Context.NETWORK_STATS_SERVICE) as NetworkStatsManager
        data class M(var wr:Long=0, var wt:Long=0, var mr:Long=0, var mt:Long=0)
        val byUid = mutableMapOf<Int,M>()
        for (type in listOf(ConnectivityManager.TYPE_WIFI, ConnectivityManager.TYPE_MOBILE)) {
            val result: NetworkStats = network.querySummary(type, null, start, end)
            try {
                val bucket = NetworkStats.Bucket()
                while (result.hasNextBucket()) {
                    result.getNextBucket(bucket)
                    if (bucket.uid < 0) continue
                    val m = byUid.getOrPut(bucket.uid) { M() }
                    if (type == ConnectivityManager.TYPE_WIFI) { m.wr += bucket.rxBytes; m.wt += bucket.txBytes }
                    else { m.mr += bucket.rxBytes; m.mt += bucket.txBytes }
                }
            } finally { result.close() }
        }
        val pm = context.packageManager
        return byUid.map { (uid,m) ->
            val apps = pm.getPackagesForUid(uid)
            val labels = apps?.mapNotNull { pkg ->
                try { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() } catch (_: PackageManager.NameNotFoundException) { null }
            }.orEmpty()
            val name = labels.distinct().joinToString(" / ").ifEmpty { apps?.joinToString(" / ") ?: "UID $uid (ناشناس/سیستم)" }
            AppTraffic(uid, name, m.wr + m.mr, m.wt + m.mt, m.wr, m.mr, m.wt, m.mt)
        }.filter { it.rx + it.tx > 0 }.sortedByDescending { it.rx + it.tx }
    }
}
