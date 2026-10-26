using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Respiro.Core;

// Independently implemented from the npm/cacache documented content and index formats.
internal sealed class NpmCacheProof
{
    internal sealed record Reference(string Index, string Hex, string Integrity, long Size, string Origin, bool NeedsPublicProbe, bool Packument, string PackageName);
    readonly string root; readonly NpmPublicProof? publicProof;
    Dictionary<string, Reference>? entries;
    internal bool Truncated { get; private set; }
    internal NpmCacheProof(string sha512Root,NpmPublicProof? publicProof=null) { root = sha512Root; this.publicProof=publicProof; }
    internal static string Bucket(string basePath, string hex) => Path.Combine(basePath, hex[..2], hex[2..4], hex[4..]);
    internal static bool Hex(string s, int length) => s.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    string IndexRoot => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, "index-v5");
    internal Reference Find(string path, CancellationToken token)
    {
        var relative = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 3 || !Hex(relative[0], 2) || !Hex(relative[1], 2) || !Hex(relative[2], 124)) throw new IOException("Não é endereço SHA-512 canônico do cacache.");
        string digest = string.Concat(relative);
        entries ??= Load(token);
        if (!entries.TryGetValue(digest, out var reference)) throw new IOException("Sem entrada vigente e íntegra no index-v5 para download do registro público npm; conteúdo preservado.");
        return reference;
    }
    Dictionary<string, Reference> Load(CancellationToken token)
    {
        var found = new Dictionary<string, Reference>(StringComparer.Ordinal);
        var fs = new WindowsFileSystem(); var stack = new Stack<IEnumerator<DiskEntry>>(); int visited = 0;
        try
        {
            for(string? p=IndexRoot;p!=null;p=Path.GetDirectoryName(p)) if(File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Link no índice npm.");
            stack.Push(fs.Enumerate(IndexRoot).GetEnumerator());
            while(stack.Count>0)
            {
                token.ThrowIfCancellationRequested(); var it=stack.Peek(); DiskEntry e;
                try { if(!it.MoveNext()){stack.Pop().Dispose();continue;}e=it.Current; } catch(IOException){stack.Pop().Dispose();continue;} catch(UnauthorizedAccessException){stack.Pop().Dispose();continue;}
                if(e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if(e.Directory){stack.Push(fs.Enumerate(e.Path).GetEnumerator());continue;}
                if(++visited>50000){Truncated=true;break;}
                try { using var file=LockedFile.Open(e.Path);file.RequireUnnamedStreamOnly(); var r=Read(file,e.Path); if(r!=null)found.TryAdd(r.Hex,r); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.ComponentModel.Win32Exception) { }
            }
        }
        finally {while(stack.Count>0)stack.Pop().Dispose();}
        return found;
    }
    internal Reference? Read(LockedFile file, string path) { try { return ReadCore(file, path); } catch(Exception ex) when(ex is JsonException or InvalidOperationException or ArgumentException or FormatException or CryptographicException) { throw new IOException("Índice npm inválido ou inconclusivo.", ex); } }
    Reference? ReadCore(LockedFile file, string path)
    {
        if(file.Stamp.Bytes>1024*1024)throw new IOException("Índice npm excede limite de validação.");
        var text=new UTF8Encoding(false,true).GetString(file.ReadAt(0,(int)Math.Min(65536,file.Stamp.Bytes)));
        // Fail closed rather than using a truncated index. Index buckets normally contain tiny records.
        if(file.Stamp.Bytes>65536)throw new IOException("Bucket npm grande; revisão individual necessária.");
        string? last=null;
        foreach(var line in text.Split('\n'))
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            int tab=line.IndexOf('\t');if(tab!=40)throw new IOException("Registro npm incompleto.");
            string json=line[(tab+1)..];
            if(!Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json))).Equals(line[..tab],StringComparison.OrdinalIgnoreCase))throw new IOException("Checksum do índice npm inválido.");
            last=json;
        }
        if(last==null)return null;
        using var doc=JsonDocument.Parse(last);var entry=doc.RootElement;
        if(!entry.TryGetProperty("key",out var keyProperty)||keyProperty.ValueKind!=JsonValueKind.String)return null;
        string key=keyProperty.GetString()!;
        string bucket=Bucket(IndexRoot,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        if(!path.Equals(bucket,StringComparison.Ordinal))throw new IOException("Endereço do índice não corresponde à chave npm.");
        const string prefix="make-fetch-happen:request-cache:";
        if(!key.StartsWith(prefix,StringComparison.Ordinal)||!Uri.TryCreate(key[prefix.Length..],UriKind.Absolute,out var url)||url.Scheme!="https"||url.Host!="registry.npmjs.org"||!url.IsDefaultPort||url.UserInfo.Length!=0||url.Query.Length!=0||url.Fragment.Length!=0)return null;
        // The public registry also hosts private scoped packages. Domain alone is insufficient.
        var segments=Uri.UnescapeDataString(url.AbsolutePath).Split('/');
        bool scoped=segments.Length==5&&segments[1].StartsWith('@');int packageIndex=scoped?2:1;
        bool packument=segments.Length==2;
        string package=segments.Length>packageIndex?segments[packageIndex]:"";
        if(package.Length==0||package.Length>214||!(package[0] is >= 'a' and <= 'z' or >= '0' and <= '9')||!package.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))return null;
        if(!packument&&(segments.Length!=(scoped?5:4)||segments[packageIndex+1]!="-"||!segments[packageIndex+2].StartsWith(package+"-",StringComparison.Ordinal)||!segments[packageIndex+2].EndsWith(".tgz",StringComparison.Ordinal)))return null;
        if(!entry.TryGetProperty("metadata",out var metadata)||!metadata.TryGetProperty("url",out var origin)||origin.GetString()!=url.AbsoluteUri)return null;
        if(!entry.TryGetProperty("integrity",out var integrity)||integrity.ValueKind!=JsonValueKind.String)return null;
        var sri=integrity.GetString()!;if(!sri.StartsWith("sha512-",StringComparison.Ordinal))return null;
        byte[] hash;try{hash=Convert.FromBase64String(sri[7..]);}catch(FormatException){return null;}if(hash.Length!=64)return null;
        if(!entry.TryGetProperty("size",out var size)||!size.TryGetInt64(out long bytes)||bytes<0)return null;
        if(!entry.TryGetProperty("time",out var time)||!time.TryGetInt64(out long timestamp)||timestamp<946684800000||timestamp>DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds())return null;
        return new(path,Convert.ToHexString(hash).ToLowerInvariant(),sri,bytes,url.AbsoluteUri,scoped,packument,package);
    }
    internal void Verify(Reference reference, LockedFile payload, CancellationToken token)
    {
        if(!reference.Packument){var header=payload.ReadAt(0,4);if(header[0]!=0x1f||header[1]!=0x8b||header[2]!=8||(header[3]&0xe0)!=0)throw new IOException("Payload não é tarball gzip reconhecido do npm.");}
        if(reference.Size!=payload.Stamp.Bytes)throw new IOException("Tamanho diverge do registro npm.");
        if(payload.Stamp.Bytes>2L*1024*1024*1024)throw new IOException("Conteúdo npm excede limite de validação.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA512);var buffer=new byte[65536];long offset=0;
        while(offset<payload.Stamp.Bytes){token.ThrowIfCancellationRequested();int n=RandomAccess.Read(payload.Handle,buffer,offset);if(n==0)throw new IOException("Conteúdo npm incompleto.");hash.AppendData(buffer,0,n);offset+=n;}
        if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(reference.Hex,StringComparison.OrdinalIgnoreCase))throw new IOException("SHA-512 do conteúdo não corresponde à origem registrada.");
        if(reference.Packument)VerifyPackument(reference,payload);
        if(reference.NeedsPublicProbe){if(publicProof==null)throw new IOException("Pacote com escopo: exige consulta pública anônima explicitamente autorizada; preservado.");publicProof.Require(reference.Origin,payload,token);}
    }
    static void VerifyPackument(Reference reference,LockedFile file)
    {
        if(file.Stamp.Bytes>8*1024*1024)throw new IOException("Metadados npm maiores que 8 MiB exigem revisão individual.");
        try
        {
            using var stream=new SnapshotStream(file);using var document=JsonDocument.Parse(stream,new JsonDocumentOptions{MaxDepth=64});var root=document.RootElement;
            var allowed=new HashSet<string>(StringComparer.Ordinal){"_id","_rev","name","dist-tags","versions","time","maintainers","description","keywords","author","repository","bugs","homepage","license","readme","readmeFilename","users","contributors","_attachments","deprecated","modified","access"};
            if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Any(p=>!allowed.Contains(p.Name))||root.GetProperty("name").GetString()!=reference.PackageName)throw new IOException("Conteúdo não corresponde aos metadados públicos npm esperados; configurações e credenciais são preservadas.");
            if(root.TryGetProperty("_id",out var id)&&id.GetString()!=reference.PackageName)throw new IOException("Identidade do packument diverge da origem.");
            var versions=root.GetProperty("versions");var latest=root.GetProperty("dist-tags").GetProperty("latest").GetString();
            if(versions.ValueKind!=JsonValueKind.Object||latest==null||!versions.TryGetProperty(latest,out _))throw new IOException("Histórico de versões npm inconclusivo.");
            foreach(var version in versions.EnumerateObject())
            {
                if(version.Value.ValueKind!=JsonValueKind.Object||version.Value.GetProperty("name").GetString()!=reference.PackageName||version.Value.GetProperty("version").GetString()!=version.Name)throw new IOException("Estrutura de versões npm diverge da origem; preservado.");
            }
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException){throw new IOException("Formato de metadados npm inconclusivo; não é configuração descartável.");}
    }
    sealed class SnapshotStream(LockedFile file):Stream
    {
        long position;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>file.Stamp.Bytes;public override long Position{get=>position;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){int n=RandomAccess.Read(file.Handle,buffer.AsSpan(offset,count),position);position+=n;return n;}
        public override int Read(Span<byte> buffer){int n=RandomAccess.Read(file.Handle,buffer,position);position+=n;return n;}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long length)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }}