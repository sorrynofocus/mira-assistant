namespace Mira.Core;

public interface ISessionStore
{
    // Returns an opaque random session token. Only its hash is stored.
    Task<string> CreateAsync(Guid userId, CancellationToken cancellationToken = default);

    // Returns the owning user for an active session, or null when it is unknown, revoked, expired, idle, or the user is disabled.
    Task<Guid?> ValidateAsync(string sessionToken, CancellationToken cancellationToken = default);

    Task RevokeAsync(string sessionToken, CancellationToken cancellationToken = default);

    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default);
}
