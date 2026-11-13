# 开机自启管理

一个中文 Windows 桌面程序，用于查看、关闭和恢复本机软件的自启动入口。使用 C#、.NET 10、WPF 和 SQLite，默认以普通用户身份运行。

## 安装版

下载 Release 中的 StartupManager-0.1.0-win-x64-Setup.exe，双击按向导安装。安装到当前用户目录，无需预先安装 .NET。开始菜单搜索“开机自启管理”即可打开，也可在安装时勾选桌面快捷方式。在 Windows 设置的应用列表中卸载；备份不会随卸载删除，已关闭的入口需先在软件中恢复。

开发者安装 Inno Setup 6 后，可用以下命令同时生成便携版和安装版：

```powershell
./scripts/build.ps1 -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

## 下载与要求

当前版本为 **0.1.0 预发布**，目标为 Windows x64 桌面环境。尚未完成多系统版本、重新登录和 UAC 全流程验收，详情见 VALIDATION.md。ARM64 和 32 位 Windows 暂不提供发布包。

在本仓库的 GitHub Releases 页面下载 ZIP（不要下载 Source code 作为运行包），完整解压后运行 StartupManager.exe。仓库刚创建且尚无 Release 时，请按下方开发步骤构建。程序未提供代码签名；请核对下载来源和 Release 所附 SHA256：

```powershell
Get-FileHash ./StartupManager-0.1.0-win-x64.zip -Algorithm SHA256
```

将输出与同名 .sha256 文件比较。校验用于检测文件变化，不替代可信下载来源。使用和分发遵循 [MIT 许可](LICENSE)，开发协作见 [CONTRIBUTING.md](CONTRIBUTING.md)，发布步骤见 [docs/RELEASING.md](docs/RELEASING.md)。

## 运行

打开发布目录中的 **StartupManager.exe**。请保留发布目录中的其他文件，它们包含运行时和权限辅助程序，无需另行安装 .NET。

1. 打开后自动读取本机启动项，点击“刷新启动项”可重新扫描。
2. 选中入口查看路径、参数、触发时机和作用范围。
3. 点击“关闭这个自启入口”，核对操作对象后确认。
4. 在“本工具已关闭”筛选中找到入口，点击“恢复原配置 / 开启任务”。

关闭自启不会退出正在运行的软件，也不会卸载它。系统公共范围的操作会按需出现 UAC 授权窗口；取消授权时不会发送修改请求。提权需要同一 Windows 账户，使用另一个管理员账户的凭据不受支持。

## 支持范围与状态含义

| 来源 | 支持行为 |
| --- | --- |
| 当前用户和机器级 Run 注册表 | 读取、备份关闭、恢复；覆盖适用的 32/64 位视图 |
| 当前用户和公共启动文件夹 | 读取普通文件及快捷方式、备份关闭、恢复 |
| 含开机或登录触发器的计划任务 | 读取、禁用任务、恢复或开启已有任务 |

- **状态未知**：注册表或启动文件存在，但 Windows 自身的禁用设置和策略可能阻止执行。本工具不读写未经验证的 StartupApproved 二进制状态；仍可以通过移除活动入口关闭自启，并恢复原始配置。
- **已开启 / 外部已关闭**：计划任务服务报告的启用状态。已开启并不保证任务条件一定满足。
- **本工具已关闭**：活动入口已移除或任务已禁用，并保存了恢复记录。
- **待恢复检查**：上次操作没有完整结束，需根据备份与当前配置恢复。
- **配置冲突**：原入口重新出现、发生变化，或任务被删除。不会覆盖现有配置；可以选择“保留当前配置并归档旧备份”。归档不删除备份文件。

列表按程序路径分组，组标题显示入口数和汇总状态。脚本解释器及无法确定归属的入口不自动合并。关闭分组会逐项报告结果；机器级入口逐项提权。

Windows 内置计划任务（`\Microsoft\`）只读，默认隐藏。所有自启触发器均被禁用的任务也为只读。一个任务含有其他触发器时，禁用整个任务也会影响这些触发器，确认窗口会说明。

首版不管理服务、驱动、RunOnce、其他用户的私有启动文件夹、打包应用的特殊启动机制等，因此并非系统全部自启机制的完整清单。要开启由 Windows 设置关闭的注册表或文件夹入口，请使用侧边栏“Windows 启动设置”。

## 数据与恢复

- 当前用户备份：`%LOCALAPPDATA%\StartupManager\v1`
- 机器级备份：`%PROGRAMDATA%\StartupManager\v1`
- `state.db`：SQLite 备份索引、操作历史和界面设置。
- `files\<备份 ID>\original`：启动文件的原始副本。

备份记录保存注册表原始值类型和数据、快捷方式原始内容及权限信息、任务原始 XML 和启用状态。恢复前会检查配置冲突，修改后会重新读取核验。请保留这些目录；删除索引可能导致无法在界面中恢复已关闭项目。程序不会自动清除历史备份。

SQLite 使用 Windows 自带的 `winsqlite3.dll`，无需额外本地数据库服务。机器级目录限制为管理员和 SYSTEM 写入，普通用户只读。

## 开发

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet build StartupManager.sln -c Release
dotnet run --project tests/StartupManager.Tests -c Release
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

如果 SDK 没有加入 PATH，可指定路径：

```powershell
.\scripts\build.ps1 -DotnetPath 'C:\path\to\dotnet.exe'
```

发布产物位于 `artifacts/release/<版本-唯一编号>/win-x64`。ZIP 和 SHA256 位于 artifacts/packages。每次发布使用独立的新目录，避免旧文件混入。构建脚本先运行测试，再分别发布主程序和辅助程序到同一目录。测试通过后，仅需重新发布时可使用 `-SkipTests`。

## 项目结构

```text
src/StartupManager.App                 WPF 界面和 MVVM
src/StartupManager.Core                模型、分组和命令解析
src/StartupManager.Windows             系统适配器、操作服务、权限通信
src/StartupManager.Storage             SQLite 和备份访问权限
src/StartupManager.PrivilegedHelper    按需提权的限定操作辅助程序
tests/StartupManager.Tests             核心与本机受控集成测试
scripts/build.ps1                      测试与独立运行发布
```

## 验证说明

自动化测试覆盖解析和分组、状态汇总、配置变化、备份持久化、恢复冲突、中断恢复、读取失败，以及专用 HKCU Run 值、启动快捷方式和登录任务的关闭恢复。测试只创建 `StartupManager.Test.<随机 ID>` 项，并在结束时清理这些测试项，不修改现有软件的启动配置。

配置核验与真实登录行为验证分开记录。没有经过重新登录或虚拟机重启验证的场景不能视为行为验收通过。具体本次执行结果见项目中的 `VALIDATION.md`。

## 参考

- [Windows 启动应用设置](https://support.microsoft.com/en-US/Windows/Experience/Startup-Boot/configure-startup-applications-in-windows)
- [Run / RunOnce 注册表项](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)
- [.NET 支持政策](https://dotnet.microsoft.com/platform/support/policy)
