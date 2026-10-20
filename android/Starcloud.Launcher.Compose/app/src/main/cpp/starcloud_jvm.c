// starcloud_jvm.c：进程内 HotSpot 拉起（对齐 android/src/Starcloud.Android.Host/Runtime/JvmHost.cs
// 的 spike S1 实证形态）。so 不打进 APK：位于组件部署目录（glfw-stub / jre / 转译层），
// 按依赖优先级 dlopen(RTLD_GLOBAL)。Kotlin 侧经 StarcloudJvm 外部函数调用。
//
// 导出：
//   starcloudSetEnv(name, value)                       环境预置（SDL 活动启动前必须完成）
//   starcloudRunJvm(nativeDirs[], logPath, cwd,
//                   envPairs[], options[], mainClass,
//                   programArgs[]) -> jint             JVM 全生命周期，返回退出码
#include <dlfcn.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <jni.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <android/log.h>

#define TAG "StarcloudJvm"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

// dlopen 依赖优先级（对齐 JvmHost.PriorityOf）：libc++_shared 先于 pojavexec，
// pojavexec 先于 awt，libjvm 随后；其余（转译层 / openal / jre 同伴库）无顺序要求
static const int PRIO_LIBCXX = 0;
static const int PRIO_POJAVEXEC = 1;
static const int PRIO_AWT = 2;
static const int PRIO_LIBJVM = 3;
static const int PRIO_OTHER = 10;

static int priority_of(const char *fileName) {
    if (strcmp(fileName, "libc++_shared.so") == 0) return PRIO_LIBCXX;
    if (strcmp(fileName, "libpojavexec.so") == 0) return PRIO_POJAVEXEC;
    if (strcmp(fileName, "libpojavexec_awt.so") == 0) return PRIO_AWT;
    if (strcmp(fileName, "libjvm.so") == 0) return PRIO_LIBJVM;
    return PRIO_OTHER;
}

typedef struct {
    char *path;
    int priority;
} LibEntry;

static int cmp_lib(const void *a, const void *b) {
    const LibEntry *x = a, *y = b;
    if (x->priority != y->priority) return x->priority - y->priority;
    return strcmp(x->path, y->path); // 同优先级按路径排序保证加载顺序确定
}

// JNI 异常辅助：有挂起异常则描述并返回非零；异常消息直达 logcat（stderr 重定向到
// 文件后 ExceptionDescribe 受 stdio 全缓冲影响，真机排障不可依赖）
static int check_exception(JNIEnv *env, const char *where) {
    if ((*env)->ExceptionCheck(env)) {
        jthrowable thr = (*env)->ExceptionOccurred(env);
        jclass tClass = thr ? (*env)->FindClass(env, "java/lang/Throwable") : NULL;
        jmethodID toStr = (tClass && thr)
            ? (*env)->GetMethodID(env, tClass, "toString", "()Ljava/lang/String;") : NULL;
        if (thr && toStr) {
            jstring msg = (jstring)(*env)->CallObjectMethod(env, thr, toStr);
            if (msg) {
                const char *c = (*env)->GetStringUTFChars(env, msg, NULL);
                LOGE("%s 异常: %s", where, c ? c : "(null)");
                (*env)->ReleaseStringUTFChars(env, msg, c);
                (*env)->DeleteLocalRef(env, msg);
            }
        }
        if (tClass) (*env)->DeleteLocalRef(env, tClass);
        if (thr) (*env)->DeleteLocalRef(env, thr);
        LOGE("%s 挂起异常", where);
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        return -1;
    }
    return 0;
}

