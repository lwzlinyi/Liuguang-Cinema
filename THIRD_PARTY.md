# 第三方代码、服务与许可说明

PCbox不是上述项目的官方分支。本文记录源码复用和协议参考，不表示上游作者为本项目背书。

## 代码复用

| 上游 | 本项目中的用途 | 来源与许可 |
| --- | --- | --- |
| FongMi/TV | TVBox 配置协议、交互和兼容性参考；未复制其完整 Android 应用 | [FongMi/TV](https://github.com/FongMi/TV)，[GPL-3.0](https://github.com/FongMi/TV/blob/fongmi/LICENSE.md) |
| FongMi/CatVodSpider | Java 爬虫、Bean、网络与工具类快照，构建时由 `engine/catvod/pom.xml` 选择 | 固定提交 [6a43253](https://github.com/FongMi/CatVodSpider/commit/6a43253e48c9e6cd81bba19440a794448411c45f)，快照位于 `third_party/v2024`。截至 2026-11-22，上游仓库未提供可识别的许可证文件；本项目不因此授予额外权利 |
| AndroidCatVodSpider | 夸克、UC、Libvio、ChangZhang 等相关实现与工具类 | [lushunming/AndroidCatVodSpider](https://github.com/lushunming/AndroidCatVodSpider)，固定提交 [8415d16](https://github.com/lushunming/AndroidCatVodSpider/commit/8415d161e217118e5560d2e608df9948dfbc417e)，快照位于 `third_party/quark`。上游当前分支提供 [MIT License](https://github.com/lushunming/AndroidCatVodSpider/blob/multiThread/LICENSE)；固定快照本身未附带该文件 |
| bizhangjie/CatVodSpider | 公开 Spider 实现参考 | [bizhangjie/CatVodSpider](https://github.com/bizhangjie/CatVodSpider)，MIT；原文保留于 `third_party/bizhangjie/CatVodSpider-main/LICENSE` |
| CatVodTVSpider | CatVod 架构与接口来源 | [CatVodTVOfficial/CatVodTVSpider](https://github.com/CatVodTVOfficial/CatVodTVSpider)。未据此声明额外许可 |
| VideoLAN / LibVLCSharp | 音视频解码、 demux、音轨与字幕、Windows 渲染 | LibVLC 3.0.24、LibVLCSharp.WinUI 3.10.0；[源码与版本说明](third_party/VideoLAN/SOURCE.md)，LGPL-2.1-or-later。许可证原文在 `third_party/VideoLAN` |
| LibVLCSharp 及相关 .NET 包 | C# 播放控制与 WinUI 绑定 | 版本见 `src/TV/packages.lock.json` 和 `src/TV/TV.csproj`；各包许可证随安装包的 `licenses` 目录分发 |
| Inno Setup 简体中文翻译 | 安装器与增量更新器的中文界面 | [Inno-Setup-Chinese-Simplified-Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)，MIT；固定副本位于 `installer/Languages`，许可证随源码及安装包分发 |

`engine/catvod/src` 还包含桌面 JSON IPC 桥接、Android API 兼容层和项目维护的站点修正。Maven 实际纳入的上游目录以 `engine/catvod/pom.xml` 为准，不能把整个 `third_party` 目录视为单一许可。

## 公开配置与在线服务

- 饭太硬公开 TVBox 配置由 `SourceConfigService.BuiltInEndpoints` 直接读取。应用实现缓存、主备刷新、配置合并、原生适配器和 Java 爬虫路由，不托管配置或视频内容。
- 公开配置收录可参考 [gaotianliuyun/gao](https://github.com/gaotianliuyun/gao) 和 [cyao2q/files](https://github.com/cyao2q/files)。它们是第三方收录仓库，不是饭太硬或PCbox官方仓库。
- 阿里、夸克、UC、百度网盘使用各自公开或官方账号/文件接口；账号凭据由用户在本地提供，服务条款由各服务方定义。
- 哔哩哔哩账号、画质、节目匹配、弹幕和字幕使用 B 站接口。在线内容的可用性、权限与地域限制由原服务决定。
- 弹弹play 开放弹幕网络（[doc.dandanplay.com/open](https://doc.dandanplay.com/open/)）用于在线弹幕匹配与拉取，遵循弹弹play 开放平台调用规范与鉴权协议。
- 阿里云百炼 / DashScope API（`paraformer-v2`、`qwen3-asr-flash-filetrans`、`qwen3-asr-flash-realtime`、`qwen-mt-flash`、`qwen-mt-plus`）用于可选的 AI 字幕生成、翻译与实时字幕，由用户自行申请 API Key 并按阿里云百炼条款与计费规则使用。
- 豆瓣用于公开元数据与推荐补充。封面、节目信息和站点内容权利归原权利人。
- Apple 公开流媒体样片仅用于开发验收，不随应用分发。

## NuGet、Maven 与安装器

直接依赖声明位于：

- `src/TV/TV.csproj` 与 `src/TV/packages.lock.json`
- `engine/catvod/pom.xml`
- `global.json`、`Directory.Build.props`

打包脚本会收集 NuGet 包内的 LICENSE、NOTICE、COPYING 等文件，并附带 CatVodSpider 和 VideoLAN 许可文本。Java 依赖及其传递依赖仍分别适用各自许可证。

## 许可边界

根目录 `LICENSE` 仅适用于本项目原创代码。第三方代码、配置、字体、图标、服务和媒体内容不被该许可证重新授权。对于未明确声明许可证的上游快照，本文仅记录来源，不声称已经获得额外授权；需要单独再分发这些部分时，应核实上游条款或移除对应组件。
