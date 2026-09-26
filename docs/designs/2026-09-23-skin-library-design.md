# 多皮肤管理 + 离线换肤设计

> 交付版本 0.3.1（**0.3.1 修订**：本地皮肤库收窄到只服务离线账户，第三方改走皮肤站衣柜）。
> 目标：皮肤这件事在启动器里说得清、换得成 —— 离线账户管本地库，第三方账户管皮肤站上的皮肤，
> 正版账户不掺和。

## 1. 需求与边界

| 账户类型 | 启动器里怎么换 | 游戏里谁能看到 |
| --- | --- | --- |
| 离线账户 | 本地皮肤库（导入 / 默认皮肤 / 应用 / 取消），随时切换 | 本机可见（单人 / 局域网）；同房间其他玩家通常看到默认皮肤 |
| 第三方（外置）账户 | **皮肤站衣柜**：列出该账号在皮肤站上的皮肤，点一张即为当前皮肤 | authlib-injector 注入后所有人可见（皮肤本来就是站点给角色下发的） |
| 微软正版账户 | **不提供换肤**（只读说明） | —— |

为什么正版账户不给换：皮肤归微软账号所有，**游戏里读的是账号档案里的皮肤**，
启动器在本地贴一张纹理进去根本不进游戏。曾经保留过「同步到账号」（官方 `PUT /profile/skins`），
但那条路要求本次运行里有 Minecraft 令牌，多数时候点下去只是白点一次、徒增困惑，
所以 0.3.1 把它连同按钮一起撤掉——换正版皮肤去官方渠道。

三条诚实边界写在界面上，不靠用户猜：

1. 离线账户的皮肤靠**本机纹理注入**生效：单人 / 局域网能看见，同房间其他人通常是默认皮肤；
2. 正版账户区间里只有一句说明，没有一堆点了没用的按钮；
3. 第三方换肤依赖皮肤站的开放 API 与本次运行的登录令牌（令牌不落盘，重启后要重新登录一次）。

## 2. 皮肤库

`SkinLibraryService`（Core）管一个目录 `%APPDATA%/starcloud-launcher/skins/`：

- 一张皮肤一个 PNG（`<id>.png`，id 为 12 位随机 hex）+ 一份索引 `library.json`；
- 条目字段：`id / name / file / model / source / source_url / width / height / added_at`；
- 合法纹理判定与渲染层**同一口径**（`PngDecoder` + `SkinTexture.IsValidSize`）：
  64×32、64×64 或它们的等比放大，非 PNG / 尺寸不符当场拒绝并给出可读原因；
- 型号 `auto / classic / slim`：`auto` 时按纹理判定（双层皮肤在 (54,20) 处透明 = 纤细手臂），
  `DetectSlim` 与账户页 3D 预览共用；
- 索引写入是原子的（`.tmp` + `File.Move(overwrite)`）；索引损坏按空库处理，
  不让一条坏 JSON 把整个换肤功能拖下水。
- **读可以兜底，写必须拒绝**：索引读不出来时按空库返回是给*界面*用的兜底，
  但写路径如果也照着这个空库落盘，就成了"用户攒的皮肤 + 索引一起没了"。
  所以写路径（导入 / 改名 / 改型号 / 删除）单独走 `TryLoad`：读不出来就当场返回可读错误、
  一个字节都不写。删除另外记一行日志（谁的、什么来源、还剩几张），出问题时有据可查。

导入来源两种：本地文件（`ImportPath`）与链接（`ImportUrlAsync`，走启动器下载引擎拿镜像与重试）。
账户只记 `skin_id` + 型号覆盖（`accounts.json`）；**删皮肤时 `ClearSkinReference` 会清掉全部引用**，
不会留下指向空洞的引用。

预置皮肤的来历必须可辨认：写进库的预置条目 `source = preset`、`source_url = 预设 id`。

**头像不能直接贴纹理**：账户挂了库里的皮肤之后，头像一度是"把 64×64 纹理塞进小方框"——
屏幕上出来的是头、身子、胳膊挤在一起的一团色块。头像走与卡片同一条光栅化路径，只画头部
（`RenderOptions.HeadOnly`）出 128×128 的正面头像（平视、居中，头部占画面约七成半），
落盘缓存到 `cache/skin-heads/`。首字母色块仍然是最底层兜底：没有皮肤、渲染失败、无网络都退回它。
0.3.1 早期版本的「默认皮肤」是启动器自己用代码画的（`classic` / `slim` / `desert` …），
后来换成了游戏官方资产（`steve` / `alex` / `ari` …），老条目就成了库里来路不明、画风也不对的东西，
甚至挂在用户正在用的账户上。`SkinPresetMigration.PurgeLegacyPresetSkins` 在启动时做一次幂等清理：
只删「`source = preset` 且 `source_url` 不在当前 `SkinPresets.All` 里」的条目，删完 `ClearSkinReference`；
用户自己导入的（`file` / `url`）一律不动。日志会记一行清理结果。

