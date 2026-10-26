using System.Diagnostics;
using System.Text;
namespace Respiro.Core;
internal record QueryOutput(bool Success, string Text, string Error);
internal static class OfficialWindowsQueries
{
    static int running;
    internal static async Task<QueryOutput> Collect(CancellationToken token)
    {
        // Fixed system executable and a fixed read-only script; no policy override, profiles or user scripts.
        const string script = """
$ErrorActionPreference='Stop'
Import-Module "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules\CimCmdlets\CimCmdlets.psd1" -ErrorAction Stop
[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
$r=[ordered]@{Store=@();Features=@();Pagefiles=@();ShadowStorage=@();RestorePoints=@();Delivery=$null;Reserved=$null;ComponentStore=$null;Power=@();Startup=@();Errors=@()}
try { Import-Module "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules\Appx\Appx.psd1" -ErrorAction Stop; $r.Store=@(Get-AppxPackage -ErrorAction Stop | Select-Object Name,Publisher,@{n='Version';e={$_.Version.ToString()}},InstallLocation,IsFramework,NonRemovable) } catch { $r.Errors+=('Store: '+$_.Exception.Message) }
try { $r.Features=@(Get-CimInstance Win32_OptionalFeature -OperationTimeoutSec 8 -ErrorAction Stop | Select-Object Name,Caption,InstallState) } catch { $r.Errors+=('Recursos opcionais: '+$_.Exception.Message) }
try { $r.Startup=@(Get-CimInstance Win32_StartupCommand -OperationTimeoutSec 8 -ErrorAction Stop | Select-Object Name,Location) } catch { $r.Errors+=('Inicializacao: '+$_.Exception.Message) }
try { $r.Pagefiles=@(Get-CimInstance Win32_PageFileUsage -OperationTimeoutSec 8 -ErrorAction Stop | Select-Object Name,@{n='Bytes';e={[long]$_.AllocatedBaseSize*1MB}},CurrentUsage,PeakUsage) } catch { $r.Errors+=('Paginacao: '+$_.Exception.Message) }
try { $r.ShadowStorage=@(Get-CimInstance Win32_ShadowStorage -OperationTimeoutSec 8 -ErrorAction Stop | Select-Object Volume,UsedSpace,AllocatedSpace,MaxSpace) } catch { $r.Errors+=('Copias de sombra: '+$_.Exception.Message) }
try { $r.RestorePoints=@(Get-CimInstance -Namespace root/default -ClassName SystemRestore -OperationTimeoutSec 8 -ErrorAction Stop | Select-Object SequenceNumber,Description,CreationTime) } catch { $r.Errors+=('Restauracao: '+$_.Exception.Message) }
try { Import-Module "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules\DeliveryOptimization\DeliveryOptimization.psd1" -ErrorAction Stop; $r.Delivery=Get-DeliveryOptimizationPerfSnap -ErrorAction Stop | Select-Object CacheSizeBytes,TotalDiskBytes,AvailableDiskBytes } catch { $r.Errors+=('Otimizacao de Entrega: '+$_.Exception.Message) }
try { Import-Module "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules\Dism\Dism.psd1" -ErrorAction Stop; $r.Reserved=Get-WindowsReservedStorageState -ErrorAction Stop | Select-Object ReservedStorageState } catch { $r.Errors+=('Armazenamento reservado: '+$_.Exception.Message) }
try { $r.Power=@(& "$env:SystemRoot\System32\powercfg.exe" /a 2>&1 | ForEach-Object { "$_" }); if($LASTEXITCODE -ne 0){$r.Errors+='powercfg /a retornou falha.'} } catch { $r.Errors+=('Energia: '+$_.Exception.Message) }
try {
  $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if($admin){$lines=@(& "$env:SystemRoot\System32\dism.exe" /Online /Cleanup-Image /AnalyzeComponentStore /English 2>&1 | ForEach-Object {"$_"}); $r.ComponentStore=@{ExitCode=$LASTEXITCODE;Report=($lines -join "`n")}}
  else{$r.ComponentStore=@{ExitCode=740;Report='Analise oficial requer token administrativo. Nenhuma elevacao foi solicitada.'}}
} catch {$r.Errors+=('Componentes: '+$_.Exception.Message)}
$r | ConvertTo-Json -Depth 5 -Compress
""";
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return new(false, "", "Uma consulta oficial anterior ainda está terminando.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(a);
        Process? p = null;
        try
        {
            p = Process.Start(start)!;
            var stdout = ReadBounded(p.StandardOutput, 4 * 1024 * 1024); var stderr = ReadBounded(p.StandardError, 65536);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                var background = p; p = null;
                _ = ReleaseAfterExit(background, stdout, stderr);
                if (token.IsCancellationRequested) throw;
                return new(false, "", "Consulta excedeu 90 segundos. Nenhum processo foi encerrado; resultado inconclusivo.");
            }
            var output = await stdout; var errors = await stderr;
            return new(p.ExitCode == 0 && !output.Truncated, output.Text, output.Truncated ? "Resposta excedeu o limite de 4 MiB." : errors.Text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(false, "", e.Message); }
        finally { if (p != null) { p.Dispose(); Interlocked.Exchange(ref running, 0); } }
    }
    static async Task ReleaseAfterExit(Process p, Task<(string Text, bool Truncated)> output, Task<(string Text, bool Truncated)> errors)
    { try { await p.WaitForExitAsync(); await output; await errors; } catch { } finally { p.Dispose(); Interlocked.Exchange(ref running, 0); } }
    static async Task<(string Text, bool Truncated)> ReadBounded(StreamReader reader, int limit)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; bool truncated = false; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0) { int keep = Math.Min(count, limit - text.Length); if (keep > 0) text.Append(buffer, 0, keep); if (keep < count) truncated = true; }
        return (text.ToString(), truncated);
    }
}


