package com.tungsten.fclauncher;

import dalvik.annotation.optimization.CriticalNative;

/**
 * 自研（清洁室）JNI 契约类：libpojavexec.so 启动时用本类探测运行时是否支持
 * CriticalNative 快速调用约定。契约形状：
 *   - testCriticalNative(int, int) 由 so 经 RegisterNatives 注册为 CriticalNative，
 *     必须带 @CriticalNative 注解，否则 ART 不按 critical 约定链接；
 *   - invokeTest() 是普通 Java 方法（非 native），其方法体触发一次 critical 调用，
 *     供 so 侧验证参数是否按约定正确传递（失败时走 "CriticalNative is not available" 降级）。
 *
 * 方法名、签名与注解由 libpojavexec.so 的查找常量与 ART 运行期报错共同确定，非引用第三方源码。
 */
public class CriticalNativeTest {

    /** CriticalNative 调用约定校验入口（实现由 so 提供） */
    @CriticalNative
    public static native void testCriticalNative(int arg0, int arg1);

    /** 触发一次 critical 调用，供 so 侧判定约定是否生效 */
    public static void invokeTest() {
        testCriticalNative(0, 0);
    }
}
