# MC 26.3 横屏方向问题：进展与明天继续的入口

最后更新：2026-10-05 凌晨（本轮以"崩溃重启"收尾，明天从这里接着改）

## 目标

MC 26.3 在华为 BTK-W00（面板原生方向 = 竖屏，物理横屏使用）上以**横屏**运行且画面方向正确。

## 现状与铁证

- 游戏能跑：`Using graphics backend Vulkan, using drivers: 1.3.296 turnip Mesa driver Mesa 24.3.4`
- 问题：横屏时画面整体转 90°（竖屏模式方向正确，已实测）
- SurfaceFlinger（横屏运行中）：
  - 系统图层（壁纸等）buffer = **2200x1440（横屏）**
  - 游戏 SurfaceView buffer = **1440x2200（面板原生竖屏）**，`geomBufferTransform=7`，
    `geomBufferUsesDisplayInverseTransform=false`
- SDL 日志：`Window size: 2200x1440`（SDL/MC 认为窗口是横屏）
- 截图：MC 已把画面**预旋转 90°** 画进那块竖屏 buffer（界面整体转、比例正常）
- libSDL3.so 导入符号：有 `ANativeWindow_setBuffersGeometry`，**没有** `ANativeWindow_setBuffersTransform`
  （即 SDL 从不补 buffer 变换标记）；且运行期实测 SDL **并未调用** setBuffersGeometry
  → 竖屏 buffer 是 **系统 framework 分配** 的（旋转显示器上的预旋转）
- `libnativewindow.so` 导出 `ANativeWindow_setBuffersTransform`（libandroid.so 里没有）

根因链：(framework 给出原生竖屏 surface) → (Vulkan WSI 上报 currentTransform=ROT90) →
(MC 预旋转作画) → (无人给 buffer 打"已预旋转"标记) → (合成器不做反向补偿) → 画面转 90°。

## 已试过且**无效**的路（勿重复）

1. 环境变量可见性（POJAVEXEC_EGL / SDL_EGL_LIBRARY / SDL_OPENGL_LIBRARY / LD_LIBRARY_PATH）——已用 native getenv 回读证实全部正确
2. 活动方向声明/锁定（sensorLandscape / sensorPortrait / reverseLandscape）
3. 全屏方式：`windowFullscreen` 主题、旧 `setSystemUiVisibility`、`WindowInsetsController` + `setDecorFitsSystemWindows(false)`
4. 改写 SDL 的自然方向/当前旋转（`nativeSetNaturalOrientation` + `onNativeRotationChanged(0)`）——SDL 布局 belief 变了但 buffer 几何不变
5. `SurfaceHolder.setFixedSize(2200,1440)`——只改了图层 contentCrop，导致合成器按错误裁剪**非等比拉伸**（"横屏被压成竖屏"），已移除
6. 反射 `SurfaceView.setTransformHint(0)`——该 API 在 framework 中**不存在**（NoSuchMethodException）
7. GL/zink 路线——MC 的 GL 后端要求桌面 GL 3.3 core，Android 只给 GLES 3.2，必然崩（GLSL 3.30 is not supported）

## 当前代码状态（已装机、会崩溃）

新增 native 库（源码 `NativeLibs/src/starcloud_windowhook.c`，用 NDK 26.1 编成
`NativeLibs/arm64-v8a/libstarcloudwindow.so`，由 `SdlGameActivity.loadLibraries()` 最先加载）：

- **最小 GOT 改写**（自实现，同 bytehook 原理）：`dl_iterate_phdr` 定位模块 + 解析 PT_DYNAMIC/
  DT_JMPREL，直接改调用方 PLT 槽。已验证工作正常：
  - `libSDL3.so` 的 `ANativeWindow_setBuffersGeometry` 命中 1 处
  - `libSDL3.so` / `liblwjgl` 的 `dlsym` 各命中 1 处
- **Vulkan 表面参数归一（换的新思路）**：接管 `dlsym` → 包装 `vkGetInstanceProcAddr` →
  把 `vkGetPhysicalDeviceSurfaceCapabilitiesKHR` 的返回改成"普通横屏表面"
  （`currentTransform = identity`、宽高对调），使 MC 按横屏建交换链且不预旋转，WSI 把窗口
  buffer 几何设为横向尺寸，合成器 1:1 输出。

## 明天第一步（已定位，预计改动很小）

