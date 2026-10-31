using System.Net;
using Microsoft.Extensions.Configuration;

namespace Mira.Api;

public static class LocalBindingPolicy
{
    public const string Url = "http://127.0.0.1:5080";
    public const int Port = 5080;

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            throw new InvalidOperationException("Kestrel endpoint configuration is unavailable in local-only mode.");
        }

        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls) && !string.Equals(urls, Url, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The API binding must be {Url}.");
        }

        if (!string.IsNullOrWhiteSpace(configuration["HTTP_PORTS"]) ||
            !string.IsNullOrWhiteSpace(configuration["HTTPS_PORTS"]))
        {
            throw new InvalidOperationException("HTTP_PORTS and HTTPS_PORTS are unavailable in local-only mode.");
        }
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Replace the default Kestrel configuration loader. Configured endpoints
            // must not be added later through a file reload.
            options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            options.Listen(IPAddress.Loopback, Port);
        });
    }
}
