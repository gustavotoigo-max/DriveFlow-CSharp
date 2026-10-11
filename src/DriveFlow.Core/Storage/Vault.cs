using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DriveFlow.Core.Storage;

/// <summary>Cifra tokens e endereços de sessão para o usuário atual.</summary>
public interface IVault
{
    string Seal(string value);
    string Open(string value);
}

/// <summary>
/// DPAPI do usuário do Windows, sem entropia extra: lê o que a versão Python gravou
/// (CryptProtectData com CRYPTPROTECT_UI_FORBIDDEN) e vice-versa.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiVault : IVault
{
    public string Seal(string value) => string.IsNullOrEmpty(value) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public string Open(string value) => string.IsNullOrEmpty(value) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