崩溃原因：日志显示 `真实 dlsym=0x0`。我们 `find_symbol_in("libdl.so","dlsym")` 返回空，
因为 Android 10+ 的 libdl.so **只有 DT_GNU_HASH、没有 DT_HASH**（现有实现按 DT_HASH 遍历）。
于是我们插入的 `dlsym` 对任何符号都返回 NULL → LWJGL/SDL 拿到空函数指针 → 崩溃重启。

修法（推荐，最小改动）：
- 把本库里的 `dlsym` 改名为 `starcloud_dlsym`（GOT 改写不需要符号同名），
  这样本库内部调用 `dlsym()` 不再自绑定，直接走 libdl 的真实实现 → `g_real_dlsym` 正确解析；
- 如仍需自解析符号，则补 DT_GNU_HASH 遍历（或改用 `dlvsym`）。

改完后：`真实 dlsym` 应非空，`dlsym 命中 vkGetInstanceProcAddr` 日志出现，
随后看 `原 caps / 改用 caps` 两行，判断 WSI 是否按伪造的横屏 caps 建链。

## 横屏方向问题：已解决（2026-10-05）

Vulkan caps 伪造链路验证成功：
- `dlsym 改名 starcloud_dlsym` 修复崩溃后，`dlsym 命中 vkGetInstanceProcAddr` 命中，
  `vkGetPhysicalDeviceSurfaceCapabilitiesKHR` 伪造（transform→identity + 宽高对调）生效；
- MC 按横屏建交换链，WSI 把窗口 buffer 几何改为横向，方向正确（用户确认「正常了！」）；
- 有趣：r33 轮实测「真实 caps」已是横屏 2200x1440（上局 setBuffersGeometry 改的 buffer 被保留），
  transform=0x2（ROT90 标记仍在，但 MC 不再预旋转）。

## MC 26.3 SDL 输入链定案（2026-10-06，含对上一节结论的修正）

**重要修正**：上一节"触控被除以自动 GUI 缩放（≈6 倍）"的结论**已被推翻**。实测在
`vmmode=0`（直接映射）+ `touchgain_x/_y=1.0` 下，MC 的指针**精确跟随触摸位置**
（tap(1100,745) → MC 指针停在 Singleplayer 正中），**坐标域本来就是对的**
（2200x1440 直读，无缩放）。此前的"左上角"是**虚拟鼠标（trackpad）模式**造成的，
不是 MC 的 belief 问题。`guiScale` / `touchgain` 均无需补偿。

### 已确证的架构事实（反汇编 + 运行期探针双重取证）

- MC 26.3 用 **SDL3**（`org.lwjgl.sdl`，LWJGL 3.4.1），`SDL video driver: android`；
  官方 26.3 起 GLFW→SDL3，且**游戏中固定 relative mouse mode**。
- libSDL3.so 由 FCL APK 提供（`components/out/lwjgl341.zip` 内，可直接本地解出，
  **无需从设备取库**）；`onNativeMouse` 等 JNI 由 `JNI_OnLoad` **动态注册**
  （导出表无 `Java_*`，需解析 JNINativeMethod 表定位，见本轮方法）。
- FCL 只对 9 个窗口/初始化类 SDL 函数做 dlsym 代理
  （`sdlDlsymProxy`：InitSubSystem/CreateWindow/GetWindowFromID/...），**不代理 PollEvent**。
- **SDL 内部无鼠标设备**：`SDL_HasMouse()=0`、鼠标列表 count(`base+0x1f5510`)=0。
  `SDL_GetMouseState` 仅在 count>=1 时遍历列表、OR 各 mouse 的 `[mouse+4]` 按键位。
- `SDL_SendMouseButton`（内部，`base+0xbd60c`）在列表为空时会**自动 realloc 补建鼠标**
  （`0xbd700`）；其首行门控字节 `[base+0x1f53f5]` 必须为 1（实测=1）。
- 取窗口：`[base+0x1f6028]` = Android_Window；鼠标全局位置 `base+0x1f54a0`(x/y)、
  `base+0x1f54b0`(last)；`SDL_GetMouseFocus()` 返回该窗口（焦点正常，窗口 ID=0x2）。
- 事件通路：`SDL_EventEnabled` 对 mouse button(0x401)/motion(0x400)/finger(0x700) 均为 1；
  推送 USER(0x8000) 事件后 PEEK 复查为 0 → **MC 确实在消费 SDL 事件队列**。

