using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core;

namespace Mira.Tests;

public sealed class AuthTests
{
    // 52 characters with spaces, symbols, accents, CJK and an emoji (surrogate pair).
    private const string LongPassword = "correct horse ✓ battery staple — Ünïcødé 密码 😀 #42!";

    [Fact]
    public void LongPasswordIsAtLeast35Characters() => Assert.True(LongPassword.Length >= 35);

    [Fact]
    public async Task LongUnicodePasswordLogsInAndMeReturnsTheUser()
    {
        using var factory = new MiraApiFactory();
        var account = await factory.CreateUserAsync("Chris", LongPassword);
        using var client = factory.CreateClient();

        var login = await client.LoginAsync("chris", LongPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(account.Id.ToString(), me.GetProperty("id").GetString());
        Assert.Equal("Chris", me.GetProperty("username").GetString());
    }

    [Fact]
    public async Task PasswordIsNeitherTruncatedNorTrimmed()
    {
        using var factory = new MiraApiFactory();
        var password = " " + new string('p', 200) + " ";
        await factory.CreateUserAsync("owner", password);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("owner", password[..^2])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("owner", password.Trim())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("owner", password)).StatusCode);
    }

    [Fact]
    public async Task WrongPasswordAndUnknownUserGetTheSameGenericResponse()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        var wrong = await client.LoginAsync("owner", LongPassword + "x");
        var unknown = await client.LoginAsync("nobody", LongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var wrongTitle = (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
        var unknownTitle = (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
        Assert.Equal("Invalid username or password.", wrongTitle);
        Assert.Equal(wrongTitle, unknownTitle);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DisabledAccountCannotLogIn()
    {
        using var factory = new MiraApiFactory();
        var account = await factory.CreateUserAsync("owner", LongPassword);
        await factory.Services.GetRequiredService<IUserAccountService>().SetDisabledAsync(account.Id, true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("owner", LongPassword)).StatusCode);
    }

    [Fact]
    public async Task DisablingAnAccountEndsItsActiveSession()
    {
        using var factory = new MiraApiFactory();
        var account = await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();
        await client.LoginAsync("owner", LongPassword);

        await factory.Services.GetRequiredService<IUserAccountService>().SetDisabledAsync(account.Id, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task PasswordNeverAppearsInLogs()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        await client.LoginAsync("owner", LongPassword + "wrong");
        await client.LoginAsync("owner", LongPassword);

        Assert.NotEmpty(factory.Logs);
        Assert.DoesNotContain(factory.Logs, line => line.Contains(LongPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs, line => line.Contains("battery staple", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginRequiresCsrfToken()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "owner", password = LongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedWritesRequireCsrfToken()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();
        await client.LoginAsync("owner", LongPassword);

        client.SetCsrf("not-a-real-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ClientExtensions.StreamRequest("hello"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ClientExtensions.StreamRequest("hello"))).StatusCode);
    }

    [Fact]
    public async Task PreLoginCsrfTokenIsRejectedAfterLogin()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();
        var anonymousToken = await client.RefreshCsrfAsync();
        await client.PostAsJsonAsync("/api/auth/login", new { username = "owner", password = LongPassword });

        client.SetCsrf(anonymousToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ClientExtensions.StreamRequest("hello"))).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://127.0.0.1:9999")]
    public async Task CrossOriginWritesAreRefused(string origin)
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();
        await client.RefreshCsrfAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password = LongPassword })
        };
        request.Headers.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task SameOriginDevServerWriteIsAllowed()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();
        await client.RefreshCsrfAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "owner", password = LongPassword })
        };
        request.Headers.Add("Origin", "http://127.0.0.1:4321");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task RepeatedFailuresThrottleTheUsernameEvenWithTheRightPassword()
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { ["Mira:Auth:MaxFailedLoginsPerUsername"] = "3" });
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("owner", "wrong password " + i)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.LoginAsync("OWNER", LongPassword)).StatusCode);
    }

    [Fact]
    public async Task UnknownUsernamesAreThrottledTheSameWay()
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { ["Mira:Auth:MaxFailedLoginsPerUsername"] = "2" });
        using var client = factory.CreateClient();

        await client.LoginAsync("ghost", "x1");
        await client.LoginAsync("ghost", "x2");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.LoginAsync("ghost", "x3")).StatusCode);
    }

    [Fact]
    public async Task LoginEndpointIsRateLimited()
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { ["Mira:Auth:LoginRequestsPerMinute"] = "3" });
        using var client = factory.CreateClient();
        await client.RefreshCsrfAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { username = "user" + i, password = "x" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        var limited = await client.PostAsJsonAsync("/api/auth/login", new { username = "user9", password = "x" });
        Assert.Equal("Too many sign-in attempts. Wait a minute and try again.",
            (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task LogoutRevokesTheServerSideSession()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient(new() { HandleCookies = false });

        await client.RefreshCsrfAsyncWithoutCookies();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "owner", password = LongPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mira.session=", StringComparison.Ordinal)).Split(';')[0];
        var loginCsrf = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
        var csrfCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{sessionCookie}; {MergeAntiforgeryCookie(csrfCookie, login)}");
        client.SetCsrf(loginCsrf);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);

        // Replaying the old cookie after logout must fail: the session was revoked server-side, not just cleared in the browser.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task EachLoginIssuesANewSession()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        var first = await client.LoginAsync("owner", LongPassword);
        var second = await client.LoginAsync("owner", LongPassword);

        static string Session(HttpResponseMessage r) =>
            r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mira.session=", StringComparison.Ordinal)).Split(';')[0];
        Assert.NotEqual(Session(first), Session(second));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SessionCookieIsHttpOnlyAndSameSiteStrict()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", LongPassword);
        using var client = factory.CreateClient();

        var login = await client.LoginAsync("owner", LongPassword);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mira.session=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    // The antiforgery cookie may be reissued on login; prefer the newest value.
    private static string MergeAntiforgeryCookie(string original, HttpResponseMessage login)
    {
        var reissued = login.Headers.GetValues("Set-Cookie").FirstOrDefault(c => c.StartsWith("mira.csrf=", StringComparison.Ordinal));
        return reissued is null ? original : reissued.Split(';')[0];
    }
}

internal static class CookielessClientExtensions
{
    public static async Task RefreshCsrfAsyncWithoutCookies(this HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/csrf");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mira.csrf=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.SetCsrf(body.GetProperty("csrfToken").GetString()!);
    }
}
