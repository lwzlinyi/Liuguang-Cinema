# PCbox · Liuguang Cinema

一款基于 **C#、WinUI 3 和 LibVLC** 的 Windows 原生类 TVBox 影音客户端。支持 TVBox 配置、点播、直播、跨站搜索、个人网盘、本地媒体、下载与投屏接收。

本项目不是 Android TVBox 的模拟器，也不是 FongMi/TV 的官方分支。界面、Windows 服务层和播放控制由 C# 实现；复杂 CatVod 站点通过独立 Java 进程运行。

> 应用本身不托管影视内容。站点、直播列表、网盘文件、字幕和弹幕来自用户配置或第三方服务，可用性与使用规则由原服务决定。

## 功能

| 模块 | 能力 |
| --- | --- |
| 点播与直播 | 分类筛选、选集、多线路、频道分组、直播源导入、收藏与续播 |
| 搜索与换源 | 跨站并发搜索、历史与已载入片库联想、IME 输入、单站重试、同名片源切换、详情集数搜索 |
| 播放器 | LibVLC 硬解、HLS/DASH、画质与倍速、音轨、字幕、弹幕、全屏、画中画、逐集片头片尾 |
| 弱网处理 | 后台原生状态读取、缓冲提示、自动暂停预缓存后续播、有限重试与可关闭的自动画质调整 |
| 网盘 | 阿里、夸克、UC、百度的登录配置、分享文件与个人云盘浏览；本地安全媒体代理、Range 预读和地址刷新 |
| 弹幕与字幕 | 弹弹play 开放弹幕库（16 MB 哈希/片名智能匹配 + 手动搜索）、B 站在线匹配、本地弹幕/字幕；完整弹幕与字幕时间轴设置（速度、前移、后移） |
| AI 字幕与翻译 | 接入阿里云百炼 API：自动提取音轨并生成对齐时间轴的字幕（`paraformer-v2` / `qwen3-asr-flash-filetrans`）、字幕翻译（`qwen-mt-flash` / `qwen-mt-plus`）与实时字幕（`qwen3-asr-flash-realtime`） |
| 下载 | 单集另存、多选批量下载、默认下载目录、HLS 分片离线保存、完成项进入媒体库 |
| 本地媒体 | 多文件导入、文件夹递归索引与自动更新、同名侧车字幕/弹幕、Windows“打开方式”直达播放 |
| 投屏接收 | DLNA / UPnP 发现、手机扫码、浏览器选择视频、电脑确认后播放 |
| 存储维护 | 分类扫描与清理、未完成下载清理、显式选择的已下载媒体/过期临时文件/回收站清理 |
| 软件更新 | GitHub 正式版检查、默认开启的自动检查、更新说明弹窗、SHA-256 校验、断点续传与文件级增量安装 |

## 下载与安装

### 系统要求

- 64 位 Windows 10 1809 或更高版本，推荐 Windows 11（已针对 Windows 10 / 多显示器 / RDP / 虚拟机做自动降级兼容）
- 支持 D3D11 的显卡驱动；硬解能力取决于 GPU 与驱动（不支持硬解时自动回退软件解码）
- 无需管理员权限、无需系统 Java 或 .NET 运行时，并已内置 Microsoft 签名应用本地 VC++ 运行库，纯净系统开箱即用