### 关键实验结论（失败路径已穷举）

| 注入方式 | 结果 |
|---|---|
| `SDLActivity.onNativeMouse`（DOWN/UD，button=1/0） | 无 hover、无点击 |
| 手搓 `SDL_PushEvent` mouse button（ret=1） | 无效（事件入队但 MC 不响应） |
| 直调内部 `SDL_SendMouseButton`(0xbd60c)（mice 0→1） | 无点击 |
| 写 `mouse[0]+4` 按键位 / 长按 700ms | 无点击（但 hover 高亮出现） |
| 手搓 `SDL_PushEvent` motion(0x400) | **无 hover**（不被 MC 接受） |
| **`SDL_WarpMouseInWindow`(公开 API)** | **hover 高亮生效**（唯一有效路径） |

**定案**：**MC 的指针位置/hover 由 SDL 全局状态（`0x1f54a0`/`0x1f54b0`）驱动**——
`SDL_WarpMouseInWindow` 正是直接写这两处全局，故有效；而任何"纯 `SDL_PushEvent`
入队"的 motion/button **都不被 MC 采用**（即使事件确实进了队列且被消费）。
**但点击（mouse button）注入仍未打通**：无论事件、状态位、长按，MC 都不触发点击。
剩余怀疑：MC 在 Android 上可能进入触摸语义（Mojira MC-268291：Touchscreen Mode: On 时
MC 忽略鼠标左右中键），而 SDL 侧**未注册 touch 设备**（SDL3 未导出 `SDL_AddTouch`），
finger 事件亦无效。

### 下一步候选（给后续接手）

1. **查 MC 的 options.txt**（已可读写：
   `/storage/emulated/0/Android/data/cn.starcloud.launcher.android/files/.minecraft/options.txt`，
   当前**无 `touchscreen` 项**）——尝试写入 `touchscreen:false` 或查 26.3 的触摸判定键，
   排除"MC 自动进入触摸模式导致忽略鼠标键"。
2. **注册 SDL 触摸设备**：找 libSDL3 内部 touch 注册函数（对标 `SDL_SendTouch` 内部路径），
   让 finger 事件可用（MC 若是触摸语义，这是正路）。
3. **反编译 MC 26.3 客户端 jar** 找输入判定（`SdlInput`/`MouseHandler` 类）——需改 package
   为 debuggable 或 root 才能读取设备私有目录。
4. 对照 FCL 官方跑 26.3 的触控行为（其是否正常、如何注册设备）。

### 本轮新增的探针/工具（libstarcloudwindow.so）

- `SDL 探针`：门控字节 / mice / hasmouse / ev_btn|motion|finger / 队列消费（USER+PEEK）
- `桥探针`：libpojavexec 的 `bridge+0x27240/258/250`（GLFW 回调指针）——**实测全为 0**，
  证明 **MC 不注册 GLFW 回调，CallbackBridge 方案（方案 D）不可行**
- JNI：`probeWarpMouse`(SDL_WarpMouseInWindow)、`probeHandMotion`(手搓 motion)、
  `probePushButton`(手搓 button + 直调 SendMouseButton + 写按键位)
- Java 开关：`debug.starcloud.probewarp=1`(启用原生注入)、`handmotion=1`(手搓 motion)、
  `vmmode=0`(直接映射)、`touchgain_x/_y=1.0`

### 方案 B：FCL 定制 SDL3 的输入投递链（2026-10-06 续，已摸清全链）

**定位手段**（无 root / release 包下的可行路径）：
- libSDL3.so 从 `android/components/out/lwjgl341.zip` 本地解出即可，无需从设备取
- JNI 函数表：`JNI_OnLoad`(0xafab0) → `RegisterNatives`(vtable[0x6b8])，共 5 组；
  第一组类含 **44 个方法**，表在 `vaddr 0x1ecfe8`（.data 段）。
  **该表在文件里为全 0**（靠 `R_AARCH64_RELATIVE` 重定位填充）：用 `llvm-readelf -r`
  取 addend 即可还原 name/signature/fnPtr 三元组。

**还原出的关键 JNI 入口**：

| 方法 | 签名 | 地址 |
|---|---|---|
| onNativeTouch | (IIIFFF)V | 0xb1914 |
| onNativeMouse | (IIFFZ)V | 0xb1a78 |
| onNativePinchStart/Update/End | — | 0xb1994 / 0xb19dc / 0xb1a30 |
| onNativePen | (IIIIFFF)V | 0xb1aec |
| nativeAddTouch | — | 0xb1310 |
| onNativeKeyDown/Up | (I)V | 0xb183c / 0xb1880 |

