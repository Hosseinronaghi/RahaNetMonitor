package ir.raha.netmonitor

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit

/** User-opt-in periodic sampling; WorkManager execution is inexact and can be delayed. */
object BackgroundMonitor {
    private const val WORK_NAME = "raha_periodic_snapshot"
    private const val CHANNEL = "raha_consumption_alerts"
    private const val PREF = "monitor_settings"
    private const val ENABLED = "periodic_enabled"
    private const val LIMIT = "alert_threshold_mib"

    fun enabled(context: Context): Boolean = context.getSharedPreferences(PREF, Context.MODE_PRIVATE).getBoolean(ENABLED, false)
    fun thresholdMiB(context: Context): Int = context.getSharedPreferences(PREF, Context.MODE_PRIVATE).getInt(LIMIT, 100)
    fun setThreshold(context: Context, amount: Int) {
        context.getSharedPreferences(PREF, Context.MODE_PRIVATE).edit().putInt(LIMIT, amount.coerceIn(10, 5000)).apply()
    }
    fun configure(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREF, Context.MODE_PRIVATE).edit().putBoolean(ENABLED, enabled).apply()
        val manager = WorkManager.getInstance(context)
        if (enabled) {
            val request = PeriodicWorkRequestBuilder<SampleWorker>(15, TimeUnit.MINUTES)
                .addTag(WORK_NAME).build()
            manager.enqueueUniquePeriodicWork(WORK_NAME, ExistingPeriodicWorkPolicy.KEEP, request)
        } else manager.cancelUniqueWork(WORK_NAME)
    }
    fun alert(context: Context, title: String, message: String, uid: Int) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "هشدارهای مصرف اینترنت", NotificationManager.IMPORTANCE_DEFAULT))
        val notification = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle(title).setContentText(message)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message))
            .setAutoCancel(true).build()
        manager.notify(uid, notification)
    }
}

class SampleWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        if (!BackgroundMonitor.enabled(applicationContext)) return@withContext Result.success()
        try {
            val stats = Stats(applicationContext)
            if (!stats.hasAccess()) return@withContext Result.failure()
            val apps = stats.today()
            if (apps.isEmpty()) return@withContext Result.success()
            History(applicationContext).use { history ->
                val changes = history.changes(apps)
                history.save(apps)
                val highest = changes?.maxByOrNull { it.value.rx + it.value.tx }
                val threshold = BackgroundMonitor.thresholdMiB(applicationContext).toLong() * 1024 * 1024
                val canNotify = Build.VERSION.SDK_INT < 33 ||
                    applicationContext.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
                if (canNotify && highest != null && highest.value.rx + highest.value.tx >= threshold &&
                    AlertThrottle.shouldNotify(applicationContext, highest.key)) {
                    val name = apps.firstOrNull { it.uid == highest.key }?.name ?: "UID ${highest.key}"
                    BackgroundMonitor.alert(applicationContext, "هشدار مصرف اینترنت", "$name از برداشت قبلی بیش از ${BackgroundMonitor.thresholdMiB(applicationContext)} مگابایت مصرف کرده است.", highest.key)
                }
            }
            Result.success()
        } catch (_: SecurityException) { Result.failure() }
        catch (_: Exception) { Result.retry() }
    }
}

/** Rate-limit automatic alerts per UID; independent of the manual refresh UI. */
internal object AlertThrottle {
    private const val PREF = "raha_alert_cooldown"
    private const val SIX_HOURS_MS = 6L * 60 * 60 * 1000
    fun shouldNotify(context: Context, uid: Int): Boolean {
        val prefs = context.getSharedPreferences(PREF, Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        val key = "uid_$uid"
        val last = prefs.getLong(key, 0L)
        if (last > 0 && now >= last && now - last < SIX_HOURS_MS) return false
        return prefs.edit().putLong(key, now).commit()
    }
}
