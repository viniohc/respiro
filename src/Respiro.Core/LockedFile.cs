using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
namespace Respiro.Core;

internal sealed class LockedFile : IDisposable
{
    readonly List<SafeFileHandle> parents = [];
    internal SafeFileHandle Handle { get; private set; } = null!;
    internal List<string> Ancestors { get; } = [];
    internal FileStamp Stamp { get; private set; } = null!;
    internal static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\") || path.Length < 4 || path[1] != ':' || path[2] != '\\')
            throw new IOException("Somente caminhos locais absolutos são aceitos.");
        if (path[2..].Contains(':') || path.Split('\\').Skip(1).Any(p => p.EndsWith(' ') || p.EndsWith('.') || p is "." or ".."))
            throw new IOException("Caminho ambíguo, fluxo alternativo ou segmento inválido.");
        var full = Path.GetFullPath(path);
        if (!full.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Caminho não canônico.");
        return full;
    }
    internal static LockedFile Open(string path, bool deletion = false, bool write = false, bool create = false)
    {
        path = Canonical(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") throw new IOException("Remoção limitada a volume NTFS local fixo.");
        var file = new LockedFile();
        try
        {
            var chain = new Stack<string>();
            for (var p = Path.GetDirectoryName(path); p != null; p = Path.GetDirectoryName(p)) chain.Push(p);
            while (chain.TryPop(out var parent))
            {
                // No FILE_SHARE_DELETE: pin each ancestor before resolving the next component.
                var h = OpenNative(parent, 0x80, 3, 0x02200000);
                file.parents.Add(h);
                var info = Information(h);
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("Ancestral é link/junction ou não é diretório.");
                VerifyFinal(h, parent);
                file.Ancestors.Add(Identity(info));
                foreach (var marker in new[] { ".git", ".limpapc-protect", ".no-clean", "E2.5", "E2-5", "AGENTS.md" })
                    if (File.Exists(Path.Combine(parent, marker)) || Directory.Exists(Path.Combine(parent, marker)))
                        throw new IOException("Marcador de projeto ou proteção encontrado: " + marker);
            }
            file.Handle = OpenNative(path, 0x80000000u | (deletion ? 0x10000u : 0) | (write ? 0x40000000u : 0), 0, 0x00200000, create ? 1u : 3u);
            var target = Information(file.Handle);
            if ((target.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Offline | FileAttributes.Encrypted)) != 0)
                throw new IOException("Arquivo protegido, diretório, link ou estado incompatível.");
            if (target.Links != 1) throw new IOException("Hard links: conteúdo compartilhado bloqueado.");
            VerifyFinal(file.Handle, path);
            file.Stamp = new(Identity(target), ((long)target.SizeHigh << 32) | target.SizeLow, ToLong(target.Write), ToLong(target.Creation));
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    internal string Hash(CancellationToken token = default, long maxBytes = 64L * 1024 * 1024)
    {
        if (Stamp.Bytes > maxBytes) throw new IOException($"Arquivo excede o limite de {Sizes.Format(maxBytes)} deste adaptador.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long offset = 0;
        while (true) { token.ThrowIfCancellationRequested(); int n = RandomAccess.Read(Handle, buffer, offset); if (n == 0) break; hash.AppendData(buffer, 0, n); offset += n; }
        if (offset != Stamp.Bytes) throw new IOException("Tamanho mudou durante a leitura.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal void RequireUnchanged()
    {
        var info = Information(Handle);
        var current = new FileStamp(Identity(info), ((long)info.SizeHigh << 32) | info.SizeLow, ToLong(info.Write), ToLong(info.Creation));
        if (current != Stamp || info.Links != 1 || (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Offline | FileAttributes.Encrypted)) != 0) throw new IOException("Arquivo mudou ou ficou protegido durante a comparação.");
        RequireUnnamedStreamOnly();
    }
    internal byte[] ReadAt(long offset, int length)
    {
        if (offset < 0 || length < 0 || length > 65536 || offset > Stamp.Bytes - length) throw new IOException("Estrutura do arquivo fora dos limites.");
        var bytes = new byte[length]; int read = RandomAccess.Read(Handle, bytes, offset);
        if (read != length) throw new IOException("Leitura incompleta; estado inconclusivo.");
        return bytes;
    }
    internal long AllocationSize()
    {
        if (!GetFileInformationByHandleEx(Handle, 1, out StandardInfo info, (uint)Marshal.SizeOf<StandardInfo>()) || info.Allocation < 0 || info.DeletePending)
            throw NativeError("Alocação ou estado indisponível");
        if((Information(Handle).Attributes & (uint)(FileAttributes.Compressed|FileAttributes.SparseFile))!=0)
        {
            if(!GetCompressionInformation(Handle,8,out var compressed,(uint)Marshal.SizeOf<CompressionInfo>())||compressed.CompressedFileSize<0)throw NativeError("Ocupação física comprimida/esparsa inconclusiva");
            return compressed.CompressedFileSize;
        }
        return info.Allocation;
    }
    [StructLayout(LayoutKind.Sequential)] struct CompressionInfo { public long CompressedFileSize; public ushort Format; public byte UnitShift,ChunkShift,ClusterShift,Reserved1,Reserved2,Reserved3; }
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="GetFileInformationByHandleEx")] static extern bool GetCompressionInformation(SafeFileHandle h,int type,out CompressionInfo info,uint size);
    internal void RequireUnnamedStreamOnly()
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (!GetStreamInformation(Handle, 7, buffer, 65536)) throw NativeError("Fluxos NTFS inconclusivos");
            int offset = 0, entries = 0;
            while (true)
            {
                if (offset < 0 || offset > 65536 - 24 || ++entries > 128) throw new IOException("Tabela de fluxos inválida.");
                int next = Marshal.ReadInt32(buffer, offset), length = Marshal.ReadInt32(buffer, offset + 4);
                if (length < 0 || length % 2 != 0 || offset + 24L + length > 65536) throw new IOException("Fluxo NTFS inválido.");
                string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 24), length / 2) ?? "";
                if (name != "::$DATA") throw new IOException("Fluxo NTFS adicional: conteúdo protegido.");
                if (next == 0) break;
                if (next < 24) throw new IOException("Tabela de fluxos inválida.");
                offset = checked(offset + next);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] struct StandardInfo { public long Allocation, End; public uint Links; [MarshalAs(UnmanagedType.U1)] public bool DeletePending; [MarshalAs(UnmanagedType.U1)] public bool Directory; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int type, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")] static extern bool GetStreamInformation(SafeFileHandle h, int type, IntPtr info, uint size);
    internal string ReadSmall()
    {
        if (Stamp.Bytes > 16384) throw new IOException("Comprovante excede 16 KiB.");
        var bytes = new byte[(int)Stamp.Bytes]; int n = RandomAccess.Read(Handle, bytes, 0);
        if (n != bytes.Length) throw new IOException("Leitura inconclusiva.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
    internal void MarkForDeletion()
    {
        var disposition = new Disposition { Delete = true };
        if (!SetFileInformationByHandle(Handle, 4, ref disposition, 1)) throw NativeError("Windows recusou a remoção");
    }
    static SafeFileHandle OpenNative(string path, uint access, uint share, uint flags, uint creation = 3)
    {
        var h = CreateFile(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        int error = Marshal.GetLastWin32Error(); h.Dispose();
        throw new IOException($"Acesso/uso inconclusivo (Windows {error}): {new Win32Exception(error).Message}");
    }
    static Info Information(SafeFileHandle h) => GetFileInformationByHandle(h, out var info) ? info : throw NativeError("Metadados indisponíveis");
    static string Identity(Info i) => $"{i.VolumeSerialNumber:X8}:{i.FileIndexHigh:X8}{i.FileIndexLow:X8}";
    static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME t) => ((long)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;
    static IOException NativeError(string prefix) => new(prefix + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    static void VerifyFinal(SafeFileHandle h, string expected)
    {
        var b = new System.Text.StringBuilder(32768); var n = GetFinalPathNameByHandle(h, b, (uint)b.Capacity, 0);
        if (n == 0 || n >= b.Capacity) throw NativeError("Caminho final inconclusivo");
        var final = b.ToString(); if (final.StartsWith("\\\\?\\")) final = final[4..];
        if (!final.TrimEnd('\\').Equals(expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("O caminho final foi redirecionado.");
    }
    public void Dispose() { Handle?.Dispose(); for (int i = parents.Count - 1; i >= 0; i--) parents[i].Dispose(); parents.Clear(); }
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow; }
    [StructLayout(LayoutKind.Sequential)] struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")] static extern uint GetFinalPathNameByHandle(SafeFileHandle h, System.Text.StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle h, int type, ref Disposition info, uint size);
}
internal record FileStamp(string Identity, long Bytes, long WriteTime, long CreationTime);


