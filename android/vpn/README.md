# TBTun Android VpnService shim (source only)

This is the thin Kotlin/Java + JNI shell from spec chapter 14. It is not a
complete Play-ready app. The product library is `libtbt.so`, published from
`src/Tbt.NativeLib` as a NativeAOT shared library for `linux-bionic-arm64`.

Build the AOT image first (NDK clang must be first on `PATH`):

    export ANDROID_NDK_ROOT=/workspace/tools/android-ndk-r27c
    export PATH="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin:$PATH"
    dotnet publish src/Tbt.NativeLib/Tbt.NativeLib.csproj \
        -c Release -r linux-bionic-arm64 \
        -p:PublishAot=true -p:NativeLib=Shared \
        -p:DisableUnsupportedError=true \
        -p:PublishAotUsingRuntimePack=true \
        -o artifacts/publish/Tbt.NativeLib/bionic-arm64
    # rename tbt.so → libtbt.so if the publisher did not already
    cp artifacts/publish/Tbt.NativeLib/bionic-arm64/libtbt.so \
       android/vpn/app/src/main/jniLibs/arm64-v8a/libtbt.so

Then assemble the APK on a machine with the Android SDK (this environment has
no Android workload / SDK):

    ./gradlew :app:assembleDebug

`TbtVpnService` calls `Builder.establish()`, sets the fd non-blocking, starts a
foreground notification (not a liveness guarantee), and passes the service
instance to JNI so `VpnService.protect(fd)` runs on every path socket before
`connect`. Doze / full session handshake / bundling `libssl` are still open
(see docs/DECISIONS.md D13).

## Host SDK / APK

The box can build the debug APK once the Android SDK command-line tools, a JDK 17,
and Gradle are on disk (this environment uses `/workspace/tools/android-sdk`,
`/workspace/tools/jdk-17`, `/workspace/tools/gradle-8.11.1`, and NDK r27c):

    export JAVA_HOME=/workspace/tools/jdk-17
    export ANDROID_HOME=/workspace/tools/android-sdk
    printf 'sdk.dir=%s\nndk.dir=/workspace/tools/android-ndk-r27c\n' "$ANDROID_HOME"         > android/vpn/local.properties
    # copy the published linux-bionic-arm64 libtbt.so into jniLibs/arm64-v8a first
    (cd android/vpn && gradle :app:assembleDebug)

The APK is not a device-tested artifact. Server address and PSK are passed to
`TbtVpnService` as intent extras (`server_ipv4`, `server_port`, `psk_hex`, `host`,
`path_count`).

## Host end-to-end (no netns)

    tests/native/run-e2e.sh

Starts a loopback echo, a `tbtun server --listen` (no TUN privileges), and a
linux-x64 `libtbt.so` client on a socketpair fake TUN; injects a SYN plus data
and checks the echo.
