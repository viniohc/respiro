using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
namespace Respiro.Core;
internal static class WindowsArtifactFormats
{
    static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    internal static void Minidump(LockedFile file, DateTime now, CancellationToken token)
    {
        var h = file.ReadAt(0, 32);
        if (!h.AsSpan(0, 4).SequenceEqual("MDMP"u8) || (U32(h, 4) & 0xffff) != 0xa793) throw new IOException("Não é minidump Windows reconhecido; conteúdo preservado.");
        uint count = U32(h, 8), offset = U32(h, 12);
        if (count == 0 || count > 4096 || offset < 32 || offset + (long)count * 12 > file.Stamp.Bytes) throw new IOException("Diretório MINIDUMP inválido ou incompleto.");
        var created = DateTimeOffset.FromUnixTimeSeconds(U32(h, 20)).UtcDateTime;
        if (created < new DateTime(2000, 1, 1) || now - created < TimeSpan.FromDays(30) || now - DateTime.FromFileTimeUtc(file.Stamp.WriteTime) < TimeSpan.FromDays(30)) throw new IOException("Dump recente ou data interna inconclusiva; mínimo de 30 dias.");
        bool systemInfo = false;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); var entry = file.ReadAt(offset + i * 12L, 12);
            uint type = U32(entry, 0), size = U32(entry, 4), rva = U32(entry, 8);
            if (rva + (long)size > file.Stamp.Bytes) throw new IOException("Stream de dump ultrapassa o arquivo.");
            if (type == 7 && size >= 56 && rva >= 32) systemInfo = true;
        }
        if (!systemInfo) throw new IOException("Dump sem SystemInfoStream válido; origem inconclusiva.");
    }
    internal static void Thumbnail(LockedFile file, string path, CancellationToken token)
    {
        string[] suffixes = ["16", "32", "48", "96", "256", "768", "1280", "1920", "2560", "sr", "wide", "exif", "wide_alternate", "custom_stream"];
        int kind = Array.FindIndex(suffixes, s => Path.GetFileName(path) == $"thumbcache_{s}.db");
        var h = file.ReadAt(0, 24);
        if (kind < 0 || !h.AsSpan(0, 4).SequenceEqual("CMMM"u8) || U32(h, 4) != 32 || U32(h, 8) != kind) throw new IOException("Não é cache CMMM v32 compatível com o nome e tipo; bancos pessoais e índices são preservados.");
        // Windows 10/11 v32 uses a reserved DWORD followed by first/free offsets (16/20).
        if (U32(h, 12) != 0) throw new IOException("Variante de cabeçalho CMMM não validada.");
        long position = U32(h, 16), end = U32(h, 20);

        if (position < 24 || end < position || end > file.Stamp.Bytes) throw new IOException("Limites do cache inconclusivos.");
        int entries = 0;
        while (position < end)
        {
            token.ThrowIfCancellationRequested(); if (++entries > 100000) throw new IOException("Limite de validação de entradas do cache atingido.");
            var e = file.ReadAt(position, 56); long size = U32(e, 4), identifier = U32(e, 16), padding = U32(e, 20), data = U32(e, 24);
            if (!e.AsSpan(0, 4).SequenceEqual("CMMM"u8) || size < 56 || position + size > end || identifier % 2 != 0 || 56 + identifier + padding + data > size) throw new IOException("Entrada CMMM inválida; cache preservado.");
            position += size;
        }
        if (position != end) throw new IOException("Estrutura de miniaturas incompleta.");
    }
    internal record Recycled(string Original, long Bytes, DateTime Deleted);
    internal static Recycled RecycleMetadata(LockedFile metadata, DateTime now)
    {
        if (metadata.Stamp.Bytes is < 30 or > 32768) throw new IOException("Metadados da Lixeira fora dos limites.");
        var b = metadata.ReadAt(0, (int)metadata.Stamp.Bytes);
        if (BinaryPrimitives.ReadInt64LittleEndian(b) != 2) throw new IOException("Versão de metadados da Lixeira não validada.");
        long bytes = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8)), date = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)); uint chars = U32(b, 24);
        if (bytes < 0 || chars < 2 || 28L + chars * 2 != b.Length || b[^1] != 0 || b[^2] != 0) throw new IOException("Par $I/$R inconclusivo.");
        string original = new UnicodeEncoding(false, false, true).GetString(b, 28, (int)chars * 2 - 2);
        if (original.Contains('\0')) throw new IOException("Origem contém terminador inesperado.");
        DateTime deleted; try { deleted = DateTime.FromFileTimeUtc(date); } catch (ArgumentOutOfRangeException) { throw new IOException("Data de exclusão inválida."); }
        if (deleted < new DateTime(2000, 1, 1) || deleted > now.AddMinutes(1)) throw new IOException("Data de exclusão inconclusiva.");
        return new(LockedFile.Canonical(original), bytes, deleted);
    }
    internal static void RejectProtectedContent(LockedFile file)
    {
        var b = file.ReadAt(0, (int)Math.Min(32, file.Stamp.Bytes));
        if (b.AsSpan().StartsWith("SQLite format 3"u8) || b.AsSpan().StartsWith("-----BEGIN"u8) || b.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 }) || b.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 3, 4 })) throw new IOException("Conteúdo de banco, credencial, documento ou arquivo pessoal reconhecido: preservado.");
        var text = Encoding.UTF8.GetString(b).TrimStart('\ufeff', ' ', '\r', '\n', '\t');
        if (text.StartsWith('{') || text.StartsWith('[') || text.StartsWith("<?xml") || text.StartsWith("import ") || text.StartsWith("using ") || text.StartsWith("#!/")) throw new IOException("Possível código ou configuração; preservado.");
    }
}

