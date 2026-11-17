using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mira.Api.Preferences;
using Mira.Core;

namespace Mira.Tests;

public sealed class PreferencesApiTests
{
    private const string Path = "/api/me/preferences";

    [Fact]
    public async Task SignedOutRequestsGet401()
    {
        using var factory = new MiraApiFactory();
        using var client = factory.CreateClient();
        await client.RefreshCsrfAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(Path, new { theme = "system" })).StatusCode);
    }

    [Fact]
    public async Task PutWithoutCsrfIs403AndChangesNothing()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(Path, new { theme = "sovereign" })).StatusCode);
        Assert.Equal("mira-dark", (await GetAsync(client)).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task GetReturnsTheFullDefaultObject()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"theme":"mira-dark","fontSize":"default","inspectorOpenByDefault":false}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"theme":"sovereign"}""", "sovereign", "default", false)]
    [InlineData("""{"theme":"system"}""", "system", "default", false)]
    [InlineData("""{"fontSize":"small"}""", "mira-dark", "small", false)]
    [InlineData("""{"fontSize":"large"}""", "mira-dark", "large", false)]
    [InlineData("""{"inspectorOpenByDefault":true}""", "mira-dark", "default", true)]
    [InlineData("""{"theme":"sovereign","fontSize":"large","inspectorOpenByDefault":true}""", "sovereign", "large", true)]
    public async Task PutThenGetRoundTrips(string body, string theme, string fontSize, bool inspector)
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var put = await PutAsync(client, body);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        foreach (var result in new[] { await put.Content.ReadFromJsonAsync<JsonElement>(), await GetAsync(client) })
        {
            Assert.Equal(theme, result.GetProperty("theme").GetString());
            Assert.Equal(fontSize, result.GetProperty("fontSize").GetString());
            Assert.Equal(inspector, result.GetProperty("inspectorOpenByDefault").GetBoolean());
        }
    }

    [Fact]
    public async Task PartialUpdateKeepsOtherKeys()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");
        await PutAsync(client, """{"theme":"sovereign","fontSize":"large","inspectorOpenByDefault":true}""");

        var result = await (await PutAsync(client, """{"fontSize":"small"}""")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sovereign", result.GetProperty("theme").GetString());
        Assert.Equal("small", result.GetProperty("fontSize").GetString());
        Assert.True(result.GetProperty("inspectorOpenByDefault").GetBoolean());
    }

    [Theory]
    [InlineData("""{"theme":"neon-zebra"}""", "theme: Must be one of: mira-dark, sovereign, system.")]
    [InlineData("""{"theme":"Sovereign"}""", "theme: Must be one of: mira-dark, sovereign, system.")]
    [InlineData("""{"theme":null}""", "theme: Must be one of: mira-dark, sovereign, system.")]
    [InlineData("""{"fontSize":17}""", "fontSize: Must be one of: small, default, large.")]
    [InlineData("""{"inspectorOpenByDefault":"true"}""", "inspectorOpenByDefault: Must be true or false.")]
    [InlineData("""{"theme":"system","theme":"sovereign"}""", "theme: Preference given more than once.")]
    [InlineData("""{"ownerUserId":"00000000-0000-0000-0000-000000000001"}""", "Unknown preference.")]
    [InlineData("""{"<img src=x onerror=alert(1)>":"x"}""", "Unknown preference.")]
    [InlineData("""{}""", "At least one preference is required.")]
    [InlineData("""["theme"]""", "The body must be a JSON object.")]
    [InlineData("""  "sovereign"  """, "The body must be a JSON object.")]
    public async Task InvalidUpdatesGet400WithoutEchoingInputAndChangeNothing(string body, string expectedTitle)
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var response = await PutAsync(client, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("neon-zebra", text);
        Assert.DoesNotContain("onerror", text);
        Assert.DoesNotContain("ownerUserId", text);
        Assert.Equal(UserPreferences.Default.Theme, (await GetAsync(client)).GetProperty("theme").GetString());
    }

    [Theory]
    [InlineData("{\"theme\":\"sovereign\"", "application/json")]       // truncated
    [InlineData("{\"theme\": secret-marker-value}", "application/json")] // not JSON
    [InlineData("", "application/json")]                                // empty body
    public async Task MalformedJsonGetsAFixed400WithNoRequestContent(string body, string contentType)
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var response = await client.PutAsync(Path, new StringContent(body, Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(PreferenceEndpoints.InvalidJsonTitle, JsonDocument.Parse(text).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("secret-marker-value", text);
        Assert.DoesNotContain("sovereign", text);
        Assert.DoesNotContain("LineNumber", text);
        Assert.DoesNotContain(factory.Logs, l => l.Contains("secret-marker-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task NonJsonContentTypeGetsAFixed415(string contentType)
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        var response = await client.PutAsync(Path, new StringContent("""{"theme":"sovereign"}""", Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(PreferenceEndpoints.UnsupportedMediaTypeTitle, JsonDocument.Parse(text).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("sovereign", text);
        Assert.Equal("mira-dark", (await GetAsync(client)).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task OldCookieGets401AfterLogout()
    {
        using var factory = new MiraApiFactory();
        await factory.CreateUserAsync("owner", TestUsers.Password);
        using var client = factory.CreateClient();
        var login = await client.LoginAsync("owner", TestUsers.Password);
        var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mira.session=", StringComparison.Ordinal)).Split(';')[0];

        using var replay = factory.CreateClient(new() { HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync(Path)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task UsersAreIsolated()
    {
        using var factory = new MiraApiFactory();
        using var alice = await factory.LoggedInClientAsync("alice");
        using var bob = await factory.LoggedInClientAsync("bob");

        await PutAsync(alice, """{"theme":"sovereign","fontSize":"large","inspectorOpenByDefault":true}""");
        Assert.Equal("""{"theme":"mira-dark","fontSize":"default","inspectorOpenByDefault":false}""", await (await bob.GetAsync(Path)).Content.ReadAsStringAsync());

        await PutAsync(bob, """{"theme":"system"}""");
        Assert.Equal("sovereign", (await GetAsync(alice)).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task ValuesSurviveAnApiRestart()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?> { ["Mira:DatabasePath"] = System.IO.Path.Combine(directory, "mira.db") };
        try
        {
            using (var factory = new MiraApiFactory(settings))
            using (var client = await factory.LoggedInClientAsync("owner"))
            {
                Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, """{"theme":"system","fontSize":"small"}""")).StatusCode);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var restarted = new MiraApiFactory(settings))
            using (var client = restarted.CreateClient())
            {
                await client.LoginAsync("owner", TestUsers.Password);
                var result = await GetAsync(client);
                Assert.Equal("system", result.GetProperty("theme").GetString());
                Assert.Equal("small", result.GetProperty("fontSize").GetString());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task PreferenceValuesAndBodiesAreNotLogged()
    {
        using var factory = new MiraApiFactory();
        using var client = await factory.LoggedInClientAsync("owner");

        await PutAsync(client, """{"theme":"sovereign","fontSize":"large"}""");
        await PutAsync(client, """{"theme":"marker-off-allowlist"}""");
        await PutAsync(client, """{"marker-unknown-key":1}""");
        await GetAsync(client);

        Assert.NotEmpty(factory.Logs);
        foreach (var marker in new[] { "sovereign", "\"large\"", "marker-off-allowlist", "marker-unknown-key" })
        {
            Assert.DoesNotContain(factory.Logs, l => l.Contains(marker, StringComparison.Ordinal));
        }
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string json) =>
        client.PutAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> GetAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>(Path);
}