**输入投递链（反汇编实证）**：

```
onNativeTouch(0xb1914)
  └─ Android_OnTouch(0x1d9c28)
       ├─ touchID 规范化: x20 = (w1>=0 ? w1+1 : w1);  fingerID = pointerFingerId+1
       ├─ 按 action 分派: 0(DOWN)→0x700, 1/6(UP)→0x701, 3(CANCEL)→0x703, 2(MOVE)→0xc12a8
       └─ SDL_SendTouch 内部实现(0xc0d9c)  ← 【真实投递函数】
            ├─ type==0x700 且门控 [base+0x1f5590]bit0==0 时：
            │    · 由归一化坐标 × 窗口 w/h 算出像素坐标
            │    · 调 0xbce38 更新鼠标位置、0xbdad0 发 mouse motion
            │    · 再调 0xbdad0(=SDL_SendMouseButton, w5=-1) 发 **mouse 左键 DOWN**
            │      参数为 (x0=0, x1=window, w2=-1, w3=1, w4=1) —— 关键：**mouse id = -1**
            │      （SDL_TOUCH_MOUSEID，即"触摸合成鼠标"）
            ├─ finger 状态写入设备链表的 finger 结构 {id@0, x@8, y@0xc, pressure@0x10}
            └─ 构造 finger 事件后 SDL_PushEvent
```

**finger 事件布局**（与 SDL3 官方 SDL_TouchFingerEvent 完全一致）：
`type@0 / touchID@16 / fingerID@24 / x@32 / y@36 / dx@40 / dy@44 / pressure@48 / windowID@52`
（x/y 为**归一化 0..1**，SDL 内部再乘窗口 w/h 转像素）

**重要旁证**：`SDL_SendTouch` 自己也是用 `SDL_PushEvent` 投递的——**说明 SDL_PushEvent 通道本身没问题**。
`SDL_GetTouchDevices`(0xc08a8) 揭示触摸设备列表：count@`base+0x1f5580`、array@`base+0x1f5588`；
实测 **touches=4，ID = -1 / -3 / 5 / 6**（-1 为 SDL_MOUSE_TOUCHID）。
`0x710/0x711/0x712` 是 FCL 自有的**捏合手势**事件（onNativePinch*），**不是** finger。

**门控/状态字节小结**：
- `base+0x1f53f5` == 1（SDL_SendMouseButton 前置门控，实测=1）
- `base+0x1f5590` bit0 == 0（触摸→鼠标合成开关，实测=0，即允许合成）
- `base+0x1f5510/0x1f5518` = 鼠标列表 count/array；`base+0x1f5518[0]+4` = 按键位
- `base+0x1f54a0/a4` = 鼠标 x/y；`base+0x1f54b0` = last x/y；`base+0x1f6028` = Android_Window

**方案 B 的验证结果（仍失败）**：
即便**直接调用 0xc0d9c（SDL_SendTouch），走与真实触摸完全相同的链路**（含 SDL 自动合成
mouse motion + mouse button，mouse id=-1），MC 依旧不切换界面；
直调 `SDL_SendMouseButton(mouse id=-1)` / 写 `mouse[0]+4` 按键位 / 700ms 长按，
以及**完整的 onNativeTouch 原生链**（vmmode=0 + touchmouse=0）**同样不触发点击**。

**当前结论**：MC 26.3 在本环境下的**点击**（mouse button / finger 两条链）始终不生效，
而**指针位置/hover 可用**（`SDL_WarpMouseInWindow` 有效，因它直接写全局状态），
唯一可见的"输入被处理"痕迹是某次注入后 MC 的界面语言被意外改成了世界语（待查）。
剩余怀疑方向：MC 26.3 桌面构建在 Android 上的输入适配缺陷；或 MC 侧存在未被发现的
输入开关（options.txt 里无 `touchscreen` 项）。建议后续改 package 为 debuggable / root
后反编译 MC 客户端 jar 直接核对输入判定逻辑。




症状：横屏下触控只在**左上角约 1/6~1/7 的区域**有响应（用户实测：点哪里都映射进该区域）。