// 收集 dirs 下全部 so 按优先级 dlopen(RTLD_NOW|GLOBAL)；核心 so 失败返回 -1，
// 其余失败记日志跳过（渲染链可由 LWJGL 运行时显式加载兜底）
static int load_native_libraries(JNIEnv *env, jobjectArray nativeDirs) {
    LibEntry libs[512];
    int count = 0;
    jsize dirCount = (*env)->GetArrayLength(env, nativeDirs);
    for (int d = 0; d < dirCount; d++) {
        jstring jdir = (jstring)(*env)->GetObjectArrayElement(env, nativeDirs, d);
        const char *dir = (*env)->GetStringUTFChars(env, jdir, NULL);
        DIR *dp = opendir(dir);
        if (dp) {
            struct dirent *ent;
            while ((ent = readdir(dp)) != NULL && count < 512) {
                size_t len = strlen(ent->d_name);
                if (len < 4 || strcmp(ent->d_name + len - 3, ".so") != 0) continue;
                char path[1024];
                snprintf(path, sizeof(path), "%s/%s", dir, ent->d_name);
                libs[count].path = strdup(path);
                libs[count].priority = priority_of(ent->d_name);
                count++;
            }
            closedir(dp);
        }
        (*env)->ReleaseStringUTFChars(env, jdir, dir);
        (*env)->DeleteLocalRef(env, jdir);
    }
    qsort(libs, count, sizeof(LibEntry), cmp_lib);
    for (int i = 0; i < count; i++) {
        // 关键：必须 dlopen(RTLD_GLOBAL)——libpojavexec 对 JVM 子进程孵化的 JNI 钩子
        // 经 dlsym(RTLD_DEFAULT) 解析 libjava.so 原函数，LOCAL 加载拿不到
        void *h = dlopen(libs[i].path, RTLD_NOW | RTLD_GLOBAL);
        if (!h) {
            LOGE("dlopen 失败 %s: %s", libs[i].path, dlerror());
            if (libs[i].priority < PRIO_OTHER) return -1;
        } else {
            LOGI("dlopen %s", libs[i].path);
        }
        free(libs[i].path);
    }
    return 0;
}

// 在数组第 i 个元素上取 UTF 字符串；调用方负责 ReleaseStringUTFChars
// 数组元素 UTF 字符串对：GetString 与 Release 配对，local ref 由 release 释放
typedef struct {
    jstring js;
    const char *c;
} JUtf;

static JUtf array_utf(JNIEnv *env, jobjectArray arr, int i) {
    JUtf r;
    r.js = (jstring)(*env)->GetObjectArrayElement(env, arr, i);
    r.c = (*env)->GetStringUTFChars(env, r.js, NULL);
    return r;
}

static void release_utf(JNIEnv *env, JUtf v) {
    (*env)->ReleaseStringUTFChars(env, v.js, v.c);
    (*env)->DeleteLocalRef(env, v.js);
}

static int jliExitCode = -999;
static void log_jli_exit(void) {
    LOGI("exit 补记：JLI 退出码=%d", jliExitCode);
}

// Pojav jre_launcher.c 的关键前置：JVM 启动前清空全部信号 handler，
// 给 HotSpot 的信号检测一个干净起点（Android 对 SIGSEGV=SIG_DFL 有特殊检查，用 SIG_IGN 规避）。
// 宿主进程（SDL 活动 FCL 钩子等）残留的信号 handler 不清会干扰 HotSpot 信号初始化。
static void reset_signal_handlers_for_jvm(void) {
    struct sigaction clean_sa;
    memset(&clean_sa, 0, sizeof(clean_sa));
    for (int sigid = SIGHUP; sigid < NSIG; sigid++) {
        if (sigid == SIGSEGV) clean_sa.sa_handler = SIG_IGN;
        else clean_sa.sa_handler = SIG_DFL;
        sigaction(sigid, &clean_sa, NULL);
    }
}

// JLI_Launch 原型（jre lib/jli/libjli.so 导出，bin/java 的真实入口）
typedef int (*JLI_Launch_fn)(int argc, char **argv,
        int jargc, const char **jargv,
        int appclassc, const char **appclassv,
        const char *fullversion, const char *dotversion,
        const char *pname, const char *lname,
        int javaargs, int cpwildcard, int javaw, int ergo);

