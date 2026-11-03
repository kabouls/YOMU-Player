using System.Text.Json;

namespace YomuPlayer.Desktop.Auth;

// Everything persisted between launches. The access token is deliberately not stored: it is short-lived and is
// refreshed from the refresh token on demand.
public sealed class AuthState
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? PhotoUrl { get; set; }

    public bool HasClient => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(RefreshToken);
}

// The active build's data folder, DPAPI-protected for the current Windows user.
internal static class AuthStore
{
    private static readonly string DirectoryPath = AppPaths.DataRoot;
    private static readonly string FilePath = AppPaths.AuthPath;

    public static AuthState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AuthState();
            var json = Dpapi.Unprotect(File.ReadAllBytes(FilePath));
            return JsonSerializer.Deserialize<AuthState>(json) ?? new AuthState();
        }
        catch (Exception e)
        {
            Log.Write("auth load failed: " + e.Message);
            return new AuthState();
        }
    }

    public static void Save(AuthState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        File.WriteAllBytes(FilePath, Dpapi.Protect(json));
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception e)
        {
            Log.Write("auth clear failed: " + e.Message);
        }
    }
}
