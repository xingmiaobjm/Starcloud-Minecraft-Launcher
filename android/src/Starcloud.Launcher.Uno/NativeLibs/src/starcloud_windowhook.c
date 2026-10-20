/*
 * 窗口 buffer 变换修正库（SDL/Vulkan 预旋转链路）
 *
 * 背景：本机面板原生方向为竖屏，横屏时显示旋转 90°。SDL 会把 ANativeWindow 的
 * buffer 几何设成面板原生（竖屏）尺寸，MC 的 Vulkan 后端据此把画面预旋转后画进该
 * 竖屏 buffer，但没有人把 buffer 标记为"已预旋转"，导致 SurfaceFlinger 既不做反向
 * 旋转、又按错误裁剪拉伸（实测 geomBufferSize=1440x2200 而窗口是 2200x1440）。
 *
 * 本库靠符号插入（加载顺序优先于 libnativewindow.so）拦截 SDL 必调的
 * ANativeWindow_setBuffersGeometry：转发原调用后，按环境变量追加一次
 * ANativeWindow_setBuffersTransform，把 buffer 的变换标记补上。
 *
 * 开关（进程环境变量，便于不改 native 代码反复试）：
 *   STARCLOUD_WINDOW_TRANSFORM = inverse | identity | rot90 | rot270 | 空(不干预)
 *   STARCLOUD_WINDOW_GEOMETRY  = swap（把 SDL 传入的宽高对调，用于验证是否被转置）
 */

#include <android/log.h>
#include <android/native_window.h>
#include <dlfcn.h>
#include <elf.h>
#include <jni.h>
#include <link.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/system_properties.h>
#include <stdio.h>
#include <unistd.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>

#define TAG "StarcloudWinHook"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)

/* AOSP system/window.h: NATIVE_WINDOW_TRANSFORM_INVERSE_DISPLAY（表示 buffer 已在
 * 面板原生方向完成预旋转，需合成器按显示变换反向补偿） */
#define TRANSFORM_INVERSE_DISPLAY 0x08
#define TRANSFORM_ROT_90 0x40
#define TRANSFORM_ROT_270 0x80

typedef int (*SetBuffersGeometryFn)(ANativeWindow *, int32_t, int32_t, int32_t);
typedef int (*SetBuffersTransformFn)(ANativeWindow *, int);

static SetBuffersGeometryFn g_real_set_geometry;
static SetBuffersTransformFn g_real_set_transform;

static void resolve_real(void)
{
    if (g_real_set_geometry != NULL) {
        return;
    }
    void *h = dlopen("libnativewindow.so", RTLD_NOW);
    if (h == NULL) {
        h = dlopen("libandroid.so", RTLD_NOW);
    }
    if (h == NULL) {
        LOGW("dlopen 失败：%s", dlerror());
        return;
    }
    g_real_set_geometry = (SetBuffersGeometryFn)dlsym(h, "ANativeWindow_setBuffersGeometry");
    g_real_set_transform = (SetBuffersTransformFn)dlsym(h, "ANativeWindow_setBuffersTransform");
    LOGI("真实符号 setGeometry=%p setTransform=%p", g_real_set_geometry, g_real_set_transform);
}

/* 返回期望写入的变换值，-1 表示不干预。
 * 优先读系统属性（便于 adb shell setprop 免重编调参），其次读进程环境变量：
 *   debug.starcloud.wtransform / STARCLOUD_WINDOW_TRANSFORM
 */
static int desired_transform(void)
{
    char prop[PROP_VALUE_MAX];
    prop[0] = '\0';
    __system_property_get("debug.starcloud.wtransform", prop);
    const char *mode = (prop[0] != '\0') ? prop : getenv("STARCLOUD_WINDOW_TRANSFORM");
    if (mode == NULL || *mode == '\0') {
        return -1;
    }
    if (strcmp(mode, "inverse") == 0) {
        return TRANSFORM_INVERSE_DISPLAY;
    }
    if (strcmp(mode, "identity") == 0) {
        return 0;
    }
    if (strcmp(mode, "rot90") == 0) {
        return TRANSFORM_ROT_90;
    }
    if (strcmp(mode, "rot270") == 0) {
        return TRANSFORM_ROT_270;
    }
    LOGW("未知变换取值=%s，不干预", mode);
    return -1;
}

/* 拦截：SDL 设置 buffer 几何时转发，并打印其实际传入的尺寸（用于确认是否被转置） */
int ANativeWindow_setBuffersGeometry(ANativeWindow *window, int32_t width, int32_t height,
                                     int32_t format)
{
    resolve_real();

    int32_t w = width;
    int32_t h = height;
    const char *gmode = getenv("STARCLOUD_WINDOW_GEOMETRY");
    if (gmode != NULL && strcmp(gmode, "swap") == 0) {
        w = height;
        h = width;
    }

    if (g_real_set_geometry == NULL) {
        LOGW("setGeometry 真实符号缺失，放弃");
        return -1;
    }
    int rc = g_real_set_geometry(window, w, h, format);

    int want = desired_transform();
    LOGI("setBuffersGeometry 传入 %dx%d（实际 %dx%d，fmt=%d）rc=%d，目标变换=%d",
         width, height, w, h, format, rc, want);

    if (want >= 0 && g_real_set_transform != NULL) {
        int trc = g_real_set_transform(window, want);
        LOGI("setBuffersTransform(%d) rc=%d", want, trc);
    }
    return rc;
}

/* 记录所有 buffer 变换设置（含 Vulkan WSI 的），便于诊断 */
int ANativeWindow_setBuffersTransform(ANativeWindow *window, int transform)
{
    resolve_real();
    int want = desired_transform();
    LOGI("setBuffersTransform 被调用(%d)，目标变换=%d", transform, want);
    if (g_real_set_transform == NULL) {
        return -1;
    }
    if (want >= 0) {
        transform = want;
    }
    return g_real_set_transform(window, transform);
}

/*
 * 把本库提升到全局组（保留：对部分路径仍有用）
 */
JNIEXPORT void JNICALL
Java_com_starcloud_launcher_SdlGameActivity_promoteSelfGlobal(JNIEnv *env, jclass clazz)
{
    (void)env;
    (void)clazz;
    Dl_info info;
    if (dladdr((void *)&Java_com_starcloud_launcher_SdlGameActivity_promoteSelfGlobal, &info) == 0
        || info.dli_fname == NULL) {
        LOGW("dladdr 失败，无法提升全局可见性");
        return;
    }
    void *h = dlopen(info.dli_fname, RTLD_NOW | RTLD_GLOBAL);
    LOGI("RTLD_GLOBAL 提升: %s -> %p (%s)", info.dli_fname, h, h != NULL ? "ok" : dlerror());
}

