using System.Text.Json;
using System.Text;
using Microsoft.Win32;

namespace Respiro.Core;

public enum StartupOrigin { UserRun, MachineRun, UserFolder, CommonFolder, LogonTask }
public sealed record StartupEntry(string Name, StartupOrigin Origin, string Locator, string Command, int ValueKind,
    string Target, string Publisher, string ExecutableHash, string? ShortcutBase64, bool Manageable, string Reason, string NativeState = "Presente; ativação efetiva não confirmada")
{
    public string Source => Origin.ToString() + ": " + Locator;
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "Não identificado" : Publisher;
    public string TargetText => string.IsNullOrWhiteSpace(Target) ? "Não identificado com segurança" : Target;
    public string Impact => "Não medido; alterar inicialização não libera espaço de instalação.";
}
public sealed record OptimizationEvent(Guid Id, DateTime At, string Action, string State, StartupEntry? Entry, string Detail)
{
    internal static string FailureDetail(Exception e) => e.GetType().Name +
        (e is System.ComponentModel.Win32Exception win32 ? $" (Win32={win32.NativeErrorCode})" : "") + ": " + e.Message;
}
public interface IOptimizationJournal
{
    void Append(OptimizationEvent operation);
    IReadOnlyList<OptimizationEvent> Read();
}
public sealed class OptimizationJournal(string path) : IOptimizationJournal
{
    const long Limit = 4 * 1024 * 1024;
    public void Append(OptimizationEvent operation)
    {
        string full = LockedFile.Canonical(Path.GetFullPath(path));
        for (var p = new DirectoryInfo(Path.GetDirectoryName(full)!); p != null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Registro de operações redirecionado.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(operation) + "\n");
        using var log = LockedFile.Open(full, write: true, create: !File.Exists(full));
        log.RequireUnchanged();
        if (log.Stamp.Bytes > 0 && log.ReadAt(log.Stamp.Bytes - 1, 1)[0] != 10) throw new IOException("Registro interrompido: nova alteração bloqueada para preservar o recibo.");
        if (log.Stamp.Bytes + bytes.Length > Limit) throw new IOException("Registro cheio: operação bloqueada para preservar recibos.");
        RandomAccess.Write(log.Handle, bytes, log.Stamp.Bytes); RandomAccess.FlushToDisk(log.Handle);
    }
    public IReadOnlyList<OptimizationEvent> Read()
    {
        if (!File.Exists(path)) return [];
        using var log = LockedFile.Open(Path.GetFullPath(path));
        log.RequireUnchanged();
        if (log.Stamp.Bytes > Limit) throw new IOException("Registro inválido.");
        var buffer = new byte[checked((int)log.Stamp.Bytes)]; int offset = 0;
        while (offset < buffer.Length)
        {
            int n = RandomAccess.Read(log.Handle, buffer.AsSpan(offset, Math.Min(65536, buffer.Length - offset)), offset);
            if (n == 0) throw new IOException("Registro incompleto."); offset += n;
        }
        var result = new List<OptimizationEvent>();
        foreach (var line in Encoding.UTF8.GetString(buffer).Split('\n').Take(10000))
        {
            try { if (JsonSerializer.Deserialize<OptimizationEvent>(line, new JsonSerializerOptions { MaxDepth = 16 }) is { } item) result.Add(item); }
            catch (JsonException) { }
        }
        return result;
    }
}
public interface IStartupBackend
{
    StartupEntry? Read(StartupEntry original);
    void ValidateOriginal(StartupEntry original);
    void Remove(StartupEntry original);
    void Restore(StartupEntry original);
}
public sealed class StartupChangePlan
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Created { get; } = DateTime.UtcNow;
    public StartupEntry Entry { get; }
    public bool Restoring { get; }
    public string Confirmation => (Restoring ? "REATIVAR " : "DESATIVAR ") + Entry.Name;
    internal StartupChangePlan(StartupEntry entry, bool restoring) { Entry = entry; Restoring = restoring; }
}
public sealed class StartupChangeService(IStartupBackend backend, IOptimizationJournal journal)
{
    readonly HashSet<Guid> used = [];
    public StartupChangePlan Prepare(StartupEntry entry, bool restoring)
    {
        backend.ValidateOriginal(entry);
        var current = backend.Read(entry);
        if (restoring ? current != null : current != entry) throw new IOException("Entrada mudou ou já existe; atualize a lista.");
        return new(entry, restoring);
    }
    public string Execute(StartupChangePlan plan, string confirmation)
    {
        if (confirmation != plan.Confirmation || DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(5))
            throw new IOException("Confirmação incorreta ou prévia expirada.");
        using var mutex = new Mutex(false, @"Local\Respiro.Optimization." + Environment.UserName);
        bool held;
        try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Outra alteração está em andamento.");
        try
        {
            if (used.Contains(plan.Id)) throw new IOException("Prévia já utilizada.");
            backend.ValidateOriginal(plan.Entry);
            var current = backend.Read(plan.Entry);
            if (plan.Restoring ? current != null : current != plan.Entry) throw new IOException("Entrada mudou; nada foi alterado.");
            string action = plan.Restoring ? "startup-restore" : "startup-disable";
            journal.Append(new(plan.Id, DateTime.UtcNow, action, "intent", plan.Entry, "Estado original preservado antes da alteração."));
            used.Add(plan.Id);
            try { if (plan.Restoring) backend.Restore(plan.Entry); else backend.Remove(plan.Entry); }
            catch (Exception e)
            {
                try { journal.Append(new(plan.Id, DateTime.UtcNow, action, "failed", plan.Entry, OptimizationEvent.FailureDetail(e))); } catch { }
                throw;
            }
            string detail = plan.Restoring ? "Entrada reposta sem sobrescrever outra; o estado de habilitação do Windows continua independente." :
                "Entrada removida do mecanismo de inicialização; recibo permite reposição. O aplicativo continua instalado.";
            try { journal.Append(new(plan.Id, DateTime.UtcNow, action, "completed", plan.Entry, detail)); }
            catch (Exception e) { detail += " A intenção foi gravada, mas o resultado não pôde ser registrado: " + e.Message; }
            return detail;
        }
        finally { mutex.ReleaseMutex(); }
    }
    public IReadOnlyList<StartupEntry> Restorable()
    {
        var entries = journal.Read().Where(e => e.Action == "startup-disable" && e.State == "intent" && e.Entry != null)
            .Reverse().Select(e => e.Entry!).DistinctBy(e => (e.Origin, e.Locator.ToUpperInvariant(), e.Name.ToUpperInvariant())).Take(500);
        var result = new List<StartupEntry>();
        foreach (var entry in entries)
            try { backend.ValidateOriginal(entry); if (backend.Read(entry) == null) result.Add(entry with { NativeState = "Desativada pelo Respiro; recibo disponível" }); } catch { }
        return result;
    }
}
public sealed class WindowsStartupBackend : IStartupBackend
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public void ValidateOriginal(StartupEntry item)
    {
        BetaReleasePolicy.Demand(BetaOperation.StartupChange);
        if (!item.Manageable || item.Origin != StartupOrigin.UserRun || item.Locator != RunKey ||
            item.Name.Length == 0 || item.Command.Length > 260 || item.ValueKind is not (1 or 2) || item.ShortcutBase64 != null)
            throw new IOException("Somente valores Run compatíveis do usuário atual podem ser alterados.");
        if (!WindowsManagementGuard.AllowsChanges(out var reason)) throw new IOException(reason);
        if (!OptimizationSafety.CompatibleStartupCommand(item.Command)) throw new IOException("Argumentos de inicialização fora da compatibilidade.");
        if (OptimizationSafety.ParseExecutable(item.Command) != item.Target) throw new IOException("Destino não corresponde ao comando.");
        var evidence = OptimizationSafety.Inspect(item.Target);
        if (!evidence.Compatible || evidence.Hash != item.ExecutableHash) throw new IOException("Executável alterado, protegido ou não comprovado: " + evidence.Reason);
    }
    public StartupEntry? Read(StartupEntry original)
    {
        ValidateOriginal(original);
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(RunKey);
        if (key == null || !StartupRegistryTransaction.NameExists(key.GetValueNames(), original.Name)) return null;
        var raw = key.GetValue(original.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return original with { Command = raw as string ?? "", ValueKind = (int)key.GetValueKind(original.Name) };
    }
    public void Remove(StartupEntry original)
    {
        if (Read(original) != original) throw new IOException("Entrada mudou antes da alteração.");
        StartupRegistryTransaction.Change(original, false);
    }
    public void Restore(StartupEntry original)
    {
        ValidateOriginal(original);
        StartupRegistryTransaction.Change(original, true);
    }
}
