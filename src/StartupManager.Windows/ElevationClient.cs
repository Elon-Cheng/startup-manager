using StartupManager.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace StartupManager.Windows;

public static class ElevationClient
{
    public static async Task<OperationResult> ExecuteAsync(OperationRequest request)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "StartupManager.PrivilegedHelper.exe");
        if (!File.Exists(helper)) return new(false, "缺少权限辅助程序，请使用完整发布包。");
        var name = "StartupManager-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Process? process = null;
        bool sent = false;
        try
        {
            process = Process.Start(new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas",
                Arguments = "--pipe " + name, WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) return new(false, "无法启动权限辅助程序。");
            var connect = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connect, exited) == exited && !pipe.IsConnected) return new(false, "辅助程序未连接。请使用同一 Windows 账户进行提权。");
            await connect;
            await PipeProtocol.Send(pipe, Json.Write(request), timeout.Token);
            sent = true;
            return Json.Read<OperationResult>(await PipeProtocol.Receive(pipe, timeout.Token));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new(false, "你取消了管理员授权，未执行此操作。"); }
        catch (OperationCanceledException) { return new(false, sent ? "等待结果超时，操作可能已执行，请刷新检查实际状态。" : "权限连接超时，未发送操作。"); }
        catch (Exception e) { return new(false, "权限辅助程序：" + e.Message + (sent ? " 请刷新检查实际状态。" : "")); }
        finally { process?.Dispose(); }
    }
}

public static class PipeProtocol
{
    private const int Limit = 1024 * 1024;
    public static async Task Send(Stream stream, string value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > Limit) throw new InvalidDataException("操作数据过大。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    public static async Task<string> Receive(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        int size = BitConverter.ToInt32(length);
        if (size < 1 || size > Limit) throw new InvalidDataException("操作数据长度无效。");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return Encoding.UTF8.GetString(bytes);
    }
}