/* ---- 最小 GOT 改写（同 bytehook 原理）：直接改调用方自己的 PLT 条目，
 * 绕开"符号查找顺序不敌系统库"的问题 ---- */

static uintptr_t g_module_base;

static int find_module_cb(struct dl_phdr_info *info, size_t size, void *data)
{
    (void)size;
    const char *want = (const char *)data;
    if (info->dlpi_name != NULL && strstr(info->dlpi_name, want) != NULL) {
        g_module_base = (uintptr_t)info->dlpi_addr;
        return 1;
    }
    return 0;
}

/* 把已加载模块 lib_substr 中对 sym 的 GOT 条目改写为 replace，返回命中数 */
static int patch_got(const char *lib_substr, const char *sym, void *replace)
{
    g_module_base = 0;
    dl_iterate_phdr(find_module_cb, (void *)lib_substr);
    if (g_module_base == 0) {
        LOGW("未找到已加载模块 %s", lib_substr);
        return -1;
    }

    const ElfW(Ehdr) *eh = (const ElfW(Ehdr) *)g_module_base;
    const ElfW(Phdr) *ph = (const ElfW(Phdr) *)(g_module_base + eh->e_phoff);
    const ElfW(Dyn) *dyn = NULL;
    for (int i = 0; i < eh->e_phnum; i++) {
        if (ph[i].p_type == PT_DYNAMIC) {
            dyn = (const ElfW(Dyn) *)(g_module_base + ph[i].p_vaddr);
            break;
        }
    }
    if (dyn == NULL) {
        LOGW("模块无 PT_DYNAMIC");
        return -1;
    }

    const ElfW(Sym) *symtab = NULL;
    const char *strtab = NULL;
    const ElfW(Rela) *jmprel = NULL;
    size_t pltrelsz = 0;
    for (const ElfW(Dyn) *d = dyn; d->d_tag != DT_NULL; d++) {
        switch (d->d_tag) {
            case DT_SYMTAB: symtab = (const ElfW(Sym) *)(g_module_base + d->d_un.d_ptr); break;
            case DT_STRTAB: strtab = (const char *)(g_module_base + d->d_un.d_ptr); break;
            case DT_JMPREL: jmprel = (const ElfW(Rela) *)(g_module_base + d->d_un.d_ptr); break;
            case DT_PLTRELSZ: pltrelsz = d->d_un.d_val; break;
            default: break;
        }
    }
    if (symtab == NULL || strtab == NULL || jmprel == NULL) {
        LOGW("模块缺少 symtab/strtab/jmprel");
        return -1;
    }

    long page = sysconf(_SC_PAGESIZE);
    int patched = 0;
    size_t count = pltrelsz / sizeof(ElfW(Rela));
    for (size_t i = 0; i < count; i++) {
        size_t idx = ELF64_R_SYM(jmprel[i].r_info);
        if (idx == 0 || strcmp(strtab + symtab[idx].st_name, sym) != 0) {
            continue;
        }
        uintptr_t *slot = (uintptr_t *)(g_module_base + jmprel[i].r_offset);
        uintptr_t addr = (uintptr_t)slot;
        uintptr_t start = addr & ~(uintptr_t)(page - 1);
        uintptr_t end = (addr + sizeof(uintptr_t) + page - 1) & ~(uintptr_t)(page - 1);
        if (mprotect((void *)start, end - start, PROT_READ | PROT_WRITE) != 0) {
            LOGW("mprotect 失败: %s", dlerror());
            continue;
        }
        *slot = (uintptr_t)replace;
        patched++;
    }
    LOGI("GOT 改写 %s 中的 %s：命中 %d 处", lib_substr, sym, patched);
    return patched;
}

/* ================= Vulkan 表面参数归一（横屏方向的关键修复） =================
 *
 * 思路：不与合成器对抗，而是改变上层"看到的表面"。本机面板原生竖屏，横屏时
 * Vulkan 表面会带 currentTransform=ROT90，MC 据此把画面预旋转后画进竖屏 buffer，
 * 而合成器侧不做反向补偿 → 画面整体转 90°。
 * 这里包装 vkGetInstanceProcAddr，把 vkGetPhysicalDeviceSurfaceCapabilitiesKHR
 * 的返回改成"普通横屏表面"（currentTransform=identity、宽高对调），使 MC 按横屏
 * 建立交换链且不做预旋转；Android WSI 会把窗口 buffer 几何设为该横向尺寸，
 * 合成器即可 1:1 输出。
 * 通过 GOT 改写 liblwjgl/libSDL3 对 dlsym 的调用接入（LWJGL 用 dlsym 取
 * vkGetInstanceProcAddr）。
 */

#include <vulkan/vulkan.h>
#include <pthread.h>

static PFN_vkGetInstanceProcAddr g_real_gipa;
static PFN_vkGetDeviceProcAddr g_real_gdpa;
static PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR g_real_caps;

/* 伪造 surface capabilities：抹掉旋转、宽高对调 */
static VkResult my_get_phys_dev_surface_caps(VkPhysicalDevice physicalDevice, VkSurfaceKHR surface,
                                            VkSurfaceCapabilitiesKHR *caps)
{
    if (g_real_caps == NULL) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    VkResult r = g_real_caps(physicalDevice, surface, caps);
    if (r == VK_SUCCESS && caps != NULL) {
        /* 日志节流：每帧 acquire 都会进来，只在值变化时打印，避免刷爆 logcat */
        static uint32_t last_raw_w, last_raw_h, last_raw_t;
        static uint32_t last_fix_w, last_fix_h;
        static int have_last;
        uint32_t w = caps->currentExtent.width;
        uint32_t h = caps->currentExtent.height;
        uint32_t t = caps->currentTransform;
        if (!have_last || w != last_raw_w || h != last_raw_h || t != last_raw_t) {
            LOGI("原 caps: extent=%ux%u transform=0x%x", w, h, t);
        }
        if (w != UINT32_MAX && h != UINT32_MAX && w < h) {
            caps->currentExtent.width = h;
            caps->currentExtent.height = w;
        }
        caps->currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        caps->supportedTransforms |= VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        if (!have_last || caps->currentExtent.width != last_fix_w
            || caps->currentExtent.height != last_fix_h) {
            LOGI("改用 caps: extent=%ux%u transform=identity",
                 caps->currentExtent.width, caps->currentExtent.height);
            last_fix_w = caps->currentExtent.width;
            last_fix_h = caps->currentExtent.height;
        }
        last_raw_w = w;
        last_raw_h = h;
        last_raw_t = t;
        have_last = 1;
    }
    return r;
}

