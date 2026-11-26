# JeekLauncher

JeekLauncher 是一个 Windows 应用启动器。按下快捷键，输入几个字母或拼音，就能找到并打开应用。

## 特点

- 默认按 `Alt+Space` 呼出，程序常驻系统托盘
- 支持英文名称、英文首字母、中文拼音首字母和全拼搜索
- 记住常用查询和启动频率
- 自动发现开始菜单和 Microsoft Store 应用
- 可添加自定义目录并自动跟踪快捷方式变化
- 支持中英文、亮色/暗色主题、开机启动和自动更新

## 安装

在命令提示符（CMD）中运行：

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/tifish/JeekLauncher/main/install.ps1 | iex"
```

如果 GitHub 无法访问，中国大陆可使用镜像：

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekLauncher/main/install.ps1 | iex"
```

安装程序会创建开始菜单快捷方式、设置当前用户开机启动并运行程序。未安装 .NET 10 运行库时会自动安装。

## 使用

1. 按 `Alt+Space` 打开搜索框。
2. 输入名称的一部分、英文首字母或中文拼音，例如 `vsc`、`bl`、`wangyi`。
3. 用方向键或 `Tab` 选择结果，按 `Enter` 启动。
4. 按 `Esc` 关闭；点击窗口外也会自动隐藏。

托盘菜单可以重新扫描、管理快捷词、打开设置或退出程序。

## 设置

右键托盘图标，选择「设置」即可修改热键、语言、主题、开机启动、扫描目录、配置位置和更新频率。保存后大多数设置立即生效。

## 卸载

先在设置中取消「开机自动启动」（或在任务计划程序中删除 `JeekLauncher-<用户名>` 任务），然后退出程序，删除安装目录和开始菜单快捷方式。

## 从源码运行

需要 Windows 和 .NET 10 SDK：

```powershell
git clone --recursive https://github.com/tifish/JeekLauncher.git
cd JeekLauncher
.\Run.cmd
```

项目需求与核心机制说明见 [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)。

## 许可

[MIT](LICENSE) © 2026 Tinyfish

