package com.tbtun.vpn

/**
 * Thin JNI wrapper around libtbt.so (NativeAOT C ABI in tbt.h).
 * loadLibrary order: tbt first (the AOT image), then tbtjni (this package's
 * hand-written JNI, linked against libtbt).
 */
object TbtNative {
    init {
        System.loadLibrary("tbt")
        System.loadLibrary("tbtjni")
    }

    fun start(
        tunFd: Int,
        service: TbtVpnService,
        flags: Int = 0,
        localPort: Int = 0,
        localIpv4: Int = 0,
        windowClamp: Int = 0,
        maxConnections: Int = 0,
        serverIpv4: Int = 0,
        serverPort: Int = 0,
        psk: ByteArray? = null,
        pathCount: Int = 1,
        mss: Int = 0,
        host: String? = null,
    ): Int = nativeStart(
        tunFd, flags, localPort, localIpv4, windowClamp, maxConnections,
        serverIpv4, serverPort, psk, pathCount, mss, host, service,
    )

    fun stop() = nativeStop()

    fun stats(): String = nativeStats()

    @JvmStatic
    private external fun nativeStart(
        tunFd: Int,
        flags: Int,
        localPort: Int,
        localIpv4: Int,
        windowClamp: Int,
        maxConnections: Int,
        serverIpv4: Int,
        serverPort: Int,
        psk: ByteArray?,
        pathCount: Int,
        mss: Int,
        host: String?,
        service: TbtVpnService,
    ): Int

    @JvmStatic
    private external fun nativeStop()

    @JvmStatic
    private external fun nativeStats(): String
}
