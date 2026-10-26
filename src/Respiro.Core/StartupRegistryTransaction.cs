using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
namespace Respiro.Core;

/// <summary>Only the existing HKCU Run key. Native transactions reject concurrent third-party changes.</summary>
internal static class StartupRegistryTransaction
{
    static SafeFileHandle Begin()
    {
        var handle = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 5000, "Respiro: alteração individual confirmada de Run");
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Transações de Registro indisponíveis.", new Win32Exception(Marshal.GetLastWin32Error())); }
        return handle;
    }
    internal static void ProbeWithoutRegistryChanges()
    {
        using var transaction = Begin();
        if (!RollbackTransaction(transaction)) throw new IOException("Não foi possível encerrar a transação de teste.");
    }
    internal static void ProbeRunReadOnly()
    {
        using var transaction = Begin();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        int error = RegOpenKeyTransacted(root.Handle, WindowsStartupBackend.RunKey, 0, 0x101, out var handle, transaction, IntPtr.Zero);
        using (handle)
        {
            if (error != 0) throw new IOException("Consulta transacional somente leitura indisponível.", new Win32Exception(error));
        }
        if (!RollbackTransaction(transaction)) throw new IOException("Rollback da consulta falhou.");
    }
    internal static bool NameExists(IEnumerable<string> names, string selected) => names.Contains(selected, StringComparer.OrdinalIgnoreCase);
    internal static void Change(StartupEntry entry, bool restore)
    {
        using var transaction = Begin();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            int error = RegOpenKeyTransacted(root.Handle, WindowsStartupBackend.RunKey, 0, 0x103, out var nativeKey, transaction, IntPtr.Zero);
            using (nativeKey)
            {
                if (error != 0) throw new IOException("A chave Run original não pôde ser aberta em transação.", new Win32Exception(error));
                using var key = RegistryKey.FromHandle(nativeKey, RegistryView.Registry64);
                bool exists = NameExists(key.GetValueNames(), entry.Name);
                if (restore)
                {
                    if (exists) throw new IOException("Nome já utilizado; nenhum valor será sobrescrito.");
                    key.SetValue(entry.Name, entry.Command, (RegistryValueKind)entry.ValueKind);
                }
                else
                {
                    if (!exists || key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != entry.Command ||
                        (int)key.GetValueKind(entry.Name) != entry.ValueKind) throw new IOException("Run mudou; alteração cancelada.");
                    key.DeleteValue(entry.Name, true);
                }
                if (!CommitTransaction(transaction)) throw new IOException("Conflito ou falha ao confirmar a alteração; transação revertida.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        catch { RollbackTransaction(transaction); throw; }
    }
    [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr id, uint options, uint isolation, uint flags, uint timeout, string description);
    [DllImport("ktmw32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("ktmw32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RollbackTransaction(SafeFileHandle transaction);
    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW", CharSet = CharSet.Unicode)] static extern int RegOpenKeyTransacted(SafeRegistryHandle root, string subkey, uint options, uint access, out SafeRegistryHandle key, SafeFileHandle transaction, IntPtr extended);
}
