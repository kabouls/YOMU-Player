using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Windows.System;

namespace YomuPlayer.Desktop.Auth;

// Browser based Google sign-in with an installed-app OAuth client:
//   1. a loopback TCP listener is opened on 127.0.0.1 (random port);
//   2. the default browser is sent to Google's authorization endpoint with PKCE + state;
//   3. Google redirects back to 127.0.0.1 with a one-time code;
//   4. the code is exchanged for an access token and a long-lived refresh token;
//   5. the refresh token and the client credentials are stored with DPAPI.
// No password ever passes through this app, and the browser's existing Google session is reused.
public sealed class GoogleAuth
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "openid email profile https://www.googleapis.com/auth/youtube";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AuthState _state;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;

    public GoogleAuth()
    {
        _state = AuthStore.Load();
        ApplyBundledClient();
    }

    // A public build ships google-client.json next to the exe; copying it into the DPAPI store here means the
    // wizard is skipped on every later launch, and a user-supplied client still takes precedence.
    private void ApplyBundledClient()
    {
        if (_state.HasClient) return;

        var config = GoogleClientConfig.Load();
        if (config is null) return;

        _state.ClientId = config.ClientId;
        _state.ClientSecret = config.ClientSecret;
        try
        {
            AuthStore.Save(_state);
            Log.Write("using bundled Google OAuth client");
        }
        catch (Exception e)
        {
            Log.Write("could not persist bundled Google OAuth client: " + e.Message);
        }
    }

    // Raised when sign-in state or profile changes (possibly from a background thread).
    public event Action? Changed;

    public bool HasClient => _state.HasClient;
    public bool IsSignedIn => _state.IsSignedIn;
    public string? Name => _state.Name;
    public string? Email => _state.Email;
    public string? PhotoUrl => _state.PhotoUrl;

    public void ConfigureClient(string clientId, string clientSecret)
    {
        _state.ClientId = clientId.Trim();
        _state.ClientSecret = clientSecret.Trim();
        AuthStore.Save(_state);
        Changed?.Invoke();
    }

    // Removes the tokens but keeps the one-time Google Cloud client configuration, so the next sign-in is one click.
    public void SignOut()
    {
        _state.RefreshToken = null;
        _state.Name = null;
        _state.Email = null;
        _state.PhotoUrl = null;
        _accessToken = null;
        _accessExpiresAt = default;
        try { AuthStore.Save(_state); }
        catch (Exception e) { Log.Write("sign-out save failed: " + e.Message); }
        AppServices.ClearCaches();
        Changed?.Invoke();
    }

    // Forgets everything, including the Google Cloud client ID/secret.
    public void ResetAll()
    {
        _state = new AuthState();
        _accessToken = null;
        _accessExpiresAt = default;
        AuthStore.Clear();
        AppServices.ClearCaches();
        Changed?.Invoke();
    }

    // Opens the browser and waits for the loopback redirect. Must be awaited from the UI thread (it uses Launcher
    // and brings the window back on success).
    public async Task SignInAsync(CancellationToken ct = default)
    {
        if (!HasClient)
            throw new InvalidOperationException("Google sign-in is not configured yet.");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}";
            var state = RandomUrlSafe(16);
            var verifier = RandomUrlSafe(32);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            var url = AuthorizationEndpoint + "?" + string.Join("&", new[]
            {
                "client_id=" + Uri.EscapeDataString(_state.ClientId!),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "response_type=code",
                "scope=" + Uri.EscapeDataString(Scope),
                "access_type=offline",
                "prompt=consent",
                "include_granted_scopes=true",
                "state=" + state,
                "code_challenge=" + challenge,
                "code_challenge_method=S256"
            });

            if (!await Launcher.LaunchUriAsync(new Uri(url)))
                throw new InvalidOperationException("Could not open the default browser. Open this URL manually:\n" + url);

            string code;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                code = await WaitForCodeAsync(listener, state, timeout.Token);
            }

            await ExchangeCodeAsync(code, verifier, redirectUri, ct);
            AppServices.ClearCaches();
            Changed?.Invoke();

            // The browser is showing the "finishing" page; bring this window back.
            try { App.MainWindow?.HandleExternal("focus"); }
            catch (Exception e) { Log.Write("bring to front after sign-in failed: " + e.Message); }
        }
        finally
        {
            listener.Stop();
        }
    }

    // Returns a valid access token, refreshing it when it is about to expire. Returns null when signed out or when
    // Google rejects the refresh token (the UI then shows the signed-out state).
    public async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        if (!_state.IsSignedIn) return null;
        if (_accessToken is { } cached && DateTimeOffset.UtcNow < _accessExpiresAt - TimeSpan.FromMinutes(2))
            return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_accessToken is { } again && DateTimeOffset.UtcNow < _accessExpiresAt - TimeSpan.FromMinutes(2))
                return again;

            using var res = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _state.ClientId!,
                ["client_secret"] = _state.ClientSecret!,
                ["refresh_token"] = _state.RefreshToken!,
                ["grant_type"] = "refresh_token"
            }), ct);

            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                if (text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
                {
                    SignOut();
                    return null;
                }
                Log.Write("token refresh failed: " + text);
                return null;
            }

            ApplyToken(JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty token response"),
                requireRefreshToken: false);
            try { AuthStore.Save(_state); }
            catch (Exception e) { Log.Write("token save failed: " + e.Message); }
            return _accessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Write("token refresh error: " + e.Message);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---- loopback redirect ----

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();

            string request;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    request = await ReadRequestAsync(stream, readTimeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // A browser preconnect or an idle socket: drop it and keep waiting for the real redirect.
                    continue;
                }
            }

            var target = FirstLineTarget(request);

            // Browsers ask for a favicon after navigating; ignore it and keep waiting for the redirect.
            if (target.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", "not found", ct);
                continue;
            }

            var uri = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(target)
                : new Uri("http://127.0.0.1" + target);
            var query = HttpUtility.ParseQueryString(uri.Query);

            var error = query["error"];
            if (!string.IsNullOrEmpty(error))
            {
                var detail = query["error_description"] ?? error;
                var friendly = error == "access_denied"
                    ? "Google denied access. If this OAuth app is in Testing mode, add this Google account under " +
                      "OAuth consent screen -> Test users, then try again."
                    : "Google sign-in was not completed: " + detail;
                await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", Page("Sign-in couldn't finish",
                    "<p>" + HttpUtility.HtmlEncode(friendly) + "</p><p>You can close this tab and try again.</p>"), ct);
                throw new InvalidOperationException(friendly);
            }

            var code = query["code"];
            if (string.IsNullOrEmpty(code))
            {
                await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", "missing code", ct);
                continue;
            }

            var returnedState = query["state"];
            if (!string.Equals(returnedState, expectedState, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 400, "text/html; charset=utf-8", Page("Sign-in rejected",
                    "<p>The sign-in response did not match this request. Please try again.</p>"), ct);
                throw new InvalidOperationException("Google sign-in state did not match.");
            }

            try
            {
                await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", Page("Finishing sign-in",
                    "<p>Google returned the authorization code. YOMU Player is completing sign-in now.</p>" +
                    "<p><a class=\"button\" href=\"yomu://focus\">Return to YOMU Player</a></p>" +
                    "<p class=\"muted\">Or close this tab and go back to the app.</p>"), ct);
            }
            catch
            {
                // The code is already captured; a browser closing the tab must not abort the sign-in.
            }
            return code;
        }
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        while (text.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        return text.ToString();
    }

    private static string FirstLineTarget(string request)
    {
        var line = request.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Error" };
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {bytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static string Page(string title, string body) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{title}} - YOMU Player</title>
        <style>
          html,body{height:100%;margin:0;background:#0c0c0c;color:#f2f2f2;
            font-family:"Segoe UI",system-ui,sans-serif;display:grid;place-items:center}
          main{max-width:520px;padding:32px;text-align:center}
          h1{font-size:28px;margin:0 0 12px} p{color:#b8b8b8;line-height:1.5}
          .button{display:inline-block;margin-top:8px;padding:10px 18px;border-radius:20px;
            background:#ff0033;color:#fff;text-decoration:none;font-weight:600}
          .muted{font-size:12px;color:#777}
        </style></head><body><main><h1>{{title}}</h1>{{body}}</main></body></html>
        """;

    // ---- token handling ----

    private async Task ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        using var res = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _state.ClientId!,
            ["client_secret"] = _state.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        }), ct);

        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            JsonNode? node = null;
            try { node = JsonNode.Parse(text); } catch { /* non-JSON error page */ }

            var error = node?["error"]?.GetValue<string>();
            var detail = node?["error_description"]?.GetValue<string>() ?? error ?? text;
            var message = error switch
            {
                "invalid_client" =>
                    "Google rejected the app's client ID/secret. In Google Cloud, create an OAuth client of type " +
                    "Desktop app, paste both values again, and make sure the YouTube Data API v3 is enabled.",
                "redirect_uri_mismatch" =>
                    "Google rejected the redirect URI. The OAuth client must be of type Desktop app, not " +
                    "Web application or TVs and Limited Input devices.",
                "invalid_grant" =>
                    "The sign-in code expired or was already used. Try signing in again.",
                _ => "Google sign-in failed: " + detail
            };
            Log.Write("Google token exchange failed: " + text);
            throw new InvalidOperationException(message);
        }

        var token = JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty token response");
        var grantedScopes = token["scope"]?.GetValue<string>() ?? "";
        if (!grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("https://www.googleapis.com/auth/youtube"))
        {
            Log.Write("Google sign-in granted scopes: " +
                      (grantedScopes.Length == 0 ? "(none)" : grantedScopes));
            throw new InvalidOperationException(
                "Google signed you in, but did not grant YouTube access. In Google Auth Platform -> Data Access, " +
                "add https://www.googleapis.com/auth/youtube. Then revoke YOMU Player at " +
                "myaccount.google.com/permissions, sign out of the app, and sign in again. On the Google consent " +
                "screen, tick the YouTube permission before continuing.");
        }

        ApplyToken(token, requireRefreshToken: true);
        AuthStore.Save(_state);
    }

    private void ApplyToken(JsonNode node, bool requireRefreshToken)
    {
        _accessToken = node["access_token"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("Google did not return an access token.");
        var expiresIn = node["expires_in"]?.GetValue<int>() ?? 3600;
        _accessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        var refresh = node["refresh_token"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(refresh)) _state.RefreshToken = refresh;
        else if (requireRefreshToken && string.IsNullOrEmpty(_state.RefreshToken))
            throw new InvalidOperationException(
                "Google did not return a refresh token. Remove this app at myaccount.google.com/permissions, then try again.");

        if (node["id_token"]?.GetValue<string>() is { } idToken && ParseIdToken(idToken) is { } profile)
        {
            _state.Name = profile.Name;
            _state.Email = profile.Email;
            _state.PhotoUrl = profile.PhotoUrl;
        }
    }

    private static (string? Name, string? Email, string? PhotoUrl)? ParseIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Convert.FromBase64String(payload));
            if (json is null) return null;

            return (json["name"]?.GetValue<string>(),
                json["email"]?.GetValue<string>(),
                json["picture"]?.GetValue<string>());
        }
        catch
        {
            return null;
        }
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