## 3. 离线账户怎么让游戏里真的看到

客户端拿皮肤的唯一途径是「profile 的 `textures` 属性里有一个 http(s) URL，自己下载」。
正版 / 第三方由会话服务器给这个属性；离线账户没有会话服务器，于是启动器自己补上：

```
启动器起一个只监听 127.0.0.1 的极简 HTTP 服务（随机端口）
   ↓ GET /skin/<uuid>.png
启动时注入 --userProperties
   {"textures":["<base64 of {profileId, profileName, textures:{SKIN:{url}}}>"]}
```

### 3.1 版本边界（重要）

`--userProperties` 的解析方式在 1.19.3 变过一次：

- **≤ 1.19.2**：属性值是原样 JSON，客户端会把它当成未签名属性使用 → 本项目注入**有效**；
- **≥ 1.19.3**：客户端要求属性整体 Base64，并且只把**签名过的**属性交给
  `PlayerSocialManager`。启动器没有 Mojang 的签名私钥，因此这段注入在新版本里
  **只保证不报错、不保证生效**。

编码按版本号选择（`OfflineSkins.NeedsBase64`）：`1.19.3+`、快照（`24w14a` 这类）、
以及任何解析不出主次号的版本一律走 Base64；更早版本用原样 JSON。

对 1.19.3+ 的离线皮肤，本轮采用「能保证的部分」：

1. **启动器内部全部形象预览**都读库内皮肤（账户页 3D 形象、账户卡头像、首页头像），
   所以离线用户至少在自己这一侧看到的是一致的；
2. 回环服务照常提供纹理，`launcher.log` 的 `[skin]` 分类会记录
   「游戏已取走离线皮肤」——这是判断注入是否被客户端采纳的证据；
3. 旧版本（≤ 1.19.2）离线账户在游戏里直接可见。

**正版账户也走同一条注入通道**（`LaunchAccountService` 的微软分支同样带 `SkinUrl`），版本边界完全一样：
1.19.2 及更早游戏内直接生效，1.19.3 起不保证生效。所以界面上正版账户每次更换皮肤前都会弹一次
强制警告，把"只改本机形象 / 不动微软账号上的官方皮肤 / 不保证所有版本生效"讲清楚。

未来若要覆盖 1.19.3+ 的离线皮肤，兜底方案是内置 CustomSkinLoader 之类的客户端皮肤 Mod
（启动器自动装到对应实例），代价是引入第三方 Mod 依赖，需要单独一轮评估。

### 3.2 回环服务的安全边界

- 只绑 `127.0.0.1`，端口由系统分配（避免与其他程序撞端口，也避免固定端口被外部探测）；
- 只实现两个 GET：`/`（健康检查）与 `/skin/<uuid>.png`；
- 返回内容只来自皮肤库文件，不做任何磁盘路径拼接，不暴露启动器其他数据；
- 用裸 `TcpListener` 而不是 `HttpListener`：后者在 Windows 上对非管理员进程需要 urlacl
  注册，会因为「访问被拒绝」直接起不来；
- 进程退出即关闭（`OfflineSkins.Reset()` / `Dispose`）。

## 4. 第三方「皮肤站衣柜」

第三方账号的皮肤本来就存在**皮肤站**上（Blessing Skin / LittleSkin 一系的开放 API），
所以这里不去碰本地皮肤库，而是把站上属于该账号的皮肤列出来、点一张换掉：

| 动作 | 接口 | 说明 |
| --- | --- | --- |
| 列出衣柜 | `GET {base}/api/v1/user/skins?page=1&page_size=100` | 分页包裹（`data.data`）与直接数组（`data`）都认；条目取 `tid`/`id`、`name`、`model`、`url` |
| 设为当前 | `PUT {base}/api/v1/user/profile/skin` + `{"tid": …}` | 皮肤站支持「直接选中」时一步到位，皮肤不下载、不重传 |
| 回退 | 下站上的 PNG → `PUT {base}/api/v1/user/profile/skin` + `{model, file: base64}` | 服务端不认 `tid` 写法（400/404/405/501）时用；口径是标准 Yggdrasil 换肤，站站通用 |

