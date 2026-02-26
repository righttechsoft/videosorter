using System.Security.Cryptography;
using System.Text;

namespace VideoSorter.Helpers;

public static class HashHelper
{
    public static string ComputeCacheKey(string filePath, DateTime lastModifiedUtc, long fileSize)
    {
        var input = $"{filePath}|{lastModifiedUtc:O}|{fileSize}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
