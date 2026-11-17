using System.Text.Json;

namespace Mira.Core;

// M3 per-user preferences (GET/PUT /api/me/preferences). Fixed contract (chunk 0): keys and values are allowlisted here,
// and nothing else is ever stored or returned. See docs/M3_ACCOUNT_SETTINGS.md.
public static class PreferenceKeys
{
    public const string Theme = "theme";
    public const string FontSize = "fontSize";
    public const string InspectorOpenByDefault = "inspectorOpenByDefault";

    public static readonly IReadOnlyList<string> All = [Theme, FontSize, InspectorOpenByDefault];
}

public static class ThemeValues
{
    public const string MiraDark = "mira-dark";
    public const string Sovereign = "sovereign";
    public const string System = "system"; // follows prefers-color-scheme: dark -> mira-dark, light -> sovereign

    public static readonly IReadOnlyList<string> All = [MiraDark, Sovereign, System];
}

public static class FontSizeValues
{
    public const string Small = "small";
    public const string Default = "default";
    public const string Large = "large";

    public static readonly IReadOnlyList<string> All = [Small, Default, Large];
}

// The full preferences object. GET returns it; PUT returns it after the update. JSON (web defaults, camelCase):
// { "theme": "mira-dark", "fontSize": "default", "inspectorOpenByDefault": false }
public sealed record UserPreferences(string Theme, string FontSize, bool InspectorOpenByDefault)
{
    public static UserPreferences Default { get; } = new(ThemeValues.MiraDark, FontSizeValues.Default, false);

    // Builds the object from stored key/value rows. Unknown keys and values not on the allowlist are ignored (default used).
    public static UserPreferences FromStored(IReadOnlyDictionary<string, string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var theme = rows.TryGetValue(PreferenceKeys.Theme, out var t) && ThemeValues.All.Contains(t) ? t : Default.Theme;
        var fontSize = rows.TryGetValue(PreferenceKeys.FontSize, out var f) && FontSizeValues.All.Contains(f) ? f : Default.FontSize;
        var inspector = rows.TryGetValue(PreferenceKeys.InspectorOpenByDefault, out var i) && bool.TryParse(i, out var b) ? b : Default.InspectorOpenByDefault;
        return new UserPreferences(theme, fontSize, inspector);
    }

    public UserPreferences Apply(PreferenceUpdate update) => new(
        update.Theme ?? Theme, update.FontSize ?? FontSize, update.InspectorOpenByDefault ?? InspectorOpenByDefault);
}

// A validated partial update: null means "leave unchanged".
public sealed record PreferenceUpdate(string? Theme = null, string? FontSize = null, bool? InspectorOpenByDefault = null)
{
    public bool IsEmpty => Theme is null && FontSize is null && InspectorOpenByDefault is null;

    // The rows to upsert, with stored string values ("true"/"false" for booleans).
    public IReadOnlyDictionary<string, string> ToStored()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Theme is not null) rows[PreferenceKeys.Theme] = Theme;
        if (FontSize is not null) rows[PreferenceKeys.FontSize] = FontSize;
        if (InspectorOpenByDefault is { } inspector) rows[PreferenceKeys.InspectorOpenByDefault] = inspector ? "true" : "false";
        return rows;
    }
}

public sealed record PreferenceValidationError(string Key, string Message);

// The 400 rules for PUT, shared so the API and its tests agree. Errors name a key only if it is allowlisted, and never
// echo a submitted value or an unknown key name.
public static class PreferenceRules
{
    public static bool TryParseUpdate(JsonElement body, out PreferenceUpdate update, out PreferenceValidationError? error)
    {
        update = new PreferenceUpdate();
        error = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            error = new PreferenceValidationError("", "The body must be a JSON object.");
            return false;
        }

        string? theme = null, fontSize = null;
        bool? inspector = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            var key = property.Name;
            if (!PreferenceKeys.All.Contains(key))
            {
                // Never echo a client-supplied name: only allowlisted keys appear in errors.
                error = new PreferenceValidationError("", "Unknown preference.");
                return false;
            }

            if (!seen.Add(key))
            {
                error = new PreferenceValidationError(key, "Preference given more than once.");
                return false;
            }

            var value = property.Value;
            switch (key)
            {
                case PreferenceKeys.Theme when value.ValueKind == JsonValueKind.String && ThemeValues.All.Contains(value.GetString()!):
                    theme = value.GetString();
                    break;
                case PreferenceKeys.FontSize when value.ValueKind == JsonValueKind.String && FontSizeValues.All.Contains(value.GetString()!):
                    fontSize = value.GetString();
                    break;
                case PreferenceKeys.InspectorOpenByDefault when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    inspector = value.GetBoolean();
                    break;
                default:
                    error = new PreferenceValidationError(key, key == PreferenceKeys.InspectorOpenByDefault
                        ? "Must be true or false."
                        : $"Must be one of: {string.Join(", ", key == PreferenceKeys.Theme ? ThemeValues.All : FontSizeValues.All)}.");
                    return false;
            }
        }

        update = new PreferenceUpdate(theme, fontSize, inspector);
        if (update.IsEmpty)
        {
            error = new PreferenceValidationError("", "At least one preference is required.");
            return false;
        }

        return true;
    }
}

// Owner-scoped storage. The owner ID always comes from the authenticated principal, never from the request.
public interface IUserPreferenceStore
{
    // The owner's preferences with defaults filled in; never null, even with no stored rows.
    Task<UserPreferences> GetAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    // Upserts the changed keys in one transaction and returns the full preferences after the update.
    Task<UserPreferences> UpdateAsync(Guid ownerUserId, PreferenceUpdate update, CancellationToken cancellationToken = default);
}
