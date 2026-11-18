using Mira.Core;

namespace Mira.Infrastructure;

// The seeded built-in MIRA persona (migration 5 writes the same text; a test keeps them equal).
public static class BuiltInPersona
{
    public const string Name = "MIRA";
    public const string Description = "Helpful, candid and concise.";
    public const string Markdown = """
        # MIRA

        ## Identity
        You are MIRA (Memory, Intelligence, Reasoning and Autonomy), a local assistant.

        ## Style
        - Helpful, candid and concise.
        - Say plainly when you do not know something.

        ## Boundaries
        - Never claim to have taken an action, called a tool or remembered something that did not happen.
        - Do not invent memories, sources or facts.
        """;

    public static readonly DateTimeOffset SeededAt = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    public static PersonaDetail Detail { get; } = new(
        PersonaIds.BuiltInMira, null, 1, true, SeededAt, SeededAt, Name, Description, Markdown);
}

// Result of setting a conversation's persona with both ownership checks in one step.
public enum ConversationPersonaResult
{
    Updated,
    ConversationNotFound,
    PersonaNotFound
}

// Process-memory IPersonaStore: a test double with the same semantics as SqlitePersonaStore.
public sealed class InMemoryPersonaStore(TimeProvider time) : IPersonaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _personas = new();
    private readonly Dictionary<Guid, Guid> _defaults = new();

    public Task<IReadOnlyList<Persona>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            IReadOnlyList<Persona> list = new[] { Summary(BuiltInPersona.Detail) }
                .Concat(_personas.Values.Where(p => p.Owner == ownerUserId)
                    .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                    .Select(p => Summary(Detail(p))))
                .ToArray();
            return Task.FromResult(list);
        }
    }

    public Task<PersonaDetail?> GetAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(Readable(ownerUserId, personaId));
        }
    }

    public Task<IReadOnlyList<PersonaVersionInfo>> ListVersionsAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            IReadOnlyList<PersonaVersionInfo> versions = personaId == PersonaIds.BuiltInMira
                ? [new PersonaVersionInfo(personaId, 1, BuiltInPersona.Name, BuiltInPersona.Description, BuiltInPersona.SeededAt)]
                : Owned(ownerUserId, personaId) is { } p
                    ? p.Versions.OrderByDescending(v => v.Version).Take(PersonaLimits.MaxVersionsPerPersona)
                        .Select(v => new PersonaVersionInfo(v.PersonaId, v.Version, v.Name, v.Description, v.CreatedAt)).ToArray()
                    : [];
            return Task.FromResult(versions);
        }
    }

    public Task<PersonaVersion?> GetVersionAsync(Guid ownerUserId, Guid personaId, int version, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            PersonaVersion? result = personaId == PersonaIds.BuiltInMira
                ? version == 1 ? new PersonaVersion(personaId, 1, BuiltInPersona.Name, BuiltInPersona.Description, BuiltInPersona.Markdown, BuiltInPersona.SeededAt) : null
                : Owned(ownerUserId, personaId)?.Versions.FirstOrDefault(v => v.Version == version);
            return Task.FromResult(result);
        }
    }

    public Task<PersonaDetail> CreateAsync(Guid ownerUserId, string name, string? description, string markdown, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        var (n, d, m) = Clean(name, description, markdown);
        lock (_gate)
        {
            if (_personas.Values.Count(p => p.Owner == ownerUserId) >= PersonaLimits.MaxUserPersonas)
            {
                throw new PersonaConflictException("Persona limit reached.");
            }

            var now = time.GetUtcNow();
            var entry = new Entry(Guid.NewGuid(), ownerUserId, now) { UpdatedAt = now, ActiveVersion = 1 };
            entry.Versions.Add(new PersonaVersion(entry.Id, 1, n, d, m, now));
            _personas.Add(entry.Id, entry);
            return Task.FromResult(Detail(entry));
        }
    }

    public Task<PersonaDetail> UpdateAsync(Guid ownerUserId, Guid personaId, string name, string? description, string markdown, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        var (n, d, m) = Clean(name, description, markdown);
        lock (_gate)
        {
            var entry = Owned(ownerUserId, personaId) ?? throw new KeyNotFoundException("Persona not found for this owner.");
            var now = time.GetUtcNow();
            var version = entry.Versions.Max(v => v.Version) + 1;
            entry.Versions.Add(new PersonaVersion(entry.Id, version, n, d, m, now));
            entry.ActiveVersion = version;
            entry.UpdatedAt = now;
            entry.Versions.RemoveAll(v => v.Version <= version - PersonaLimits.MaxVersionsPerPersona);
            return Task.FromResult(Detail(entry));
        }
    }

    public Task<PersonaDetail> RevertAsync(Guid ownerUserId, Guid personaId, int version, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            var entry = Owned(ownerUserId, personaId) ?? throw new KeyNotFoundException("Persona not found for this owner.");
            if (entry.Versions.All(v => v.Version != version)) throw new KeyNotFoundException("Persona version not found.");
            entry.ActiveVersion = version;
            entry.UpdatedAt = time.GetUtcNow();
            return Task.FromResult(Detail(entry));
        }
    }

    public Task<bool> DeleteAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            if (Owned(ownerUserId, personaId) is null) return Task.FromResult(false);
            if (_defaults.TryGetValue(ownerUserId, out var current) && current == personaId) _defaults[ownerUserId] = PersonaIds.BuiltInMira;
            _personas.Remove(personaId);
            return Task.FromResult(true);
        }
    }

    public Task<Guid> GetDefaultAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(_defaults.TryGetValue(ownerUserId, out var id) ? id : PersonaIds.BuiltInMira);
        }
    }

    public Task SetDefaultAsync(Guid ownerUserId, Guid personaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            if (Readable(ownerUserId, personaId) is null) throw new KeyNotFoundException("Persona not found for this owner.");
            _defaults[ownerUserId] = personaId;
        }

        return Task.CompletedTask;
    }

    public Task<PersonaResolution> ResolveForTurnAsync(Guid ownerUserId, Guid pinnedPersonaId, CancellationToken cancellationToken = default)
    {
        RequireOwner(ownerUserId);
        lock (_gate)
        {
            return Task.FromResult(Readable(ownerUserId, pinnedPersonaId) is { } persona
                ? new PersonaResolution(persona, false)
                : new PersonaResolution(BuiltInPersona.Detail, true));
        }
    }

    // Callers hold _gate.
    private PersonaDetail? Readable(Guid ownerUserId, Guid personaId) =>
        personaId == PersonaIds.BuiltInMira ? BuiltInPersona.Detail : Owned(ownerUserId, personaId) is { } p ? Detail(p) : null;

    private Entry? Owned(Guid ownerUserId, Guid personaId) =>
        _personas.TryGetValue(personaId, out var p) && p.Owner == ownerUserId ? p : null;

    private static PersonaDetail Detail(Entry p)
    {
        var v = p.Versions.Single(x => x.Version == p.ActiveVersion);
        return new PersonaDetail(p.Id, p.Owner, p.ActiveVersion, false, p.CreatedAt, p.UpdatedAt, v.Name, v.Description, v.Markdown);
    }

    internal static Persona Summary(PersonaDetail d) =>
        new(d.Id, d.OwnerUserId, d.ActiveVersion, d.IsBuiltIn, d.CreatedAt, d.UpdatedAt, d.Name, d.Description);

    // Store-level backstop to PersonaRules (the API validates first): strip controls, trim, enforce limits.
    internal static (string Name, string? Description, string Markdown) Clean(string name, string? description, string markdown)
    {
        var n = PersonaRules.SanitizeName(name) ?? throw new ArgumentException("A valid name is required.", nameof(name));
        var m = PersonaRules.SanitizeMarkdown(markdown) ?? throw new ArgumentException("A valid body is required.", nameof(markdown));
        string? d = null;
        if (description is not null && !string.IsNullOrWhiteSpace(PersonaPrompt.StripControls(description, false)))
        {
            d = PersonaRules.SanitizeDescription(description) ?? throw new ArgumentException("The description is too long.", nameof(description));
        }

        return (n, d, m);
    }

    internal static void RequireOwner(Guid ownerUserId)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("An authenticated owner is required.", nameof(ownerUserId));
    }

    private sealed class Entry(Guid id, Guid owner, DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public Guid Owner { get; } = owner;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset UpdatedAt { get; set; }
        public int ActiveVersion { get; set; }
        public List<PersonaVersion> Versions { get; } = [];
    }
}
