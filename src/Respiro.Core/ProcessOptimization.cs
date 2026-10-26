using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Respiro.Core;

public sealed record ProcessSample(int Id, long StartedUtcTicks, string Name, string Publisher, string Executable,
    double? CpuPercent, long? MemoryBytes, double? IoBytesPerSecond, int Related, bool CanRequestClose, string Reason, string ExecutableHash)
{
    public string Cpu => CpuPercent is double n ? $"{n:N1}%" : "Não disponível";
    public string Memory => MemoryBytes is long n ? Sizes.Format(n) : "Não disponível";
    public string Io => IoBytesPerSecond is double n ? Sizes.Format((long)n) + "/s" : "Não disponível";
    public string Relation => $"{Related} com o mesmo executável (pista, não dependência)";
    public string Protection => CanRequestClose ? "Fechamento normal disponível" : "Protegido / somente diagnóstico";
}
public static class ProcessMonitor
{
    sealed record Point(int Id, long Started, string Name, string Path, string Company, double CpuMs, long Memory, ulong? Io, bool Window);
    public static async Task<IReadOnlyList<ProcessSample>> Sample(CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        var first = Read(token);
        await Task.Delay(800, token);
        var second = Read(token);
        double seconds = timer.Elapsed.TotalSeconds;
        var rows = new List<ProcessSample>();
        foreach (var item in second.Values)
        {
            token.ThrowIfCancellationRequested();
            first.TryGetValue(item.Id, out var before);
            bool same = before?.Started == item.Started;
            double? cpu = same ? CpuDelta(before!.CpuMs, item.CpuMs, seconds, Environment.ProcessorCount) : null;
            double? io = same && before!.Io is ulong previous && item.Io is ulong now && now >= previous ? (now - previous) / seconds : null;
            bool allowedName = new[] { "spotify.exe", "vlc.exe", "telegram.exe" }.Contains(Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase);
            var evidence = BetaReleasePolicy.Allows(BetaOperation.NormalClose) && allowedName && item.Window ? OptimizationSafety.Inspect(item.Path) :
                new ExecutableEvidence(item.Path, item.Company, "", false, BetaReleasePolicy.Reason(BetaOperation.NormalClose));
            rows.Add(new(item.Id, item.Started, item.Name, item.Company, item.Path, cpu, item.Memory, io,
                second.Values.Count(p => p.Path.Length > 0 && p.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)),
                item.Window && evidence.Compatible, evidence.Reason, evidence.Hash));
        }
        return rows.OrderByDescending(p => p.MemoryBytes).ToArray();
    }
    internal static double? CpuDelta(double before, double after, double seconds, int processors) =>
        seconds <= 0 || processors < 1 || after < before ? null : Math.Clamp((after - before) / (seconds * 10 * processors), 0, 100);

    static Dictionary<int, Point> Read(CancellationToken token)
    {
        var result = new Dictionary<int, Point>();
        using var self = Process.GetCurrentProcess();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes.Take(1024))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string path = "", company = "";
                    try { path = process.MainModule?.FileName ?? ""; company = path.Length == 0 ? "" : FileVersionInfo.GetVersionInfo(path).CompanyName ?? ""; } catch { }
                    ulong? io = null;
                    try { if (GetProcessIoCounters(process.Handle, out var c)) io = c.ReadBytes + c.WriteBytes; } catch { }
                    result[process.Id] = new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName,
                        path, company, process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, io,
                        process.SessionId == self.SessionId && process.MainWindowHandle != IntPtr.Zero);
                }
                catch { /* Access denied or exited: do not invent zero measurements. */ }
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
}
public interface IProcessCloseBackend
{
    bool StillSame(ProcessSample selected);
    bool RequestNormalClose(ProcessSample selected);
}
public sealed class WindowsProcessCloseBackend : IProcessCloseBackend
{
    public bool StillSame(ProcessSample selected)
    {
        if (!BetaReleasePolicy.Allows(BetaOperation.NormalClose)) return false;
        try
        {
            using var p = Process.GetProcessById(selected.Id);
            using var self = Process.GetCurrentProcess();
            if (!selected.CanRequestClose || !new[] { "spotify.exe", "vlc.exe", "telegram.exe" }.Contains(Path.GetFileName(selected.Executable), StringComparer.OrdinalIgnoreCase) ||
                p.Id == Environment.ProcessId || p.SessionId != self.SessionId ||
                p.StartTime.ToUniversalTime().Ticks != selected.StartedUtcTicks || p.MainWindowHandle == IntPtr.Zero ||
                !string.Equals(p.MainModule?.FileName, selected.Executable, StringComparison.OrdinalIgnoreCase)) return false;
            var evidence = OptimizationSafety.Inspect(selected.Executable);
            return evidence.Compatible && evidence.Hash == selected.ExecutableHash;
        }
        catch { return false; }
    }
    public bool RequestNormalClose(ProcessSample selected)
    {
        BetaReleasePolicy.Demand(BetaOperation.NormalClose);
        return RequestForSameWindow(selected, StillSame);
    }
    // Shared native mechanism; authorization remains mandatory and production still uses StillSame.
    internal static bool RequestForSameWindow(ProcessSample selected, Func<ProcessSample, bool> authorize)
    {
        using var p = Process.GetProcessById(selected.Id);
        using var self = Process.GetCurrentProcess();
        _ = p.SafeHandle; // Pin this process identity before checking it and posting WM_CLOSE.
        if (p.Id == Environment.ProcessId || p.SessionId != self.SessionId ||
            p.StartTime.ToUniversalTime().Ticks != selected.StartedUtcTicks || p.MainWindowHandle == IntPtr.Zero ||
            !string.Equals(p.MainModule?.FileName, selected.Executable, StringComparison.OrdinalIgnoreCase) ||
            !authorize(selected)) throw new IOException("Processo mudou ou tornou-se protegido.");
        return p.CloseMainWindow();
    }
}
public sealed class ProcessCloseService(IProcessCloseBackend backend, IOptimizationJournal journal)
{
    public string Request(ProcessSample selected, string confirmation)
    {
        if (confirmation != "FECHAR " + selected.Id || !selected.CanRequestClose || !backend.StillSame(selected)) throw new IOException("Confirmação ou identidade do processo inválida.");
        var id = Guid.NewGuid();
        journal.Append(new(id, DateTime.UtcNow, "normal-close", "intent", null, $"PID={selected.Id}; início={selected.StartedUtcTicks}; {selected.Name}. Pode haver trabalho não salvo."));
        bool sent;
        try { sent = backend.RequestNormalClose(selected); }
        catch (Exception e)
        {
            try { journal.Append(new(id, DateTime.UtcNow, "normal-close", "failed", null, OptimizationEvent.FailureDetail(e))); } catch { }
            throw;
        }
        string detail = sent ? "Solicitação de fechamento enviada. O aplicativo pode pedir para salvar, recusar ou permanecer na bandeja. Atualize para conferir." :
            "O Windows não aceitou o pedido de fechamento para esta janela. Nenhum encerramento forçado foi executado.";
        try { journal.Append(new(id, DateTime.UtcNow, "normal-close", sent ? "requested" : "not-sent", null, detail)); } catch { detail += " Resultado sem registro adicional; intenção preservada."; }
        return detail + " Isso não altera a inicialização futura e não comprova economia de RAM.";
    }
}