失败一律翻译成人话：401/403 → 「登录状态失效，请重新登录」（回退也过不去，直接失败）；
404/405/501 → 「这个认证服务器没有开放皮肤站接口」；HTTP 200 但业务 `code != 0` → 取 `message`
当失败（Blessing Skin 一系就是这么报错的，不能只看状态码）。

卡片缩略图不是把 64×64 纹理直接贴上去，而是**下载后走本地皮肤卡同一条渲染**（`SkinThumbnails`：
后台线程解码 + 光栅化 + 落盘缓存，`BitmapImage` 回到 UI 线程建）。衣柜一次最多渲染
`WardrobeThumbLimit = 24` 张，避免一次几十个请求把界面拖住。

> 令牌从哪来：`AccountStore` 的内存会话（`GetSession`）。第三方 token **不落盘**，
> 所以重启启动器后第一次进这页会提示「点『重新登录该账号』」——那个弹窗与启动游戏时的
> 就地登录是同一个（`AuthDialogs.ExternalPasswordAsync`），登录完直接刷新衣柜。

## 4.1 正版「同步到账号」（0.3.1 已撤下界面）

`SkinUploadService` 仍在，但界面上不再有入口：微软那条现在只作为 Core 能力保留
（`UploadMicrosoftAsync` / `ReadMicrosoftSkinAsync`，带单测），第三方那条被皮肤站衣柜复用为回退路径。

历史口径（撤下前的实现，留档）：

| 账户 | 请求 | 说明 |
| --- | --- | --- |
| 微软 | `PUT https://api.minecraftservices.com/minecraft/profile/skins` | multipart：`variant`（`classic` / `slim`）+ `file`（image/png）；Bearer 用**本次会话内存里的** MC access token |
| 第三方 | `PUT {base}/api/v1/user/profile/skin` | JSON：`{model, file: base64}`；标准 Yggdrasil 换肤接口 |

失败一律翻译成可读句子（400/401/403/429 分类，第三方 404/405/501 → 「该服务器没有实现换肤接口」），
不让用户对着 HTTP 状态码发呆。换肤后可用 `ReadMicrosoftSkinAsync` 回读档案确认（只读校验，失败不影响换肤）。

## 5. 界面

皮肤界面是**账户详情页（二级页）的「形象与皮肤」分区**，右侧内容按账户类型切换
（`AccountDetailPage.ApplySkinMode`）：

```
离线账户（本地皮肤库）
  左：3D 形象（拖动旋转 / 滚轮缩放）   右：已保存皮肤（卡片网格，首格是「添加皮肤 · 拖放 PNG」）
      「拖动以旋转 · 滚轮缩放」提示          ［卡片］［卡片］［卡片］…
      皮肤名 / 尺寸 · 型号 / 使用状态        重命名 · 右键删除
      型号下拉 · 导入皮肤图片…               从链接导入
      应用到该账户 / 取消使用                默认皮肤（启动器自带预置，点一下即加入并应用）

第三方账户（皮肤站衣柜）
  左：账号形象（3D，来自认证服务器）   右：皮肤站衣柜（刷新衣柜 / 重新登录该账号 + 卡片网格）
      皮肤说明一句                          ［站上的皮肤］［…］  点一张 = 设为当前皮肤

微软正版账户（本地皮肤库 + 强制警告）
  左：3D 形象（与离线账户同一套）      右：已保存皮肤 + 默认皮肤（与离线账户相同）
      「正版账户的皮肤归微软账号所有：这里的换肤只在本机生效……」一句说明
      每次更换（应用 / 点预置皮肤 / 取消使用）前都弹一次确认框
      （ConfirmSkinChangeAsync：只改本机形象、不动微软账号上的官方皮肤、注入不保证所有版本生效）
```

几个实现要点：

- **卡片缩略图不是原始纹理**：64×64 贴图在卡片里就是看不懂的色块，所以走 Core 的软件光栅化器
  渲成小立绘（`SkinThumbnails`），结果按「文件路径 + 写入时间」做指纹缓存在
  `cache/skin-thumbs/`，重开启动器也不用重算。
- **首格占位卡与皮肤卡共用一个模板**，靠 `IsAdd` 的可见性切换——这样它天然排在网格第一格，
  点它走文件选择，选中态会立刻还原（它不是一个可用的皮肤）。
