# RyzenAdj（功耗监测/切换）内置目录

本目录用于放置 `ryzenadj.exe` 及其 Windows 运行依赖，发布后会被复制到：

`release/XhMonitor-v*/Service/tools/RyzenAdj/`

这样你可以在发布包里**直接替换** `ryzenadj.exe` 和依赖文件来升级版本，无需重新编译。

## 需要的文件（Windows x64）

从 RyzenAdj 的 GitHub Releases 下载 `ryzenadj-win64.zip`，解压后把以下文件放到本目录（保持同一目录）：

- `ryzenadj.exe`
- `WinRing0x64.dll`
- `WinRing0x64.sys`
- `inpoutx64.dll`

说明：
- RyzenAdj 需要管理员权限才能正常读取/设置功耗相关指标。
- 只要这些文件和 `ryzenadj.exe` 在同一目录即可，程序会按默认路径自动发现：`Service/tools/RyzenAdj/ryzenadj.exe`。
- 如需随发布包分发 RyzenAdj 二进制文件，请一并保留其 `LICENSE`（LGPL）文件。
