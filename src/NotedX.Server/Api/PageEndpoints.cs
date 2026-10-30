using NotedX.Core.Notes;
using NotedX.Server.Realtime;

namespace NotedX.Server.Api;

internal static class PageEndpoints
{
    public static void MapPageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/pages", ListAsync);
        api.MapGet("/pages/{id:guid}", GetAsync);
        api.MapPost("/pages", CreateAsync);
        api.MapPut("/pages/{id:guid}", UpdateAsync);
        api.MapPut("/pages/{id:guid}/move", MoveAsync);
        api.MapDelete("/pages/{id:guid}", DeleteAsync);
        api.MapPost("/pages/{id:guid}/restore", RestoreAsync);
        api.MapGet("/pages/{id:guid}/revisions", RevisionsAsync);
        api.MapGet("/search", SearchAsync);
    }

    private static async Task<IResult> ListAsync(
        PageService pages,
        Guid? workspaceId,
        Guid? parentId,
        bool? includeDeleted,
        bool? favorites,
        CancellationToken cancellationToken)
    {
        var results = await pages.ListAsync(
            workspaceId,
            parentId,
            includeDeleted ?? false,
            favorites ?? false,
            cancellationToken);

        return Results.Ok(results.Select(PageContractMapper.ToSummary).ToArray());
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        PageService pages,
        bool? includeDeleted,
        CancellationToken cancellationToken)
    {
        var page = await pages.GetAsync(id, includeDeleted ?? false, cancellationToken);
        return page is null
            ? ApiResults.NotFound("That page was not found.")
            : Results.Ok(PageContractMapper.ToDetail(page));
    }

    private static async Task<IResult> CreateAsync(
        CreatePageRequest request,
        PageService pages,
        RealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (!PageContractMapper.TryMapBlocks(request.Blocks, out var blocks, out var error))
        {
            return ApiResults.BadRequest(error!);
        }

        try
        {
            var page = await pages.CreateAsync(
                new PageDraft(
                    request.WorkspaceId,
                    request.ParentId,
                    request.Title,
                    request.Icon,
                    blocks),
                cancellationToken);

            await notifier.PageChangedAsync(page.Id, "created");
            return Results.Created($"/api/pages/{page.Id}", PageContractMapper.ToDetail(page));
        }
        catch (PageValidationException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdatePageRequest request,
        PageService pages,
        RealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (!PageContractMapper.TryMapBlocks(request.Blocks, out var blocks, out var error))
        {
            return ApiResults.BadRequest(error!);
        }

        try
        {
            var page = await pages.UpdateAsync(
                id,
                new PageChanges(request.Title, request.Icon, request.IsFavorite, blocks, request.Tags),
                cancellationToken);

            if (page is null)
            {
                return ApiResults.NotFound("That page was not found.");
            }

            var updated = await pages.GetAsync(id, false, cancellationToken);
            await notifier.PageChangedAsync(id, "updated");

            return updated is null
                ? ApiResults.NotFound("That page was removed while it was being saved.")
                : Results.Ok(PageContractMapper.ToDetail(updated));
        }
        catch (PageValidationException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> MoveAsync(
        Guid id,
        MovePageRequest request,
        PageService pages,
        RealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await pages.MoveAsync(id, request.ParentId, request.Position, cancellationToken);
            if (page is null)
            {
                return ApiResults.NotFound("That page was not found.");
            }

            await notifier.PageChangedAsync(id, "updated");
            return Results.Ok(PageContractMapper.ToSummary(page));
        }
        catch (PageValidationException ex)
        {
            return ApiResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        bool? permanent,
        PageService pages,
        RealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        if (permanent == true)
        {
            var deleted = await pages.DeletePermanentlyAsync(id, cancellationToken);
            if (!deleted)
            {
                return ApiResults.NotFound("That trashed page was not found.");
            }

            await notifier.PageChangedAsync(id, "deleted");
            return Results.NoContent();
        }

        var trashed = await pages.SoftDeleteAsync(id, cancellationToken);
        if (!trashed)
        {
            return ApiResults.NotFound("That page was not found or is already in the trash.");
        }

        await notifier.PageChangedAsync(id, "trashed");
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreAsync(
        Guid id,
        PageService pages,
        RealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var restored = await pages.RestoreAsync(id, cancellationToken);
        if (!restored)
        {
            return ApiResults.NotFound("That trashed page was not found.");
        }

        await notifier.PageChangedAsync(id, "restored");
        return Results.NoContent();
    }

    private static async Task<IResult> RevisionsAsync(
        Guid id,
        int? limit,
        PageService pages,
        CancellationToken cancellationToken)
    {
        var page = await pages.GetAsync(id, includeDeleted: true, cancellationToken);
        if (page is null)
        {
            return ApiResults.NotFound("That page was not found.");
        }

        var revisions = await pages.GetRevisionsAsync(id, limit ?? PageService.MaxRevisionsPerPage, cancellationToken);
        return Results.Ok(revisions
            .Select(revision => new RevisionDto(revision.Id, revision.CreatedAt, revision.ContentSnapshot))
            .ToArray());
    }

    private static async Task<IResult> SearchAsync(
        PageService pages,
        string? q,
        int? limit,
        CancellationToken cancellationToken)
    {
        var hits = await pages.SearchAsync(q, limit ?? 30, cancellationToken);
        return Results.Ok(hits);
    }
}
