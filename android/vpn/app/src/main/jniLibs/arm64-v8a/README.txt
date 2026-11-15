Copy the linux-bionic-arm64 NativeAOT output here as libtbt.so, e.g.:

  cp artifacts/publish/Tbt.NativeLib/bionic-arm64/libtbt.so \
     android/vpn/app/src/main/jniLibs/arm64-v8a/libtbt.so

System.loadLibrary("tbt") resolves this file; libtbtjni.so is built by CMake
and linked against it.
