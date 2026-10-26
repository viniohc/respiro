using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace Respiro.Core;

public sealed record StartupInventoryResult(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Notices, bool Cancelled);
public static class StartupInventory
{
    public static StartupInventoryResult Read(CancellationToken token = default)
    {
        var rows = new List<StartupEntry>(); var notices = new List<string> { BetaReleasePolicy.Reason(BetaOperation.StartupChange) };
        bool localDevice = WindowsManagementGuard.AllowsChanges(out string managementReason);
        if (!localDevice) notices.Add(managementReason);
        try
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(WindowsStartupBackend.RunKey);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames().Take(300))
                    {
                        token.ThrowIfCancellationRequested();
                        string command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                        string target = OptimizationSafety.ParseExecutable(command) ?? "";
                        var evidence = OptimizationSafety.Inspect(target);
                        int kind = (int)key.GetValueKind(name);
                        bool user = hive == RegistryHive.CurrentUser;
                        rows.Add(new(name, user ? StartupOrigin.UserRun : StartupOrigin.MachineRun,
                            user ? WindowsStartupBackend.RunKey : $"{hive}/{view}/{WindowsStartupBackend.RunKey}",
                            command, kind, target, evidence.Publisher, evidence.Hash, null,
                            BetaReleasePolicy.Allows(BetaOperation.StartupChange) && user && localDevice && name.Length > 0 && command.Length <= 260 && kind is 1 or 2 && evidence.Compatible && OptimizationSafety.CompatibleStartupCommand(command),
                            user ? (!localDevice ? managementReason : !OptimizationSafety.CompatibleStartupCommand(command) ? "Comando ou argumentos fora da compatibilidade: diagnóstico." : evidence.Reason) : "Entrada da máquina: somente diagnóstico; sem elevação ou alteração."));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { notices.Add($"Run {hive}/{view}: {e.Message}"); }
            }
            foreach (var kind in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                var folder = Environment.GetFolderPath(kind); if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(folder).Take(300))
                    {
                        token.ThrowIfCancellationRequested();
                        string target = "", command = "", payload = ""; bool validShortcut = false;
                        try
                        {
                            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw new IOException("Somente atalhos .lnk são compatíveis.");
                            using (var file = LockedFile.Open(path))
                            {
                                if (file.Stamp.Bytes > 65536) throw new IOException("Atalho excede o limite.");
                                file.RequireUnchanged(); payload = Convert.ToBase64String(file.ReadAt(0, (int)file.Stamp.Bytes));
                            }
                            object? shell = null, shortcut = null;
                            try
                            {
                                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                                shortcut = ((dynamic)shell!).CreateShortcut(path);
                                target = (string)((dynamic)shortcut).TargetPath;
                                command = "\"" + target + "\" " + (string)((dynamic)shortcut).Arguments;
                                validShortcut = OptimizationSafety.ParseExecutable(command) == target;
                            }
                            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
                        }
                        catch (Exception e) { notices.Add(Path.GetFileName(path) + ": " + e.Message); }
                        var evidence = OptimizationSafety.Inspect(target);
                        bool user = kind == Environment.SpecialFolder.Startup;
                        rows.Add(new(Path.GetFileName(path), user ? StartupOrigin.UserFolder : StartupOrigin.CommonFolder,
                            path, command, 0, target, evidence.Publisher, evidence.Hash, validShortcut ? payload : null,
                            false,
                            "Atalho: somente diagnóstico; reversão de todos os campos do formato não comprovada. " + evidence.Reason));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { notices.Add("Pasta Inicializar: " + e.Message); }
            }
            ReadTasks(rows, notices, token);
        }
        catch (OperationCanceledException) { return new(rows, notices, true); }
        return new(rows, notices, false);
    }
    static string TaskTargets(object definition)
    {
        object actions = ((dynamic)definition).Actions;
        var paths = new List<string>();
        try
        {
            for (int i = 1; i <= Math.Min(4, (int)((dynamic)actions).Count); i++)
            {
                object action = ((dynamic)actions)[i];
                try { if ((int)((dynamic)action).Type == 0) paths.Add((string)((dynamic)action).Path); }
                finally { Marshal.FinalReleaseComObject(action); }
            }
        }
        finally { Marshal.FinalReleaseComObject(actions); }
        return paths.Count == 0 ? "Ação não executável ou inacessível" : string.Join(" ; ", paths);
    }
    static void ReadTasks(List<StartupEntry> rows, List<string> notices, CancellationToken token)
    {
        object? scheduler = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)scheduler!).Connect();
            object root = ((dynamic)scheduler).GetFolder("\\");
            var queue = new Queue<object>(); queue.Enqueue(root); int examined = 0;
            try
            {
                while (queue.Count > 0)
                {
                    object folder = queue.Dequeue();
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        object tasks = ((dynamic)folder).GetTasks(1);
                        try
                        {
                            int count = (int)((dynamic)tasks).Count;
                            for (int i = 1; i <= count; i++)
                            {
                                token.ThrowIfCancellationRequested();
                                if (++examined > 1500) { notices.Add("Limite de 1.500 tarefas consultadas; inventário parcial."); return; }
                                object task = ((dynamic)tasks)[i], definition = ((dynamic)task).Definition;
                                object triggers = ((dynamic)definition).Triggers;
                                try
                                {
                                    bool logon = false;
                                    for (int t = 1; t <= (int)((dynamic)triggers).Count; t++)
                                    {
                                        object trigger = ((dynamic)triggers)[t];
                                        try { if ((int)((dynamic)trigger).Type == 9) logon = true; }
                                        finally { Marshal.FinalReleaseComObject(trigger); }
                                    }
                                    if (logon)
                                        rows.Add(new((string)((dynamic)task).Name, StartupOrigin.LogonTask, (string)((dynamic)task).Path,
                                            "", 0, TaskTargets(definition), "Não identificado", "", null, false,
                                            "Tarefa de logon: diagnóstico. Principal, políticas e dependências não autorizam alteração automática.",
                                            (bool)((dynamic)task).Enabled ? "Tarefa habilitada" : "Tarefa desabilitada"));
                                }
                                finally { Marshal.FinalReleaseComObject(triggers); Marshal.FinalReleaseComObject(definition); Marshal.FinalReleaseComObject(task); }
                            }
                        }
                        finally { Marshal.FinalReleaseComObject(tasks); }
                        object children = ((dynamic)folder).GetFolders(0);
                        try
                        {
                            for (int i = 1; i <= (int)((dynamic)children).Count && queue.Count < 200; i++)
                                queue.Enqueue(((dynamic)children)[i]);
                        }
                        finally { Marshal.FinalReleaseComObject(children); }
                    }
                    finally { Marshal.FinalReleaseComObject(folder); }
                }
            }
            finally { while (queue.TryDequeue(out var folder)) Marshal.FinalReleaseComObject(folder); }
        }
        catch (Exception e) when (e is not OperationCanceledException) { notices.Add("Agendador de Tarefas: " + e.Message); }
        finally { if (scheduler != null) Marshal.FinalReleaseComObject(scheduler); }
    }
}
