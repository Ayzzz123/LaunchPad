using System.Security.Cryptography;
using System.Text;

namespace Launchpad.Services;

internal static class AppIdGenerator
{
    public static string CreateId(string prefix, string identity) =>
        $"{prefix}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]}";
}
