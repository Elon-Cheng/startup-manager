# 发布流程

1. 在 Directory.Build.props 更新 Version，并在 CHANGELOG.md 记录变更。
2. 在 Windows x64 运行 scripts/build.ps1，核对测试输出，不能把跳过项目记为通过。
3. 在干净的 Windows 测试账户解压 ZIP，验证无 SDK 运行、中文路径、普通用户关闭恢复、机器范围 UAC 同意/取消和配置冲突。
4. 在虚拟机验证关闭前、关闭后、恢复后的重新登录行为；在 VALIDATION.md 中追加本次环境和结果。
5. 推送源码到 GitHub，确认 Actions 成功。创建对应版本标签和 GitHub Release，上传 artifacts/packages 内的 ZIP 和 SHA256 文件。未完成行为验收时标为 Pre-release。
6. 发布说明列出 Windows 架构、已知限制、测试跳过项和是否签名。维护者应自行审阅第三方运行时的许可文件。

构建不会自动推送或发布。ZIP 是完整便携包，不能只分发主 EXE。升级先退出应用，解压到新文件夹；用户备份保留在原数据目录。卸载前先恢复需要保留的启动项，再删除程序目录，备份数据按用户需要另行保留。
