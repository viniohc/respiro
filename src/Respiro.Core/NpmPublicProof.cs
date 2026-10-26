using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
namespace Respiro.Core;
internal sealed class NpmPublicProof
{
    static readonly HttpClient ProductionClient=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=true,DefaultProxyCredentials=null,Credentials=null,MaxConnectionsPerServer=2}){Timeout=TimeSpan.FromSeconds(8)};
    readonly HttpClient client;readonly TimeProvider clock;
    sealed record Receipt(DateTimeOffset At,long Bytes,string ETag);
    readonly Dictionary<string,Receipt> receipts=new(StringComparer.Ordinal);
    int requests,networkFailures;bool unavailable;
    internal int RequestCount=>requests;
    internal NpmPublicProof():this(ProductionClient,TimeProvider.System){}
    internal NpmPublicProof(HttpClient client,TimeProvider? clock=null){this.client=client;this.clock=clock??TimeProvider.System;}
    internal void BeginRealExecution(){receipts.Clear();requests=0;}
    internal void Require(string origin,LockedFile file,CancellationToken token)
    {
        var uri=new Uri(origin);
        if(uri.Scheme!="https"||uri.Host!="registry.npmjs.org"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new IOException("Origem não autorizada para prova pública.");
        if(!receipts.TryGetValue(origin,out var receipt)||clock.GetUtcNow()-receipt.At>TimeSpan.FromMinutes(5))
        {
            if(unavailable||networkFailures>=3||requests>=500)throw new IOException("Consulta pública indisponível ou limite de 500 origens atingido; preservado.");
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Head,uri);request.Headers.UserAgent.ParseAdd("Respiro/0.6");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));requests++;
                using var response=client.Send(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(response.StatusCode==(HttpStatusCode)429||(int)response.StatusCode>=500)unavailable=true;
                if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("HEAD anônimo retornou HTTP "+(int)response.StatusCode+"; disponibilidade pública não confirmada, sem credenciais nem redirecionamento.");
                var etag=response.Headers.ETag;
                string? tag=etag?.Tag.Trim('"');
                if(etag==null||etag.IsWeak||tag==null||!(NpmCacheProof.Hex(tag.ToLowerInvariant(),32)||NpmCacheProof.Hex(tag.ToLowerInvariant(),40))||(response.Content.Headers.TryGetValues("Content-Length",out var lengths)&&(!long.TryParse(lengths.FirstOrDefault(),out long length)||length!=file.Stamp.Bytes)))throw new IOException("Cabeçalhos públicos não comprovam tamanho e checksum do tarball local; preservado.");
                receipt=new(clock.GetUtcNow(),file.Stamp.Bytes,tag.ToLowerInvariant());receipts[origin]=receipt;
            }
            catch(OperationCanceledException)when(!token.IsCancellationRequested){networkFailures++;throw new IOException("Consulta pública excedeu oito segundos; preservado.");}
            catch(HttpRequestException){networkFailures++;throw new IOException("Consulta HTTPS pública inconclusiva; preservado.");}
        }
        if(receipt.Bytes!=file.Stamp.Bytes)throw new IOException("Tamanho diverge da prova pública recente.");
        // ETag is an extra origin check, never a substitute for the mandatory SHA-512 content proof.
        try {
        using var hash=IncrementalHash.CreateHash(receipt.ETag.Length==40?HashAlgorithmName.SHA1:HashAlgorithmName.MD5);var bytes=new byte[65536];long offset=0;
        while(offset<file.Stamp.Bytes){token.ThrowIfCancellationRequested();int n=RandomAccess.Read(file.Handle,bytes,offset);if(n==0)throw new IOException("Leitura incompleta da prova pública.");hash.AppendData(bytes,0,n);offset+=n;}
        if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(receipt.ETag,StringComparison.OrdinalIgnoreCase))throw new IOException("Checksum público diverge do conteúdo local; preservado.");
        } catch(CryptographicException) { throw new IOException("Checksum adicional não permitido pela política criptográfica existente; preservado sem contornar a política."); }
    }
}