static PFN_vkVoidFunction my_get_instance_proc_addr(VkInstance instance, const char *name)
{
    if (name == NULL || g_real_gipa == NULL) {
        return NULL;
    }
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) {
        return (PFN_vkVoidFunction)my_get_instance_proc_addr;
    }
    if (strcmp(name, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR") == 0) {
        if (g_real_caps == NULL) {
            g_real_caps = (PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR)g_real_gipa(instance, name);
            LOGI("真实 caps 函数=%p", g_real_caps);
        }
        return (PFN_vkVoidFunction)my_get_phys_dev_surface_caps;
    }
    return g_real_gipa(instance, name);
}

static PFN_vkVoidFunction my_get_device_proc_addr(VkDevice device, const char *name)
{
    if (name == NULL || g_real_gdpa == NULL) {
        return NULL;
    }
    if (strcmp(name, "vkGetDeviceProcAddr") == 0) {
        return (PFN_vkVoidFunction)my_get_device_proc_addr;
    }
    return g_real_gdpa(device, name);
}

/* 插入 dlsym 的包装（刻意不叫 dlsym：本库内部对 dlsym 的调用须解析到 libdl
 * 的真实实现，若同名会自绑定；GOT 改写不要求符号同名）。
 * 只改写少数名字，其余原样转发 */
void *starcloud_dlsym(void *handle, const char *name)
{
    void *real = dlsym(handle, name);
    if (name == NULL) {
        return real;
    }
    if (strcmp(name, "vkGetInstanceProcAddr") == 0 && real != NULL) {
        if (g_real_gipa == NULL) {
            g_real_gipa = (PFN_vkGetInstanceProcAddr)real;
            LOGI("dlsym 命中 vkGetInstanceProcAddr，改用包装");
        }
        return (void *)my_get_instance_proc_addr;
    }
    if (strcmp(name, "vkGetDeviceProcAddr") == 0 && real != NULL) {
        if (g_real_gdpa == NULL) {
            g_real_gdpa = (PFN_vkGetDeviceProcAddr)real;
            LOGI("dlsym 命中 vkGetDeviceProcAddr，改用包装");
        }
        return (void *)my_get_device_proc_addr;
    }
    return real;
}

/* 静默版 GOT 改写：模块尚未加载时不打印 */
static int patch_got_quiet(const char *lib_substr, const char *sym, void *replace)
{
    g_module_base = 0;
    dl_iterate_phdr(find_module_cb, (void *)lib_substr);
    if (g_module_base == 0) {
        return 0;
    }
    return patch_got(lib_substr, sym, replace);
}

/* 后台等待 LWJGL/SDL 载入后接管其 dlsym（LWJGL 在 MC 初始化 Vulkan 时才加载） */
static void *vulkan_waiter(void *arg)
{
    (void)arg;
    int done_lwjgl = 0;
    int done_sdl = 0;
    for (int i = 0; i < 1200 && !(done_lwjgl && done_sdl); i++) {
        if (!done_lwjgl && patch_got_quiet("liblwjgl", "dlsym", (void *)starcloud_dlsym) > 0) {
            LOGI("liblwjgl 的 dlsym 已接管");
            done_lwjgl = 1;
        }
        if (!done_sdl && patch_got_quiet("libSDL3.so", "dlsym", (void *)starcloud_dlsym) > 0) {
            LOGI("libSDL3 的 dlsym 已接管");
            done_sdl = 1;
        }
        usleep(50 * 1000);
    }
    return NULL;
}

static void start_vulkan_waiter(void)
{
    pthread_t t;
    if (pthread_create(&t, NULL, vulkan_waiter, NULL) == 0) {
        pthread_detach(t);
    } else {
        LOGW("后台线程创建失败");
    }
}

/* ---- SDL 窗口尺寸探针（点击偏差取证）----
 * 反汇编定案：Java onNativeMouse → libSDL3 转发器读 [基址+0x1f6028] 得 Android_Window，
 * motion/button 都经 0xbcf18 边界检查（x/y 必须 ∈ [0,w)×[0,h)，w/h 是 SDL_Window
 * 的 int 字段 @+0x20/+0x24）。SDL_GetWindowFromID 被 FCL inline-hook，返回值未必是
 * 该链路实际用的窗口。此探针直接读真实全局：Android_Window 指针/w/h/flags(@+0x49)
 * + 鼠标内部坐标（[+0x1f54a0]=mouse->x/y、[+0x1f54b0]=last、[+0x1f54d0]=有效位），
 * 与 FromID 对照，判定边界检查为何失败 */
static char sdl_lib_path[512];

static int probe_find_module_cb(struct dl_phdr_info *info, size_t size, void *data)
{
    (void)size;
    if (info->dlpi_name != NULL && strstr(info->dlpi_name, "libSDL3.so") != NULL) {
        *(uintptr_t *)data = (uintptr_t)info->dlpi_addr;
        snprintf(sdl_lib_path, sizeof(sdl_lib_path), "%s", info->dlpi_name);
        return 1;
    }
    return 0;
}

/* ---- GLFW 桥探针（方案 D 定性）----
 * CallbackBridge 的 native 实现不经 SDL，而是直接调用 MC 注册的 GLFW 回调：
 * 反汇编取证 nglfwSetCursorPosCallback 把回调指针写进 bridge+0x27240，
 * critical_send_cursor_pos 读同一地址并 br 过去（button 在 +0x27258、key 在 +0x27250）。
 * 探针读这三处指针：非 0 = MC 注册了 GLFW 回调（桥可用）；恒 0 = 26.x 走 SDL，桥静默失效 */
static int probe_find_pojav_cb(struct dl_phdr_info *info, size_t size, void *data)
{
    (void)size;
    if (info->dlpi_name != NULL && strstr(info->dlpi_name, "libpojavexec.so") != NULL) {
        *(uintptr_t *)data = (uintptr_t)info->dlpi_addr;
        return 1;
    }
    return 0;
}

