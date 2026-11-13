using System.IO.Pipes;
using StartupManager.Core;
using StartupManager.Windows;

if (args.Length != 2 || args[0] != "--pipe" || !args[1].StartsWith("StartupManager-") ||
    !Guid.TryParseExact(args[1]["StartupManager-".Length..], "N", out _) || !StartupService.IsAdministrator) return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
try
{
    await pipe.ConnectAsync(timeout.Token);
    var request = Json.Read<OperationRequest>(await PipeProtocol.Receive(pipe, timeout.Token));
    var result = request.Entry.Scope != ScopeKind.Machine
        ? new OperationResult(false, "辅助程序只接受系统公共范围操作。")
        : new StartupService().Execute(request);
    await PipeProtocol.Send(pipe, Json.Write(result), timeout.Token);
    return result.Success ? 0 : 1;
}
catch { return 3; }
