plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.tbtun.vpn"
    compileSdk = 34
    // NDK r27c; must match the clang used to publish libtbt.so.
    ndkVersion = "27.2.12479018"
    defaultConfig {
        applicationId = "com.tbtun.vpn"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "0.1"
        ndk { abiFilters += "arm64-v8a" }
        externalNativeBuild {
            cmake {
                arguments += listOf("-DANDROID_STL=c++_shared")
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    externalNativeBuild {
        cmake { path = file("src/main/cpp/CMakeLists.txt") }
    }
    packaging {
        jniLibs { keepDebugSymbols += "**/*.so" }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
}
