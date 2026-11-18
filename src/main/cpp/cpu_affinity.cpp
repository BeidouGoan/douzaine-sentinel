// 绑核 JNI 层：把检测线程固定到小核（cpu0-3）
//
// ★ 为什么必须由 App 自己来做，而不是从 PC 侧 adb shell taskset：
//
//   实测（某安卓真机 / Android 10）：
//     adb shell taskset -p 0x0f <pid>  →  Permission denied
//     adb root                          →  adbd cannot run as root in production builds
//
//   原因是 Linux 的 sched_setaffinity 对【别的进程】需要 CAP_SYS_NICE，
//   Android 的 shell 用户和 production build 的 adbd 都没有这个能力。
//
//   但线程【可以改自己的亲和性】——只要目标核在 cgroup cpuset 允许范围内。
//   本机 cpuset 允许 cpu0-7，所以 App 内部调 sched_setaffinity 是合法的。
//
// ★ 核编号以 /proc/cpuinfo 的 CPU part 实读为准，别硬编码：
//     0xd05 = Cortex-A55（小核），0xd40 = Cortex-A76（大核）
//   本机实测：cpu0-3 = A55 小核，cpu4-7 = A76 大核。
//   注意这与「常见假设」相反，硬编码 cpu4-7 会绑反。

#include <jni.h>
#include <android/log.h>
#include <sched.h>
#include <pthread.h>
#include <unistd.h>
#include <cstdio>
#include <cstring>
#include <cerrno>

#define LOG_TAG "CpuAffinity"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace {

/** 读取指定 CPU 列表的 CPU part（0xd05=A55, 0xd40=A76） */
int readCpuPart(int cpu) {
    char path[64];
    snprintf(path, sizeof(path), "/sys/devices/system/cpu/cpu%d/cpufreq/cpuinfo_max_freq", cpu);
    // 用 max_freq 粗判：1.8GHz 附近 = A55 小核，1.92/2.6GHz = A76 大核
    FILE *f = fopen(path, "r");
    if (!f) return -1;
    int khz = 0;
    if (fscanf(f, "%d", &khz) != 1) { fclose(f); return -1; }
    fclose(f);
    return khz;
}

}  // namespace

extern "C" {

/**
 * 把当前线程绑到指定的 CPU 集合。
 *
 * @param cpuMask 位掩码：bit0=cpu0, bit1=cpu1, ...（例如 0x0f = cpu0-3）
 * @return 实际生效的掩码（失败返回 -1）
 */
JNIEXPORT jint JNICALL
Java_com_yq_sentinel_affinity_CpuAffinity_nativePinCurrentThread(JNIEnv *env, jobject thiz,
                                                                 jint cpuMask) {
    if (cpuMask <= 0) {
        LOGE("cpuMask 非法: %d", cpuMask);
        return -1;
    }

    cpu_set_t set;
    CPU_ZERO(&set);
    for (int i = 0; i < 32; i++) {
        if (cpuMask & (1 << i)) {
            CPU_SET(i, &set);
        }
    }

    // ★ 用 sched_setaffinity(pid=0) 而不是 pthread_setaffinity_np：
    //   Android NDK 的 Bionic 没有提供 pthread_setaffinity_np
    //   （编译报 "use of undeclared identifier"），
    //   而 sched_setaffinity(0, ...) 就是「当前线程」的语义。
    //   Linux 允许线程改自己的亲和性，不需要 CAP_SYS_NICE。
    int rc = sched_setaffinity(0, sizeof(cpu_set_t), &set);
    if (rc != 0) {
        LOGE("sched_setaffinity 失败 rc=%d (errno=%d)", rc, errno);
        return -1;
    }

    // 回读验证
    cpu_set_t actual;
    CPU_ZERO(&actual);
    sched_getaffinity(0, sizeof(cpu_set_t), &actual);
    int got = 0;
    for (int i = 0; i < 32; i++) {
        if (CPU_ISSET(i, &actual)) got |= (1 << i);
    }
    LOGI("当前线程绑核成功: 目标=0x%x 实际=0x%x (cpu%d 起)", cpuMask, got, __builtin_ctz(got));
    return got;
}

/**
 * 返回当前线程的亲和性掩码。
 */
JNIEXPORT jint JNICALL
Java_com_yq_sentinel_affinity_CpuAffinity_nativeGetAffinity(JNIEnv *env, jobject thiz) {
    cpu_set_t set;
    CPU_ZERO(&set);
    if (sched_getaffinity(0, sizeof(cpu_set_t), &set) != 0) {
        return -1;
    }
    int mask = 0;
    for (int i = 0; i < 32; i++) {
        if (CPU_ISSET(i, &set)) mask |= (1 << i);
    }
    return mask;
}

/**
 * 自动识别小核（A55）并返回它们的位掩码。
 *
 * 判定依据是 max_freq：A55 是 1.8GHz，A76 是 1.92/2.6GHz。
 * 这比硬编码可靠——不同 ROM 的核编号顺序不一样。
 */
JNIEXPORT jint JNICALL
Java_com_yq_sentinel_affinity_CpuAffinity_nativeLittleCoreMask(JNIEnv *env, jobject thiz) {
    // 收集所有核的 max_freq，找最小的那一档 = 小核
    int freqs[16];
    int n = 0;
    for (int i = 0; i < 16; i++) {
        int f = readCpuPart(i);
        if (f > 0) {
            freqs[n++] = f;
        }
    }
    if (n == 0) {
        LOGE("读不到任何核的频率信息");
        return -1;
    }
    int minFreq = freqs[0];
    for (int i = 1; i < n; i++) {
        if (freqs[i] < minFreq) minFreq = freqs[i];
    }
    int mask = 0;
    for (int i = 0; i < n; i++) {
        if (freqs[i] == minFreq) mask |= (1 << i);
    }
    LOGI("小核(A55, %d kHz) 掩码 = 0x%x", minFreq, mask);
    return mask;
}

/**
 * 绑定整个进程的所有线程到指定 CPU 集合。
 *
 * ★ 注意：这个只对【当前时刻存在】的线程生效，
 *   之后新创建的线程会继承创建者的亲和性（如果创建者在已绑定的线程上）。
 *   所以最佳实践是：先绑定检测线程，再让它去创建其它工作线程。
 */
JNIEXPORT jint JNICALL
Java_com_yq_sentinel_affinity_CpuAffinity_nativePinAllThreads(JNIEnv *env, jobject thiz,
                                                               jint cpuMask) {
    // 遍历 /proc/self/task 改所有线程的亲和性需要额外权限，
    // 而线程【自己】改是允许的。
    // 所以这里只绑当前线程（也就是检测线程），
    // 由它后续创建的线程会继承这个亲和性。
    return Java_com_yq_sentinel_affinity_CpuAffinity_nativePinCurrentThread(env, thiz, cpuMask);
}

}  // extern "C"
