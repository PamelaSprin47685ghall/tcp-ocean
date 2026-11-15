package com.tbtun.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.system.Os
import android.system.OsConstants
import android.util.Log

/**
 * Minimal VpnService shell: establish a TUN, hand the fd to libtbt, keep a
 * foreground notification (not a liveness guarantee — see spec ch. 14), and
 * expose [protect] so the JNI thunk can keep path sockets out of the TUN.
 * SO_MARK is not used.
 */
class TbtVpnService : VpnService() {

    private var tun: ParcelFileDescriptor? = null

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
        startForeground(NOTIF_ID, notification("TBTun starting"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            tearDown()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        if (tun != null) return START_STICKY
        val pfd = Builder()
            .setSession("TBTun")
            .setMtu(1280)
            .addAddress("10.200.3.2", 24)
            .addRoute("0.0.0.0", 0)
            .addDnsServer("1.1.1.1")
            .setBlocking(false)
            .establish()
        if (pfd == null) {
            Log.e(TAG, "Builder.establish() returned null (VPN not prepared?)")
            stopSelf()
            return START_NOT_STICKY
        }
        tun = pfd
        // Defensive: also set O_NONBLOCK on the adopted fd.
        try {
            val fl = Os.fcntlInt(pfd.fileDescriptor, OsConstants.F_GETFL, 0)
            Os.fcntlInt(pfd.fileDescriptor, OsConstants.F_SETFL, fl or OsConstants.O_NONBLOCK)
        } catch (e: Exception) {
            Log.w(TAG, "fcntl O_NONBLOCK", e)
        }
        val fd = pfd.fd
        val serverIpv4 = intent?.getIntExtra(EXTRA_SERVER_IPV4, 0) ?: 0
        val serverPort = intent?.getIntExtra(EXTRA_SERVER_PORT, 0) ?: 0
        val pskHex = intent?.getStringExtra(EXTRA_PSK_HEX)
        val psk = pskHex?.chunked(2)?.map { it.toInt(16).toByte() }?.toByteArray()
        val host = intent?.getStringExtra(EXTRA_HOST)
        val pathCount = intent?.getIntExtra(EXTRA_PATH_COUNT, 1) ?: 1
        val rc = TbtNative.start(
            fd, this,
            serverIpv4 = serverIpv4, serverPort = serverPort,
            psk = psk, pathCount = pathCount, host = host,
        )
        if (rc != 0) {
            Log.e(TAG, "tbt_start failed: $rc")
            tearDown()
            stopSelf()
            return START_NOT_STICKY
        }
        startForeground(NOTIF_ID, notification("TBTun is connected"))
        return START_STICKY
    }

    override fun onRevoke() {
        tearDown()
        super.onRevoke()
    }

    override fun onDestroy() {
        tearDown()
        super.onDestroy()
    }

    private fun tearDown() {
        try {
            TbtNative.stop()
        } catch (e: Exception) {
            Log.w(TAG, "tbt_stop", e)
        }
        try {
            tun?.close()
        } catch (e: Exception) {
            Log.w(TAG, "close tun", e)
        }
        tun = null
    }

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT < 26) return
        val nm = getSystemService(NotificationManager::class.java) ?: return
        val ch = NotificationChannel(CHANNEL_ID, "TBTun", NotificationManager.IMPORTANCE_LOW)
        ch.description = "Foreground notification required by VpnService; not a liveness signal."
        ch.setShowBadge(false)
        nm.createNotificationChannel(ch)
    }

    private fun notification(text: String): Notification {
        val b = if (Build.VERSION.SDK_INT >= 26)
            Notification.Builder(this, CHANNEL_ID)
        else
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        return b.setContentTitle("TBTun")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .build()
    }

    companion object {
        const val ACTION_STOP = "com.tbtun.vpn.STOP"
        const val EXTRA_SERVER_IPV4 = "server_ipv4"
        const val EXTRA_SERVER_PORT = "server_port"
        const val EXTRA_PSK_HEX = "psk_hex"
        const val EXTRA_HOST = "host"
        const val EXTRA_PATH_COUNT = "path_count"
        private const val TAG = "TbtVpnService"
        private const val CHANNEL_ID = "tbtun"
        private const val NOTIF_ID = 14
    }
}
