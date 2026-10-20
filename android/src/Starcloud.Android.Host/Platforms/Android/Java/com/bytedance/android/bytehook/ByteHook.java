package com.bytedance.android.bytehook;

/**
 * 自研（清洁室）JNI 契约类：libbytehook.so 在 JNI_OnLoad 中经 RegisterNatives
 * 把下列 native 方法注册到本类，注册表任一条目缺失即抛 NoSuchMethodError 并
 * 触发 CheckJNI abort（启动链崩溃根因）。
 *
 * 方法名与签名取自 libbytehook.so 内嵌常量（RegisterNatives 表的方法名字面量与
 * JNI 签名描述符），非引用任何第三方源码。除 nativeGetVersion 被 JNI_OnLoad
 * 直接注册外，其余为 bytehook C API 调用时按需查找，宿主不主动调用。
 *
 * 注意：不要在本类添加 System.loadLibrary 静态初始化，加载时机由宿主控制。
 */
public final class ByteHook {

    private ByteHook() {
    }

    /** 版本串，如 "1.0.10"；JNI_OnLoad 首个注册项 */
    public static native String nativeGetVersion();

    /** 架构串，如 "arm64" */
    public static native String nativeGetArch();

    /** 当前工作模式：自动/手动 */
    public static native int nativeGetMode();

    /** 调试开关状态 */
    public static native boolean nativeGetDebug();

    /** 记录开关状态 */
    public static native boolean nativeGetRecordable();

    /** 按索引取 hook 记录 */
    public static native String nativeGetRecords(int index);

    /** 初始化 hook 框架，返回结果码 */
    public static native int nativeInit(int mode, boolean debug);

    /** 添加调用者白名单，返回结果码 */
    public static native int nativeAddIgnore(String caller);

    /** 设置调试开关 */
    public static native void nativeSetDebug(boolean debug);

    /** 设置记录开关 */
    public static native void nativeSetRecordable(boolean recordable);
}
