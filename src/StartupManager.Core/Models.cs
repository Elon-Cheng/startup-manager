using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StartupManager.Core;

public enum SourceKind { Registry, StartupFolder, ScheduledTask }
public enum ScopeKind { CurrentUser, Machine }
public enum EntryState { Enabled, DisabledByUs, DisabledExternally, Unknown, Conflict, RecoveryNeeded }
public enum OperationKind { Disable, Restore, Enable, KeepCurrent }

public sealed record StartupEntry
{
    public SourceKind Source { get; init; }
    public ScopeKind Scope { get; init; }
    public string Location { get; init; } = "";
    public string Name { get; init; } = "";
    public int RegistryView { get; init; } = 64;
    public string Command { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string Trigger { get; init; } = "用户登录时";
    public string Note { get; init; } = "";
    public EntryState State { get; init; }
    public bool IsProtected { get; init; }
    public bool HasOtherTriggers { get; init; }
    public string Fingerprint { get; init; } = "";
    public string? BackupId { get; init; }
    public string Id => Hash($"{Source}|{Scope}|{RegistryView}|{Location.ToUpperInvariant()}|{Name.ToUpperInvariant()}");
    public string SourceLabel => Source switch { SourceKind.Registry => "注册表", SourceKind.StartupFolder => "启动文件夹", _ => "计划任务" };
    public string ScopeLabel => Scope == ScopeKind.CurrentUser ? "当前用户" : "系统公共";
    public string StateLabel => State switch
    {
        EntryState.Enabled => "已开启", EntryState.DisabledByUs => "本工具已关闭",
        EntryState.DisabledExternally => "外部已关闭", EntryState.Conflict => "配置冲突",
        EntryState.RecoveryNeeded => "待恢复检查", _ => "状态未知"
    };
    public bool CanDisable => !IsProtected && State is EntryState.Enabled or EntryState.Unknown;
    public bool CanRestore => !IsProtected && BackupId != null && State is EntryState.DisabledByUs or EntryState.RecoveryNeeded;
    public bool CanEnable => !IsProtected && State == EntryState.DisabledExternally && Source == SourceKind.ScheduledTask && BackupId == null;
    public bool CanArchive => BackupId != null && State is EntryState.Conflict or EntryState.RecoveryNeeded;
    public string GroupKey
    {
        get
        {
            var name = Path.GetFileName(Executable).ToLowerInvariant();
            return string.IsNullOrWhiteSpace(Executable) || !Path.IsPathFullyQualified(Executable) ||
                name is "cmd.exe" or "powershell.exe" or "pwsh.exe" or "wscript.exe" or "cscript.exe" or "rundll32.exe" or "mshta.exe"
                ? Id : Executable.ToUpperInvariant();
        }
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record ScanResult(List<StartupEntry> Entries, List<string> Errors);
public sealed record BackupRecord(string Id, StartupEntry Entry, string Payload, string Status, string CreatedUtc, string Message);
public sealed record HistoryRecord(string Time, string Name, string Action, string Result, string Message);
public sealed record OperationRequest(OperationKind Action, StartupEntry Entry);
public sealed record OperationResult(bool Success, string Message);
public sealed record Snapshot(StartupEntry Entry, string Payload);

public interface IStartupProvider
{
    SourceKind Source { get; }
    ScanResult Scan();
    Snapshot? Read(StartupEntry locator);
    void Disable(Snapshot snapshot, string backupDirectory);
    void Restore(BackupRecord backup, string backupDirectory);
    void Enable(Snapshot snapshot);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("数据为空。");
}

public static class EntryGrouping
{
    public static string Summarize(IEnumerable<StartupEntry> entries)
    {
        var states = entries.Select(x => x.State).Distinct().ToArray();
        if (states.Length == 1) return entries.First().StateLabel;
        if (states.Contains(EntryState.Conflict)) return "配置冲突";
        if (states.Contains(EntryState.RecoveryNeeded)) return "待恢复检查";
        if (states.Contains(EntryState.Unknown)) return "含未知状态";
        return states.Contains(EntryState.Enabled) ? "部分开启" : "已关闭（多种来源）";
    }
}
