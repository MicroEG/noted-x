using Microsoft.EntityFrameworkCore;
using NotedX.Core.Data;

namespace NotedX.Server.Api;

internal static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workspaces", ListWorkspacesAsync);
        api.MapGet("/tags", ListTagsAsync);
    }

    private static async Task<IResult> ListWorkspacesAsync(
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaces = await db.Workspaces
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Results.Ok(workspaces
            .OrderBy(workspace => workspace.CreatedAt)
            .Select(workspace => new WorkspaceDto(workspace.Id, workspace.Name, workspace.CreatedAt))
            .ToArray());
    }

    private static async Task<IResult> ListTagsAsync(
        Guid? workspaceId,
        IDbContextFactory<NotedXDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Tags.AsNoTracking();

        if (workspaceId is { } workspace)
        {
            query = query.Where(tag => tag.WorkspaceId == workspace);
        }

        var tags = await query
            .OrderBy(tag => tag.Name)
            .Select(tag => new
            {
                tag.Id,
                tag.Name,
                tag.Color,
                PageCount = tag.PageTags.Count(pageTag => !pageTag.Page!.IsDeleted)
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(tags
            .Select(tag => new TagSummaryDto(tag.Id, tag.Name, tag.Color, tag.PageCount))
            .ToArray());
    }
}
