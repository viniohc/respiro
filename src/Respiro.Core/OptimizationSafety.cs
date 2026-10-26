using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Respiro.Core;

public sealed record ExecutableEvidence(string Path, string Publisher, string Hash, bool Compatible, string Reason);

/// <summary>A deliberately small compatibility list; unknown software is diagnostic only.</summary>
public static class OptimizationSafety
{
    static readonly Dictionary<string, string> consumers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify.exe"] = "Spotify", ["vlc.exe"] = "VideoLAN", ["steam.exe"] = "Valve",
        ["chrome.exe"] = "Google", ["firefox.exe"] = "Mozilla", ["telegram.exe"] = "Telegram"
    };
    public static string? ParseExecutable(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1); if (end < 2) return null;
            path = text[1..end];
        }
        else path = text.Split(' ', 2)[0];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith('\\') || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return LockedFile.Canonical(path); } catch { return null; }
    }
    public static bool CompatibleStartupCommand(string command)
    {
        string? target = ParseExecutable(command); if (target == null) return false;
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        int end = expanded.StartsWith('"') ? expanded.IndexOf('"', 1) + 1 : expanded.IndexOf(' ');
        string args = end < 0 ? "" : expanded[end..].Trim();
        if (args.Length == 0) return consumers.ContainsKey(Path.GetFileName(target));
        return Path.GetFileName(target).ToLowerInvariant() switch
        {
            "spotify.exe" => args is "--autostart" or "--minimized" or "--autostart --minimized",
            "steam.exe" => args == "-silent",
            "chrome.exe" => args is "--no-startup-window" or "--no-startup-window /prefetch:5",
            "firefox.exe" => args == "-silent",
            "vlc.exe" => args == "--qt-start-minimized",
            "telegram.exe" => args == "-autostart",
            _ => false
        };
    }
    public static bool ProtectedPath(string path) =>
        string.IsNullOrWhiteSpace(path) || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase) ||
        new[] { @"\dev\", @"\.git\", @"\.codex\", @"\codex", @"\claude", @"\multicodex", @"\sdk\", @"\worktrees\", @"\agents\", @"\e2.5\", @"\common files\" }
        .Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));
    public static bool SupportedConsumer(string file, string publisher) =>
        consumers.TryGetValue(Path.GetFileName(file), out var vendor) && publisher.Contains(vendor, StringComparison.OrdinalIgnoreCase);

    public static ExecutableEvidence Inspect(string path)
    {
        try
        {
            if (ProtectedPath(path)) return new(path, "", "", false, "Windows, desenvolvimento ou agentes: protegido.");
            var info = new FileInfo(LockedFile.Canonical(path));
            if (!info.Exists || info.Length > 256L * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return new(path, "", "", false, "Executável inacessível, redirecionado ou fora do limite.");
            for (var p = info.Directory; p != null; p = p.Parent)
                if ((p.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    new[] { ".limpapc-protect", ".git", "AGENTS.md" }.Any(m => File.Exists(Path.Combine(p.FullName, m)) || Directory.Exists(Path.Combine(p.FullName, m))))
                    return new(path, "", "", false, "Ancestral protegido ou redirecionado.");
            string company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            if (!SupportedConsumer(path, company)) return new(path, company, "", false, "Aplicativo fora da lista de compatibilidade: somente diagnóstico.");
            string? signer = TrustedSigner(path);
            if (signer == null) return new(path, company, "", false, "Assinatura confiável não comprovada (consulta local, sem downloads).");
            string vendor = consumers[Path.GetFileName(path)];
            if (!signer.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                return new(path, company, "", false, "Signatário não corresponde ao fornecedor compatível.");
            long length = info.Length; var modified = info.LastWriteTimeUtc;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            info.Refresh();
            if (info.Length != length || info.LastWriteTimeUtc != modified) throw new IOException("Executável mudou durante a consulta.");
            return new(path, company, hash, true, "Aplicativo compatível com assinatura validada; confirmação individual obrigatória.");
        }
        catch (Exception e) { return new(path, "", "", false, "Verificação inconclusiva: " + e.Message); }
    }
    public static bool SafeMsi(InstalledApp app) =>
        app.Kind == "Tradicional" && app.WindowsInstaller && !app.SystemComponent &&
        Guid.TryParseExact(app.ProductCode, "B", out _) &&
        new[] { ("Spotify", "Spotify"), ("VLC media player", "VideoLAN"), ("Steam", "Valve"), ("Mozilla Firefox", "Mozilla"), ("Google Chrome", "Google"), ("Telegram Desktop", "Telegram") }
        .Any(x => (app.Name.Equals(x.Item1, StringComparison.OrdinalIgnoreCase) ||
            (x.Item1 == "Mozilla Firefox" && System.Text.RegularExpressions.Regex.IsMatch(app.Name, @"^Mozilla Firefox \([a-zA-Z0-9 -]{1,40}\)$"))) &&
            app.Publisher.Contains(x.Item2, StringComparison.OrdinalIgnoreCase));
    internal static string? TrustedSigner(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Marshal.StringToCoTaskMemUni(path) };
        var ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf<TrustFile>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = ptr, Flags = 0x1000, State = 1 };
        try
        {
            Marshal.StructureToPtr(file, ptr, false);
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return null;
            var provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return null;
            var signerPtr = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPtr == IntPtr.Zero) return null;
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPtr);
            if (signer.Count == 0 || signer.Chain == IntPtr.Zero) return null;
            var cert = Marshal.PtrToStructure<ProviderCertificate>(signer.Chain);
            if (cert.Certificate == IntPtr.Zero) return null;
            var context = Marshal.PtrToStructure<CertificateContext>(cert.Certificate);
            if (context.Encoded == IntPtr.Zero || context.Length is 0 or > 1048576) return null;
            var bytes = new byte[context.Length];
            Marshal.Copy(context.Encoded, bytes, 0, bytes.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(bytes);
            return certificate.Subject;
        }
        finally
        {
            data.State = 2; WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.FreeCoTaskMem(file.Path); Marshal.FreeCoTaskMem(ptr);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct ProviderSigner { public uint Size, TimeLow, TimeHigh, Count; public IntPtr Chain; }
    [StructLayout(LayoutKind.Sequential)] struct ProviderCertificate { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)] struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint Length; public IntPtr Info, Store; }
    [DllImport("wintrust.dll", ExactSpelling = true)] static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)] static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [StructLayout(LayoutKind.Sequential)] struct TrustFile { public uint Size; public IntPtr Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)] struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint State; public IntPtr StateData, Url; public uint Flags, Context;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
