using System.Security.Cryptography;
using System.Text;

namespace NasSearchBar;

// Windows DPAPI: only this Windows user on this PC can decrypt the saved password
static class Secret
{
    public static string Protect(string plain) => plain == "" ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string data)
    {
        try
        {
            return data == "" ? "" :
                Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return "";  // copied from another user/PC: ask for the password again
        }
    }
}
