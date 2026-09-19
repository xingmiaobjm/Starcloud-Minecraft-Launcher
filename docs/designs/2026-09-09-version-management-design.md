# 版本管理增强（A 阶段）设计

日期：2026-09-09
状态：设计已确认（逐段确认完成），待写实现计划

## 1. 背景与问题

对标主流启动器（PCL2 / HMCL / Prism），当前版本页能力过薄：仅 刷新 / 打开目录 /
问 Nova / 删除 四个操作，没有版本详情、没有每版本设置、没有隔离概念、
没有重命名 / 复制 / 图标 / 分类 / JSON 查看，mod 与存档管理也缺乏版本上下文。

按 brainstorming 阶段确认的推进顺序，本 spec 只覆盖 **A 阶段：版本管理增强**，
它是 B（每版本 Mod 管理）与 C（高级导出整合包）的地基。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 拆解顺序 | **A 版本管理增强 → B 每版本 Mod 管理 → C 高级导出整合包**，本 spec 只做 A |
| 隔离模型 | **三态跟随**：shared / isolated / null（跟随全局默认） |
| 每版本设置范围 | **进阶全量**：基础套餐 + 自动连服 + 环境变量 + 启动前/后命令 + 语言覆盖 |
| UI 形态 | **版本详情子页**：VersionsPage 点版本进详情（概览 / 设置 / 内容 / 操作分区） |
| 实现方案 | **方案一层叠**：独立 version-settings.json + InstanceLayout + 启动层叠合并，存量零迁移 |

## 3. 数据模型

### 3.1 存储

新存储文件：`<.minecraft>/starcloud/version-settings.json`
（跟游戏目录走，多游戏目录互不干扰；`starcloud/` 目录同时是启动器在游戏目录内的命名空间）

```json
{
  "version": 1,
  "profiles": {
    "1.20.1-fabric": {
      "isolation": null,
      "memory_min_mb": null,
      "memory_max_mb": null,
      "java_path": null,
      "window_width": null,
      "window_height": null,
      "fullscreen": null,
      "extra_jvm_args": [],
      "extra_game_args": [],
      "server_address": "",
      "env_vars": {},
      "pre_launch_command": "",
      "post_launch_command": "",
      "language_override": "",
      "icon_path": "",
      "category": "",
      "note": ""
    }
  }
}
```

- 字段全空 / 无该版本条目 = 完全跟随全局，行为与现状一致（**存量零迁移**）
- `isolation`: `"shared"` | `"isolated"` | `null`（null = 跟随全局默认隔离模式）
- `fullscreen`: `true` / `false` / `null`（null = 跟随全局）
- `env_vars`: 对象 `{ "名": "值" }`
- 写入：防抖 500ms，原子写（temp 文件 + replace）

### 3.2 InstanceLayout 服务（新）

职责：给定版本 → 解析 **GameRoot**（游戏工作目录）：

- isolation = isolated → GameRoot = `versions/<id>/`
- 其余（shared / null 且全局默认 shared）→ GameRoot = `.minecraft/`

**隔离范围只有 5 个内容目录**：`mods` / `saves` / `config` / `resourcepacks` / `shaderpacks`。
`libraries` / `assets` / `natives` / 版本 json / jar 永远共享（省磁盘、简化安装链）。

isolated 模式下 GameRoot 即版本自身目录（PCL 风格）：内容目录成为
`versions/<id>/mods` 等子目录；`options.txt` 也随之独立。

### 3.3 层叠合并（EffectiveLaunch）

启动时合并：**全局设置 + 全局默认隔离 + 版本覆盖 → EffectiveLaunch**：

| 字段 | 层级 | 未覆盖时 |
|---|---|---|
| isolation | 版本 → 全局默认 | 全局默认（新全局设置项，SettingsPage 新增「默认隔离模式」单选：共享 / 独立） |
| memory_max_mb / java_path / window_width / window_height / fullscreen | 版本 → 全局 | 全局当前值 |
| memory_min_mb | 仅版本 | 内置默认 1024 |
| server_address / extra_jvm_args / extra_game_args / env_vars / pre_launch_command / post_launch_command / language_override | 仅版本 | 关闭 / 不执行 / 不生效 |
| icon_path / category / note | 仅版本（元数据，不参与启动） | 空 |
| last_played_at | 仅版本（元数据，启动成功后由启动器写入） | null |