/* 无 root 取库：进程可读自身已加载的 so，复制到应用外部目录供离线反汇编 */
static void probe_dump_sdl_lib(void)
{
    const char *dir = "/storage/emulated/0/Android/data/cn.starcloud.launcher.android/files/tmp";
    char dst[600];
    FILE *in = NULL;
    FILE *out = NULL;
    char buf[65536];
    size_t n;

    if (sdl_lib_path[0] == '\0') {
        LOGW("SDL 探针：dump 跳过，未取得库路径");
        return;
    }
    LOGI("SDL 探针：libSDL3 路径=%s", sdl_lib_path);
    mkdir(dir, 0755);
    snprintf(dst, sizeof(dst), "%s/libSDL3_dump.so", dir);
    in = fopen(sdl_lib_path, "rb");
    if (in == NULL) {
        LOGW("SDL 探针：dump 源打开失败 %s", sdl_lib_path);
        return;
    }
    out = fopen(dst, "wb");
    if (out == NULL) {
        LOGW("SDL 探针：dump 目标写入失败 %s", dst);
        fclose(in);
        return;
    }
    while ((n = fread(buf, 1, sizeof(buf), in)) > 0) {
        if (fwrite(buf, 1, n, out) != n) {
            LOGW("SDL 探针：dump 写入中断");
            break;
        }
    }
    fclose(in);
    fclose(out);
    LOGI("SDL 探针：libSDL3 已复制到 %s", dst);
}

