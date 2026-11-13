using StartupManager.Core;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace StartupManager.Windows;

public sealed record TaskPayload(string Xml, bool Enabled, string Signature);

public sealed class TaskProvider : IStartupProvider
{
    public SourceKind Source => SourceKind.ScheduledTask;
    private static object Connect()
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try { ((dynamic)service).Connect(); return service; }
        catch { Com.Release(service); throw; }
    }

    public static void Validate(StartupEntry entry)
    {
        if (entry.Source != SourceKind.ScheduledTask || entry.Scope != ScopeKind.Machine || !entry.Location.StartsWith('\\') ||
            entry.Location.Contains("..") || entry.Location.Contains('\0') || entry.Location.Length > 1024)
            throw new InvalidDataException("无效计划任务位置。");
    }

    public ScanResult Scan()
    {
        var result = new ScanResult([], []);
        object? service = null, root = null;
        try
        {
            service = Connect();
            root = ((dynamic)service).GetFolder("\\");
            Walk(root, result);
        }
        catch (Exception e) { result.Errors.Add("计划任务：" + e.Message); }
        finally { Com.Release(root); Com.Release(service); }
        return result;
    }

    private static void Walk(object folder, ScanResult result)
    {
        object? tasks = null, folders = null;
        try
        {
            tasks = ((dynamic)folder).GetTasks(1);
            int count = ((dynamic)tasks).Count;
            for (int i = 1; i <= count; i++)
            {
                object? task = null;
                try
                {
                    task = ((dynamic)tasks)[i];
                    var snapshot = FromTask(task);
                    if (snapshot != null) result.Entries.Add(snapshot.Entry);
                }
                catch (Exception e) { result.Errors.Add("读取计划任务：" + e.Message); }
                finally { Com.Release(task); }
            }
        }
        catch (Exception e) { result.Errors.Add("任务文件夹读取：" + e.Message); }
        finally { Com.Release(tasks); }
        try
        {
            folders = ((dynamic)folder).GetFolders(0);
            int count = ((dynamic)folders).Count;
            for (int i = 1; i <= count; i++)
            {
                object? child = null;
                try { child = ((dynamic)folders)[i]; Walk(child!, result); }
                catch (Exception e) { result.Errors.Add("任务子文件夹：" + e.Message); }
                finally { Com.Release(child); }
            }
        }
        catch (Exception e) { result.Errors.Add("任务子文件夹枚举：" + e.Message); }
        finally { Com.Release(folders); }
    }

    private static Snapshot? FromTask(object task)
    {
        string xml = ((dynamic)task).Xml;
        bool enabled = ((dynamic)task).Enabled;
        string path = ((dynamic)task).Path;
        string name = ((dynamic)task).Name;
        var document = XDocument.Parse(xml);
        var ns = document.Root!.Name.Namespace;
        var triggers = document.Root.Element(ns + "Triggers")?.Elements().ToArray() ?? [];
        var startup = triggers.Where(t => t.Name.LocalName is "BootTrigger" or "LogonTrigger").ToArray();
        if (startup.Length == 0) return null;
        var actions = document.Root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        var exec = actions.Length == 1 && actions[0].Name.LocalName == "Exec" ? actions[0] : null;
        var command = exec?.Element(ns + "Command")?.Value ?? "";
        var args = exec?.Element(ns + "Arguments")?.Value ?? "";
        var isSystem = path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        var activeTrigger = startup.Any(t => !string.Equals(t.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase));
        var signature = Signature(xml);
        var user = document.Root.Element(ns + "Principals")?.Elements().FirstOrDefault()?.Element(ns + "UserId")?.Value ?? "由任务主体定义";
        var entry = new StartupEntry
        {
            Source = SourceKind.ScheduledTask, Scope = ScopeKind.Machine, Location = path, Name = name,
            Command = exec != null ? $"\"{command}\" {args}".TrimEnd() : $"{actions.Length} 个任务动作（详见任务计划程序）",
            Executable = Environment.ExpandEnvironmentVariables(command.Trim('"')), Arguments = args,
            State = !enabled ? EntryState.DisabledExternally : activeTrigger ? EntryState.Enabled : EntryState.Unknown,
            IsProtected = isSystem || !activeTrigger,
            HasOtherTriggers = triggers.Any(t => t.Name.LocalName is not ("BootTrigger" or "LogonTrigger")),
            Trigger = string.Join("、", startup.Select(t => t.Name.LocalName == "BootTrigger" ? "系统启动时" : "用户登录时").Distinct()),
            Fingerprint = StartupEntry.Hash(signature + "|" + enabled),
            Note = $"任务主体：{user}。启用状态不保证一定执行，还受任务条件限制。" +
                (isSystem ? " Windows 内置任务仅供查看。" : "") + (!activeTrigger ? " 自启触发器均已禁用，首版不修改触发器。" : "") +
                (triggers.Length > startup.Length ? " 此任务含其他触发器，关闭会同时停止这些触发器的后续执行。" : "")
        };
        return new(entry, Json.Write(new TaskPayload(xml, enabled, signature)));
    }

    private static string Signature(string xml)
    {
        var doc = XDocument.Parse(xml);
        var ns = doc.Root!.Name.Namespace;
        // Enabled is the only field this app changes. All other XML is compared on restore.
        doc.Root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Remove();
        return StartupEntry.Hash(doc.ToString(SaveOptions.DisableFormatting));
    }

    public Snapshot? Read(StartupEntry locator)
    {
        Validate(locator);
        object? service = null, root = null, task = null;
        try
        {
            service = Connect(); root = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)root).GetTask(locator.Location); }
            catch (COMException e) when ((uint)e.HResult is 0x80070002 or 0x80070003) { return null; }
            return FromTask(task!);
        }
        finally { Com.Release(task); Com.Release(root); Com.Release(service); }
    }

    private void SetEnabled(StartupEntry locator, string expectedSignature, bool enabled)
    {
        Validate(locator);
        if (locator.Location.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Windows 内置任务只读。");
        object? service = null, root = null, task = null;
        try
        {
            service = Connect(); root = ((dynamic)service).GetFolder("\\"); task = ((dynamic)root).GetTask(locator.Location);
            var snapshot = FromTask(task!) ?? throw new IOException("任务已不再包含开机或登录触发器。");
            if (snapshot.Entry.IsProtected || Json.Read<TaskPayload>(snapshot.Payload).Signature != expectedSignature) throw new IOException("任务定义已变化或不支持修改，请刷新。");
            ((dynamic)task).Enabled = enabled;
        }
        finally { Com.Release(task); Com.Release(root); Com.Release(service); }
    }

    public void Disable(Snapshot snapshot, string backupDirectory) => SetEnabled(snapshot.Entry, Json.Read<TaskPayload>(snapshot.Payload).Signature, false);
    public void Restore(BackupRecord backup, string backupDirectory)
    {
        var payload = Json.Read<TaskPayload>(backup.Payload);
        SetEnabled(backup.Entry, payload.Signature, payload.Enabled);
    }
    public void Enable(Snapshot snapshot) => SetEnabled(snapshot.Entry, Json.Read<TaskPayload>(snapshot.Payload).Signature, true);
}
