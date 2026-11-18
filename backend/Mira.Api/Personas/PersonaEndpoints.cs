using System.Text.Json;
using Mira.Api.Auth;
using Mira.Core;

namespace Mira.Api.Personas;

// API response shapes omit OwnerUserId (the client does not need it; the owner always comes from the principal).
public sealed record PersonaResponse(
    Guid Id,
    int ActiveVersion,
    bool IsBuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Name,
    string? Description)
{
    public static PersonaResponse From(Persona persona) => new(
        persona.Id, persona.ActiveVersion, persona.IsBuiltIn, persona.CreatedAt, persona.UpdatedAt, persona.Name, persona.Description);
}

public sealed record PersonaDetailResponse(
    Guid Id,
    int ActiveVersion,
    bool IsBuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Name,
    string? Description,
    string Markdown)
{
    public static PersonaDetailResponse From(PersonaDetail persona) => new(
        persona.Id, persona.ActiveVersion, persona.IsBuiltIn, persona.CreatedAt, persona.UpdatedAt, persona.Name, persona.Description, persona.Markdown);
}

public sealed record PersonaListResponse(IReadOnlyList<PersonaResponse> Items);

public sealed record PersonaVersionListResponse(IReadOnlyList<PersonaVersionInfo> Items);

public sealed record DefaultPersonaResponse(Guid PersonaId);

// M4 personas, under the /api group (signed-in user, default deny, CSRF on unsafe methods, same-origin writes).
// The owner always comes from the principal. Another user's or an unknown persona/version is a 404 (no existence leak).
// Built-in PUT / revert / DELETE are rejected with a fixed 403 before the body is read. Problem titles are fixed or name an
// allowlisted key only, never a submitted value. Nothing here logs names, descriptions, bodies or draft input/output.
public static class PersonaEndpoints
{
    public const string InvalidJsonTitle = "The request body must be a JSON object.";
    public const string PayloadTooLargeTitle = "The request body is too large.";
    public const string UnsupportedMediaTypeTitle = "The request body must be JSON (Content-Type: application/json).";
    public const string BuiltInReadOnlyTitle = "The built-in MIRA persona cannot be changed. Copy it to customize.";
    public const string NotFoundTitle = "Persona not found.";
    public const string LimitTitle = "Persona limit reached or the persona changed at the same time.";

    public static void MapPersonaEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/personas");