static void *sdl_size_probe(void *arg)
{
    (void)arg;
    void *h = NULL;
    for (int i = 0; i < 600 && h == NULL; i++) {
        h = dlopen("libSDL3.so", RTLD_NOLOAD | RTLD_NOW);
        usleep(100 * 1000);
    }
    if (h == NULL) {
        LOGW("SDL 探针：libSDL3 未加载");
        return NULL;
    }
    uintptr_t base = 0;
    dl_iterate_phdr(probe_find_module_cb, &base);
    LOGI("SDL 探针：libSDL3 基址=0x%lx", (unsigned long)base);
    typedef void *(*GetWindowFromIDFn)(unsigned int);
    typedef void (*GetWindowSizeFn)(void *, int *, int *);
    GetWindowFromIDFn get_win = (GetWindowFromIDFn)dlsym(h, "SDL_GetWindowFromID");
    GetWindowSizeFn get_size = (GetWindowSizeFn)dlsym(h, "SDL_GetWindowSize");
    /* 公开 API：返回按键位图（bit0=左键），是按键链路的 ground truth */
    typedef unsigned int (*GetMouseStateFn)(float *, float *);
    GetMouseStateFn get_mouse = (GetMouseStateFn)dlsym(h, "SDL_GetMouseState");
    /* 输入子系统状态查询：是否有鼠标设备、各输入事件类型是否启用 */
    typedef int (*HasMouseFn)(void);
    typedef int (*EventEnabledFn)(unsigned int);
    HasMouseFn has_mouse = (HasMouseFn)dlsym(h, "SDL_HasMouse");
    EventEnabledFn event_enabled = (EventEnabledFn)dlsym(h, "SDL_EventEnabled");
    if (get_win == NULL || get_size == NULL) {
        return NULL;
    }
    /* 150 轮 × 2s = 5 分钟，覆盖启动到进世界的完整操作窗口 */
    for (int i = 0; i < 150; i++) {
        /* Java 鼠标链路真实使用的全局：每轮都打，抓点击前后的瞬时值 */
        if (base != 0) {
            uintptr_t v = *(uintptr_t *)(base + 0x1f6028);
            /* Android 12 堆指针带 tag 高位（0xb4...），剥离顶字节后再做合理性检查 */
            uintptr_t core = v & 0x00FFFFFFFFFFFFFFULL;
            if (core >= 0x100000 && core < 0x800000000000ULL && (core & 7) == 0) {
                LOGI("SDL 探针：AndWin=%p w=%d h=%d flags=0x%02x",
                     (void *)v, *(int *)(v + 0x20), *(int *)(v + 0x24),
                     *(unsigned char *)(v + 0x49));
            } else {
                LOGI("SDL 探针：AndWin 槽值=0x%lx（不合理，不解引用）", (unsigned long)v);
            }
            /* SDL 内部鼠标状态是 libSDL3 数据段全局，与 AndWin 槽无关，无条件打印 */
            LOGI("SDL 探针：mouse=(%.1f,%.1f) last=(%.1f,%.1f) valid=%u",
                 *(float *)(base + 0x1f54a0), *(float *)(base + 0x1f54a4),
                 *(float *)(base + 0x1f54b0), *(float *)(base + 0x1f54b4),
                 *(unsigned char *)(base + 0x1f54d0));
        }
        /* 按键位图：轻点后短暂为 1（左键）说明 DOWN/UP 已进 SDL 并更新状态，
         * 恒为 0 则断点在 Java→SDL 按键链 */
        if (get_mouse != NULL) {
            float mx = -1.0f, my = -1.0f;
            unsigned int btn = get_mouse(&mx, &my);
            LOGI("SDL 探针：GetMouseState btn=0x%x at=(%.1f,%.1f)", btn, mx, my);
        }
        /* SDL_SendMouseButton 内部首行的门控字节：不等 1 则按键事件被整体跳过 */
        if (base != 0) {
            LOGI("SDL 探针：门控[0x1f53f5]=%u mice=%d hasmouse=%d ev_btn=%d ev_motion=%d ev_finger=%d touches=%d touch_mouse[0x1f5590]=%u",
                 (unsigned)*(unsigned char *)(base + 0x1f53f5),
                 *(int *)(base + 0x1f5510),
                 (has_mouse != NULL) ? has_mouse() : -1,
                 (event_enabled != NULL) ? event_enabled(0x401u) : -1,
                 (event_enabled != NULL) ? event_enabled(0x400u) : -1,
                 (event_enabled != NULL) ? event_enabled(0x700u) : -1,
                 *(int *)(base + 0x1f5580),
                 (unsigned)*(unsigned char *)(base + 0x1f5590));
        }
        /* 队列消费探针：i==45(90s，主菜单已就绪) 推一个 USER(0x8000) 事件，
         * i==50(100s) 用 PEEK 看它是否还在。
         * PEEK 不取出，不干扰 MC；残留=MC 没消费 SDL 队列，被消费=MC 在读队列 */
        if (i == 45 || i == 50) {
            typedef int (*PushEvFn)(void *);
            typedef int (*PeepEvFn)(void *, int, int, unsigned int, unsigned int);
            PushEvFn push_ev = (PushEvFn)dlsym(h, "SDL_PushEvent");
            PeepEvFn peep = (PeepEvFn)dlsym(h, "SDL_PeepEvents");
            if (i == 45 && push_ev != NULL) {
                unsigned char uev[128];
                memset(uev, 0, sizeof(uev));
                *(unsigned int *)uev = 0x8000u; /* SDL_EVENT_USER */
                LOGI("队列探针：push USER 事件 ret=%d", push_ev(uev));
            }
            if (i == 50 && peep != NULL) {
                unsigned char out[128];
                int n = peep(out, 1, 1 /*SDL_PEEKEVENT*/, 0x8000u, 0x8000u);
                LOGI("队列探针：10 秒后 PEEK USER n=%d（0=已被 MC 消费，1=仍残留）", n);
            }
        }
        /* 自检：验证按键状态位偏移（i==47）与鼠标焦点（i==48）。
         * 状态位写 1 后 GetMouseState 应回读 0x1，否则偏移错——MC 若靠状态判点击就用不上 */
        if (i == 47 && base != 0) {
            unsigned int *btn_state = (unsigned int *)(base + 0x1f5fec);
            unsigned int old = *btn_state;
            float mx = -1.0f, my = -1.0f;
            unsigned int rb;
            *btn_state |= 1u;
            rb = (get_mouse != NULL) ? get_mouse(&mx, &my) : 0xffffffffu;
            LOGI("状态位自检：写前=0x%x 写1后 GetMouseState=0x%x", old, rb);
            *btn_state = old;
        }
        if (i == 48) {
            typedef void *(*GetFocusFn)(void);
            typedef unsigned int (*GetWidFn)(void *);
            GetFocusFn get_focus = (GetFocusFn)dlsym(h, "SDL_GetMouseFocus");
            GetWidFn get_wid = (GetWidFn)dlsym(h, "SDL_GetWindowID");
            void *fw = (get_focus != NULL) ? get_focus() : NULL;
            void *win = (base != 0) ? *(void **)(base + 0x1f6028) : NULL;
            LOGI("焦点自检：GetMouseFocus=%p 主窗口=%p 主窗口ID=0x%x",
                 fw, win, (get_wid != NULL && win != NULL) ? get_wid(win) : 0u);
            /* 关键：对比 SDL 窗口的"逻辑尺寸"与"像素尺寸"——MC 的鼠标坐标换算与
             * 渲染缩放都依赖这两个值；若二者不一致而注入用错域，就会出现点击偏移 */
            {
                typedef int (*GetSizeFn)(void *, int *, int *);
                GetSizeFn get_sz = (GetSizeFn)dlsym(h, "SDL_GetWindowSize");
                GetSizeFn get_px = (GetSizeFn)dlsym(h, "SDL_GetWindowSizeInPixels");
                int lw = -1, lh = -1, pw = -1, ph = -1;
                if (win != NULL) {
                    if (get_sz != NULL) {
                        get_sz(win, &lw, &lh);
                    }
                    if (get_px != NULL) {
                        get_px(win, &pw, &ph);
                    }
                }
                LOGI("尺寸对照：WindowSize(逻辑)=%dx%d  WindowSizeInPixels(像素)=%dx%d",
                     lw, lh, pw, ph);
            }
        }
        /* 对照实验（i==46）：MC 的 MouseHandler.onButton 会校验
         * windowHandle(=SDL_GetWindowFromEvent(event)) 是否等于 minecraft.getWindow().handle()，
         * 不等则直接 return 丢弃。这里用同样的 button 事件调一次，看它是否返回 Android_Window */
        if (i == 46) {
            typedef long long (*GetWinFromEvFn)(void *);
            GetWinFromEvFn gwe = (GetWinFromEvFn)dlsym(h, "SDL_GetWindowFromEvent");
            void *w = (base != 0) ? *(void **)(base + 0x1f6028) : NULL;
            if (gwe != NULL) {
                unsigned char tev[128];
                unsigned int wid = 0;
                typedef unsigned int (*GetWindowIDFn)(void *);
                GetWindowIDFn get_wid = (GetWindowIDFn)dlsym(h, "SDL_GetWindowID");
                memset(tev, 0, sizeof(tev));
                *(unsigned int *)(tev + 0) = 0x401u; /* MOUSE_BUTTON_DOWN */
                *(unsigned int *)(tev + 16) = (get_wid != NULL && w != NULL) ? get_wid(w) : 0u;
                *(unsigned int *)(tev + 20) = 1u;    /* which */
                tev[24] = 1u;                        /* button = 左键 */
                tev[25] = 1u;                        /* down */
                tev[26] = 1u;                        /* clicks */
                wid = *(unsigned int *)(tev + 16);
                {
                    long long r = gwe(tev);
                    LOGI("窗口校验对照：GetWindowFromEvent(btn,wid=0x%x)=%p Android_Window=%p 相等=%d",
                         wid, (void *)r, w, (void *)r == w);
                }
            }
        }
        /* 打印真实触摸设备 ID（发出 finger 事件时必须用这个 ID，而非臆造的 1） */
        if (i == 49) {
            typedef long long *(*GetTouchDevFn)(int *);
            typedef void (*FreeFn)(void *);
            GetTouchDevFn get_td = (GetTouchDevFn)dlsym(h, "SDL_GetTouchDevices");
            FreeFn freer = (FreeFn)dlsym(h, "SDL_free");
            if (get_td != NULL) {
                int n = 0;
                long long *ids = get_td(&n);
                if (ids != NULL) {
                    LOGI("触摸设备：count=%d id[0]=%lld id[1]=%lld id[2]=%lld id[3]=%lld",
                         n, ids[0], (n > 1) ? ids[1] : 0ll,
                         (n > 2) ? ids[2] : 0ll, (n > 3) ? ids[3] : 0ll);
                    if (freer != NULL) {
                        freer(ids);
                    }
                }
            }
        }
        /* 桥探针：每 5 轮一次。经 libpojavexec 全局槽取 bridge 结构，读三处回调指针 */
        if (i % 5 == 0) {
            uintptr_t pbase = 0;
            dl_iterate_phdr(probe_find_pojav_cb, &pbase);
            if (pbase == 0) {
                LOGI("桥探针：libpojavexec 未加载");
            } else {
                uintptr_t g = *(uintptr_t *)(pbase + 0x13b30);
                uintptr_t gc = g & 0x00FFFFFFFFFFFFFFULL;
                if (gc >= 0x100000 && gc < 0x800000000000ULL && (gc & 7) == 0) {
                    uintptr_t bridge = *(uintptr_t *)g;
                    uintptr_t bc = bridge & 0x00FFFFFFFFFFFFFFULL;
                    if (bc >= 0x100000 && bc < 0x800000000000ULL && (bc & 7) == 0) {
                        LOGI("桥探针：bridge=%p cursor_cb=%p btn_cb=%p key_cb=%p",
                             (void *)bridge,
                             *(void **)(bridge + 0x27240),
                             *(void **)(bridge + 0x27258),
                             *(void **)(bridge + 0x27250));
                    } else {
                        LOGI("桥探针：bridge 槽值不合理 0x%lx", (unsigned long)bridge);
                    }
                } else {
                    LOGI("桥探针：全局槽值不合理 0x%lx", (unsigned long)g);
                }
            }
        }
        /* FromID 对照：被 FCL hook，仅作参照，每 10 轮列一次 */
        if (i % 10 == 0) {
            for (unsigned int id = 1; id <= 16; id++) {
                void *win = get_win(id);
                if (win == NULL) {
                    continue;
                }
                int w = -1, hgt = -1;
                get_size(win, &w, &hgt);
                LOGI("SDL 探针：id=%u window=%p size=%dx%d", id, win, w, hgt);
            }
        }
        usleep(2 * 1000 * 1000);
    }
    return NULL;
}

