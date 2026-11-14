using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Library;
using Melodex.Core.Playlists;
using Melodex.Core.Tools;

namespace Melodex.Core.Api;

public static class ToolEndpoints
{
    public static RouteGroupBuilder MapToolEndpoints(this RouteGroupBuilder api)
    {
        var imports = api.MapGroup("/imports").WithTags("Imports").RequireScope(Scopes.Request);

        imports.MapPost("/peek", (PeekImport body) => Refusable(() => Task.FromResult(Results.Ok(ImportService.Peek(body)))))
            .WithName("PeekImport")
            .WithSummary("Read a list without importing it: what kind it is, how many songs it has, and for a CSV, what each column looks to hold.")
            .Produces<ImportPeek>().ProducesValidationProblem();

        imports.MapGet("", async (HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, ct)) : NoOwner())
            .WithName("ListImports")
            .WithSummary("Your imports, newest first.")
            .Produces<IReadOnlyList<ImportSummary>>();

        imports.MapPost("", async (CreateImport body, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => { var made = await service.CreateAsync(user, body, ct); return Results.Created($"/api/v1/imports/{made.Id}", made); })
                    : NoOwner())
            .WithName("CreateImport")
            .WithSummary("Import a list: an M3U or M3U8 playlist, a CSV, or lines of \"Artist – Title\". Its songs are matched in the background, as a task in Activity.")
            .Produces<ImportSummary>(StatusCodes.Status201Created).ProducesValidationProblem();

        imports.MapGet("/unmatched", async (HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.UnmatchedAsync(user, ct)) : NoOwner())
            .WithName("ListUnmatchedImportSongs")
            .WithSummary("Imported songs nothing was found for, waiting in Review to be matched or dismissed.")
            .Produces<IReadOnlyList<UnmatchedImportSong>>();

        imports.MapPost("/unmatched/{lineId:guid}/match", async (Guid lineId, MatchImportSong body, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.MatchUnmatchedAsync(user, Caller.Of(http).Requester, lineId, body.TrackId, ct) ? Results.NoContent() : Results.NotFound())
                    : NoOwner())
            .WithName("MatchUnmatchedImportSong")
            .WithSummary("Say which song an unmatched one is: it goes into its import's playlist in its place, and is requested if the import asked for that.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).ProducesValidationProblem();

        imports.MapDelete("/unmatched/{lineId:guid}", async (Guid lineId, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.DismissUnmatchedAsync(user, lineId, ct) ? Results.NoContent() : Results.NotFound() : NoOwner())
            .WithName("DismissUnmatchedImportSong")
            .WithSummary("Take an unmatched song out of Review without matching it.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        imports.MapGet("/{id:guid}", async (Guid id, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.GetAsync(user, id, ct) is { } import ? Results.Ok(import) : Results.NotFound() : NoOwner())
            .WithName("GetImport")
            .WithSummary("An import and its songs, each with what it was matched to.")
            .Produces<ImportDetail>().Produces(StatusCodes.Status404NotFound);

        imports.MapPut("/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, ChangeImportLine body, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.ChangeLineAsync(user, id, lineId, body, ct) is { } line ? Results.Ok(line) : Results.NotFound())
                    : NoOwner())
            .WithName("ChangeImportLine")
            .WithSummary("Choose which song one of an import's is, or leave it out, or put it back, before the import is applied.")
            .Produces<ImportLineInfo>().Produces(StatusCodes.Status404NotFound).ProducesValidationProblem();

        imports.MapPost("/{id:guid}/apply", async (Guid id, ApplyImport body, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () =>
                        await service.ApplyAsync(user, Caller.Of(http).Requester, Caller.Of(http).May(Scopes.Listen), id, body, ct) is { } applied ? Results.Ok(applied) : Results.NotFound())
                    : NoOwner())
            .WithName("ApplyImport")
            .WithSummary("Apply an import: make a playlist of its songs or add them to one, request what you don't own, and send what wasn't found to Review.")
            .Produces<ImportApplied>().Produces(StatusCodes.Status404NotFound).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);

        imports.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ImportService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.DeleteAsync(user, id, ct) ? Results.NoContent() : Results.NotFound() : NoOwner())
            .WithName("DeleteImport")
            .WithSummary("Delete an import, stopping its matching. A playlist it made stays, as do its requests.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        var exports = api.MapGroup("/exports").WithTags("Exports");

        exports.MapGet("/playlists", async (Guid[] id, PlaylistExportFormat? format, bool? keepMissing, string? prefix, HttpContext http, ExportService service, CancellationToken ct) =>
                Owner(http) is { } user
                    ? await Refusable(async () => await service.PlaylistsAsync(user, id, format ?? PlaylistExportFormat.M3u8, keepMissing ?? false, prefix, ct) is { } file
                        ? Results.File(file.Bytes, file.ContentType, file.Name)
                        : Results.NotFound())
                    : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("ExportPlaylists")
            .WithSummary("Export your playlists: one as an M3U8 or CSV file, several as a zip of them. Songs you don't have are left out unless keepMissing; an M3U8's paths are relative to the music folder, or under prefix.")
            .Produces<byte[]>(contentType: "application/octet-stream").Produces(StatusCodes.Status404NotFound).ProducesValidationProblem();

        exports.MapGet("/library", async (LibraryExportRows? rows, LibraryExportColumn[] column, ExportService service, CancellationToken ct) =>
                await Refusable(async () =>
                {
                    var file = await service.LibraryAsync(rows ?? LibraryExportRows.Songs, column, ct);
                    return Results.File(file.Bytes, file.ContentType, file.Name);
                }))
            .RequireScope(Scopes.Read)
            .WithName("ExportLibrary")
            .WithSummary("Export the library as a CSV: a row for each song you have, or each album you have some of, with the columns asked for (all of them if none are).")
            .Produces<byte[]>(contentType: "text/csv").ProducesValidationProblem();

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Imports belong to a user, and this key doesn't: make a new one.", statusCode: StatusCodes.Status403Forbidden, title: "No user");

    private static async Task<IResult> Refusable(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ImportRefusedException e)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
        }
        catch (PlaylistRefusedException e)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
        }
        catch (LibraryBusyException e)
        {
            return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is being scanned");
        }
    }
}