## 4. 启动链路改造（GameLauncher）

- 解析 EffectiveLaunch 后，`game_directory` 变量与进程工作目录改用 **GameRoot**
- 类路径 / natives / assets / libraries 仍指向 `.minecraft` 共享区
- JVM 参数：`-Xms`（memory_min）/ `-Xmx`（memory_max）按合并值生成
- 新增启动参数注入（≥ 1.13 时）：`--width` / `--height`；fullscreen = true 时追加 `--fullscreen`
- 额外 JVM / 游戏参数按原样追加
- 环境变量：启动进程前合入（版本 env_vars 覆盖同名继承变量）
- 启动前命令：启动前执行，`$GAME_DIR` 占位符展开为 GameRoot；
  退出码 ≠ 0 → 默认阻断并弹窗「仍然启动 / 取消」；失败仅写日志不弹窗的是启动后命令
- 启动后命令：游戏进程退出后执行，失败仅记日志
- 自动连服：`server_address` 非空且版本 ≥ 1.20 → 走 `--quickPlayMultiplayer`
  （1.20 以下设置不生效，UI 已提示）
- 语言覆盖：启动前按 GameRoot 写 `options.txt` 的 `lang`

## 5. UI 设计

### 5.1 版本列表页（VersionsPage 改造）

- **工具行**：新增搜索框（按 ID / 类别过滤）+ 分类筛选下拉（全部 / 未分类 / 自定义类别）
- **版本行升级**：
  - 左：版本图标（icon_path，缺省默认 FontIcon）+ ID + 类别徽章 + 隔离标识（「独立」小徽章）
  - 右：大小 + 上次启动时间 + 「…」更多按钮
- **右键 / 更多菜单**：启动、版本详情、打开目录、重命名、复制版本、查看 JSON、
  导出整合包（C 阶段启用，先灰置占位）、问 Nova、删除
- **单击行** → 进入版本详情子页

### 5.2 版本详情子页（新页面 VersionDetailPage）

- 导航：`ContentFrame.Navigate(typeof(VersionDetailPage), versionId)`，
  顶栏返回按钮 `Frame.GoBack()`，NavigationView 选中态保持「版本」
- 布局沿用现有单列卡片风格（MaxWidth 1000）：
  - **头部**：大图标 + 版本 ID + 类别 / 隔离徽章 + 主按钮「启动」+ 换图标按钮
  - **卡片「概览」**：GameRoot 路径预览、上次启动时间、可编辑类别与备注
  - **卡片「游戏设置」**：见 §6
  - **卡片「内容」**：mods / saves / config / resourcepacks / shaderpacks 五行，
    各带「打开目录」；Mod 管理列表 B 阶段做，本阶段先做入口
  - **卡片「操作」**：验证文件、重命名、复制版本、查看 JSON、删除（危险区红字样式）

## 6. 「游戏设置」卡片交互逻辑

### 6.1 通用规则（卡片级）

| 规则 | 说明 |
|---|---|
| 数据流 | 进入详情页 → 读 version-settings.json 该版本 Profile（无则全空 = 全跟随全局）→ 控件显示覆盖值，无覆盖显示全局生效值 |
| 保存 | 即改即存：修改后防抖 500ms 原子写入；离开页面强制 flush |
| 恢复跟随 | 有全局层的字段（内存 max / Java / 窗口宽高 / 全屏 / 隔离），行尾「恢复跟随」按钮（仅已覆盖可见）→ 清除覆盖并回显全局值 |
| 校验 | NumberBox / TextBox 非法值：红框 + 行下红字提示，不写入；修复后自动恢复 |
| 运行中 | 该版本进程运行中 → 卡片顶部黄色提示条「设置将在下次启动生效」；隔离模式切换禁用（涉及目录移动） |
| 下载/校验中 | 同上提示 + 禁用涉及目录的操作 |

### 6.2 字段级逻辑

1. **隔离模式（RadioButtons，置顶）**
   - 三选：跟随全局默认 / 共享（.minecraft）/ 独立（versions/<id>/）；
     下方灰字实时预览 GameRoot 路径
   - 切换时若新旧 GameRoot 不同 → 弹迁移 ContentDialog（见 §7.4）；
     点「取消」→ RadioButtons 还原切换前值
   - 「跟随全局默认」同样触发迁移（预览行如实显示实际 GameRoot）

