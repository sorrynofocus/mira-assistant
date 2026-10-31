using System.Text;

namespace Mira.Core;

public sealed record UserAccount(Guid Id, string Username, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt);

public sealed class AccountValidationException(string message) : Exception(message);

public interface IUserAccountService
{
    Task<UserAccount> CreateAsync(string username, string password, CancellationToken cancellationToken = default);

    // Returns null for every failure (unknown user, wrong password, disabled account) so callers cannot enumerate users.
    Task<UserAccount?> VerifyCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<UserAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetDisabledAsync(Guid id, bool disabled, CancellationToken cancellationToken = default);
}

public static class UsernameRules
{
    public const int MaxLength = 64;

    public static string Clean(string? username) => (username ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    public static string NormalizeKey(string? username) => Clean(username).ToUpperInvariant();

    public static void Validate(string cleaned)
    {
        if (cleaned.Length is 0 or > MaxLength)
        {
            throw new AccountValidationException($"Username must be 1-{MaxLength} characters.");
        }

        foreach (var c in cleaned)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or '@'))
            {
                throw new AccountValidationException("Username may contain letters, digits, '.', '_', '-' and '@'.");
            }
        }
    }
}

public static class PasswordRules
{
    // Passwords are never trimmed or truncated. NFC normalization makes the same passphrase typed
    // through different input methods hash identically.
    public static string Normalize(string password) => password.Normalize(NormalizationForm.FormC);
}
