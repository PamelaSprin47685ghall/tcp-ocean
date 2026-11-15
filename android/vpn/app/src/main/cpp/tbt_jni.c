/* Hand-written JNI surface for libtbt.so.
 * Does not stash a JNIEnv across threads: the protect thunk attaches when
 * called from the NativeAOT worker and detaches before returning.
 */
#include <jni.h>
#include <android/log.h>
#include <stdint.h>
#include <string.h>
#include "tbt.h"

#define TAG "tbtjni"

static JavaVM *g_vm;
static jobject g_service; /* NewGlobalRef of the VpnService */
static jmethodID g_protect; /* boolean VpnService.protect(int) */

static int32_t protect_thunk(int32_t fd, void *ctx)
{
    JNIEnv *env = NULL;
    int attached = 0;
    jint rc;
    jboolean ok;
    (void)ctx;
    if (g_vm == NULL || g_service == NULL || g_protect == NULL)
        return 0;
    rc = (*g_vm)->GetEnv(g_vm, (void **)&env, JNI_VERSION_1_6);
    if (rc == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != 0)
            return 0;
        attached = 1;
    } else if (rc != JNI_OK) {
        return 0;
    }
    ok = (*env)->CallBooleanMethod(env, g_service, g_protect, (jint)fd);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        ok = JNI_FALSE;
    }
    if (attached)
        (*g_vm)->DetachCurrentThread(g_vm);
    return ok ? 1 : 0;
}

JNIEXPORT jint JNICALL
JNI_OnLoad(JavaVM *vm, void *reserved)
{
    JNIEnv *env = NULL;
    (void)reserved;
    g_vm = vm;
    if ((*vm)->GetEnv(vm, (void **)&env, JNI_VERSION_1_6) != JNI_OK)
        return JNI_ERR;
    return JNI_VERSION_1_6;
}

JNIEXPORT jint JNICALL
Java_com_tbtun_vpn_TbtNative_nativeStart(JNIEnv *env, jclass cls,
                                         jint tun_fd, jint flags,
                                         jint local_port, jint local_ipv4,
                                         jint window_clamp, jint max_connections,
                                         jint server_ipv4, jint server_port,
                                         jbyteArray psk, jint path_count, jint mss,
                                         jstring host,
                                         jobject service)
{
    tbt_config cfg;
    jclass svc_cls;
    jbyte *psk_bytes = NULL;
    const char *host_utf = NULL;
    (void)cls;
    if (service == NULL || tun_fd < 0)
        return TBT_ERR_ARGS;
    if (g_service != NULL) {
        (*env)->DeleteGlobalRef(env, g_service);
        g_service = NULL;
    }
    g_service = (*env)->NewGlobalRef(env, service);
    svc_cls = (*env)->GetObjectClass(env, service);
    g_protect = (*env)->GetMethodID(env, svc_cls, "protect", "(I)Z");
    if (g_protect == NULL) {
        if (g_service != NULL) {
            (*env)->DeleteGlobalRef(env, g_service);
            g_service = NULL;
        }
        return TBT_ERR_ARGS;
    }
    memset(&cfg, 0, sizeof cfg);
    cfg.version = TBT_CONFIG_VERSION;
    cfg.flags = flags;
    cfg.local_port = local_port;
    cfg.local_ipv4 = (uint32_t)local_ipv4;
    cfg.window_clamp = window_clamp;
    cfg.max_connections = max_connections;
    cfg.protect = protect_thunk;
    cfg.protect_ctx = NULL;
    cfg.server_ipv4 = (uint32_t)server_ipv4;
    cfg.server_port = server_port;
    cfg.path_count = path_count;
    cfg.mss = mss;
    if (psk != NULL) {
        jsize n = (*env)->GetArrayLength(env, psk);
        if (n == 32) {
            psk_bytes = (*env)->GetByteArrayElements(env, psk, NULL);
            cfg.psk = (const uint8_t *)psk_bytes;
            cfg.psk_len = 32;
        }
    }
    if (host != NULL) {
        host_utf = (*env)->GetStringUTFChars(env, host, NULL);
        cfg.host = host_utf;
    }
    jint rc = tbt_start(tun_fd, &cfg);
    if (host_utf != NULL)
        (*env)->ReleaseStringUTFChars(env, host, host_utf);
    if (psk_bytes != NULL)
        (*env)->ReleaseByteArrayElements(env, psk, psk_bytes, JNI_ABORT);
    return rc;
}

JNIEXPORT void JNICALL
Java_com_tbtun_vpn_TbtNative_nativeStop(JNIEnv *env, jclass cls)
{
    (void)cls;
    tbt_stop();
    if (g_service != NULL) {
        (*env)->DeleteGlobalRef(env, g_service);
        g_service = NULL;
    }
    g_protect = NULL;
}

JNIEXPORT jstring JNICALL
Java_com_tbtun_vpn_TbtNative_nativeStats(JNIEnv *env, jclass cls)
{
    char buf[4096];
    int n;
    (void)cls;
    n = tbt_stats(buf, (int32_t)sizeof buf);
    if (n < 0)
        return (*env)->NewStringUTF(env, "state=error");
    return (*env)->NewStringUTF(env, buf);
}