2. **内存 min / max（NumberBox）**
   - 步进 512，范围 512–131072 MB
   - 约束 min ≤ max：违例 → 两者红框 + 红字「最小值不能大于最大值」，不写入
   - min / max 未覆盖均显示全局当前值（对应现有全局最小 / 最大内存设置）

3. **Java（ComboBox + 浏览）**
   - 下拉项：跟随全局（置顶，选中即清除覆盖）+ JavaService 已扫描 JDK
   - 「浏览」：拾取 java.exe → `java -version` 校验 → 成功加入下拉并选中（存绝对路径）；
     失败 ContentDialog 报错不选中

4. **窗口宽 / 高 / 全屏**
   - 宽 / 高 NumberBox（200–32767），未覆盖显示全局值
   - 全屏：RadioButtons 跟随全局 / 开 / 关（ToggleSwitch 无法表达三态）

5. **自动连服地址（TextBox）**
   - 格式 `host[:port]`，port 1–65535，非法红框不写入
   - 版本 ≥ 1.20 → 灰字「启动时通过 quickPlayMultiplayer 自动连接」；
     < 1.20 → 橙色警告「此版本不支持自动连服，设置不生效」
   - 仅版本层字段：无「恢复跟随」，清空即关闭

6. **环境变量（可编辑列表）**
   - 行 = 名称 TextBox + 值 TextBox + 删除按钮；底部「添加变量」
   - 校验：名称非空、不以数字开头、仅字母 / 数字 / 下划线；
     名称重复 → 后者覆盖前者并黄字提示；值允许为空
   - 任一行改动即整表防抖写入

7. **启动前 / 后命令（TextBox）**
   - 单行，支持 `$GAME_DIR` 占位符（展开为该版本 GameRoot）
   - 启动前：失败（退出码 ≠ 0）→ 弹窗「仍然启动 / 取消」询问（不加新字段）
   - 启动后：进程退出后执行，失败仅写日志不弹窗
   - 仅版本层字段，清空即不执行

8. **语言覆盖（ComboBox）**
   - 项：跟随全局（置顶）+ zh_CN / zh_TW / en_US / ja_JP
   - 未覆盖 → 灰字显示全局当前语言；启动前按 GameRoot 写 `options.txt` 的 `lang`

9. **额外 JVM / 游戏参数（TextBox，多行，每行一个参数）**
   - 仅版本层字段；空行忽略；原样追加到启动命令（JVM 参数区 / 游戏参数区）

## 7. 关键操作细节

| 操作 | 细节 |
|---|---|
| 重命名 | ContentDialog 输入新 ID → 校验合法字符（Windows 目录名规则）→ json 文件名 + 目录名（含 jar）整体重命名 + json 内 `id` 字段同步；运行中 / 下载中禁用 |
| 复制版本 | 输入新 ID + 勾选「同时复制 mods/存档等隔离内容」（仅源为 isolated 时出现）→ 复制 json（改 id）+ jar + 可选隔离目录；共享内容不复制 |
| 查看 JSON | ContentDialog 只读格式化显示 + 「打开所在文件夹」按钮 |
| 隔离切换 | 切 RadioButtons 即弹迁移对话框：标题「切换版本隔离」，正文列出实际将移动的目录（5 个内容目录中**已存在**的，空目录不列）；按钮：**立即移动（默认）/ 仅切换不移动 / 取消**；重名双方保留，新位置加 ` (2)` 后缀；任一移动失败 → 回滚已移动项 + 报错，选择还原 |
| 删除 | 沿用现有确认；isolated 版本额外提示「将同时删除独立目录下的 mods/存档等」 |

## 8. 验收标准

1. `dotnet build` 0 错误（沿用现有基线）
2. UIA 冒烟：列表 → 点版本进详情 → 改内存 / 隔离 → 返回后徽章更新 → 重启后设置保留
3. isolated 版本启动后 `game_directory` 指向 `versions/<id>/`
4. 隔离切换触发迁移对话框且可取消；重名文件双方保留加后缀
5. 重命名后 列表 / JSON / 启动 三者一致

## 9. 非目标

- B 阶段：每版本 Mod 管理列表（本阶段详情页「内容」卡片只做目录入口）
- C 阶段：高级导出整合包（列表菜单灰置占位）
- 全局设置页除新增「默认隔离模式」单选外不做其他改动
