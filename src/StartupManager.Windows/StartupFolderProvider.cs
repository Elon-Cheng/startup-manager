using StartupManager.Core;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;

namespace StartupManager.Windows;

public sealed record FilePayload(string Hash, string Security);

public sealed class StartupFolderProvider : IStartupProvider
{
    public SourceKind Source => SourceKind.StartupFolder;
    public static string Folder(ScopeKind scope) => Environment.GetFolderPath(scope == ScopeKind.CurrentUser ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);

    public static void Validate(StartupEntry entry)
    {
        var root = Folder(entry.Scope);
        if (entry.Source != SourceKind.StartupFolder || !Enum.IsDefined(entry.Scope) || string.IsNullOrWhiteSpace(root) ||
            !string.Equals(Path.GetFullPath(entry.Location), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Name) || entry.Name != Path.GetFileName(entry.Name) || entry.Name is "." or ".." || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("拒绝访问范围外的启动文件。");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("启动文件夹是重解析点，暂不支持修改。");
    }

    public ScanResult Scan()
    {
        var result = new ScanResult([], []);
        foreach (var scope in Enum.GetValues<ScopeKind>())
        {
            try
            {
                var folder = Folder(scope);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var item = Read(new() { Source = Source, Scope = scope, Name = Path.GetFileName(file), Location = folder });
                        if (item != null) result.Entries.Add(item.Entry);
                    }
                    catch (Exception ex) { result.Errors.Add($"启动文件 {Path.GetFileName(file)}：{ex.Message}"); }
                }
            }
            catch (Exception ex) { result.Errors.Add($"启动文件夹 {scope}：{ex.Message}"); }
        }
        return result;
    }

    public static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public Snapshot? Read(StartupEntry locator)
    {
        Validate(locator);
        var path = Path.Combine(locator.Location, locator.Name);
        // File.Exists hides permission errors, so query attributes explicitly.
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("链接或目录不在支持范围内。");
        var hash = HashFile(path);
        var security = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var executable = path;
        var args = "";
        var note = "入口已配置；Windows 自身的禁用设置仍可能生效。关闭时保留原文件，恢复时移回原位置。";
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                shortcut = ((dynamic)shell!).CreateShortcut(path);
                executable = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
                args = (string)((dynamic)shortcut).Arguments;
            }
            catch (Exception e) { executable = ""; note += " 快捷方式目标读取失败：" + e.Message; }
            finally { Com.Release(shortcut); Com.Release(shell); }
        }
        return new(locator with { Command = $"\"{executable}\" {args}".TrimEnd(), Executable = executable,
            Arguments = args, State = EntryState.Unknown, Fingerprint = hash, BackupId = null, Note = note }, Json.Write(new FilePayload(hash, security)));
    }

    public void Disable(Snapshot snapshot, string backupDirectory)
    {
        Validate(snapshot.Entry);
        var current = Read(snapshot.Entry) ?? throw new IOException("启动文件已不存在。");
        if (current.Entry.Fingerprint != snapshot.Entry.Fingerprint) throw new IOException("启动文件已变化，请刷新。");
        Directory.CreateDirectory(backupDirectory);
        CheckBackupPath(backupDirectory);
        var source = Path.Combine(snapshot.Entry.Location, snapshot.Entry.Name);
        var destination = Path.Combine(backupDirectory, "original");
        // Copy first so a failed delete still leaves the original usable and the backup intact.
        File.Copy(source, destination, false);
        File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        if (HashFile(destination) != snapshot.Entry.Fingerprint) throw new IOException("备份内容校验失败，原文件未删除。");
        var inherited = new FileSecurity();
        inherited.SetAccessRuleProtection(false, false);
        new FileInfo(destination).SetAccessControl(inherited);
        if (HashFile(source) != snapshot.Entry.Fingerprint) throw new IOException("备份期间启动文件发生变化，原文件未删除。");
        File.Delete(source);
    }

    public static void CheckBackupPath(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("备份路径包含重解析点。");
            current = current.Parent;
        }
    }

    public void Restore(BackupRecord backup, string backupDirectory)
    {
        Validate(backup.Entry);
        CheckBackupPath(backupDirectory);
        var destination = Path.Combine(backup.Entry.Location, backup.Entry.Name);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("原位置已有文件，未覆盖。");
        var source = Path.Combine(backupDirectory, "original");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份文件不能是链接。");
        var payload = Json.Read<FilePayload>(backup.Payload);
        if (HashFile(source) != payload.Hash) throw new IOException("备份文件校验失败，未恢复。");
        Directory.CreateDirectory(backup.Entry.Location);
        File.Copy(source, destination, false);
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(payload.Security, AccessControlSections.Access);
        new FileInfo(destination).SetAccessControl(security);
        File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        File.SetAttributes(destination, File.GetAttributes(source));
    }

    public void Enable(Snapshot snapshot) => throw new NotSupportedException("请通过 Windows 启动应用设置管理外部禁用状态。");
}

internal static class Com
{
    public static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