- **拖放导入**：整个分区 `AllowDrop`，接受 `StorageItems` 里的 `.png`，支持一次多张。
- **卡片网格的高度要自己算**：`ItemsWrapGrid` 只在拿到确定高度时才换行，高度无限会排成一整行。
  预置皮肤按行数给足高度（不出现第二根滚动条），已保存皮肤交给 star 行、多了在卡片区内滚。
- **默认皮肤是游戏官方的 9 个默认角色**：资产在 `App/Assets/Skins/default/`（从官方客户端 jar 的
  `assets/minecraft/textures/entity/player/{wide,slim}/` 提取，来源与版权说明见该目录 README）。
  客户端为每个角色都打包了 wide / slim 两套纹理，但一个角色官方只用其中一套，所以
  `Core/Rendering/SkinPresets.cs` 把「角色 → 版型」钉成清单（依据是手臂宽度：Steve / Ari / Kai /
  Sunny / Zuri 为 4px classic，Alex / Efe / Makena / Noor 为 3px slim），并有单元测试盯着这条官方事实。
- **`PngEncoder` 仍然要**：卡片缩略图渲染完是一张位图，要落成 PNG 才能交给 `BitmapImage`
  同步拿到 `ImageSource`（见 `SkinThumbnails`）。它只做 RGBA8 / filter 0 的极简编码。
- **点默认皮肤之后，页面上必须看得到结果**：同一张预置皮肤只入库一次（按 `source + source_url`
  去重），所以点一张<em>已经在库里</em>的卡片，皮肤库那边不会多出新卡。只靠 ToolTip 提示"已在库中"的
  时代，用户点了页面毫无变化，读起来就是"选了默认皮肤却没进已保存皮肤"。现在预置卡片自己带徽标
  （`使用中` / `已在库中` / 什么都不显示 = 点击使用），点完还会把库里对应的那张卡
  `ScrollIntoView` 滚进视野，状态行同时写明"已加入皮肤库并选中「X」"。
- **删除放在卡片的右键菜单里**：原先页面上是一排「新名字 / 重命名 / 删除」按钮，删除删的是
  "当前选中项"——想删第二张却删了第一张太容易。现在 `SavedList_ContextRequested` 给命中那张卡
  弹菜单（右键 / 长按 / 键盘菜单键都能唤出），先把它选中再执行，页面上不再有删除按钮。

两条实现约束：

- **ContentDialog 不允许嵌套**，所以重命名与链接导入做成弹窗内的行内输入，而不是再弹一层；
- 皮肤相关的每一步（导入 / 应用 / 取消 / 删除 / 换型号）都**就地刷新**：卡片、左侧预览、
  账户页头像与卡片上的皮肤行都跟着更新。

## 6. 相关文件

| 文件 | 职责 |
| --- | --- |
| `Core/Services/SkinLibrary.cs` | 皮肤库（导入 / 校验 / 重命名 / 型号 / 删除）+ 纹理规则 |
| `Core/Services/OfflineSkinServer.cs` | 回环纹理服务 + `--userProperties` 构造（版本编码分支） |
| `Core/Services/SkinUploadService.cs` | 微软 / 第三方换肤上传与失败翻译 |
| `Core/Services/SkinStationService.cs` | 皮肤站衣柜：列皮肤 / 设为当前 / 下载回退上传（第三方账户换肤） |
| `Core/Services/AccountService.cs` | 账户上的 `skin_id` / `skin_model` 与引用清理 |
| `App/Assets/Skins/default/` | 官方默认皮肤资产（9 个角色，含来源与版权说明的 README） |
| `Core/Rendering/SkinPresets.cs` | 默认皮肤清单：角色 → 文件 → 版型（官方事实，有测试盯） |
| `Core/Rendering/PngEncoder.cs` | 极简 PNG 编码器（卡片缩略图落盘用） |
| `Core/Rendering/SkinHeadImage.cs` | 账户头像：只画头部的 128×128 正面头像 |
| `Core/Services/SkinPresetMigration.cs` | 启动时清理老版本代码画的预置皮肤（幂等，用户导入的不动） |
| `App/Views/AccountDetailPage.xaml(.cs)` | 「形象与皮肤」分区：3D 预览 + 已保存皮肤卡片 + 默认皮肤卡片 + 拖放 |
| `App/Views/Shared/SkinThumbnails.cs` | 卡片小立绘与账户头像（渲染 → PNG → 磁盘指纹缓存） |
| `App/Views/Controls/SkinFigureView.cs` | 3D 预览（`SetLocalSkin` 读库内文件） |
| `tests/SkinTests.cs` | 库 / 账户绑定 / 两种编码 / 回环服务真实取图 |