static void start_sdl_size_probe(void)
{
    pthread_t t;
    if (pthread_create(&t, NULL, sdl_size_probe, NULL) == 0) {
        pthread_detach(t);
    } else {
        LOGW("SDL 探针线程创建失败");
    }
}

/* 由 Java 在 libSDL3.so 载入之后调用：改写其 GOT 条目指向本库实现 */
JNIEXPORT void JNICALL
Java_com_starcloud_launcher_SdlGameActivity_installHooks(JNIEnv *env, jclass clazz)
{
    (void)env;
    (void)clazz;
    patch_got("libSDL3.so", "ANativeWindow_setBuffersGeometry",
              (void *)ANativeWindow_setBuffersGeometry);
    patch_got("libSDL3.so", "ANativeWindow_setBuffersTransform",
              (void *)ANativeWindow_setBuffersTransform);
    start_vulkan_waiter();
    start_sdl_size_probe();
}

/* SDL3 鼠标移动事件（SDL_MouseMotionEvent），用于手搓 push 对照 warp：
 * state@24, x@28, y@32, xrel@36, yrel@40；type=0x400 */
typedef struct {
    unsigned int type;
    unsigned int reserved;
    unsigned long long timestamp;
    unsigned int window_id;
    unsigned int which;
    unsigned int state;
    float x;
    float y;
    float xrel;
    float yrel;
} sdl_motion_ev_t;

typedef union {
    unsigned char raw[128];
    sdl_motion_ev_t motion;
} sdl_motion_buf_t;

#define SDL_EVENT_MOUSE_MOTION 0x400u

/* 手搓 motion 事件 push（对照 warp：验证 SDL_PushEvent 的 motion 是否被 MC 接受） */
JNIEXPORT void JNICALL
Java_org_libsdl_app_SDLSurface_probeHandMotion(JNIEnv *env, jclass clazz, jfloat x, jfloat y)
{
    void *h;
    uintptr_t base = 0;
    void *win;
    typedef int (*PushEventFn)(void *);
    typedef unsigned int (*GetWindowIDFn)(void *);
    typedef unsigned long long (*GetTicksNSFn)(void);
    PushEventFn push;
    sdl_motion_buf_t ev;
    (void)env;
    (void)clazz;
    h = dlopen("libSDL3.so", RTLD_NOLOAD | RTLD_NOW);
    if (h == NULL) {
        return;
    }
    dl_iterate_phdr(probe_find_module_cb, &base);
    win = (base != 0) ? *(void **)(base + 0x1f6028) : NULL;
    push = (PushEventFn)dlsym(h, "SDL_PushEvent");
    if (win == NULL || push == NULL) {
        return;
    }
    {
        GetWindowIDFn get_id = (GetWindowIDFn)dlsym(h, "SDL_GetWindowID");
        GetTicksNSFn get_ticks = (GetTicksNSFn)dlsym(h, "SDL_GetTicksNS");
        float ox = *(float *)(base + 0x1f54a0);
        float oy = *(float *)(base + 0x1f54a4);
        memset(&ev, 0, sizeof(ev));
        ev.motion.type = SDL_EVENT_MOUSE_MOTION;
        ev.motion.timestamp = (get_ticks != NULL) ? get_ticks() : 0ull;
        ev.motion.window_id = (get_id != NULL) ? get_id(win) : 0u;
        ev.motion.which = 1u;
        ev.motion.state = 0u;
        ev.motion.x = (float)x;
        ev.motion.y = (float)y;
        ev.motion.xrel = (float)x - ox;
        ev.motion.yrel = (float)y - oy;
        LOGI("手搓 motion push：at=(%.1f,%.1f) ret=%d", (double)x, (double)y, push(&ev));
    }
}

/* 试验：经 SDL 公开 API SDL_WarpMouseInWindow 注入 motion。
 * 官方实现会把 SDL_EVENT_MOUSE_MOTION push 进事件队列；若 MC 的按钮因此出现
 * 悬停高亮，说明 MC 消费 SDL 事件队列（后续自建 SDL_PushEvent 方案可行）；
 * 若仍无反应，说明 MC 不读该队列，需另找事件源 */
JNIEXPORT void JNICALL
Java_org_libsdl_app_SDLSurface_probeWarpMouse(JNIEnv *env, jclass clazz, jfloat x, jfloat y)
{
    void *h;
    uintptr_t base = 0;
    typedef void (*WarpFn)(void *, float, float);
    WarpFn warp;
    void *win;
    (void)env;
    (void)clazz;
    h = dlopen("libSDL3.so", RTLD_NOLOAD | RTLD_NOW);
    if (h == NULL) {
        LOGW("warp 试验：libSDL3 未加载");
        return;
    }
    dl_iterate_phdr(probe_find_module_cb, &base);
    warp = (WarpFn)dlsym(h, "SDL_WarpMouseInWindow");
    win = (base != 0) ? *(void **)(base + 0x1f6028) : NULL;
    LOGI("warp 试验：win=%p fn=%p x=%.1f y=%.1f", win, (void *)warp, (double)x, (double)y);
    if (warp != NULL && win != NULL) {
        warp(win, (float)x, (float)y);
    }
}

