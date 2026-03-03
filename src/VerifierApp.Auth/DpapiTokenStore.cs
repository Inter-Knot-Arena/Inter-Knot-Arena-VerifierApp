using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerifierApp.Core.Models;
using VerifierApp.Core.Services;

namespace VerifierApp.Auth;

public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public DpapiTokenStore(string? path = null)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(baseDir, "InterKnotArena", "VerifierApp");
        Directory.CreateDirectory(root);
        _path = path ?? Path.Combine(root, "tokens.bin");
    }

    public async Task SaveAsync(VerifierTokens tokens, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        var plain = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, encrypted, ct);
    }

    public async Task<VerifierTokens?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(_path, ct);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<VerifierTokens>(json, JsonOptions);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        return Task.CompletedTask;
    }
}
