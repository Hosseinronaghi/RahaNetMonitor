package ir.raha.netmonitor

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

data class TrafficChange(val rx: Long, val tx: Long)

data class SavedSnapshot(val ts: Long, val totalRx: Long, val totalTx: Long, val apps: Int)

class History(context: Context): SQLiteOpenHelper(context, "raha-traffic.db", null, 1) {
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE snapshots (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, uid INTEGER NOT NULL, label TEXT NOT NULL, rx INTEGER NOT NULL, tx INTEGER NOT NULL, wifi_rx INTEGER NOT NULL, mobile_rx INTEGER NOT NULL)")
        db.execSQL("CREATE INDEX idx_snapshots_ts_uid ON snapshots(ts,uid)")
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) { /* preserve existing records */ }
    /** Difference from last capture on the SAME local calendar date; null = no baseline. */
    fun changes(apps: List<AppTraffic>): Map<Int, TrafficChange>? {
        val now = java.util.Calendar.getInstance()
        now.set(java.util.Calendar.HOUR_OF_DAY, 0)
        now.set(java.util.Calendar.MINUTE, 0)
        now.set(java.util.Calendar.SECOND, 0)
        now.set(java.util.Calendar.MILLISECOND, 0)
        val cutoff = now.timeInMillis
        val lastTs = readableDatabase.rawQuery(
            "SELECT MAX(ts) FROM snapshots WHERE ts >= ?", arrayOf(cutoff.toString())
        ).use { c -> if (c.moveToFirst() && !c.isNull(0)) c.getLong(0) else return null }
        val previous = mutableMapOf<Int, Pair<Long,Long>>()
        readableDatabase.rawQuery("SELECT uid,rx,tx FROM snapshots WHERE ts=?", arrayOf(lastTs.toString())).use { c ->
            while (c.moveToNext()) previous[c.getInt(0)] = Pair(c.getLong(1), c.getLong(2))
        }
        return apps.mapNotNull { app ->
            val old = previous[app.uid] ?: return@mapNotNull null
            app.uid to TrafficChange((app.rx - old.first).coerceAtLeast(0), (app.tx - old.second).coerceAtLeast(0))
        }.toMap()
    }
    fun save(apps: List<AppTraffic>) {
        if (apps.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            val capturedAt = System.currentTimeMillis()
            // Give concurrent UI/background transactions distinct monotonically increasing timestamps.
            val latest = db.rawQuery("SELECT MAX(ts) FROM snapshots", null).use { c ->
                if (c.moveToFirst() && !c.isNull(0)) c.getLong(0) else 0L
            }
            if (latest > 0 && capturedAt >= latest && capturedAt - latest < 60_000L) {
                val stored = mutableMapOf<Int, Pair<Long, Long>>()
                db.rawQuery("SELECT uid,rx,tx FROM snapshots WHERE ts=?", arrayOf(latest.toString())).use { c ->
                    while (c.moveToNext()) stored[c.getInt(0)] = c.getLong(1) to c.getLong(2)
                }
                if (stored.size == apps.size && apps.all { stored[it.uid] == (it.rx to it.tx) }) {
                    db.setTransactionSuccessful()
                    return
                }
            }
            val now = maxOf(capturedAt, latest + 1L)
            apps.forEach { a ->
                db.insertOrThrow("snapshots", null, ContentValues().apply {
                    put("ts", now); put("uid", a.uid); put("label", a.name); put("rx", a.rx); put("tx", a.tx)
                    put("wifi_rx", a.wifiRx); put("mobile_rx", a.mobileRx)
                })
            }
            // Keep at most 30 days of snapshots to prevent unbounded database growth.
            db.delete("snapshots", "ts < ?", arrayOf((capturedAt - 30L * 24 * 60 * 60 * 1000).toString()))
            db.setTransactionSuccessful()
        } finally { db.endTransaction() }
    }
    fun snapshotCount(): Long = readableDatabase.rawQuery("SELECT COUNT(DISTINCT ts) FROM snapshots", null).use { c ->
        if (c.moveToFirst()) c.getLong(0) else 0
    }
    fun recent(limit: Int = 40): List<SavedSnapshot> {
        val result = mutableListOf<SavedSnapshot>()
        readableDatabase.rawQuery("SELECT ts, SUM(rx), SUM(tx), COUNT(*) FROM snapshots GROUP BY ts ORDER BY ts DESC LIMIT ?", arrayOf(limit.coerceIn(1,200).toString())).use { c ->
            while (c.moveToNext()) result.add(SavedSnapshot(c.getLong(0), c.getLong(1), c.getLong(2), c.getInt(3)))
        }
        return result
    }
    fun csv(): String {
        val output = StringBuilder("timestamp_utc,uid,app,download_bytes,upload_bytes,wifi_download_bytes,mobile_download_bytes\n")
        readableDatabase.rawQuery("SELECT ts,uid,label,rx,tx,wifi_rx,mobile_rx FROM snapshots ORDER BY ts DESC LIMIT 25000", null).use { c ->
            while (c.moveToNext()) {
                val time = java.time.Instant.ofEpochMilli(c.getLong(0)).toString()
                // Prevent spreadsheet formula injection in exported package labels.
                val label = c.getString(2).replace("\r", " ").replace("\n", " ")
                val safe = if (label.trimStart().firstOrNull() in listOf('=', '+', '-', '@', '\t')) "'" + label else label
                val escaped = "\"" + safe.replace("\"", "\"\"") + "\""
                output.append("$time,${c.getInt(1)},$escaped,${c.getLong(3)},${c.getLong(4)},${c.getLong(5)},${c.getLong(6)}\n")
            }
        }
        return output.toString()
    }
}
