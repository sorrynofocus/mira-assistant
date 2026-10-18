using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mira.Api;
using Mira.Core;
using Mira.Infrastructure;

namespace Mira.Tests;

public sealed class ApiHostTests
{
    [Fact]
    public async Task HealthIsTheOnlyPublicM0Route()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());

        var chat = await client.GetAsync("/api/chat/stream");
        Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
    }

    [Fact]
    public async Task ForeignHostIsRejected()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "evil.example";

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void HostResolvesDenyAllGovernance()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        IGovernanceService governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();

        Assert.IsType<DenyAllGovernanceService>(governance);
        Assert.False(governance.Evaluate(new CapabilityRequest("web", "write", "any")).Allowed);
    }

    [Theory]
    [InlineData("Kestrel:Endpoints:Alt:Url", "http://0.0.0.0:5081")]
    [InlineData("urls", "http://0.0.0.0:5081")]
    [InlineData("HTTP_PORTS", "5081")]
    public void HostRefusesBindingOverrides(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void HostRefusesUnsupportedProviderMode()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mira:ProviderMode"] = "Azure"
                })));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
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
        start.Environment["Kestrel__Endpoints__Alt__Url"] = "http://0.0.0.0:5081";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

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
