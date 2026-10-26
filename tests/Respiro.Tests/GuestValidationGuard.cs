using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
namespace Respiro.Tests;

internal static class GuestValidationGuard
{
    internal static bool IsActive { get; private set; }
    internal static string FixtureExecutable { get; private set; } = "";
    internal static string MachineFingerprint()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        string id = key?.GetValue("MachineGuid") as string ?? throw new IOException("Identidade da máquina indisponível.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }
    internal static void ValidateInputs(string sourceHost, string current, bool acknowledged, string model)
    {
        if (!acknowledged || sourceHost.Length != 64 || current.Length != 64 ||
            sourceHost.Equals(current, StringComparison.OrdinalIgnoreCase))
            throw new IOException("GUEST BLOCKED: falta autorização explícita ou este é o host de preparação.");
        if (!new[] { "Virtual Machine", "VirtualBox", "VMware", "KVM", "QEMU", "HVM", "Parallels" }
            .Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("GUEST BLOCKED: máquina virtual descartável não identificada.");
    }
    internal static void Require(string manifestPath, bool acknowledged)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { MaxDepth = 8 });
        var data = manifest.RootElement;
        if (data.GetProperty("SchemaVersion").GetInt32() != 1) throw new IOException("GUEST BLOCKED: manifesto inválido.");
        string source = data.GetProperty("ForbiddenHostMachineHash").GetString() ?? "";
        string current = MachineFingerprint();
        using var hostResource = typeof(GuestValidationGuard).Assembly.GetManifestResourceStream("Respiro.TestPreparationHost")
            ?? throw new IOException("GUEST BLOCKED: atestação compilada ausente.");
        using var reader = new StreamReader(hostResource);
        string compiledHost = reader.ReadToEnd().Trim();
        if (source != compiledHost || current == compiledHost) throw new IOException("GUEST BLOCKED: host de preparação ou manifesto divergente da atestação compilada.");
        // Reject this host before querying the guest or starting any fixture.
        if (!acknowledged || source.Length != 64 || source.Equals(current, StringComparison.OrdinalIgnoreCase))
            throw new IOException("GUEST BLOCKED: este computador não é um convidado autorizado.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; (Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 3).Model | ConvertTo-Json -Compress");
        using var query = Process.Start(start) ?? throw new IOException("Consulta de convidado não iniciou.");
        var output = query.StandardOutput.ReadToEndAsync();
        var errors = query.StandardError.ReadToEndAsync();
        if (!query.WaitForExit(10000) || query.ExitCode != 0) throw new IOException("GUEST BLOCKED: modelo virtual inconclusivo.");
        string model = JsonSerializer.Deserialize<string>(output.GetAwaiter().GetResult().Trim()) ?? "";
        _ = errors.GetAwaiter().GetResult();
        ValidateInputs(source, current, acknowledged, model);
        string kit = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string fixture = Path.GetFullPath(Path.Combine(kit, data.GetProperty("FixtureRelativePath").GetString() ?? ""));
        if (!fixture.StartsWith(kit + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fixture) != "Respiro.OperationalHost.exe") throw new IOException("GUEST BLOCKED: fixture fora do kit.");
        using var file = File.OpenRead(fixture);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        if (hash != data.GetProperty("FixtureSHA256").GetString()) throw new IOException("GUEST BLOCKED: fixture alterada.");
        if (!data.TryGetProperty("FixtureFiles", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
            throw new IOException("GUEST BLOCKED: manifesto de dependências ausente.");
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies.EnumerateArray())
        {
            string relative = dependency.GetProperty("Path").GetString() ?? "";
            string path = Path.GetFullPath(Path.Combine(kit, relative));
            if (!path.StartsWith(Path.Combine(kit, "fixture") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !verified.Add(Path.GetFileName(path))) throw new IOException("GUEST BLOCKED: dependência fora da fixture ou duplicada.");
            using var input = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(input)) != dependency.GetProperty("SHA256").GetString())
                throw new IOException("GUEST BLOCKED: dependência da fixture alterada.");
        }
        foreach (var required in new[] { "Respiro.OperationalHost.exe", "Respiro.OperationalHost.dll",
            "Respiro.OperationalHost.deps.json", "Respiro.OperationalHost.runtimeconfig.json" })
            if (!verified.Contains(required)) throw new IOException("GUEST BLOCKED: dependência obrigatória ausente.");
        FixtureExecutable = fixture; IsActive = true;
    }
    internal static void AssertActive()
    {
        if (!IsActive) throw new IOException("GUEST BLOCKED: operações nativas de fixture exigem convidado autorizado.");
    }
}
