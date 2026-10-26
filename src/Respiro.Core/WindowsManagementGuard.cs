using System.Runtime.InteropServices;
namespace Respiro.Core;
internal static class WindowsManagementGuard
{
    internal static bool AllowsChanges(out string reason)
    {
        IntPtr domain = IntPtr.Zero, aad = IntPtr.Zero;
        try
        {
            uint code = NetGetJoinInformation(null, out domain, out int status);
            int hr = NetGetAadJoinInformation(null, out aad);
            bool allowed = AllowsChanges(code, status, hr, aad != IntPtr.Zero);
            reason = allowed ? "Dispositivo local sem vínculo corporativo reportado." :
                "Vínculo de domínio/conta corporativa ou consulta inconclusiva: alterações diretas bloqueadas.";
            return allowed;
        }
        catch (Exception e) { reason = "Gerenciamento do dispositivo inconclusivo: " + e.Message; return false; }
        finally { if (domain != IntPtr.Zero) NetApiBufferFree(domain); if (aad != IntPtr.Zero) NetFreeAadJoinInformation(aad); }
    }
    internal static bool AllowsChanges(uint domainCode, int status, int aadCode, bool aad) =>
        domainCode == 0 && status is 1 or 2 && aadCode >= 0 && !aad; // SUCCEEDED(HRESULT); S_FALSE is success with no join information.
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern uint NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetGetAadJoinInformation(string? tenant, out IntPtr info);
    [DllImport("netapi32.dll")] static extern uint NetApiBufferFree(IntPtr buffer);
    [DllImport("netapi32.dll")] static extern void NetFreeAadJoinInformation(IntPtr info);
}
