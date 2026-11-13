# 参与开发

使用 Windows x64 和 .NET 10 SDK。克隆仓库后运行：

```powershell
dotnet build StartupManager.sln -c Release
dotnet run --project tests/StartupManager.Tests -c Release --no-build
./scripts/build.ps1
```

测试会临时创建随机命名的 HKCU Run 值、启动快捷方式和登录计划任务，结束时清理。请勿在测试期间注销或重启。任务创建被系统策略拒绝时会报告 SKIP；SKIP 不等于验证通过。

修改系统适配器时，必须增加对应的恢复、配置冲突或失败路径测试。不得覆盖现有用户配置，不得把读取失败当成入口不存在。PR 请说明问题、变更、测试结果和未验证场景。

提交前检查没有 state.db、真实启动项导出、用户目录、日志、凭据和发布二进制。报告问题时使用虚构路径替换个人信息。请使用 GitHub Pull Request；CI 通过后再合并。
