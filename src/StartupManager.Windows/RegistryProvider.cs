using Microsoft.Win32;
using StartupManager.Core;

namespace StartupManager.Windows;

public sealed record RegistryPayload(string Value, int Kind);

public sealed class RegistryProvider : IStartupProvider
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public SourceKind Source => SourceKind.Registry;

    public ScanResult Scan()
    {
        var result = new ScanResult([], []);
        foreach (var scope in Enum.GetValues<ScopeKind>())
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { 64, 32 } : new[] { 32 })
        {
            // HKCU\Software is shared between WOW64 views; enumerating twice creates phantom duplicates.
            if (scope == ScopeKind.CurrentUser && view == 32 && Environment.Is64BitOperatingSystem) continue;
            try
            {
                using var root = Base(scope, view);
                using var key = root.OpenSubKey(RunPath);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    try
                    {
                        var entry = Read(new() { Source = Source, Scope = scope, RegistryView = view, Location = RunPath, Name = name });
                        if (entry != null) result.Entries.Add(entry.Entry);
                    }
                    catch (Exception e) { result.Errors.Add($"注册表 {name}：{e.Message}"); }
                }
            }
            catch (Exception e) { result.Errors.Add($"注册表 {scope}/{view}：{e.Message}"); }
        }
        return result;
    }

    private static RegistryKey Base(ScopeKind scope, int view) => RegistryKey.OpenBaseKey(scope == ScopeKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        view == 64 ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Registry32);

    public static void Validate(StartupEntry entry)
    {
        if (entry.Source != SourceKind.Registry || entry.Location != RunPath || !Enum.IsDefined(entry.Scope) || entry.RegistryView is not (32 or 64) || entry.Name.Contains('\0'))
            throw new InvalidDataException("拒绝访问范围外的注册表位置。");
    }

    public Snapshot? Read(StartupEntry locator)
    {
        Validate(locator);
        using var root = Base(locator.Scope, locator.RegistryView);
        using var key = root.OpenSubKey(RunPath);
        if (key == null || !key.GetValueNames().Contains(locator.Name, StringComparer.OrdinalIgnoreCase)) return null;
        var kind = key.GetValueKind(locator.Name);
        var value = key.GetValue(locator.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var supported = kind is RegistryValueKind.String or RegistryValueKind.ExpandString && value is string;
        var command = value as string ?? "[非字符串值，仅读取]";
        var (exe, args) = CommandParser.Parse(command);
        var payload = Json.Write(new RegistryPayload(command, (int)kind));
        var approvalName = locator.Scope == ScopeKind.Machine && locator.RegistryView == 32 ? "Run32" : "Run";
        using var approval = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + approvalName);
        var hasApproval = approval?.GetValue(locator.Name) != null;
        return new(locator with
        {
            Command = command, Executable = exe, Arguments = args, State = EntryState.Unknown,
            Fingerprint = StartupEntry.Hash(payload), BackupId = null, IsProtected = !supported,
            Note = hasApproval ? "检测到 Windows 启动控制记录，未解析其内部格式。可关闭此入口；恢复原配置后，Windows 自身的禁用设置仍可能生效。"
                : "入口已配置；实际执行还受 Windows 设置及策略影响。可关闭此入口，恢复时保留原始配置。"
        }, payload);
    }

    public void Disable(Snapshot snapshot, string backupDirectory)
    {
        Validate(snapshot.Entry);
        using var root = Base(snapshot.Entry.Scope, snapshot.Entry.RegistryView);
        using var key = root.OpenSubKey(RunPath, true) ?? throw new IOException("启动项已不存在。");
        var current = Read(snapshot.Entry) ?? throw new IOException("启动项已不存在。");
        if (current.Entry.Fingerprint != snapshot.Entry.Fingerprint || current.Entry.IsProtected) throw new IOException("配置已变化或不支持修改，请刷新。");
        key.DeleteValue(snapshot.Entry.Name, true);
        key.Flush();
    }

    public void Restore(BackupRecord backup, string backupDirectory)
    {
        Validate(backup.Entry);
        var payload = Json.Read<RegistryPayload>(backup.Payload);
        if (payload.Kind is not ((int)RegistryValueKind.String) and not ((int)RegistryValueKind.ExpandString)) throw new InvalidDataException("不支持恢复此值类型。");
        using var root = Base(backup.Entry.Scope, backup.Entry.RegistryView);
        using var key = root.CreateSubKey(RunPath, true);
        if (key.GetValueNames().Contains(backup.Entry.Name, StringComparer.OrdinalIgnoreCase)) throw new IOException("原位置已有配置，未覆盖。请刷新查看冲突。");
        key.SetValue(backup.Entry.Name, payload.Value, (RegistryValueKind)payload.Kind);
        key.Flush();
    }

    public void Enable(Snapshot snapshot) => throw new NotSupportedException("请通过 Windows 启动应用设置管理外部禁用状态。");
}
