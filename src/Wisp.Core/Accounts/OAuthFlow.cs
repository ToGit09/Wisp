using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Wisp.Core.Accounts;

/// <summary>
/// OAuth 2.0 authorization code flow with PKCE for native apps (RFC 8252):
/// the system browser handles the login, a one-shot loopback listener receives the code.
/// </summary>
public static class OAuthFlow
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<Credentials> AuthorizeAsync(OAuthProvider provider, string loginHint, CancellationToken ct)
    {
        if (!provider.IsConfigured)
            throw new InvalidOperationException($"Die Anmeldung über {provider.Name} ist in dieser Version noch nicht eingerichtet.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://{provider.RedirectHost}:{port}/";

        var url = provider.AuthorizeUrl + "?" + Query(new()
        {
            ["client_id"] = provider.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = provider.Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["login_hint"] = loginHint,
            ["access_type"] = "offline", // Google: issue a refresh token
            ["prompt"] = "consent",
        });
        OpenBrowser(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var parameters = await ReceiveCallbackAsync(listener, timeout.Token);

        if (parameters["state"] != state) throw new InvalidOperationException("Ungültige Antwort vom Anmeldedienst.");
        if (parameters["error"] is { } error) throw new InvalidOperationException($"Anmeldung abgebrochen ({error}).");
        var code = parameters["code"] ?? throw new InvalidOperationException("Keine Anmeldung erhalten.");

        return await TokenRequestAsync(provider, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, null, ct);
    }

    public static Task<Credentials> RefreshAsync(OAuthProvider provider, Credentials current, CancellationToken ct) =>
        TokenRequestAsync(provider, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken ?? throw new InvalidOperationException("Kein Refresh-Token."),
        }, current.RefreshToken, ct);

    static async Task<Credentials> TokenRequestAsync(OAuthProvider provider, Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        form["client_id"] = provider.ClientId;
        if (provider.ClientSecret is { Length: > 0 } secret) form["client_secret"] = secret;
        if (provider == OAuthConfig.Microsoft) form["scope"] = provider.Scopes;

        using var response = await Http.PostAsync(provider.TokenUrl, new FormUrlEncodedContent(form), ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var reason = root.TryGetProperty("error", out var e) ? e.GetString() : response.StatusCode.ToString();
            throw new UnauthorizedAccessException($"Anmeldung abgelehnt ({reason}).");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        return new Credentials
        {
            AccessToken = root.GetProperty("access_token").GetString(),
            // Providers may or may not rotate the refresh token; keep the old one if none is returned.
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : previousRefresh,
            AccessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60),
        };
    }

    static async Task<System.Collections.Specialized.NameValueCollection> ReceiveCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            // "GET /?code=...&state=... HTTP/1.1"
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || !parts[1].StartsWith("/?", StringComparison.Ordinal))
            {
                await RespondAsync(stream, 404, "", ct); // e.g. favicon request
                continue;
            }
            var parameters = HttpUtility.ParseQueryString(parts[1][2..]);
            await RespondAsync(stream, 200,
                "<!doctype html><meta charset=utf-8><title>Wisp</title><body style=\"font-family:sans-serif;padding:3em\">" +
                "<h2>Fertig.</h2><p>Sie können dieses Fenster schließen und zu Wisp zurückkehren.</p>", ct);
            return parameters;
        }
    }

    static async Task RespondAsync(Stream stream, int status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }

    public static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    static string Query(Dictionary<string, string> values) =>
        string.Join("&", values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
