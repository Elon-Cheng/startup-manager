using Microsoft.Win32;
using StartupManager.Core;
using StartupManager.Storage;
using StartupManager.Windows;
using System.Runtime.InteropServices;
using System.Security.Principal;

if (args.Length == 2 && args[0] == "--probe")
{
    File.AppendAllText(args[1], DateTimeOffset.Now.ToString("O") + Environment.NewLine);
    return 0;
}

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-results", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
int passed = 0, failed = 0, skipped = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " :: " + e); }
}

(StartupService Service, MemoryProvider Provider, BackupStore Store) Fixture()
{
    var provider = new MemoryProvider();
    var root = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    var store = new BackupStore(ScopeKind.CurrentUser, Path.Combine(root, "user"));
    return (new StartupService([provider], s => s == ScopeKind.CurrentUser ? store : new BackupStore(s, Path.Combine(root, "machine"))), provider, store);
}

Test("Command parsing preserves quoted Unicode path and arguments", () =>
{
    var parsed = CommandParser.Parse("\"C:\\中文 路径\\app.exe\" --flag \"two words\"");
    Check(parsed.Executable == @"C:\中文 路径\app.exe" && parsed.Arguments == "--flag \"two words\"", "Quoted parsing failed");
    Check(CommandParser.Parse(@"C:\Program Files\app.exe --flag").Executable == "", "Ambiguous command must remain unresolved");
});
Test("Identity distinguishes source and scope; comparison is case insensitive", () =>
{
    var entry = new StartupEntry { Source = SourceKind.Registry, Location = RegistryProvider.RunPath, Name = "Example" };
    Check(entry.Id == (entry with { Name = "EXAMPLE" }).Id, "Case identity");
    Check(entry.Id != (entry with { Scope = ScopeKind.Machine }).Id, "Scope identity");
    Check(entry.Id != (entry with { RegistryView = 32 }).Id, "View identity");
});
Test("Grouping never merges ambiguous launchers", () =>
{
    var a = new StartupEntry { Name = "A", Executable = @"C:\Windows\System32\cmd.exe" };
    Check(a.GroupKey != (a with { Name = "B" }).GroupKey, "Launcher was merged");
    Check(EntryGrouping.Summarize([a with { State = EntryState.Enabled }, a with { State = EntryState.DisabledByUs }]) == "部分开启", "Mixed summary");
    Check(EntryGrouping.Summarize([a with { State = EntryState.Enabled }, a with { State = EntryState.Unknown }]) == "含未知状态", "Unknown summary");
});
Test("Disable and restore persist backups across service instances", () =>
{
    var (service, provider, store) = Fixture();
    var original = provider.Live!.Entry;
    Check(service.Execute(new(OperationKind.Disable, original)).Success, "Disable failed");
    Check(provider.Live == null && store.ActiveBackups().Count == 1, "Missing durable backup");
    var fresh = new StartupService([provider], scope => scope == ScopeKind.CurrentUser ? new BackupStore(scope, store.Root) : new BackupStore(scope, store.Root + "-machine"));
    var disabled = fresh.Scan().Entries.Single();
    Check(disabled.State == EntryState.DisabledByUs, "Disabled state");
    Check(fresh.Execute(new(OperationKind.Restore, disabled)).Success, "Restore failed");
    Check(provider.Live?.Entry.Fingerprint == original.Fingerprint && store.ActiveBackups().Count == 0, "Restore equality");
    Check(store.History().Count >= 4, "Missing audit history");
});
Test("Changed config is rejected before any mutation", () =>
{
    var (service, provider, store) = Fixture();
    var stale = provider.Live!.Entry;
    provider.Replace("changed");
    Check(!service.Execute(new(OperationKind.Disable, stale)).Success, "Stale value accepted");
    Check(provider.Live!.Entry.Command == "changed" && store.ActiveBackups().Count == 0, "Original changed");
});
Test("Restore conflict never overwrites new value; archive preserves it", () =>
{
    var (service, provider, store) = Fixture();
    Check(service.Execute(new(OperationKind.Disable, provider.Live!.Entry)).Success, "Disable");
    var disabled = service.Scan().Entries.Single();
    provider.Replace("replacement");
    Check(!service.Execute(new(OperationKind.Restore, disabled)).Success, "Conflict overwritten");
    var conflict = service.Scan().Entries.Single();
    Check(conflict.State == EntryState.Conflict && !conflict.CanRestore, "Conflict status");
    Check(service.Execute(new(OperationKind.KeepCurrent, conflict)).Success, "Archive");
    Check(provider.Live!.Entry.Command == "replacement" && store.ActiveBackups().Count == 0, "Archive changed current");
});
Test("Interrupted modification exposes recoverable original", () =>
{
    var (service, provider, store) = Fixture();
    var original = provider.Live!;
    store.Prepare(original);
    provider.Live = null;
    var pending = service.Scan().Entries.Single();
    Check(pending.State == EntryState.RecoveryNeeded && pending.CanRestore, "Recovery not exposed");
    Check(service.Execute(new(OperationKind.Restore, pending)).Success, "Recovery failed");
});
Test("Failed delete keeps backup and reports actual state", () =>
{
    var (service, provider, store) = Fixture();
    provider.FailDisable = true;
    Check(!service.Execute(new(OperationKind.Disable, provider.Live!.Entry)).Success, "Failure misreported");
    Check(provider.Live != null && store.ActiveBackups().Count == 1, "Original or backup lost");
    Check(service.Scan().Entries.Single().State == EntryState.Conflict, "Pending unchanged original not detected");
});
Test("Unavailable provider does not pretend original is absent", () =>
{
    var (service, provider, _) = Fixture();
    Check(service.Execute(new(OperationKind.Disable, provider.Live!.Entry)).Success, "Disable");
    provider.FailRead = true;
    var scan = service.Scan();
    Check(scan.Entries.Single().State == EntryState.Unknown && !scan.Entries.Single().CanRestore && scan.Errors.Count > 0, "Access error hidden");
});
Test("Providers reject out-of-scope locators", () =>
{
    bool rejected = false;
    try { RegistryProvider.Validate(new() { Source = SourceKind.Registry, Location = @"Software\Other", Name = "x" }); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Registry arbitrary path accepted");
    rejected = false;
    try { StartupFolderProvider.Validate(new() { Source = SourceKind.StartupFolder, Location = StartupFolderProvider.Folder(ScopeKind.CurrentUser), Name = "..\\x.exe" }); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Folder traversal accepted");
});

Test("Pipe rejects oversized and truncated operation messages", () =>
{
    using var tooLarge = new MemoryStream(BitConverter.GetBytes(2 * 1024 * 1024));
    bool rejected = false;
    try { PipeProtocol.Receive(tooLarge, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Oversized message accepted");
    using var truncated = new MemoryStream([5, 0, 0, 0, 65]);
    rejected = false;
    try { PipeProtocol.Receive(truncated, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (EndOfStreamException) { rejected = true; }
    Check(rejected, "Truncated message accepted");
});

Test("Backup folder identifiers reject traversal", () =>
{
    var (_, _, store) = Fixture();
    bool rejected = false;
    try { store.BackupDirectory("..\\..\\target"); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Backup traversal accepted");
});

Test("Real HKCU Run round trip preserves ExpandString and arguments", () =>
{
    var name = "StartupManager.Test." + Guid.NewGuid().ToString("N");
    using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
    using var run = root.CreateSubKey(RegistryProvider.RunPath, true);
    var original = $"\"{Environment.ProcessPath}\" --probe \"{Path.Combine(testRoot, "登录 验证.log")}\"";
    try
    {
        run.SetValue(name, original, RegistryValueKind.ExpandString);
        var provider = new RegistryProvider();
        var store = new BackupStore(ScopeKind.CurrentUser, Path.Combine(testRoot, "real-registry"));
        var service = new StartupService([provider], scope => scope == ScopeKind.CurrentUser ? store : new BackupStore(scope, Path.Combine(testRoot, "unused-machine")));
        var entry = provider.Read(new() { Source = SourceKind.Registry, Name = name, Location = RegistryProvider.RunPath, RegistryView = Environment.Is64BitOperatingSystem ? 64 : 32 })!.Entry;
        Check(entry.State == EntryState.Unknown, "Must not infer startup approval");
        Check(service.Execute(new(OperationKind.Disable, entry)).Success, "Real registry disable");
        Check(run.GetValue(name) == null, "Value not removed");
        var disabled = service.Scan().Entries.Single(x => x.Id == entry.Id);
        Check(service.Execute(new(OperationKind.Restore, disabled)).Success, "Real registry restore");
        Check((string?)run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == original && run.GetValueKind(name) == RegistryValueKind.ExpandString, "Type or arguments changed");
    }
    finally { run.DeleteValue(name, false); }
});

Test("Real Startup shortcut round trip preserves bytes and working directory", () =>
{
    var name = "StartupManager.Test." + Guid.NewGuid().ToString("N") + ".lnk";
    var folder = StartupFolderProvider.Folder(ScopeKind.CurrentUser);
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, name);
    object? shell = null, link = null;
    try
    {
        shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
        link = ((dynamic)shell!).CreateShortcut(path);
        ((dynamic)link).TargetPath = Environment.ProcessPath;
        ((dynamic)link).Arguments = "--probe \"" + Path.Combine(testRoot, "shortcut.log") + "\"";
        ((dynamic)link).WorkingDirectory = testRoot;
        ((dynamic)link).Save();
        var creationTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, creationTime);
        var original = StartupFolderProvider.HashFile(path);
        var provider = new StartupFolderProvider();
        var store = new BackupStore(ScopeKind.CurrentUser, Path.Combine(testRoot, "real-file"));
        var service = new StartupService([provider], scope => scope == ScopeKind.CurrentUser ? store : new BackupStore(scope, Path.Combine(testRoot, "unused-machine2")));
        var entry = provider.Read(new() { Source = SourceKind.StartupFolder, Name = name, Location = folder })!.Entry;
        Check(service.Execute(new(OperationKind.Disable, entry)).Success, "Real shortcut disable");
        Check(!File.Exists(path), "Shortcut not removed");
        var disabled = service.Scan().Entries.Single(x => x.Id == entry.Id);
        Check(service.Execute(new(OperationKind.Restore, disabled)).Success, "Real shortcut restore");
        Check(StartupFolderProvider.HashFile(path) == original, "Shortcut bytes changed");
        Check(File.GetCreationTimeUtc(path) == creationTime, "Shortcut creation time changed");
        Check(service.Execute(new(OperationKind.Disable, provider.Read(entry)!.Entry)).Success, "Second disable");
        var backup = store.ActiveBackups().Single();
        var backupFile = Path.Combine(store.BackupDirectory(backup.Id), "original");
        var originalBytes = File.ReadAllBytes(backupFile);
        try
        {
            File.AppendAllText(backupFile, "corrupted");
            var corrupted = service.Scan().Entries.Single(x => x.Id == entry.Id);
            Check(!service.Execute(new(OperationKind.Restore, corrupted)).Success, "Corrupt backup accepted");
            Check(!File.Exists(path), "Corrupt backup was copied into startup folder");
        }
        finally { File.WriteAllBytes(backupFile, originalBytes); }
        Check(service.Execute(new(OperationKind.Restore, service.Scan().Entries.Single(x => x.Id == entry.Id))).Success, "Recovery after damaged backup repair");
    }
    finally
    {
        if (link != null) Marshal.FinalReleaseComObject(link);
        if (shell != null) Marshal.FinalReleaseComObject(shell);
        if (File.Exists(path)) File.Delete(path);
    }
});

// A task fixture may be forbidden by local Windows policy; report a skip rather than ask for elevation.
object? scheduler = null, taskFolder = null;
string taskName = "StartupManager.Test." + Guid.NewGuid().ToString("N");
bool taskCreated = false;
try
{
    scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
    ((dynamic)scheduler!).Connect();
    taskFolder = ((dynamic)scheduler).GetFolder("\\");
    dynamic definition = ((dynamic)scheduler).NewTask(0);
    definition.RegistrationInfo.Description = "Temporary StartupManager integration test";
    definition.Principal.UserId = WindowsIdentity.GetCurrent().User!.Value;
    definition.Principal.LogonType = 3;
    definition.Settings.Enabled = true;
    definition.Settings.ExecutionTimeLimit = "PT1M";
    dynamic trigger = definition.Triggers.Create(9);
    trigger.UserId = WindowsIdentity.GetCurrent().User!.Value;
    dynamic taskAction = definition.Actions.Create(0);
    taskAction.Path = Environment.ProcessPath;
    taskAction.Arguments = "--probe \"" + Path.Combine(testRoot, "task.log") + "\"";
    dynamic registered = ((dynamic)taskFolder).RegisterTaskDefinition(taskName, definition, 2, null, null, 3);
    taskCreated = true;
    Marshal.FinalReleaseComObject(registered);
    Test("Real logon task disable / restore / external enable", () =>
    {
        var provider = new TaskProvider();
        var locator = new StartupEntry { Source = SourceKind.ScheduledTask, Scope = ScopeKind.Machine, Location = "\\" + taskName, Name = taskName };
        var snapshot = provider.Read(locator)!;
        Check(snapshot.Entry.State == EntryState.Enabled, "Task enabled state");
        provider.Disable(snapshot, "");
        Check(provider.Read(locator)!.Entry.State == EntryState.DisabledExternally, "Task disable failed");
        provider.Restore(new(Guid.NewGuid().ToString("N"), snapshot.Entry, snapshot.Payload, "Disabled", "", ""), "");
        Check(provider.Read(locator)!.Entry.Fingerprint == snapshot.Entry.Fingerprint, "Task restore mismatch");
        provider.Disable(provider.Read(locator)!, "");
        provider.Enable(provider.Read(locator)!);
        Check(provider.Read(locator)!.Entry.State == EntryState.Enabled, "Task enable failed");
    });
}
catch (COMException e) when ((uint)e.HResult == 0x80070005)
{
    skipped++; Console.WriteLine("SKIP Real scheduled-task fixture: local policy denied creation; no elevation requested.");
}
catch (Exception e) { failed++; Console.WriteLine("FAIL Scheduled-task fixture :: " + e); }
finally
{
    if (taskCreated) ((dynamic)taskFolder!).DeleteTask(taskName, 0);
    if (taskFolder != null) Marshal.FinalReleaseComObject(taskFolder);
    if (scheduler != null) Marshal.FinalReleaseComObject(scheduler);
}

Console.WriteLine($"RESULT passed={passed} failed={failed} skipped={skipped}");
Console.WriteLine("Evidence: " + testRoot);
return failed == 0 ? 0 : 1;

sealed class MemoryProvider : IStartupProvider
{
    public SourceKind Source => SourceKind.Registry;
    public Snapshot? Live { get; set; }
    public bool FailDisable { get; set; }
    public bool FailRead { get; set; }
    public MemoryProvider() => Replace("original");
    public void Replace(string value)
    {
        var payload = Json.Write(new RegistryPayload(value, (int)RegistryValueKind.String));
        Live = new(new() { Source = Source, Location = RegistryProvider.RunPath, Name = "Fixture", Command = value, State = EntryState.Unknown,
            Fingerprint = StartupEntry.Hash(payload) }, payload);
    }
    public ScanResult Scan() => new(Live == null ? [] : [Live.Entry], []);
    public Snapshot? Read(StartupEntry locator) => FailRead ? throw new UnauthorizedAccessException("Test access denied") : Live;
    public void Disable(Snapshot snapshot, string backupDirectory) { if (FailDisable) throw new IOException("Test delete denied"); Live = null; }
    public void Restore(BackupRecord backup, string backupDirectory) { if (Live != null) throw new IOException("Conflict"); Live = new(backup.Entry, backup.Payload); }
    public void Enable(Snapshot snapshot) => throw new NotSupportedException();
}