已排除（铁证）：
- SDL 窗口 belief 正确：探针实测 `SDL_GetWindowSize=2200x1440, SDL_GetWindowSizeInPixels=2200x1440`
  （注意：FCL inline-hook 了 `SDL_GetWindowFromID`——探针 16 个 ID 全返回同一指针）
- Java 侧 SDLSurface 归一化正确（raw→norm→SDL_SendTouch 链路日志核对过）
- MC 的 GUI 布局是正常横屏（用户确认画面无拉伸、按钮横屏比例）
- 画面显示方向已正确（上一节）

关键实验（options.txt 的 guiScale）：
- `guiScale:0`（自动，约 6~7 倍）→ 触控响应区 ≈ 1/6~1/7（≈2200/320=6.875 → 6）
- `guiScale:1` → UI 元素变小（渲染跟随 options），但**触控响应区不变**（仍 1/6~1/7）
- 结论：**MC 26.x 的触控输入坐标被除以了独立的「自动 GUI 缩放」（≈像素宽/320≈6）**，
  该值不随 options 的 guiScale 变——输入映射与渲染缩放脱钩，导致 UI 空间里只有左上
  1/6 区域能命中（其余越界被 clamp）
- options.txt 已恢复 `guiScale:0`（测试后还原）

下一步候选（按优先级）：
1. **补偿法**：在 Java 归一化（SDLSurface.getNormalizedX/Y）乘一个增益 gain=自动缩放值（≈6），
   让 MC 除回去后恰好等于真实坐标。注意 SDL 仿真鼠标会 clamp 到窗口边界——若 MC 走的是
   仿真鼠标路径此法无效（坐标被截断），若走 FINGER 路径（norm 无 clamp）则有效；
   先加 setprop 调参（debug.starcloud.touchgain）免重编试 6.0 / 6.875 / 7.0
2. **查 MC 26.x 输入源码**：客户端 jar 在设备 .minecraft 下，反编译找
   finger/mouse → GUI 坐标映射点，确认除以的是什么（autoScale? dpi? window/320?）
3. 对比 FCL/Pojav 上游是否已修此问题（他们跑 26.x 触控正常与否）——若上游正常，看其
   窗口尺寸/密度设置与我们差异（可能我们没有给 SDL 正确的 DPI，MC 自动缩放算错）
4. 若是 DPI 问题：检查 nativeSetScreenResolution 传给 SDL 的 density（480/160=3.0），
   MC 可能用 density 推导输入缩放（3≠6，若公式为 dpi/80=6 吻合）


- 设备：华为 BTK-W00，`MEEYD23C06205802`，Android 12（API 31），物理 2200x1440 / 面板原生 1440x2200
- adb：`C:\Users\xingmiao\AppData\Local\Android\Sdk\platform-tools\adb.exe`
- NDK：`C:\Users\xingmiao\AppData\Local\Android\Sdk\ndk\26.1.10909125`
- 编译 hook 库：
  `aarch64-linux-android28-clang.cmd -shared -fPIC -O2 -o NativeLibs\arm64-v8a\libstarcloudwindow.so NativeLibs\src\starcloud_windowhook.c -llog`
- 构建：`dotnet build android/src/Starcloud.Launcher.Phone/Starcloud.Launcher.Phone.csproj -c Release -f net10.0-android -p:RunAOTCompilation=false -p:AndroidPackageFormat=apk`
- 免重编调参：`adb shell setprop debug.starcloud.wtransform inverse|identity|rot90|rot270`
- 最近日志：`%TEMP%\logcat-stream-r30.log`（本轮）、r29（去掉 setFixedSize 后）

---

# 触控层定案（Bedrock 风格控制器，2026-10-06）

上面「点击始终不生效」的结论**已被推翻**——真正原因有两处，均已修复并实机验证。

## 根因 1：坐标域（已验证）

MC 的 `MouseHandler.getScaledXPos = xpos × guiScaledWidth / fbWidth`，除数是 **framebuffer**
尺寸而非窗口尺寸。本机 `fb=854x480`、窗口 `2200x1440`，宽高比不同（1.779 vs 1.528），
故注入绝对坐标前必须乘 `gain = fb / 窗口`：

```
gain_x = 854 / 2200 = 0.3882      gain_y = 480 / 1440 = 0.3333
```

（`debug.starcloud.touchgain_x/_y` 可取覆盖；早期误用 1/density=0.5333 是"点哪都不中"的表象原因）

## 根因 2：按键状态位图（关键，最易踩坑）

