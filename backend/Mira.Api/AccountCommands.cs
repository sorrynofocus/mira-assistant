using System.Text;
using Mira.Core;
using Mira.Infrastructure.Sqlite;

namespace Mira.Api;

// Local account bootstrap: `create-user --username <name>`. No default credentials exist; the owner creates the first account here.
public static class AccountCommands
{
    public const string CreateUserCommand = "create-user";

    public static async Task<int> CreateUserAsync(string[] args)
    {
        string? username = null;
        var passwordFromStdin = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--username" when i + 1 < args.Length:
                    username = args[++i];
                    break;
                case "--password-stdin":
                    passwordFromStdin = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return Usage();
            }
        }

        if (string.IsNullOrWhiteSpace(username)) return Usage();

        var builder = WebApplication.CreateBuilder();
        builder.AddMiraConfiguration([]);
        builder.Services.AddMiraCore();
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<MiraOptions>();
        var database = app.Services.GetRequiredService<MiraDatabase>();
        await database.MigrateAsync();

        string? password;
        if (passwordFromStdin || Console.IsInputRedirected)
        {
            // Console.In uses the legacy console code page on Windows, which would corrupt non-ASCII passphrases.
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            password = await stdin.ReadLineAsync();
        }
        else
        {
            password = ReadHidden("Password: ");
            var confirm = ReadHidden("Confirm password: ");
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Passwords do not match. No account was created.");
                return 1;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("No password was provided. No account was created.");
            return 1;
        }

        try
        {
            var account = await app.Services.GetRequiredService<IUserAccountService>().CreateAsync(username, password);
            Console.WriteLine($"Created user '{account.Username}' in {database.FilePath}.");
            return 0;
        }
        catch (AccountValidationException ex)
        {
            Console.Error.WriteLine($"{ex.Message} No account was created. (Password limits: {options.Auth.MinimumPasswordLength}-{options.Auth.MaximumPasswordLength} characters.)");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: create-user --username <name> [--password-stdin]");
        return 2;
    }

    // Reads without echo. Handles Backspace, including surrogate pairs; never trims or truncates.
    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) buffer.Length--;
                if (buffer.Length > 0 && char.IsHighSurrogate(buffer[^1])) buffer.Length--;
                continue;
            }

            if (key.KeyChar != '\0') buffer.Append(key.KeyChar);
        }
    }
}
