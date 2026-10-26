using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Respiro.Core;
public sealed class WindowsFileSystem : IReadOnlyFileSystem
{
    public DiskEntry Stat(string path) { var a=File.GetAttributes(path); bool d=a.HasFlag(FileAttributes.Directory); var f=new FileInfo(path); return new(path,d,a,d?0:f.Length,d?Directory.GetLastWriteTimeUtc(path):f.LastWriteTimeUtc); }
    public IEnumerable<DiskEntry> Enumerate(string path)
    {
        foreach(var f in new DirectoryInfo(path).EnumerateFileSystemInfos("*",new EnumerationOptions { RecurseSubdirectories=false, IgnoreInaccessible=false, AttributesToSkip=0, ReturnSpecialDirectories=false }))
            yield return new(f.FullName,f.Attributes.HasFlag(FileAttributes.Directory),f.Attributes,f is FileInfo fi?fi.Length:0,f.LastWriteTimeUtc);
    }
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public Allocation Allocated(string path)
    {
        using var h=CreateFile(path,0,7,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
        if(h.IsInvalid || !GetFileInformationByHandle(h,out var i)) return new(null,null);
        string id=$"{i.VolumeSerialNumber:X8}:{i.FileIndexHigh:X8}{i.FileIndexLow:X8}";
        // FILE_STANDARD_INFO.AllocationSize: allocation, not rounded logical size.
        if(!GetFileInformationByHandleEx(h,1,out var s,(uint)Marshal.SizeOf<StandardInfo>())) return new(null,id,i.Links);
        if((i.Attributes & (uint)(FileAttributes.Compressed|FileAttributes.SparseFile))!=0)
        {
            if(!GetCompressionInformation(h,8,out var compression,(uint)Marshal.SizeOf<CompressionInfo>())||compression.CompressedFileSize<0)return new(null,id,i.Links);
            return new(compression.CompressedFileSize,id,i.Links);
        }
        return new(s.AllocationSize,id,i.Links);
    }
    public string ProbeUse(string path)
    {
        try { using var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None); return "Uso não detectado nesta consulta; não garante ausência de dependências."; }
        catch(IOException) { return "Bloqueado: arquivo em uso ou indisponível."; }
        catch(UnauthorizedAccessException) { return "Inconclusivo: acesso negado."; }
    }
    [StructLayout(LayoutKind.Sequential)] struct CompressionInfo { public long CompressedFileSize; public ushort Format; public byte UnitShift,ChunkShift,ClusterShift,Reserved1,Reserved2,Reserved3; }
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="GetFileInformationByHandleEx")] static extern bool GetCompressionInformation(SafeFileHandle h,int type,out CompressionInfo info,uint size);
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write; public uint VolumeSerialNumber,SizeHigh,SizeLow,Links,FileIndexHigh,FileIndexLow; }
    [StructLayout(LayoutKind.Sequential)] struct StandardInfo { public long AllocationSize,EndOfFile; public uint NumberOfLinks; [MarshalAs(UnmanagedType.U1)] public bool DeletePending; [MarshalAs(UnmanagedType.U1)] public bool Directory; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")] static extern SafeFileHandle CreateFile(string p,uint a,uint share,IntPtr sec,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle h,int type,out StandardInfo info,uint size);
}