从 [GitHub Releases](https://github.com/binchen6/Liuguang-Cinema/releases) 下载 `TV-Setup-<version>-x64.exe`（秒开安装器）或免安装便携包 `TV-<version>-win-x64-portable.zip`。

社区开源构建默认尚未附带商业 Authenticode 代码签名证书，在开启 Windows 11 **智能应用控制（Smart App Control）** 或 SmartScreen 的设备上可能出现提示或拦截。详情与合规处理/签名路径请见 [Windows 智能应用控制与签名说明](docs/smart-app-control.md)。下载后请务必核对发布页提供的 SHA-256：

```powershell
Get-FileHash .\TV-Setup-1.8.1-x64.exe -Algorithm SHA256
Get-Content .\TV-Setup-1.8.1-x64.exe.sha256
```

## 使用指南

### 片源与搜索

应用内置饭太硬公开配置的主备接口，优先使用最近成功的本地缓存，再按配置策略刷新。也可以在“设置 → 片源”导入兼容的单仓、多仓或直播列表。

搜索只有按 Enter 或点击“搜索”才提交。输入建议来自搜索历史、已载入片库和受防抖/取消控制的站点请求；不会把按键回显本身当成一次完整搜索。

详情页可搜索集数、手动查找其它来源。播放地址失效时会先尝试刷新；历史记录可自动检索同名新来源，失败不会用错误地址覆盖原记录。

### 播放器

常用快捷键：

- `Space`：播放/暂停
- `←` / `→`：后退/前进 5 秒
- `↑` / `↓`：音量
- `M`：静音；`F`：全屏；`P`：选集；`D`：弹幕开关
- `Z` / `X`：字幕前移 / 后移 0.5 秒；`A`：切换画面比例；`I`：媒体信息 HUD
- `Esc`：关闭当前层级或返回

点击播放器底部「字幕」按钮可调整字幕偏移（前移/后移）、字幕速度（`0.80x`–`1.25x`），并直接发起阿里云百炼 AI 字幕生成、双语翻译或开启实时字幕。前往「设置 → 弹幕与 AI」可一键跳转阿里云百炼控制台生成 API Key 并选择识别与翻译模型。

“跳过”菜单可设置片头结束和片尾开始时间，也可把当前播放位置设为边界。设置按站点、影片、线路和集数分别保存。手动原画不会被弱网算法自动降档。

顶部、进度条和底部控件在无操作 5 秒后一起隐藏，鼠标也会隐藏；移动或点击画面可恢复。菜单、登录或换源对话框打开时不会自动隐藏。

### 网盘

在“我的云盘”或“设置 → 网盘”管理账号。播放未登录或登录过期的资源时会提示重新登录，成功后尽量恢复影片、线路、集数、画质和进度。

网盘凭据使用当前 Windows 用户的 DPAPI 加密后存入本地数据库。不要把数据库、Cookie、Token 或扫码截图上传到 Issue。

### 本地视频

在“媒体库 → 本地媒体”中：

- **导入视频文件**：可多选，只建立索引，不移动文件。
- **导入文件夹并自动更新**：递归读取支持的视频格式，监听新增、修改和删除；重解析点与无权限目录会跳过。
- **直接打开文件**：只播放，不加入文件夹监视。

安装器只把PCbox加入支持格式的“打开方式”候选，不修改系统默认播放器。通过文件打开应用时跳过站点配置、直播、网盘代理和 Java 爬虫初始化；若完整应用已经运行，则复用现有窗口。

同名 `.srt`、`.ass`、`.ssa`、`.vtt` 字幕以及 `.xml`、`.json` 弹幕会被识别。移除本地库条目或停止监视文件夹不会删除磁盘原文件。

### 下载与清理

详情页下载面板支持多选和全选，批量下载默认保存到系统视频库下的“PCbox”，目录可在“设置 → 存储”修改。单集“另存”仍可自行选择位置。

高级清理先扫描再勾选。应用缓存默认可选；已下载媒体、用户临时目录中的过期文件和回收站必须单独选择并确认。被占用、无权限或不符合年龄/路径条件的文件会跳过，不会强制删除。

### 软件更新

「设置 → 关于」可手动检查 GitHub Releases，或关闭默认开启的启动自动检查。自动检查每 12 小时最多一次，播放中仅记录更新，离开播放页后再显示统一风格的更新弹窗。

点击「下载安装」后，应用优先下载与当前版本匹配的文件级增量安装包；没有对应补丁或补丁下载失败时回退完整安装包。下载支持断点续传，必须匹配 Release 提供的 SHA-256 才会启动安装。安装由 Inno Setup 完成，会先保存设置并关闭应用，不会直接覆盖正在使用的程序文件。

## 隐私与安全

默认数据目录为 `%LOCALAPPDATA%\TV`：

- `tv.db`：设置、历史、收藏、下载与本地媒体索引，以及加密凭据
- `config/last-good.json`：最近成功的配置缓存
- `cache`：图片、在线字幕和临时媒体缓存
- `diagnostic.log`、`diagnostic.log.1`：当前及上一份轮转诊断日志，单份上限 2 MB
- `last-error.log`：未处理异常

应用没有账号遥测上传功能。访问第三方站点、网盘、豆瓣或 B 站时，网络请求会发送到相应服务，适用其隐私政策。媒体代理只监听 `127.0.0.1`，并按来源限制敏感请求头与重定向范围。

DNS 设置只影响应用内连接，不修改 Windows 系统 DNS。安全加速有连接数和分块上限，遇到限流或错误会退避，不保证突破服务方限制。

## 从源码构建

### 准备环境

- .NET 10 SDK
- Windows 10/11 SDK 19041 或更高
- JDK 17+ 与 Maven（构建 CatVod 引擎）
- Inno Setup 6（仅打包安装器需要）

```powershell
git clone https://github.com/binchen6/Liuguang-Cinema.git
cd Liuguang-Cinema
dotnet restore src/TV/TV.csproj --locked-mode
dotnet build src/TV/TV.csproj -c Release --no-restore
```

开发可执行文件位于：

```text
src/TV/bin/Release/net10.0-windows10.0.19041.0/win-x64/TV.exe
```

构建 Java 引擎：

```powershell
mvn -DskipTests -f engine/catvod/pom.xml package
```

使用隔离数据目录运行，避免影响正式配置：

```powershell
$env:TV_DATA_ROOT = Join-Path $PWD 'artifacts/tmp/dev-profile'
& .\src\TV\bin\Release\net10.0-windows10.0.19041.0\win-x64\TV.exe
```

### 打包安装器

```powershell
pwsh -NoProfile -File ./scripts/build-installer.ps1
```

本机已有 Maven 缓存和构建依赖时可使用 `-MavenOffline -NoRestore`。所有正式安装包统一输出到：

```text
artifacts/installers/TV-Setup-<version>-x64.exe
artifacts/installers/TV-Setup-<version>-x64.exe.sha256
```

版本号来自 `Directory.Build.props`。发布目录包含运行库、Java、CatVod 引擎和第三方许可文件。

## 项目结构

```text
src/TV/                  C# / WinUI 主程序
  Controls/              可复用控件与无障碍行为
  Models/                媒体、配置、网盘与播放模型
  Services/AI/           阿里云百炼 AI 字幕生成、翻译、音轨提取与实时字幕
  Services/Vod/          CMS、原生站点和换源服务
  Services/Playback/     解析、HLS/DASH、下载、弹弹play/B站弹幕与字幕
  Services/Cloud/        网盘登录、文件接口与本地安全代理
  Services/Library/      本地媒体索引与目录监视
  Services/Maintenance/  分类扫描和安全清理
  ViewModels/            页面状态与命令
  Views/                 各页面 XAML 和交互
engine/catvod/           Java 17 CatVod JSON IPC 引擎
third_party/             固定版本的上游源码快照与许可
scripts/                 构建、打包和验收脚本
installer/               Inno Setup 安装器与输出完整性检查
artifacts/qa-fixture/    隔离测试夹具与回归脚本
```

更多技术说明见[开发指南](docs/development.md)、[智能应用控制与签名说明](docs/smart-app-control.md)、[站点适配矩阵](docs/site-matrix.csv)、[验收边界](docs/site-verification.md)和[设计契约](docs/design-guide.md)。当前版本说明见[1.8.1 发行说明](docs/releases/release-1.8.1.md)，历史文档索引见[docs/README](docs/README.md)。

## 验证与贡献

欢迎提交 Issue 和 Pull Request。请包含 Windows 版本、复现步骤、相关站点类型和去敏后的日志。不要上传真实 Cookie、Token、用户数据库、私人视频或完整账号截图。

提交改动前至少完成：

```powershell
dotnet build src/TV/TV.csproj -c Release --no-restore
```

并运行与改动相关的隔离回归。桌面脚本会激活窗口和发送输入，应串行运行，不要与正常工作抢焦点。配置、账号和测试数据必须留在隔离 profile，不要提交到 Git。

站点“存在适配器”“能返回内容”“能解析地址”“能持续播放”是不同验收层级，不能用类名或单张截图替代。网络较弱、不同 GPU、各地区账号和第三方站点的表现仍需分别验证。

## 许可证与致谢

本项目原创代码采用 [MIT License](LICENSE)。第三方代码和服务不适用同一许可证，详见[第三方代码、服务与许可说明](THIRD_PARTY.md)。

主要致谢：FongMi/TV 与 CatVodSpider 社区、bizhangjie、AndroidCatVodSpider 维护者、VideoLAN / LibVLCSharp、.NET 与 Windows App SDK、QRCoder、SkiaSharp、AngleSharp，以及公开配置和元数据服务的维护者。