SDL 的 `Android_OnMouse`（`src/video/android/SDL_androidmouse.c`，本仓 libSDL3.so 已确认引用
`setRelativeMouseEnabled`）用**状态位图 + 差集**判定按键：

```c
case ACTION_DOWN: changes = state & ~last_state;  button = TranslateButton(changes); last_state = state; ...
case ACTION_UP:   changes = last_state & ~state;  button = TranslateButton(changes); last_state = state; ...
```

因此 `onNativeMouse` 的 `button` 参数是 **MotionEvent 按键位**（`BUTTON_PRIMARY=1/SECONDARY=2/TERTIARY=4`），
且 **ACTION_UP 必须传"抬起后的状态"（通常 0）**。若 UP 仍传键号本身，`last_state` 永不清零 →
第二次及以后的点击全部失效（现象：只有重启后第一次点击有效）。FCL 的
`CallbackBridge.sendMouseKeycode` 维护 `sMouseButtonState` 正是这个原因。

## 模式判定（游戏内 vs 菜单）

MC 的 `InputConstants.grabMouse()` 调用 `SDL_SetWindowRelativeMouseMode(handle, true)`
（`releaseMouse` 传 false，反编译 `InputConstants.grabMouse/releaseMouse` 实证）；
SDL 的 Android 后端 `Android_SetRelativeMouseMode` → JNI `SDLActivity.setRelativeMouseEnabled(enabled)`。

故在 `SDLActivity` 记录 `sRelativeMouseActive`（`isRelativeMouseActive()`），
即可精确等价于「MC 已抓取鼠标 = 游戏内且无 GUI」。实测进世界 `auto=true`、
开背包/暂停菜单 `auto=false`，完全可靠。

## 转视角（相对鼠标）

MC `MouseHandler.onMove` 在 `mouseGrabbed==true` 时累加 **xrel/yrel**。注入方式：

```java
SDLActivity.onNativeMouse(0, MotionEvent.ACTION_MOVE, dx, dy, /*relative=*/true);
```

`Android_OnMouse` 对 DOWN/UP/MOVE 都会先发一次 `SDL_SendMouseMotion(relative, x, y)`，
**游戏内按键事件必须传 (0,0)+relative=true**，否则绝对坐标在相对模式下会产生巨大 xrel 使视角跳变。

## 实现落点

- `SDLSurface.java`：`handleTouchController()` 按 `pointerId` 分配角色
  （摇杆 / 视角 / 按钮 / 菜单指针），支持"摇杆 + 转视角"双指并行；
  `drawTouchUi()` 在 SDLSurface 父容器（`SDLActivity.mLayout`）上叠加一个**不可点击**的
  View 绘制摇杆/按钮/箭头指针（不吃触摸，触摸仍由 SDLSurface 处理）；
  `uiPulse` 每 250ms 轮询模式，切换时释放所有由触控层按下的输入。
- 区域：左侧 42% 宽且 y>20%h = 摇杆；按钮命中优先；其余 = 视角；菜单模式 = 绝对指针 + 左键。
- 按键走 `SDLActivity.onNativeKeyDown/Up(Android 键码)`：W=51 A=29 S=47 D=32 SPACE=62
  SHIFT=59 E=33 ESC=111（实测 E 开背包、ESC 开暂停菜单、W 前进均生效）。
- 兜底：左上「换」按钮手动强制游戏内布局（自动判定失效时用）。

## 实机验证结论（26.3，原版 jar，显示 "Minecraft 26.3"）

进世界 → 菜单/游戏布局自动切换；菜单点击（含连续多次）命中；拖动右侧转视角；
摇杆前进位移明显；「挖」长按挖掉方块并拾取；「包」开背包；「退」开暂停菜单。

## 测试工具坑

- `adb shell input tap / swipe` **会**应用旋转变换，坐标按横屏 (2200x1440) 给；
- `adb shell input motionevent` **不应用**同样的变换（本机为竖屏原生面板旋转横屏），
  坐标会被映射到别处 → 用它测"按住某按钮"会误触。**按住按钮请用
  `input swipe x y x y <duration>`**（起点=终点即可当长按）。
- MC 的 stdout/stderr 在
  `/storage/emulated/0/Android/data/cn.starcloud.launcher.android/files/.minecraft/jvm-stdout.log`；
- 插桩版 MC jar 验证完已还原为原版（设备上 `26.3.jar.origbak` 为原版备份）。
