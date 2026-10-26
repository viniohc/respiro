using System.IO.Pipes;
using System.Net;
using System.Text.Json;
namespace Respiro.Core;
public sealed record DockerStorageDiagnostic(bool Available,long? GuestUnusedBuildCacheBytes,int KnownUnusedRecords,int UnknownRecords,string State,string Procedure)
{
    public long HostConfirmedBytes=>0;
}
internal static class DockerStorageProbe
{
    internal static async Task<DockerStorageDiagnostic> Collect(CancellationToken token)
    {
        const string procedure="Somente diagnóstico. Antes de qualquer prune, revisar o cache com Docker e obter autorização específica. Não remover volumes/imagens de dados. Espaço liberado dentro do Linux não garante redução física do VHDX no C:; TRIM/compactação exigem análise e eventual parada explicitamente autorizada.";
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,Credentials=null};
            // No TCP/DNS or configurable contexts: every connection is to this local Windows ACL-protected pipe.
            handler.ConnectCallback=async (_,ct)=>{var pipe=new NamedPipeClientStream(".","docker_engine",PipeDirection.InOut,PipeOptions.Asynchronous);try{await pipe.ConnectAsync(ct);return pipe;}catch{pipe.Dispose();throw;}};
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5),MaxResponseContentBufferSize=4*1024*1024};
            using var request=new HttpRequestMessage(HttpMethod.Get,"http://localhost/system/df");using var response=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead,timeout.Token);
            if(response.StatusCode!=HttpStatusCode.OK)return new(false,null,0,0,"Daemon local não autorizou consulta HTTP "+(int)response.StatusCode+". Nenhuma mudança executada.",procedure);
            using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token),new JsonDocumentOptions{MaxDepth=64});return Parse(document.RootElement);
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return new(false,null,0,0,"Daemon Docker local indisponível ou consulta excedeu cinco segundos; nenhuma inicialização/elevação foi tentada.",procedure);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException){return new(false,null,0,0,"Consulta Docker local inconclusiva; acesso e serviço existentes foram preservados.",procedure);}
    }
    internal static DockerStorageDiagnostic Parse(JsonElement root)
    {
        const string procedure="Dados e volumes Docker continuam protegidos. Revisar cache de build no Docker e autorizar separadamente eventual prune. O valor abaixo é interno ao Linux; recuperar bytes no C: pode exigir TRIM/compactação e parada autorizada. Nenhuma operação foi executada.";
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("BuildCache",out var array)||array.ValueKind!=JsonValueKind.Array)return new(true,null,0,1,"API não forneceu inventário de cache de build verificável.",procedure);
        var ids=new HashSet<string>(StringComparer.Ordinal);long bytes=0;int count=0,unknown=0;
        foreach(var item in array.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("ID",out var id)||id.ValueKind!=JsonValueKind.String||string.IsNullOrEmpty(id.GetString())||!item.TryGetProperty("InUse",out var used)||used.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||!item.TryGetProperty("Shared",out var shared)||shared.ValueKind is not (JsonValueKind.True or JsonValueKind.False)||!item.TryGetProperty("Size",out var size)||size.ValueKind!=JsonValueKind.Number||!size.TryGetInt64(out long n)||n<0){unknown++;continue;}
            if(used.GetBoolean()||shared.GetBoolean()||!ids.Add(id.GetString()!))continue;
            if(n>long.MaxValue-bytes){unknown++;continue;}bytes+=n;count++;
        }
        return new(true,bytes,count,unknown,"Somente cache não compartilhado e fora de uso declarado pelo daemon; não é estimativa confirmada de recuperação física no Windows.",procedure);
    }
}