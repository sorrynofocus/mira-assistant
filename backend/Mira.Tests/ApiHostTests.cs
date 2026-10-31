using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Mira.Core;
using Mira.Infrastructure;

namespace Mira.Tests;

public sealed class ApiHostTests
{
    [Fact]
    public async Task HealthIsAnonymousAndApiDeniesByDefault()
    {
        using var factory = new MiraApiFactory();
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/conversations/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ClientExtensions.StreamRequest("hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/chat/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);
    }

    [Fact]
    public async Task ForeignHostIsRejected()
    {
        using var factory = new MiraApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "evil.example";

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void HostResolvesDenyAllGovernance()
    {
        using var factory = new MiraApiFactory();
        Assert.IsType<DenyAllGovernanceService>(factory.Services.GetRequiredService<IGovernanceService>());
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Alt:Url", "http://0.0.0.0:5081")]
    [InlineData("urls", "http://0.0.0.0:5081")]
    [InlineData("HTTP_PORTS", "5081")]
    public void HostRefusesBindingOverrides(string key, string value)
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { [key] = value });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("Mira:ProviderMode", "Azure")]
    [InlineData("Mira:Auth:MaximumPasswordLength", "64")]
    [InlineData("Mira:Chat:MaxResponseSeconds", "0")]
    public void HostRefusesInvalidOptions(string key, string value)
    {
        using var factory = new MiraApiFactory(new Dictionary<string, string?> { [key] = value });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task CreateUserCommandReadsPipedPasswordsAsUtf8()
    {
        const string password = "piped passphrase — ünïcode ✓ 密码 😀 and spaces ";
        var directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "mira.db");
        var assemblyPath = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("create-user");
        start.ArgumentList.Add("--username");
        start.ArgumentList.Add("piped");
        start.ArgumentList.Add("--password-stdin");
        start.Environment["Mira__DatabasePath"] = databasePath;

        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync(password);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());

        var options = new MiraOptions { DatabasePath = databasePath };
        var accounts = new Mira.Infrastructure.Sqlite.SqliteUserAccountService(
            new Mira.Infrastructure.Sqlite.MiraDatabase(options), options, TimeProvider.System);
        Assert.NotNull(await accounts.VerifyCredentialsAsync("piped", password));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RealApiProcessRefusesExternalKestrelEndpoint()
    {
        var assemblyPath = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add(assemblyPath);
        // A non-default loopback address proves the override is refused without ever exposing a LAN listener.
        start.Environment["Kestrel__Endpoints__Alt__Url"] = "http://127.0.0.2:5081";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Mira__DatabasePath"] = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"), "mira.db");

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The API did not reject the external endpoint within 10 seconds.");
        }

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Kestrel endpoint configuration is unavailable", await process.StandardError.ReadToEndAsync());
    }
}
