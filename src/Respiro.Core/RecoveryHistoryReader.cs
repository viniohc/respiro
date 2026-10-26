using System.Text;
using System.Text.Json;
namespace Respiro.Core;

internal sealed record RecoverySample(DateTime At, string Path, string ScopeHash, long PhysicalBytes, bool Complete);
internal static class RecoveryHistoryReader
{
    internal const int Limit = 4 * 1024 * 1024;
    internal static string Read(LockedFile file)
    {
        if (file.Stamp.Bytes > Limit) throw new IOException("Histórico excede 4 MiB; arquive manualmente, sem apagar registros de limpeza.");
        using var memory = new MemoryStream();
        for (long at = 0; at < file.Stamp.Bytes;)
        {
            int count = (int)Math.Min(65536, file.Stamp.Bytes - at);
            memory.Write(file.ReadAt(at, count)); at += count;
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
    internal static List<RecoverySample> Parse(string jsonl)
    {
        if (Encoding.UTF8.GetByteCount(jsonl) > Limit) throw new IOException("Histórico excede 4 MiB.");
        var result = new List<RecoverySample>();
        foreach (string line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var s = JsonSerializer.Deserialize<RecoverySample>(line);
                if (s == null || string.IsNullOrWhiteSpace(s.Path) || string.IsNullOrWhiteSpace(s.ScopeHash) ||
                    s.At == default || s.PhysicalBytes < 0) continue;
                result.Add(s with { At = s.At.ToUniversalTime() });
            }
            catch (JsonException) { /* Preserve malformed lines on disk; never infer a zero sample. */ }
        }
        return result;
    }
    internal static RecoverySample? Previous(IEnumerable<RecoverySample> samples, RecoveryReport report,
        RecoveryGroup group, DateTime beforeUtc, DateTime? earliestUtc = null)
    {
        string hash = RecoveryHistory.GroupHash(report, group);
        return samples.Where(s => s.Complete && s.Path.Equals(group.Path, StringComparison.OrdinalIgnoreCase) &&
                s.ScopeHash == hash && s.At < beforeUtc && (earliestUtc == null || s.At >= earliestUtc.Value))
            .OrderByDescending(s => s.At).FirstOrDefault();
    }
}