// JLI_Launch 路径：argv = ["java", <jvmArgs...>, <mainClass>, <programArgs...>]。
// 与 bin/java 完全同代码路径（真机 bin/java -version/-cp/Hello 全部健康）。
// 注意：游戏退出时 JLI 内部会 exit 整个进程（返回值通常到不了）。
JNIEXPORT jint JNICALL
Java_cn_starcloud_launcher_compose_game_StarcloudJvm_starcloudRunJvmJli(
        JNIEnv *env, jclass clazz,
        jobjectArray nativeDirs, jstring jlogPath, jstring jcwd,
        jobjectArray envPairs, jobjectArray options, jstring jmainClass,
        jobjectArray programArgs, jstring jliPath) {
    (void)clazz;
    // 诊断：打印各参数真实类名（CheckJNI 报 GetObjectArrayElement 收到 String）
    {
        jclass clsClass = (*env)->FindClass(env, "java/lang/Class");
        jmethodID getName = (*env)->GetMethodID(env, clsClass, "getName", "()Ljava/lang/String;");
#define LOG_PARAM_CLASS(param, label) do {         jclass pCls = (*env)->GetObjectClass(env, param);         jstring pName = (jstring)(*env)->CallObjectMethod(env, pCls, getName);         const char *pn = (*env)->GetStringUTFChars(env, pName, NULL);         LOGI("param %s = %s", label, pn ? pn : "(null)");         (*env)->ReleaseStringUTFChars(env, pName, pn);         (*env)->DeleteLocalRef(env, pName);         (*env)->DeleteLocalRef(env, pCls);     } while (0)
        LOG_PARAM_CLASS(options, "options");
        LOG_PARAM_CLASS(jmainClass, "jmainClass");
        LOG_PARAM_CLASS(programArgs, "programArgs");
        LOG_PARAM_CLASS(jliPath, "jliPath");
        (*env)->DeleteLocalRef(env, clsClass);
    }
    const char *jli = (*env)->GetStringUTFChars(env, jliPath, NULL);
    void *libjli = dlopen(jli, RTLD_NOW | RTLD_LOCAL);
    (*env)->ReleaseStringUTFChars(env, jliPath, jli);
    if (!libjli) {
        LOGE("dlopen libjli 失败: %s", dlerror());
        return 93;
    }
    JLI_Launch_fn jliLaunch = (JLI_Launch_fn)dlsym(libjli, "JLI_Launch");
    if (!jliLaunch) {
        LOGE("dlsym JLI_Launch 失败: %s", dlerror());
        return 94;
    }

    // argv = ["java"] + options + mainClass + programArgs
    int optCount = (*env)->GetArrayLength(env, options);
    int argCount = (*env)->GetArrayLength(env, programArgs);
    int argc = 1 + optCount + 1 + argCount;
    char **argv = calloc(argc, sizeof(char *));
    argv[0] = strdup("java");
    int idx = 1;
    for (int i = 0; i < optCount; i++) {
        JUtf o = array_utf(env, options, i);
        argv[idx++] = strdup(o.c);
        release_utf(env, o);
    }
    {
        // jmainClass 是 jstring（不能走 array_utf——那是要求数组的 helper，误用即 CheckJNI abort）
        const char *mc = (*env)->GetStringUTFChars(env, jmainClass, NULL);
        argv[idx++] = strdup(mc ? mc : "");
        (*env)->ReleaseStringUTFChars(env, jmainClass, mc);
    }
    for (int i = 0; i < argCount; i++) {
        JUtf a = array_utf(env, programArgs, i);
        argv[idx++] = strdup(a.c);
        release_utf(env, a);
    }
    for (int i = 0; i < argc; i++) LOGI("argv[%d]=%s", i, argv[i] ? argv[i] : "(null)");

    // 组件 so 的 RTLD_GLOBAL 加载链必须先于 JLI_Launch：游戏 JVM 的 native 方法
    // （FCLBridge.setLdLibraryPath 等）按 RTLD_DEFAULT 按名字解析，缺这步即
    // "No implementation found"（真机 SDL Error 实证 2026-10-10）
    if (load_native_libraries(env, nativeDirs) != 0) return 90;

    // stdout/stderr 重定向 + 无缓冲（对齐 CreateJavaVM 版 [2/6]：JLI/MC 的输出
    // 若留在 app 进程的原始 stdout 会全部丢失，真机实证）
    const char *logPath = (*env)->GetStringUTFChars(env, jlogPath, NULL);
    int logFd = open(logPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (logFd < 0) {
        LOGE("open(%s) 失败 errno=%d", logPath, errno);
        (*env)->ReleaseStringUTFChars(env, jlogPath, logPath);
        return 91;
    }
    dup2(logFd, 1);
    dup2(logFd, 2);
    if (logFd > 2) close(logFd);
    (*env)->ReleaseStringUTFChars(env, jlogPath, logPath);
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    LOGI("stdout/stderr 已重定向");

    // chdir + env 重放（对齐 [3/6]；env 已由 Kotlin 侧 starcloudSetEnv 预置，此处幂等）
    const char *cwd = (*env)->GetStringUTFChars(env, jcwd, NULL);
    if (chdir(cwd) != 0) LOGE("chdir(%s) 失败 errno=%d", cwd, errno);
    (*env)->ReleaseStringUTFChars(env, jcwd, cwd);
    int envCount = (*env)->GetArrayLength(env, envPairs);
    for (int i = 0; i < envCount; i++) {
        JUtf kv = array_utf(env, envPairs, i);
        char *buf = strdup(kv.c); // setenv 保留指针，strdup 出独立副本
        char *eq = strchr(buf, '=');
        if (eq) {
            *eq = 0;
            setenv(buf, eq + 1, 1);
        }
        free(buf);
        release_utf(env, kv);
    }

    reset_signal_handlers_for_jvm();
    LOGI("JLI_Launch 启动（argc=%d）", argc);
    // JLI 结束走 exit() 直接杀进程，返回值拿不到——atexit 钩子在 exit 路径上补记
    static int jliExitCode = -999;
    atexit(&log_jli_exit);
    int rc = jliLaunch(argc, argv, 0, NULL, 0, NULL, "", "", "java", "openjdk",
                       0 /* javaargs */, 0 /* cpwildcard */, 0 /* javaw */, 0 /* ergo */);
    jliExitCode = rc;
    LOGI("JLI_Launch 返回 %d（正常情况游戏退出时进程已终止，此行不易到达）", rc);
    return rc;
}

JNIEXPORT jint JNICALL
Java_cn_starcloud_launcher_compose_game_StarcloudJvm_starcloudSetEnv(
        JNIEnv *env, jclass clazz, jstring jname, jstring jvalue) {
    (void)clazz;
    const char *name = (*env)->GetStringUTFChars(env, jname, NULL);
    const char *value = (*env)->GetStringUTFChars(env, jvalue, NULL);
    int rc = setenv(name, value, 1 /* overwrite：RunJvm 内的重放是幂等的 */);
    (*env)->ReleaseStringUTFChars(env, jname, name);
    (*env)->ReleaseStringUTFChars(env, jvalue, value);
    return rc;
}

JNIEXPORT jint JNICALL
Java_cn_starcloud_launcher_compose_game_StarcloudJvm_starcloudRunJvm(
        JNIEnv *env, jclass clazz,
        jobjectArray nativeDirs, jstring jlogPath, jstring jcwd,
        jobjectArray envPairs, jobjectArray options, jstring jmainClass,
        jobjectArray programArgs) {
    (void)clazz;

    // [1/6] so 加载序：pojavexec（GLFW stub native）必须先于 libjvm——
    // 其注册的 JNI native 方法在游戏类首次触达时由 JVM 按需解析
    LOGI("开始加载 native 库");
    if (load_native_libraries(env, nativeDirs) != 0) return 90;

    // [2/6] stdout/stderr 重定向：JVM 启动日志 / ExceptionDescribe / hs_err 全走 stderr
    const char *logPath = (*env)->GetStringUTFChars(env, jlogPath, NULL);
    int fd = open(logPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) {
        LOGE("open(%s) 失败 errno=%d", logPath, errno);
        (*env)->ReleaseStringUTFChars(env, jlogPath, logPath);
        return 91;
    }
    dup2(fd, 1);
    dup2(fd, 2);
    if (fd > 2) close(fd);
    // 重定向到文件后 stdio 转全缓冲：JLI abort 不会 flush，错误消息会随进程丢失
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    (*env)->ReleaseStringUTFChars(env, jlogPath, logPath);
    LOGI("stdout/stderr 已重定向");

    // [3/6] 工作目录与环境（SDL 模式已在活动启动前预置过环境，此处幂等重放）
    const char *cwd = (*env)->GetStringUTFChars(env, jcwd, NULL);
    if (chdir(cwd) != 0) LOGE("chdir(%s) 失败 errno=%d", cwd, errno);
    (*env)->ReleaseStringUTFChars(env, jcwd, cwd);
    jsize envCount = (*env)->GetArrayLength(env, envPairs);
    for (int i = 0; i < envCount; i++) {
        JUtf kv = array_utf(env, envPairs, i);
        char *buf = strdup(kv.c); // setenv 保留指针，strdup 出独立副本
        char *eq = strchr(buf, '=');
        if (eq) {
            *eq = '\0';
            setenv(buf, eq + 1, 1);
        }
        free(buf);
        release_utf(env, kv);
    }

    // [4/6] 组装启动参数并创建 JVM
    jsize optCount = (*env)->GetArrayLength(env, options);
    JavaVMOption *jvmOptions = calloc(optCount, sizeof(JavaVMOption));
    for (int i = 0; i < optCount; i++) {
        JUtf o = array_utf(env, options, i);
        jvmOptions[i].optionString = strdup(o.c); // JVM 保留指针，须 strdup
        release_utf(env, o);
    }
    JavaVMInitArgs args = {
            .version = 0x00010008, // JNI_VERSION_1_8（NDK 头未定义，值固定）
            .nOptions = optCount,
            .options = jvmOptions,
            .ignoreUnrecognized = JNI_TRUE, // 容忍组件自带 JVM 参数
    };

    // 信号清空（同 JLI 路径）：HotSpot 的信号初始化需要干净起点
    reset_signal_handlers_for_jvm();

    // libjvm 定位：nativeDirs 已在 so 扫描时全量 dlopen，libjvm 句柄已在全局作用域，
    // 但 JNI_CreateJavaVM 导出仍需显式句柄——按同序重找（目录少，代价可忽略）
    char libJvmPath[1024] = {0};
    jsize dirCount = (*env)->GetArrayLength(env, nativeDirs);
    for (int d = 0; d < dirCount && libJvmPath[0] == '\0'; d++) {
        JUtf dir = array_utf(env, nativeDirs, d);
        snprintf(libJvmPath, sizeof(libJvmPath), "%s/libjvm.so", dir.c);
        release_utf(env, dir);
        if (access(libJvmPath, F_OK) != 0) libJvmPath[0] = '\0';
    }
    if (libJvmPath[0] == '\0') {
        LOGE("找不到 libjvm.so（JRE 组件未部署？）");
        return 92;
    }
    void *libJvm = dlopen(libJvmPath, RTLD_NOW | RTLD_GLOBAL);
    if (!libJvm) {
        LOGE("dlopen(%s) 失败: %s", libJvmPath, dlerror());
        return 93;
    }
    jint (*createVm)(JavaVM **, JNIEnv **, void *) = dlsym(libJvm, "JNI_CreateJavaVM");
    if (!createVm) {
        LOGE("dlsym JNI_CreateJavaVM 失败: %s", dlerror());
        return 94;
    }

    JavaVM *vm = NULL;
    JNIEnv *jvmEnv = NULL;
    LOGI("JNI_CreateJavaVM 前");
    jint rc = createVm(&vm, &jvmEnv, &args);
    if (rc != JNI_OK) {
        LOGE("JNI_CreateJavaVM rc=%d", rc);
        return 95;
    }
    LOGI("JVM 已创建");

    // [5/6] 调版本主类 main(String[])
    const char *mainClass = (*env)->GetStringUTFChars(env, jmainClass, NULL);
    char slashed[512];
    snprintf(slashed, sizeof(slashed), "%s", mainClass);
    for (char *p = slashed; *p; p++) if (*p == '.') *p = '/';
    (*env)->ReleaseStringUTFChars(env, jmainClass, mainClass);

    jclass mainClazz = (*jvmEnv)->FindClass(jvmEnv, slashed);
    LOGI("FindClass(%s)=%p pending=%d", slashed, (void *)mainClazz, (*jvmEnv)->ExceptionCheck(jvmEnv));
    if (mainClazz == NULL || check_exception(jvmEnv, "FindClass(main)")) return 96;
    jmethodID mid = (*jvmEnv)->GetStaticMethodID(jvmEnv, mainClazz, "main", "([Ljava/lang/String;)V");
    if (mid == NULL || check_exception(jvmEnv, "GetStaticMethodID(main)")) return 97;

    jclass stringClass = (*jvmEnv)->FindClass(jvmEnv, "java/lang/String");
    if (stringClass == NULL) return 98;
    jint argCount = (*jvmEnv)->GetArrayLength(jvmEnv, programArgs);
    jobjectArray argsArray = (*jvmEnv)->NewObjectArray(jvmEnv, argCount, stringClass, NULL);
    for (int i = 0; i < argCount; i++) {
        jstring a = (jstring)(*jvmEnv)->GetObjectArrayElement(jvmEnv, programArgs, i);
        (*jvmEnv)->SetObjectArrayElement(jvmEnv, argsArray, i, a);
        (*jvmEnv)->DeleteLocalRef(jvmEnv, a);
    }
    (*jvmEnv)->CallStaticVoidMethod(jvmEnv, mainClazz, mid, argsArray);
    if (check_exception(jvmEnv, "CallStaticVoidMethod(main)")) {
        LOGI("main 抛出未捕获异常（游戏崩溃退出）");
    } else {
        LOGI("main 执行完成，无挂起异常");
    }

    // [6/6] 等待 JVM 非守护线程结束后销毁
    jint exitCode = 0;
    if ((*vm)->DestroyJavaVM(vm) != JNI_OK) exitCode = 1;
    LOGI("JVM 已销毁，退出码 %d", exitCode);
    return exitCode;
}
