# 默认皮肤（9 个官方默认角色）

这里是 Minecraft Java 版的 9 个默认皮肤，供账户详情页的「默认皮肤」卡片使用。

## 来源

从官方客户端 jar 的以下路径提取，未做任何改动：

```
assets/minecraft/textures/entity/player/wide/<角色>.png   # 手臂 4px 的经典版型
assets/minecraft/textures/entity/player/slim/<角色>.png   # 手臂 3px 的纤细版型
```

提取所用版本：`1.21.11`（用户本机 `versions/1.21.11/1.21.11.jar`）。

## 每个角色用哪一套

客户端为每个角色同时打包了两套纹理，但官方只用其中一套。按手臂宽度（Minecraft Wiki
「皮肤 § 默认皮肤」）：Steve / Ari / Kai / Sunny / Zuri 是 **4px（wide）**，
Alex / Efe / Makena / Noor 是 **3px（slim）**。因此本目录里：

| 文件 | 版型 | 取自 |
| --- | --- | --- |
| steve.png / ari.png / kai.png / sunny.png / zuri.png | classic | `wide/` |
| alex.png / efe.png / makena.png / noor.png | slim | `slim/` |

## 版权说明

默认皮肤角色与纹理的版权归 Mojang / Microsoft 所有，随游戏本体分发。
启动器只在「默认皮肤」区展示这 9 个官方形象，不做二次创作或再许可；
如果启动器将来要闭源分发或用于商业用途，需自行确认这部分资产的授权。
