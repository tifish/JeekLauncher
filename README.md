# Jeek Launcher

- 通过简短的英文字符来启动软件。
- 使用 .NET 10 + Avalonia。
- 通过一个快捷键启动软件。
- 默认搜索开始菜单，可以加入定制的目录，收集所有快捷方式并缓存。
- 支持按单词首字母和拼音首字母搜索，回车启动。
- 启动一次后，记住快捷单词，后继重复使用。
- 支持多语言与主题（跟随系统 / 亮色 / 暗色），内置 GitHub 自动更新。

## 构建与运行 (Build & Run)

需要 **.NET 10 SDK**（Windows）。本仓库通过 git submodule 引入 [JeekTools.NET](https://github.com/tifish/JeekTools.NET)，克隆时请带上子模块：

```powershell
git clone --recursive https://github.com/tifish/JeekLauncher.git
# 已克隆但缺子模块：git submodule update --init --recursive
```

根目录提供快捷脚本：

```powershell
.\Build.cmd      # 编译（默认 Debug，可传 Release）
.\Run.cmd        # 编译并启动
.\Publish.cmd    # 发布优化版（ReadyToRun + NetBeauty）到 .\publish
```

也可直接调用 dotnet（输出在仓库根的 `bin` 目录）：

```powershell
dotnet build JeekLauncher\JeekLauncher.csproj -c Release
.\bin\JeekLauncher.exe
```

程序启动后常驻系统托盘，不显示主窗口。把生成的 `JeekLauncher.exe` 做一个快捷方式放进“启动”文件夹即可开机自启。

> 分发时附带 `bin\Setup.cmd`：它会自动安装 .NET 运行库（`bin\dotnet-install.ps1`）并启动程序。

## 使用 (Usage)

- **默认热键 `Alt+Space`** 呼出 / 隐藏搜索框。
- 输入**英文单词首字母**或**中文拼音首字母**搜索；不必输全，匹配**一部分**即可，单词中间的字母、部分拼音也能命中，**首字母匹配的项排在最前**。例如：
  - `vsc` / `vs` → Visual Studio Code（首字母，可只输一部分）
  - `code` / `vscode` → Visual Studio Code（单词中间的字母）
  - `bl` → 哔哩哔哩（拼音首字母）
  - `wangyi` / `yinyue` → 网易云音乐（部分拼音）
- `↑` / `↓`（或 `Tab`）选择，`Enter` 启动，`Esc` 关闭；点击窗口外自动隐藏。
- 启动过某项后，会记住这个“快捷单词”，下次输入同样的词时它排在最前面。
- 扫描完成后会**实时监控**这些目录，安装 / 卸载软件后自动更新条目，无需手动重新扫描。
- 再次运行 `JeekLauncher.exe` 会唤出已运行的实例（单实例）。
- 托盘菜单：显示 / 重新扫描 / **快捷字符串管理** / **设置** / 打开配置文件夹 / 退出。

### 设置界面 (Settings)

托盘 →「设置」。可视化修改：

- **全局热键** —— 保存后即时生效，无需重启。若热键被其它程序占用，会弹出醒目的红色提示并保留原热键，换一个再保存即可（启动时占用也会自动弹出设置并提示）。
- **语言 / Language** —— 跟随系统 / English / 中文，保存后**即时切换**，无需重启。
- **主题 / Theme** —— 跟随系统 / 亮色 / 暗色，保存后即时切换。
- **开机自动启动** —— 一个复选框即可开关（写入当前用户的注册表 `Run` 项，无需管理员权限）。
- **设置存储位置** —— 默认（AppData）/ 便携（程序目录）/ 自定义目录；切换时仅迁移可漫游设置（详见「配置」）。
- **额外扫描目录** —— 增删目录（带文件夹选择器），保存后自动重扫并纳入监控；右键目录可「打开此目录」（或双击）。
- **更新 / Updates** —— 显示当前版本；可设置「启动时检查更新」与检查频率（从不 / 每 6 小时 / 每天 / 每周），并提供「立即检查」与项目主页链接。更新经 GitHub Releases 下载（自动选用可用镜像），由 `bin\AutoUpdate.ps1` 替换文件后自动重启。

### 快捷字符串管理 (Quick words)

托盘 →「快捷字符串管理」。列出所有“快捷词 → 应用”记忆，可**新增 / 重命名 / 删除 / 清空**：

- **新增映射** —— 输入快捷词 + 在搜索框里选应用，即可手动绑定，不必先启动一次。
- **即使对应的快捷方式已被删除，快捷词也会保留**（标记为“缺失”），方便你重装软件后继续使用，或手动清理。

## 配置 (Configuration)

设置分为两类，各自存放在一个 `Config` 目录中。

### 可漫游设置（与机器无关）

存于 `Config` 目录，位置由「设置存储位置」决定：

- **默认**：`%AppData%\JeekLauncher\Config`
- **便携**：`<程序目录>\Config`（可随 U 盘携带）
- **自定义**：`<自定义目录>\Config`

文件：

- `Settings.json` — 热键与界面语言：

  ```json
  {
    "Hotkey": "Alt+Space",
    "Language": "",
    "Theme": "",
    "CheckUpdateOnStartup": true,
    "UpdateCheckIntervalHours": 24
  }
  ```

  热键支持 `Ctrl` / `Alt` / `Shift` / `Win` 组合加字母、数字、`Space`、`F1`–`F12` 等，如 `Ctrl+Alt+Space`。`Theme` 取值 `""`（跟随系统）/ `Light` / `Dark`；`UpdateCheckIntervalHours` 取值 `0`（从不）/ `6` / `24` / `168`。
- `QuickWords.json` — 记住的「快捷词 → 应用」映射（亦可在「快捷字符串管理」里编辑）。

启动时若检测到 `<程序目录>\Config` 存在，则强制便携模式（优先级高于已保存的模式）；切换离开便携模式时会自动迁移并删除该目录。

程序会监控当前可漫游 `Config` 目录（含子目录）。检测到外部修改后，会等待约 10 秒且期间没有新变化，再重新加载可漫游设置。

### 本机相关设置（不漫游）

始终存于 `%LocalAppData%\JeekLauncher\Config`：

- `LocalSettings.json` — 存储模式、自定义存储目录、本机扫描目录：

  ```json
  {
    "StorageMode": "Default",
    "CustomStorageDir": "",
    "ScanDirectories": [ "D:\\我的快捷方式" ]
  }
  ```

- `Usage.json` — 各应用的启动次数（用于排序，自动维护）。

推荐直接用托盘「设置」界面修改（即时生效）：可漫游设置的外部改动会自动重新加载；改完本机扫描目录后点托盘“重新扫描”即可。

## 实现说明 (Implementation notes)

- **界面**：Avalonia 12（Fluent 暗色主题，无边框 + 亚克力模糊），借助 Win32 消息钩子聚焦置顶。
- **多语言**：用 [Jeek.Avalonia.Localization](https://www.nuget.org/packages/Jeek.Avalonia.Localization)，翻译表为 `Data/Languages.tab`（制表符分隔，每行 `键 ⇥ en ⇥ zh`）。XAML 用 `{l:Localize Key}`、代码用 `Localizer.Get("Key")`，切换语言即时刷新（动态绑定 + 重建托盘菜单）。加语言只需在表里加一列，加文案只需加一行。
- **扫描**(双索引,仿 PowerToys Run):① 普通程序 —— 遍历「开始菜单」根目录(所有用户 + 当前用户)及自定义目录下的 `.lnk` / `.url`(COM `IShellLink` 解析目标、`SHGetFileInfo` 取图标);② 打包应用 —— 用 WinRT `PackageManager.GetAppListEntries()` 枚举 MSIX / 商店应用(如 Codex、Claude),只含真实可启动应用(比 `shell:AppsFolder` 干净得多,无控制面板 / SDK 杂项),按 AUMID 启动(`explorer shell:AppsFolder\…`)、`IShellItemImageFactory` 取图标。两者按名称去重、过滤卸载 / 帮助类条目,后台线程缓存。
- **搜索**：英文取单词 / 驼峰首字母,中文用 [ToolGood.Words.Pinyin](https://www.nuget.org/packages/ToolGood.Words.Pinyin) 取拼音**首字母**与**全拼**(词组消歧,覆盖 GB2312 之外的字)。匹配支持前缀、子串与子序列(单词中间的字母也能命中);排序分档:记忆词 > 首字母(相等/前缀) > 名称或全拼前缀 > 首字母子序列 > 子串 > 模糊子序列,同档内按历史启动次数。
- **热键**：Win32 `RegisterHotKey` + Avalonia `Win32Properties` 的 `WndProc` 钩子，全局生效，设置里修改后即时重注册。
- **监控**：每个扫描目录一个 `FileSystemWatcher`（含子目录），增删 / 重命名 `.lnk` / `.url` 触发**去抖**（800ms）后台重扫。
- **记忆解耦**：快捷词以“查询词 → 目标路径”持久化在 `usage.json`，与实时扫描结果分离，故快捷方式消失后记忆仍在。
- **开机自启**：写入 `HKCU\…\CurrentVersion\Run`（值为当前 exe 路径），无需管理员权限。
- **自检**：`JeekLauncher.exe --selftest [all|search|watch|autostart|lang]` 可无界面自测（扫描搜索 / 目录监控 / 开机自启 / 多语言），报告写入 `%TEMP%\jeek-selftest.txt`。

## 许可 (License)

[MIT](LICENSE) © 2026 Tinyfish