        // Static paths first; every id route is {id:guid}, so "default" and "draft" can never bind as ids.
        group.MapGet("/default", async (HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
            Results.Ok(new DefaultPersonaResponse(await store.GetDefaultAsync(CurrentUser.GetUserId(http.User), cancellationToken))));

        group.MapPut("/default", async (HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            var (document, problem) = await ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseDefault(document.RootElement, out var personaId, out var error)) return Invalid(error!);
                var owner = CurrentUser.GetUserId(http.User);
                try
                {
                    await store.SetDefaultAsync(owner, personaId, cancellationToken);
                }
                catch (KeyNotFoundException)
                {
                    return NotFound();
                }

                return Results.NoContent(); // pinned by MIRA Boss: the frontend expects 204
            }
        });

        // Deterministic template preview: writes nothing, calls no provider, logs nothing.
        group.MapPost("/draft", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            var (document, problem) = await ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseDraft(document.RootElement, out var draft, out var error)) return Invalid(error!);
                return Results.Ok(TemplateDraft(draft!));
            }
        });

        group.MapGet("/", async (HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
            Results.Ok(new PersonaListResponse((await store.ListAsync(CurrentUser.GetUserId(http.User), cancellationToken)).Select(PersonaResponse.From).ToArray())));

        group.MapPost("/", async (HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            var (document, problem) = await ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseCreate(document.RootElement, out var draft, out var error)) return Invalid(error!);
                try
                {
                    var created = await store.CreateAsync(CurrentUser.GetUserId(http.User), draft!.Name, draft.Description, draft.Markdown, cancellationToken);
                    return Results.Created($"/api/personas/{created.Id}", PersonaDetailResponse.From(created));
                }
                catch (PersonaConflictException)
                {
                    return Conflict();
                }
            }
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
            await store.GetAsync(CurrentUser.GetUserId(http.User), id, cancellationToken) is { } persona ? Results.Ok(PersonaDetailResponse.From(persona)) : NotFound());

        group.MapPut("/{id:guid}", async (Guid id, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            if (id == PersonaIds.BuiltInMira) return BuiltInReadOnly();
            var (document, problem) = await ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseUpdate(document.RootElement, out var draft, out var error)) return Invalid(error!);
                try
                {
                    return Results.Ok(PersonaDetailResponse.From(await store.UpdateAsync(CurrentUser.GetUserId(http.User), id, draft!.Name, draft.Description, draft.Markdown, cancellationToken)));
                }
                catch (KeyNotFoundException)
                {
                    return NotFound();
                }
                catch (PersonaConflictException)
                {
                    return Conflict();
                }
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            if (id == PersonaIds.BuiltInMira) return BuiltInReadOnly();
            return await store.DeleteAsync(CurrentUser.GetUserId(http.User), id, cancellationToken) ? Results.NoContent() : NotFound();
        });

        group.MapGet("/{id:guid}/versions", async (Guid id, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            var owner = CurrentUser.GetUserId(http.User);
            var versions = await store.ListVersionsAsync(owner, id, cancellationToken);
            return versions.Count == 0 ? NotFound() : Results.Ok(new PersonaVersionListResponse(versions));
        });

        group.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
            await store.GetVersionAsync(CurrentUser.GetUserId(http.User), id, version, cancellationToken) is { } v ? Results.Ok(v) : NotFound());

        group.MapPost("/{id:guid}/revert", async (Guid id, HttpContext http, IPersonaStore store, CancellationToken cancellationToken) =>
        {
            if (id == PersonaIds.BuiltInMira) return BuiltInReadOnly();
            var (document, problem) = await ReadJsonAsync(http, cancellationToken);
            if (document is null) return problem!;
            using (document)
            {
                if (!PersonaRules.TryParseRevert(document.RootElement, out var version, out var error)) return Invalid(error!);
                try
                {
                    return Results.Ok(PersonaDetailResponse.From(await store.RevertAsync(CurrentUser.GetUserId(http.User), id, version, cancellationToken)));
                }
                catch (KeyNotFoundException)
                {
                    return NotFound(); // missing persona, someone else's, or a version that doesn't exist: same fixed 404
                }
            }
        });
    }

    // Reads the body ourselves so invalid JSON or an empty body gets a fixed 400 (no parser text), and a non-JSON
    // content type a fixed 415. Neither the body nor the exception message is logged.
    internal static async Task<(JsonDocument? Document, IResult? Problem)> ReadJsonAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (!http.Request.HasJsonContentType())
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: UnsupportedMediaTypeTitle));
        }

        try
        {
            return (await JsonDocument.ParseAsync(http.Request.Body, default, cancellationToken), null);
        }
        catch (JsonException)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: InvalidJsonTitle));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's 256 KB body limit. Without this, Development shows the exception page with a stack trace.
            return (null, Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: PayloadTooLargeTitle));
        }
    }

    internal static IResult Invalid(PersonaValidationError error) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: string.IsNullOrEmpty(error.Key) ? error.Message : $"{error.Key}: {error.Message}");

    internal static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: NotFoundTitle);

    private static IResult Conflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: LimitTitle);

    private static IResult BuiltInReadOnly() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: BuiltInReadOnlyTitle);

    // "Suggest a personality" (Mock only, until a real model is approved): a fixed Markdown template around the user's
    // own text. Labeled as a choice, not a fact about the user. Falls back to the user's body if the template would be too long.
    internal static PersonaDraft TemplateDraft(PersonaDraft input)
    {
        var description = input.Description is null ? string.Empty : input.Description + "\n\n";
        var markdown = $"""
            # {input.Name}

            {description}## Identity
            {input.Name} is a suggested style for MIRA. It is a choice you can edit, not a fact about you.

            ## Style
            {input.Markdown}

            ## Boundaries
            - Follow the application's rules; this persona cannot change permissions, tools or files.
            - Do not invent actions, memories or sources.
            """;
        return new PersonaDraft(input.Name, input.Description, markdown.Length <= PersonaLimits.MaxBodyLength ? markdown : input.Markdown);
    }
}