/* ---- SDL 事件注入（方案 C 定案）----
 * 定案依据：MC 只消费 SDL 事件队列，而 onNativeMouse 转发器只改 SDL 内部状态、
 * 不 push 事件（SDL_WarpMouseInWindow 一调，MC 按钮就出现悬停高亮，铁证）。
 * motion 用 SDL 公开 API；button SDL3 无公开发送 API，按 SDL_MouseButtonEvent
 * 布局自建事件后 SDL_PushEvent 入队。
 * SDL_Event 是 128 字节 union；SDL_MouseButtonEvent 字段偏移固定（见下） */
typedef struct {
    unsigned int type;            /* 0  SDL_EVENT_MOUSE_BUTTON_DOWN/UP */
    unsigned int reserved;        /* 4 */
    unsigned long long timestamp; /* 8 */
    unsigned int window_id;       /* 16 */
    unsigned int which;           /* 20 SDL_MouseID */
    unsigned char button;         /* 24 SDL_BUTTON_LEFT=1/MIDDLE=2/RIGHT=3 */
    unsigned char down;           /* 25 */
    unsigned char clicks;         /* 26 */
    unsigned char pad;            /* 27 */
    float x;                      /* 28 */
    float y;                      /* 32 */
} sdl_button_ev_t;

typedef union {
    unsigned char raw[128];
    sdl_button_ev_t button;
} sdl_ev_buf_t;

/* SDL3 触摸事件（SDL_TouchFingerEvent）：Android 上 MC 视自身为触摸设备，
 * 点击走 finger 而非 mouse button。x/y 为归一化 [0,1]；touchID/fingerID 为 Uint64 */
typedef struct {
    unsigned int type;            /* 0  0x700 DOWN / 0x701 UP / 0x702 MOTION */
    unsigned int reserved;        /* 4 */
    unsigned long long timestamp; /* 8 */
    unsigned long long touch_id;  /* 16 */
    unsigned long long finger_id; /* 24 */
    float x;                      /* 32 归一化 */
    float y;                      /* 36 归一化 */
    float dx;                     /* 40 */
    float dy;                     /* 44 */
    float pressure;               /* 48 */
    unsigned int window_id;       /* 52 */
} sdl_finger_ev_t;

typedef union {
    unsigned char raw[128];
    sdl_finger_ev_t finger;
} sdl_finger_buf_t;

#define SDL_EVENT_FINGER_DOWN   0x700u
#define SDL_EVENT_FINGER_UP     0x701u
#define SDL_EVENT_FINGER_MOTION 0x702u

#define SDL_EVENT_MOUSE_BUTTON_DOWN 0x401u
#define SDL_EVENT_MOUSE_BUTTON_UP   0x402u

