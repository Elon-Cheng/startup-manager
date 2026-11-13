using Microsoft.Data.Sqlite;
using StartupManager.Core;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StartupManager.Storage;

public sealed class BackupStore
{
    public string Root { get; }
    public ScopeKind Scope { get; }
    private string DbPath => Path.Combine(Root, "state.db");
    public BackupStore(ScopeKind scope, string? rootOverride = null)
    {
        Scope = scope;
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(scope == ScopeKind.Machine
            ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData), "StartupManager", "v1");
    }

    public void EnsureWritable()
    {
        if (Scope == ScopeKind.Machine && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("机器级备份必须由管理员写入。");
        for (var parent = new DirectoryInfo(Root); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("备份路径不能包含链接。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Add(SecurityIdentifier sid, FileSystemRights rights) => security.AddAccessRule(new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        if (Scope == ScopeKind.CurrentUser) Add(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl);
        else
        {
            Add(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
            var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(admin);
            // Protect the parent too: users must not be able to replace the database directory.
            var parent = Directory.GetParent(Root) ?? throw new IOException("备份路径无效。");
            foreach (var path in new[] { parent.FullName, Root })
            {
                var info = new DirectoryInfo(path);
                if (info.Exists)
                {
                    var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                    if (owner == null || (!owner.Equals(admin) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                        throw new IOException("机器级备份目录的所有者不受信任，拒绝读取或覆盖。请由管理员检查目录。");
                }
                else info.Create();
                info.SetAccessControl(security);
            }
        }
        Directory.CreateDirectory(Root);
        new DirectoryInfo(Root).SetAccessControl(security);
        using var connection = Open(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS backups(id TEXT PRIMARY KEY, entry_id TEXT NOT NULL, entry TEXT NOT NULL, payload TEXT NOT NULL, status TEXT NOT NULL, created TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, name TEXT NOT NULL, action TEXT NOT NULL, result TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        return connection;
    }

    public List<BackupRecord> ActiveBackups()
    {
        if (!File.Exists(DbPath)) return [];
        using var connection = Open(true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, entry, payload, status, created, message FROM backups WHERE status NOT IN ('Restored','Completed','Cancelled') ORDER BY created";
        using var reader = cmd.ExecuteReader();
        var result = new List<BackupRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), Json.Read<StartupEntry>(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public BackupRecord Prepare(Snapshot snapshot)
    {
        EnsureWritable();
        if (ActiveBackups().Any(x => x.Entry.Id == snapshot.Entry.Id)) throw new InvalidOperationException("此入口已有待恢复记录，请先处理原备份。");
        var record = new BackupRecord(Guid.NewGuid().ToString("N"), snapshot.Entry, snapshot.Payload, "Prepared", DateTimeOffset.UtcNow.ToString("O"), "原始配置已保存，等待修改。");
        using var connection = Open(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO backups VALUES ($id,$entryId,$entry,$payload,$status,$created,$message)";
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$entryId", record.Entry.Id);
        cmd.Parameters.AddWithValue("$entry", Json.Write(record.Entry));
        cmd.Parameters.AddWithValue("$payload", record.Payload);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$created", record.CreatedUtc);
        cmd.Parameters.AddWithValue("$message", record.Message);
        cmd.ExecuteNonQuery();
        return record;
    }

    public string BackupDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("无效备份标识。");
        return Path.Combine(Root, "files", id);
    }

    public void SetStatus(string id, string status, string message)
    {
        using var connection = Open(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE backups SET status=$status,message=$message WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.ExecuteNonQuery();
    }

    public void Log(string name, string action, string result, string message)
    {
        EnsureWritable();
        using var connection = Open(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO history(time,name,action,result,message) VALUES($time,$name,$action,$result,$message)";
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$result", result);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.ExecuteNonQuery();
    }

    public List<HistoryRecord> History()
    {
        if (!File.Exists(DbPath)) return [];
        using var connection = Open(true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT time,name,action,result,message FROM history ORDER BY id DESC LIMIT 500";
        using var reader = cmd.ExecuteReader();
        var rows = new List<HistoryRecord>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    public string? GetSetting(string key)
    {
        if (!File.Exists(DbPath)) return null;
        using var connection = Open(true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SaveSetting(string key, string value)
    {
        EnsureWritable();
        using var connection = Open(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
