# 搜索翻译（中文关键词 → 英文）设计

日期：2026-09-21
代码：`Starcloud.Core/Services/SearchTranslationService.cs`、
`Starcloud.App/Views/Shared/SearchTranslationHint.cs`、
`Starcloud.App/Views/DownloadPage.xaml(.cs)`、
`Starcloud.App/Views/Dialogs/ContentDownloadDialog.xaml.cs`

## 1. 要解决的问题

Modrinth 与 CurseForge 的项目名只有英文。用户在搜索框里打「钠」「物品管理器」时，
服务端拿这个中文串做全文匹配，基本零结果——用户就会以为启动器「搜不到东西」。

目标：**用户照常打中文，启动器在后台把词翻成社区通用的英文名再去搜**，
并且让用户看得见「实际搜的是什么」。

## 2. 三条翻译路径（按「准 → 快 → 万能」排序）

| 顺序 | 来源 | 命中条件 | 为什么放这个位置 |
| --- | --- | --- | --- |
| ① | 内置词典（约 50 条） | 关键词与词条**完全相等** | 零网络、零延迟、不会翻错；覆盖「钠 / 锂 / 机械动力 / 物品管理器」这类高频词 |
| ② | MC 百科 `search.mcmod.cn` | 搜索页第一条模组条目标题里的括号英文名 | 括号里就是**社区通用英文名**，比机翻更贴合项目名（「钠 (Sodium)」） |
| ③ | 机器翻译 Google gtx → MyMemory | 前两条都没结果 | 兜住长尾词、描述性短语（「把这个词换成英文看看效果」这种也能翻） |

任何一步失败都不阻断：最后兜底是**按原词搜索**，行为与改造前一致。

### 额外一条短路规则：中英混输

「Sodium 钠」「create 机械动力」这类输入不送机翻——机翻会把两种写法叠起来
（「Sodium 钠」→「Sodium Sodium」），搜出来反而更差。这里直接取含拉丁字母的词元
（`LatinOnlySegment`：要求连续 2 个以上字母，所以 `钠 1.20` 不会被误当成英文项目名），
提示里标成「只取英文部分」。纯英文查询在入口就原样返回，走不到这条规则。

### 实测证据（2026-09-21，本机直连）

MC 百科 `GET https://search.mcmod.cn/s?key=<中文>` → `200`，条目 HTML：

```
物品管理   → [JEI] JEI物品管理器 (Just Enough Items)   → Just Enough Items
工业时代2  → [IC2] 工业时代2 (Industrial Craft 2)     → Industrial Craft 2
```

标题里括号分组可能是版本号（`(1.20.1)`），所以取用时要求该分组
**含拉丁字母且不含汉字**（`IsUsableEnglish`）。

Google gtx `GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en&dt=t&q=<词>`
→ `200`，响应 `[[["sodium","钠",null,null,2]],null,"zh-CN",...]`：取 `[0]` 各分段首元素拼接。
断网 / 被墙时改走 `https://api.mymemory.translated.net/get?q=<词>&langpair=zh-CN|en`
（取 `responseData.translatedText`）。

联调测试实跑结果（`STARCLOUD_IT_TRANSLATE=1`）：

```json
{"物品管理":"Just Enough Items","工业时代2":"Industrial Craft 2",
 "把这个词换成英文看看效果":"Change this word into English and see the effect"}
```

## 3. 行为约定

- **只翻含中文的词**：`ContainsCjk` 为假（纯英文 / 纯符号 / 空）时原样返回，一次网络都不发。
- **单步 6 秒超时**：翻译是旁路能力，卡住不能拖慢搜索；超时即换下一条路径。
- **翻页不重复翻**：下载页只在「新搜索」或「关键词变了」时翻译，翻页沿用上次结果
  （字段 `_queryOriginal` / `_queryEffective`）。第二次搜同一个词直接命中内存缓存。
- **两级缓存**：内存 `Dictionary` + 磁盘 `%APPDATA%/starcloud-launcher/search_translation_cache.json`
  （`{原文: 英文}`，只存真的翻译过的词）。缓存损坏按「从零开始」处理，不报错。
- **搜索框保留用户原话**：中文始终留在输入框里，翻译结果只体现在「实际搜什么」和提示上。

## 4. 界面提示

- 下载页社区搜索：结果计数旁显示 `已按「Sodium」搜索（MC 百科）`（`TranslatedHint`，未翻译则收起）。
- 资源下载窗：搜索完成后 InfoBar 显示同一句提示，点进去搜索框里的中文不被动过。

来源代号由 Core 给出（`dict` / `mcmod` / `machine` / `cache`），
文案在 App 侧本地化（`SearchTranslationHint`），Core 里不出现界面文案。

## 5. 边界与不做的部分

- 不做「英文 → 中文」：那是标题显示方向的 `ModTitleHelper`，与本服务无关。
- 不做整句翻译招标：用户输入的多个词若整体命中词典就用词典，否则整体交给百科 / 机翻，
  不做分词重组——分词容易把「物品管理器」拆坏。
- 不把翻译结果写进搜索结果：`ModrinthHit` 的字段保持服务端原样。
- 首次搜一个生僻词会有一次网络往返（最多 ~6 秒，走百科+机翻兜底），
  所以搜索期间搜索按钮保持禁用、进度环可见，用户不会以为卡死。

## 6. 测试

`dotnet/tests/Starcloud.Core.Tests/SearchTranslationTests.cs`

- 离线（默认跑）：中文识别、英文可用性判定（含版本号 / 长度上限）、
  纯英文原样返回、空查询、词典命中（零网络）、首尾空格裁剪、中英混输只取英文片段。
- 联调（`STARCLOUD_IT_TRANSLATE=1`）：百科命中「物品管理 / 工业时代2」、
  长尾词兜底机翻不抛异常、整条链路耗时上限。

全量：`dotnet test dotnet/tests/Starcloud.Core.Tests/Starcloud.Core.Tests.csproj`（283 通过）。
