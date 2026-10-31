namespace Mira.Core;

public sealed class MiraOptions
{
    public const string SectionName = "Mira";

    public static readonly string[] DefaultAllowedOrigins =
    [
        "http://127.0.0.1:4321",
        "http://localhost:4321",
        "http://127.0.0.1:5080",
        "http://localhost:5080"
    ];

    public string ProviderMode { get; set; } = "Mock";

    // Defaults to %LOCALAPPDATA%\MIRA\mira.db so private data stays outside the repository.
    public string? DatabasePath { get; set; }

    // Origins allowed to send state-changing requests. Null uses DefaultAllowedOrigins.
    public string[]? AllowedOrigins { get; set; }

    public AuthOptions Auth { get; set; } = new();

    public ChatOptions Chat { get; set; } = new();

    public string ResolveDatabasePath() =>
        string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIRA", "mira.db")
            : Path.GetFullPath(DatabasePath);

    public IReadOnlyList<string> ResolveAllowedOrigins() =>
        AllowedOrigins is { Length: > 0 } ? AllowedOrigins : DefaultAllowedOrigins;

    public void Validate()
    {
        if (!string.Equals(ProviderMode, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("M1 supports only the Mock provider mode.");
        }

        Auth.Validate();
        Chat.Validate();
    }
}

public sealed class AuthOptions
{
    public int SessionIdleMinutes { get; set; } = 30;
    public int SessionAbsoluteHours { get; set; } = 12;
    public int MinimumPasswordLength { get; set; } = 8;
    public int MaximumPasswordLength { get; set; } = 1024;
    public int MaxFailedLoginsPerUsername { get; set; } = 5;
    public int FailedLoginWindowMinutes { get; set; } = 15;
    public int LoginRequestsPerMinute { get; set; } = 20;

    public void Validate()
    {
        Require(SessionIdleMinutes is >= 1 and <= 1440, "Mira:Auth:SessionIdleMinutes must be 1-1440.");
        Require(SessionAbsoluteHours is >= 1 and <= 168, "Mira:Auth:SessionAbsoluteHours must be 1-168.");
        Require(MinimumPasswordLength >= 1, "Mira:Auth:MinimumPasswordLength must be at least 1.");
        // The handoff requires passwords of at least 35 characters to be accepted.
        Require(MaximumPasswordLength is >= 128 and <= 4096, "Mira:Auth:MaximumPasswordLength must be 128-4096.");
        Require(MinimumPasswordLength <= MaximumPasswordLength, "Mira:Auth:MinimumPasswordLength exceeds the maximum.");
        Require(MaxFailedLoginsPerUsername is >= 1 and <= 100, "Mira:Auth:MaxFailedLoginsPerUsername must be 1-100.");
        Require(FailedLoginWindowMinutes is >= 1 and <= 1440, "Mira:Auth:FailedLoginWindowMinutes must be 1-1440.");
        Require(LoginRequestsPerMinute is >= 1 and <= 1000, "Mira:Auth:LoginRequestsPerMinute must be 1-1000.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed class ChatOptions
{
    public int MaxMessageLength { get; set; } = 8000;
    public int MaxResponseSeconds { get; set; } = 120;
    public int MockDelayMilliseconds { get; set; } = 40;

    public void Validate()
    {
        if (MaxMessageLength is < 1 or > 100_000) throw new InvalidOperationException("Mira:Chat:MaxMessageLength must be 1-100000.");
        if (MaxResponseSeconds is < 1 or > 3600) throw new InvalidOperationException("Mira:Chat:MaxResponseSeconds must be 1-3600.");
        if (MockDelayMilliseconds is < 0 or > 10_000) throw new InvalidOperationException("Mira:Chat:MockDelayMilliseconds must be 0-10000.");
    }
}
