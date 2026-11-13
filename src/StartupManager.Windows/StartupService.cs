using StartupManager.Core;
using StartupManager.Storage;
using System.Security.Principal;

namespace StartupManager.Windows;

public sealed class StartupService
{
    private readonly Dictionary<SourceKind, IStartupProvider> providers;
    private readonly Func<ScopeKind, BackupStore> storeFactory;
    public StartupService(IEnumerable<IStartupProvider>? providers = null, Func<ScopeKind, BackupStore>? stores = null)
    {
        this.providers = (providers ?? [new RegistryProvider(), new StartupFolderProvider(), new TaskProvider()]).ToDictionary(x => x.Source);
        storeFactory = stores ?? (scope => new BackupStore(scope));
    }
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public ScanResult Scan()
    {
        var result = new ScanResult([], []);
        foreach (var provider in providers.Values)
        {
            try { var scanned = provider.Scan(); result.Entries.AddRange(scanned.Entries); result.Errors.AddRange(scanned.Errors); }
            catch (Exception e) { result.Errors.Add($"{provider.Source}：{e.Message}"); }
        }
        foreach (var scope in Enum.GetValues<ScopeKind>())
        {
            try
            {
                foreach (var backup in storeFactory(scope).ActiveBackups())
                {
                    if (backup.Entry.Scope != scope) { result.Errors.Add("备份作用范围不匹配，已跳过。"); continue; }
                    StartupEntry entry;
                    try
                    {
                        var live = providers[backup.Entry.Source].Read(backup.Entry);
                        var existsAsDisabled = live != null && backup.Entry.Source == SourceKind.ScheduledTask &&
                            !Json.Read<TaskPayload>(live.Payload).Enabled &&
                            Json.Read<TaskPayload>(live.Payload).Signature == Json.Read<TaskPayload>(backup.Payload).Signature;
                        if ((live == null && backup.Entry.Source != SourceKind.ScheduledTask) || existsAsDisabled)
                        {
                            entry = backup.Entry with { State = backup.Status == "Disabled" ? EntryState.DisabledByUs : EntryState.RecoveryNeeded,
                                BackupId = backup.Id, Note = "原始配置已备份。" + backup.Message,
                                Fingerprint = live?.Entry.Fingerprint ?? "" };
                        }
                        else
                        {
                            entry = (live?.Entry ?? backup.Entry) with { State = EntryState.Conflict, BackupId = backup.Id,
                                Fingerprint = live?.Entry.Fingerprint ?? "",
                                Note = "原入口已出现、任务已变化或已删除。恢复不会覆盖新配置。可保留当前配置并归档旧备份。 " + backup.Message };
                        }
                    }
                    catch (Exception ex)
                    {
                        entry = backup.Entry with { State = EntryState.Unknown, BackupId = backup.Id, IsProtected = true,
                            Note = "无法核实备份对应的当前配置：" + ex.Message };
                        result.Errors.Add(entry.Name + "：" + entry.Note);
                    }
                    result.Entries.RemoveAll(x => x.Id == backup.Entry.Id);
                    result.Entries.Add(entry);
                }
            }
            catch (Exception e) { result.Errors.Add($"{scope} 备份读取：{e.Message}"); }
        }
        result.Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public OperationResult Execute(OperationRequest request)
    {
        if (!Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Entry.Scope) || !providers.ContainsKey(request.Entry.Source))
            return new(false, "无效操作。");
        if (request.Entry.Scope == ScopeKind.Machine && !IsAdministrator) return new(false, "此操作需要管理员权限。");
        var mutexName = request.Entry.Scope == ScopeKind.Machine ? @"Global\StartupManager.v1.MachineOperations"
            : @"Local\StartupManager.v1." + WindowsIdentity.GetCurrent().User!.Value;
        using var mutex = new Mutex(false, mutexName);
        bool locked;
        try { locked = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) return new(false, "另一个修改正在执行，请稍后刷新。");
        try { return ExecuteLocked(request); }
        finally { mutex.ReleaseMutex(); }
    }

