using System.Text.Json;

namespace YomuPlayer.Desktop.Auth;

// A distributed build can ship google-client.json next to the exe, so end users never see the one-time Google
// Cloud setup. Per-user setup still wins when present: the client is copied into the DPAPI auth store on first run.
// Local development can override the shipped client with %LOCALAPPDATA%\YomuPlayer\google-client.json.
public sealed record GoogleClientConfig(string ClientId, string ClientSecret)
{
    private sealed class FileModel
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }

    public static string LocalPath => Path.Combine(AppPaths.DataRoot, "google-client.json");

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "google-client.json");

    public static GoogleClientConfig? Load() => Read(LocalPath) ?? Read(ShippedPath);

    private static GoogleClientConfig? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (model is null
                || string.IsNullOrWhiteSpace(model.ClientId)
                || string.IsNullOrWhiteSpace(model.ClientSecret))
            {
                Log.Write($"google-client.json at {path} is missing clientId/clientSecret");
                return null;
            }

            return new GoogleClientConfig(model.ClientId.Trim(), model.ClientSecret.Trim());
        }
        catch (Exception e)
        {
            Log.Write($"google-client.json at {path} could not be read: {e.Message}");
            return null;
        }
    }
}
