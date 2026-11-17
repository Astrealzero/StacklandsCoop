# Stacklands Co-op

给 Stacklands 加多人联机：**只有房主运行游戏逻辑，其他人作为「显示器」连进来**，共用同一个世界。

> **English summary**
> A multiplayer mod for Stacklands. One player hosts the only running simulation; everyone
> else connects as a display-only client — their actions are sent to the host as intents, and
> the host streams the resulting world state back. Battles, timers, month rollover and production
> therefore exist exactly once, so two machines can never drift apart.
> Transport is Steam P2P (friend lobby invite) or plain TCP. Requires Stacklands 1.5.0.25 and the
> same mod version on every player. See [ARCHITECTURE.md](ARCHITECTURE.md) for the design.

---

## 特性

- **共享一个世界**：2–4 人共用一个存档，卡牌无归属，谁都能拿、叠、用、卖。
- **同一张地图**：游戏同一时刻只模拟一张棋盘，房主换图时全员一起过去；大家共用同一条游戏时间。
- **操作以房主为准**：你的每次拿取/叠放/使用都发给房主执行，再把结果同步回来；两人抢同一张卡时，后手会收到「被使用中」并把卡弹回。
- **战斗表现同步**：血量、伤害飘字、挥砍动作、冲突连线。
- **装备与工人槽同步**：装备卡、工人卡会还原到对应槽位，不再散落在对面屏幕上。
- **商店同步**：售货机的「已付/还需」计数与价格文字跟随房主。
- **中途加入、断线重连**：随时接入当前世界；掉线后 60 秒内自动重连。
- **聊天、延迟显示、房主暂停**：暂停时所有人一起冻结。
- **双语界面**：面板与提示跟随游戏语言（中文 / 英文），日志固定中文。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 游戏版本 | **1.5.0.25**（本 mod 只对该版本编译） |
| mod 版本 | 联机双方必须一致；不一致会在握手阶段被直接拒绝并列出差异 |
| DLC | 要么都有，要么都没有 |
| 存档 | 只有房主需要存档；房主退出这局就结束 |
| 其它框架 | 不需要 BepInEx 之类的注入器 |

用本 mod 玩过的存档之后可以单人继续；但**未安装 mod 时的旧存档暂不能直接拿来开房**。

## 安装

**方式 A：创意工坊（推荐）**

在创意工坊页面点「订阅」，从 Steam 启动游戏即可。

**方式 B：手动**

1. 关闭游戏（文件被占用时拷贝会失败）。
2. 把 `stacklands_coop` 整个文件夹放到
   `%USERPROFILE%\AppData\LocalLow\sokpop\Stacklands\Mods\`（没有 `Mods` 目录就自己建一个），最终结构：
   ```
   ...\Stacklands\Mods\stacklands_coop\manifest.json
   ...\Stacklands\Mods\stacklands_coop\StacklandsCoop.dll
   ```
3. 从 Steam 启动游戏，主菜单 → Mods 确认 `Stacklands Co-op (联机)` 处于启用状态。

> 手动安装时**不要同时订阅**工坊版本：同一个 mod 出现两份副本时，游戏可能判定重复，排查问题会很难。

## 使用

主菜单按 **F9** 打开联机面板（默认收成右上角一条状态条，点「展开」看完整内容，拖标题栏可移动）。

**开一局（房主）**

1. 填昵称，选连接方式：
   - **Steam 邀请**（默认）：创建 Steam 好友大厅并弹出邀请窗口，好友接受即可进入；房间号也会显示在面板上，可以复制发给对方。
   - **IP 直连**：不起大厅，走普通网络，面板显示端口（默认 `25565`），把 IP 和端口告诉对方。
2. 等好友进来后，正常开新局或读档。

**加入（其他人）**

- 走 Steam：直接接受邀请；或手动填 `lobby:<房间号>` / 房主的 SteamID64。
- 走 IP：填房主地址，支持只写主机名用面板端口，也支持 `192.168.1.5:25566`、`[fe80::1]:25566` 这类写法。
- 加入后会自动进入一个空白世界并接收房主的完整状态，**不需要自己先开一局**。

**热键**

| 按键 | 作用 |
| --- | --- |
| F9 | 打开/关闭联机面板 |
| F10 | 往日志写一行诊断（角色、人数、延迟、卡数、月份、所在棋盘），反馈问题时附上这一行和 `Player.log` 最有用 |

## 构建

需要 **.NET SDK 8**。项目引用游戏自带的托管程序集（不随本仓库分发），因此要指定游戏目录。

脚本会自己找：`-GameDir` → `STACKLANDS_DIR` → `STACKLANDS_EXE` → 各盘常见的 Steam 安装位置；都找不到才报错。dotnet 同理：`-Dotnet` → `DOTNET_EXE` → PATH。

```powershell
# dotnet 在 PATH 上时
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Stacklands"

# dotnet 不在 PATH 上时额外传路径
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 `
  -Dotnet "C:\Program Files\dotnet\dotnet.exe" `
  -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Stacklands"
```

产物：`src\StacklandsCoop\bin\Release\netstandard2.1\StacklandsCoop.dll`，与 `mod\manifest.json` 一起放进上面的 `Mods\stacklands_coop` 目录即可。

游戏开着时 DLL 会被占用：先退游戏，或先拷成 `StacklandsCoop.dll.new` 再覆盖。

## 目录结构

```
src/StacklandsCoop/       mod 本体（C#，netstandard2.1）
  CoopMod.cs              入口，Mod 子类
  Net/                    传输层：TCP、Steam 网络套接字、帧编解码
  Protocol/               消息读写、协议版本与握手校验
  Session/                连接状态机、握手、重连、心跳
  World/                  快照编解码、主机侧收集、客机重建与插值
  Patches/                Harmony 补丁：客机冻结、指令上报、战斗事件
  UI/                     联机面板、聊天、横幅、冲突连线
  Runtime/                常驻组件，驱动每帧 tick 与热键
mod/                      manifest、图标、工坊横幅
tools/                    打包、备份与自检脚本（PowerShell）
build.ps1                 构建脚本
ARCHITECTURE.md           架构与协议概览
```

## 已知限制

- 状态效果（中毒、流血、狂热等）的图标与层数尚未同步。
- 中途加入的玩家拿不到历史聊天记录。
- 卡牌计时在两次同步之间由客户端插值，极端情况下可能差一帧。
- 房主换棋盘时其他人跟着切换，但没有转场动画。
- 商店「已付金额」在个别情况下刷新不及时（遇到时按 F10 抓一行日志）。
- 当前 Steam 大厅上限为 4 人。

## 许可证

[MIT](LICENSE)。本仓库不包含游戏本体或其资源文件；使用需要自行拥有 Stacklands。
