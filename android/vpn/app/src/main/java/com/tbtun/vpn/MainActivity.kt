package com.tbtun.vpn

import android.app.Activity
import android.content.Intent
import android.net.VpnService
import android.os.Bundle

/** Requests VpnService.prepare and starts [TbtVpnService]. Source-only shim. */
class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val prep = VpnService.prepare(this)
        if (prep != null) startActivityForResult(prep, REQ_PREPARE)
        else startVpn()
    }

    @Deprecated("startActivityForResult is the VpnService.prepare contract")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ_PREPARE && resultCode == RESULT_OK) startVpn()
        else finish()
    }

    private fun startVpn() {
        startForegroundService(Intent(this, TbtVpnService::class.java))
        finish()
    }

    companion object {
        private const val REQ_PREPARE = 1
    }
}