    private OperationResult ExecuteLocked(OperationRequest request)
    {
        var store = storeFactory(request.Entry.Scope);
        BackupRecord? backup = null;
        string action = request.Action switch { OperationKind.Disable => "关闭自启", OperationKind.Restore => "恢复原配置", OperationKind.Enable => "开启任务", _ => "保留当前配置" };
        try
        {
            var provider = providers[request.Entry.Source];
            var current = provider.Read(request.Entry);
            // Compare the actual system value with the value shown when the user made the decision.
            if ((current?.Entry.Fingerprint ?? "") != request.Entry.Fingerprint) throw new IOException("配置在操作前发生变化，请刷新后重试。");
            store.EnsureWritable();
            store.Log(request.Entry.Name, action, "开始", request.Entry.Location);
            if (request.Action is OperationKind.Restore or OperationKind.KeepCurrent)
            {
                backup = store.ActiveBackups().SingleOrDefault(x => x.Id == request.Entry.BackupId && x.Entry.Id == request.Entry.Id)
                    ?? throw new IOException("恢复记录已变化，请刷新。");
                if (request.Action == OperationKind.KeepCurrent)
                {
                    store.SetStatus(backup.Id, "Cancelled", "用户保留当前配置，原备份归档保留。");
                    store.Log(request.Entry.Name, action, "成功", "未修改系统配置，原备份仍保留在备份目录。");
                    return new(true, "已保留当前配置并归档旧备份。");
                }
                if (current?.Entry.IsProtected == true) throw new IOException("当前入口为只读。");
                if (request.Entry.Source != SourceKind.ScheduledTask && current != null) throw new IOException("原位置已有配置，未覆盖。");
                provider.Restore(backup, store.BackupDirectory(backup.Id));
                var restored = provider.Read(backup.Entry);
                if (restored?.Entry.Fingerprint != backup.Entry.Fingerprint) throw new IOException("恢复后核验未通过，请刷新检查实际状态。");
                store.SetStatus(backup.Id, "Restored", "原配置已恢复并核验。");
            }
            else
            {
                if (current == null || current.Entry.IsProtected) throw new IOException("入口已不存在或仅供查看。");
                if (request.Action == OperationKind.Disable && !current.Entry.CanDisable) throw new IOException("该入口当前不能关闭。");
                if (request.Action == OperationKind.Enable && !current.Entry.CanEnable) throw new IOException("该入口当前不能开启。");
                backup = store.Prepare(current);
                if (request.Action == OperationKind.Disable) provider.Disable(current, store.BackupDirectory(backup.Id));
                else provider.Enable(current);
                var after = provider.Read(current.Entry);
                bool verified = request.Action == OperationKind.Enable ? after?.Entry.State == EntryState.Enabled
                    : current.Entry.Source == SourceKind.ScheduledTask ? after?.Entry.State == EntryState.DisabledExternally : after == null;
                if (!verified) throw new IOException("修改后核验未通过，请刷新检查实际状态。");
                store.SetStatus(backup.Id, request.Action == OperationKind.Disable ? "Disabled" : "Completed", "修改已完成，系统配置核验通过。");
            }
            store.Log(request.Entry.Name, action, "成功", "系统配置已重新读取核验；实际登录执行行为需在下次登录验证。");
            return new(true, action + "成功，配置核验通过。" + (request.Action == OperationKind.Restore && request.Entry.Source != SourceKind.ScheduledTask
                ? " Windows 自身的禁用设置仍可能生效，可在系统启动应用设置中检查。" : ""));
        }
        catch (Exception e)
        {
            var message = e is UnauthorizedAccessException ? "访问被拒绝，未能完成操作。" : e.Message;
            try
            {
                if (backup != null) store.SetStatus(backup.Id, "Error", message);
                store.Log(request.Entry.Name, action, "失败 / 待检查", message);
            }
            catch (Exception logError) { message += " 日志写入失败：" + logError.Message; }
            return new(false, message);
        }
    }

    public List<HistoryRecord> History() => Enum.GetValues<ScopeKind>().SelectMany(scope => storeFactory(scope).History())
        .OrderByDescending(x => x.Time).Take(500).ToList();
}