/* 自建 SDL_MouseButtonEvent 入队（button 传 SDL 键号：1 左 / 2 中 / 3 右） */
JNIEXPORT void JNICALL
Java_org_libsdl_app_SDLSurface_probePushButton(JNIEnv *env, jclass clazz,
                                               jint sdlButton, jboolean down,
                                               jfloat x, jfloat y)
{
    void *h;
    uintptr_t base = 0;
    void *win;
    typedef unsigned int (*GetWindowIDFn)(void *);
    typedef int (*PushEventFn)(void *);
    typedef unsigned long long (*GetTicksNSFn)(void);
    GetWindowIDFn get_id;
    PushEventFn push;
    GetTicksNSFn get_ticks;
    sdl_ev_buf_t ev;
    (void)env;
    (void)clazz;
    h = dlopen("libSDL3.so", RTLD_NOLOAD | RTLD_NOW);
    if (h == NULL) {
        return;
    }
    dl_iterate_phdr(probe_find_module_cb, &base);
    win = (base != 0) ? *(void **)(base + 0x1f6028) : NULL;
    get_id = (GetWindowIDFn)dlsym(h, "SDL_GetWindowID");
    push = (PushEventFn)dlsym(h, "SDL_PushEvent");
    get_ticks = (GetTicksNSFn)dlsym(h, "SDL_GetTicksNS");
    if (win == NULL || push == NULL) {
        LOGW("button 注入：win/push 缺失 win=%p push=%p", win, (void *)push);
        return;
    }
    memset(&ev, 0, sizeof(ev));
    ev.button.type = down ? SDL_EVENT_MOUSE_BUTTON_DOWN : SDL_EVENT_MOUSE_BUTTON_UP;
    ev.button.timestamp = (get_ticks != NULL) ? get_ticks() : 0ull;
    ev.button.window_id = (get_id != NULL) ? get_id(win) : 0u;
    ev.button.which = 1u;
    ev.button.button = (unsigned char)sdlButton;
    ev.button.down = down ? 1 : 0;
    ev.button.clicks = 1;
    ev.button.x = (float)x;
    ev.button.y = (float)y;
    {
        int r = push(&ev);
        LOGI("button 注入：btn=%d down=%d at=(%.1f,%.1f) wid=0x%x ts=%llu ret=%d",
             (int)sdlButton, (int)down, (double)x, (double)y,
             ev.button.window_id, ev.button.timestamp, r);
    }
    /* 方案 B 正规后门：直接调用 FCL 版 SDL 内部的 SDL_SendTouch（偏移 0xc0d9c），
     * 走与真实触摸完全相同的链路（它内部会用 touchID 查设备、更新 finger 状态、
     * 并按门控 [0x1f5590] 自动合成 mouse motion + mouse button）。参数约定取自
     * onNativeTouch→Android_OnTouch(0x1d9c28) 的调用点：
     *   x0=0, x1=touchID, x2=fingerID+1, x3=window, w4=type(0x700 DOWN/0x701 UP),
     *   s0=x_norm, s1=y_norm, s2=pressure */
    {
        typedef void (*SendTouchFn)(void *, long long, long long, void *, unsigned int,
                                    float, float, float);
        SendTouchFn send_touch = (SendTouchFn)(base + 0xc0d9c);
        long long tid = -1ll;
        typedef long long *(*GetTouchDevFn)(int *);
        typedef void (*FreeFn)(void *);
        GetTouchDevFn get_td = (GetTouchDevFn)dlsym(h, "SDL_GetTouchDevices");
        FreeFn freer = (FreeFn)dlsym(h, "SDL_free");
        if (get_td != NULL) {
            int n = 0;
            long long *ids = get_td(&n);
            if (ids != NULL) {
                int k;
                for (k = 0; k < n; k++) {
                    if (ids[k] >= 0) {
                        tid = ids[k];
                        break;
                    }
                }
                if (freer != NULL) {
                    freer(ids);
                }
            }
        }
        if (tid >= 0) {
            int ww = *(int *)((char *)win + 0x20);
            int wh = *(int *)((char *)win + 0x24);
            float nx = (ww > 0) ? ((float)x / (float)ww) : 0.5f;
            float ny = (wh > 0) ? ((float)y / (float)wh) : 0.5f;
            LOGI("直调 SDL_SendTouch：type=0x%x tid=%lld fid=1 norm=(%.4f,%.4f)",
                 down ? 0x700u : 0x701u, tid, (double)nx, (double)ny);
            send_touch(NULL, tid, 1ll, win, down ? 0x700u : 0x701u, nx, ny, 0.5f);
        }
    }
    /* 再直调 SDL 内部 SDL_SendMouseButton（libSDL3 偏移 0xbd60c）。参数约定取自
     * FCL 版 SDL 内部"触摸合成鼠标"的调用点（0xc0ea8-0xc0ebc）：真实触摸的 DOWN
     * 就是调 SendMouseButton(x0=0, x1=window, w2=-1, w3=1, w4=1, w5=-1)，
     * 其中 w2=-1 是 SDL_TOUCH_MOUSEID——MC 很可能只认这个"触摸合成鼠标"的 id，
     * 而非我们早先用 1。作用：鼠标列表为空时自动补建鼠标设备并原生格式发事件 */
    {
        typedef void (*SendBtnFn)(void *, void *, int, unsigned int, int, long long);
        SendBtnFn send_btn = (SendBtnFn)(base + 0xbd60c);
        LOGI("直调 SendMouseButton：btn=%d down=%d（mouse id=-1）", (int)sdlButton, (int)down);
        send_btn(NULL, win, -1, (unsigned int)sdlButton, down ? 1 : 0, -1ll);
    }
    /* 再直接写 SDL 内部 mouse 结构的按键位。
     * 依据：warp 有效 / 手搓 push 无效，说明 MC 用 SDL_GetMouseState 轮询位置与按键；
     * 反汇编 SDL_GetMouseState：只在 mousecount>=1 时遍历列表，OR 各 mouse 的 [mouse+4]
     * 按键位（mouse 结构 0x18 字节，列表在 [base+0x1f5518]，count 在 [base+0x1f5510]）。
     * 故这里直接改 [mouse_list[0]+4] 的对应位 */
    {
        int cnt = *(int *)(base + 0x1f5510);
        void *list = *(void **)(base + 0x1f5518);
        if (cnt >= 1 && list != NULL) {
            unsigned int *btn = (unsigned int *)((char *)list + 4);
            unsigned int bit = 1u << ((unsigned int)sdlButton - 1u);
            if (down) {
                *btn |= bit;
            } else {
                *btn &= ~bit;
            }
            LOGI("mouse->button 位：写入后=0x%x", *btn);
        } else {
            LOGW("mouse->button 位：mouse 列表为空 cnt=%d", cnt);
        }
    }
    /* finger 路径（关键修正）：真实触摸设备 ID 是 -1/-3/5/6（探针实测）。
     * -1 是特殊值 SDL_MOUSE_TOUCHID；这里优先取一个**非负的真实设备 ID**
     * （遍历 SDL_GetTouchDevices 找 >=0 的），取不到再退回 -1 */
    {
        sdl_finger_buf_t fev;
        int ww = *(int *)((char *)win + 0x20);
        int wh = *(int *)((char *)win + 0x24);
        int fr;
        long long tid = -1ll;
        typedef long long *(*GetTouchDevFn)(int *);
        typedef void (*FreeFn)(void *);
        GetTouchDevFn get_td = (GetTouchDevFn)dlsym(h, "SDL_GetTouchDevices");
        FreeFn freer = (FreeFn)dlsym(h, "SDL_free");
        if (get_td != NULL) {
            int n = 0;
            long long *ids = get_td(&n);
            if (ids != NULL) {
                int k;
                for (k = 0; k < n; k++) {
                    if (ids[k] >= 0) {
                        tid = ids[k];
                        break;
                    }
                }
                if (freer != NULL) {
                    freer(ids);
                }
            }
        }
        memset(&fev, 0, sizeof(fev));
        fev.finger.type = down ? SDL_EVENT_FINGER_DOWN : SDL_EVENT_FINGER_UP;
        fev.finger.timestamp = (get_ticks != NULL) ? get_ticks() : 0ull;
        fev.finger.touch_id = (unsigned long long)tid;
        fev.finger.finger_id = 0ull;
        fev.finger.x = (ww > 0) ? ((float)x / (float)ww) : 0.5f;
        fev.finger.y = (wh > 0) ? ((float)y / (float)wh) : 0.5f;
        fev.finger.pressure = 0.5f;
        fev.finger.window_id = ev.button.window_id;
        fr = push(&fev);
        LOGI("finger 注入：type=0x%x tid=%lld norm=(%.4f,%.4f) ret=%d",
             fev.finger.type, tid, (double)fev.finger.x, (double)fev.finger.y, fr);
    }
#if 0
    {
        sdl_finger_buf_t fev;
        int ww = *(int *)((char *)win + 0x20);
        int wh = *(int *)((char *)win + 0x24);
        int fr;
        static int touch_registered = 0;
        typedef long long (*AddTouchFn)(long long, int, const char *);
        AddTouchFn add_touch = (AddTouchFn)dlsym(h, "SDL_AddTouch");
        if (!touch_registered && add_touch != NULL) {
            long long tid = add_touch(1, 0, "starcloud-touch");
            LOGI("触摸注册：SDL_AddTouch → %lld", tid);
            touch_registered = 1;
        }
        memset(&fev, 0, sizeof(fev));
        fev.finger.type = down ? SDL_EVENT_FINGER_DOWN : SDL_EVENT_FINGER_UP;
        fev.finger.timestamp = (get_ticks != NULL) ? get_ticks() : 0ull;
        fev.finger.touch_id = 1ull;
        fev.finger.finger_id = 0ull;
        fev.finger.x = (ww > 0) ? ((float)x / (float)ww) : 0.5f;
        fev.finger.y = (wh > 0) ? ((float)y / (float)wh) : 0.5f;
        fev.finger.pressure = 0.5f;
        fev.finger.window_id = ev.button.window_id;
        fr = push(&fev);
        LOGI("finger 注入：type=0x%x norm=(%.4f,%.4f) ret=%d",
             fev.finger.type, (double)fev.finger.x, (double)fev.finger.y, fr);
    }
#endif
}
