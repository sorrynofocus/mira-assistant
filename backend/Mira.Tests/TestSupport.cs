using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mira.Core;

namespace Mira.Tests;

// Every factory gets a private temporary database so tests never touch %LOCALAPPDATA%\MIRA.
public sealed class MiraApiFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mira-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;

    public MiraApiFactory(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["Mira:DatabasePath"] = Path.Combine(_directory, "mira.db"),
            ["Mira:Chat:MockDelayMilliseconds"] = "0"
        };
        foreach (var (key, value) in settings ?? []) _settings[key] = value;
        _services = services;
    }

    public ConcurrentQueue<string> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CollectingLoggerProvider(Logs));
        });
        if (_services is not null) builder.ConfigureServices(_services);
    }

    public async Task<UserAccount> CreateUserAsync(string username, string password) =>
        await Services.GetRequiredService<IUserAccountService>().CreateAsync(username, password);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner removes leftovers.
        }
    }
}

public static class ClientExtensions
{
    public static async Task<string> RefreshCsrfAsync(this HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var token = body.GetProperty("csrfToken").GetString()!;
        client.SetCsrf(token);
        return token;
    }

    public static void SetCsrf(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string username, string password)
    {
        await client.RefreshCsrfAsync();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.SetCsrf(body.GetProperty("csrfToken").GetString()!);
        }

        return response;
    }

    public static HttpRequestMessage StreamRequest(string message) => new(HttpMethod.Post, "/api/chat/stream")
    {
        Content = JsonContent.Create(new { message })
    };
}

public sealed record SseEvent(string Event, JsonElement Data);

public static class Sse
{
    public static async Task<List<SseEvent>> ReadAllAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return Parse(text);
    }

    public static List<SseEvent> Parse(string text)
    {
        var events = new List<SseEvent>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            string? data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            }

            if (name is not null && data is not null) events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    // Reads events until one matches; used to stop mid-stream.
    public static async Task<SseEvent> ReadUntilAsync(StreamReader reader, string eventName)
    {
        var block = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
            {
                block.Add(line);
                continue;
            }

            var parsed = Parse(string.Join('\n', block) + "\n\n");
            block.Clear();
            if (parsed.Count == 1 && parsed[0].Event == eventName) return parsed[0];
        }

        throw new EndOfStreamException($"Stream ended before {eventName}.");
    }
}

public sealed class CollectingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class CollectingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(';', pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            sink.Enqueue($"{category}|{formatter(state, exception)}|{values}|{exception}");
        }
    }
}
