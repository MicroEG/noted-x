using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotedX.Core.Data;
using NotedX.Core.Domain;

namespace NotedX.Core.Notes;

/// <summary>Creates, reads, updates, trashes, and searches pages and their blocks.</summary>
public sealed class PageService
{
    /// <summary>Maximum number of revisions retained per page.</summary>
    public const int MaxRevisionsPerPage = 50;

    private const int MaxTitleLength = 500;
    private const int MaxIconLength = 64;
    private const int MaxTagNameLength = 64;
    private const int MaxTagsPerPage = 20;
    private const double PositionSpacing = 1024d;

    private const string SearchSql = """
        SELECT p."Id" AS "Id",
               p."Title" AS "Title",
               p."Icon" AS "Icon",
               snippet("PageSearch", 2, '', '', '...', 12) AS "Snippet",
               p."UpdatedAt" AS "UpdatedAt"
        FROM "PageSearch"
        JOIN "Pages" p ON p."Id" = "PageSearch"."pageId"
        WHERE "PageSearch" MATCH {0} AND p."IsDeleted" = 0
        ORDER BY rank
        LIMIT {1}
        """;

    private readonly IDbContextFactory<NotedXDbContext> _contextFactory;
    private readonly ILogger<PageService>? _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a page service over the supplied database context factory.</summary>
    public PageService(
        IDbContextFactory<NotedXDbContext> contextFactory,
        ILogger<PageService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Lists pages, optionally restricted to a workspace, parent, favorites, or the trash.</summary>
    public async Task<IReadOnlyList<Page>> ListAsync(
        Guid? workspaceId = null,
        Guid? parentId = null,
        bool includeDeleted = false,
        bool favoritesOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Pages.AsNoTracking();

        if (workspaceId is { } workspace)
        {
            query = query.Where(p => p.WorkspaceId == workspace);
        }

        if (parentId is { } parent)
        {
            query = query.Where(p => p.ParentId == parent);
        }

        if (!includeDeleted)
        {
            query = query.Where(p => !p.IsDeleted);
        }

        if (favoritesOnly)
        {
            query = query.Where(p => p.IsFavorite);
        }

        return await query
            .OrderBy(p => p.Position)
            .ThenBy(p => p.Title)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Loads a page with ordered blocks, tags, and attachments.</summary>
    public async Task<Page?> GetAsync(
        Guid id,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await db.Pages
            .AsNoTracking()
            .Include(p => p.Blocks.OrderBy(b => b.Position))
            .Include(p => p.PageTags)
                .ThenInclude(pt => pt.Tag)
            .Include(p => p.Attachments)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (page is null || (!includeDeleted && page.IsDeleted))
        {
            return null;
        }

        return page;
    }

    /// <summary>Returns the most recent revisions of a page, newest first.</summary>
    public async Task<IReadOnlyList<Revision>> GetRevisionsAsync(
        Guid pageId,
        int limit = MaxRevisionsPerPage,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxRevisionsPerPage);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var revisions = await db.Revisions
            .AsNoTracking()
            .Where(r => r.PageId == pageId)
            .ToListAsync(cancellationToken);

        return revisions
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Creates a page and its blocks.</summary>
    /// <exception cref="PageValidationException">The draft is not valid.</exception>
    public async Task<Page> CreateAsync(PageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var blocks = draft.Blocks ?? Array.Empty<BlockDraft>();
        PageContent.ValidateBlocks(blocks);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var workspaceId = await ResolveWorkspaceAsync(db, draft.WorkspaceId, cancellationToken);
        var parentId = await ValidateParentAsync(db, draft.ParentId, workspaceId, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var position = await NextPositionAsync(db, workspaceId, parentId, cancellationToken);

        var page = new Page
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ParentId = parentId,
            Title = NormalizeTitle(draft.Title),
            Icon = NormalizeIcon(draft.Icon),
            Position = position,
            CreatedAt = now,
            UpdatedAt = now
        };

        var index = 0;
        foreach (var block in blocks)
        {
            page.Blocks.Add(CreateBlock(page.Id, block, index++));
        }

        page.SearchText = PageContent.BuildSearchText(page.Title, page.Blocks);

        db.Pages.Add(page);
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    /// <summary>Applies a partial update, recording a revision when content changes.</summary>
    /// <returns>The updated page, or <c>null</c> when it does not exist or is trashed.</returns>
    /// <exception cref="PageValidationException">The change is not valid.</exception>
    public async Task<Page?> UpdateAsync(
        Guid id,
        PageChanges changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Blocks is not null)
        {
            PageContent.ValidateBlocks(changes.Blocks);
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages
            .Include(p => p.Blocks)
            .Include(p => p.PageTags)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);

        if (page is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var contentChanged = false;

        if (changes.Title is not null)
        {
            var title = NormalizeTitle(changes.Title);
            if (!string.Equals(page.Title, title, StringComparison.Ordinal))
            {
                page.Title = title;
                contentChanged = true;
            }
        }

        if (changes.Icon is not null)
        {
            page.Icon = NormalizeIcon(changes.Icon);
        }

        if (changes.IsFavorite is { } favorite)
        {
            page.IsFavorite = favorite;
        }

        if (changes.Blocks is not null)
        {
            db.Blocks.RemoveRange(page.Blocks);
            page.Blocks.Clear();

            var index = 0;
            foreach (var block in changes.Blocks)
            {
                var created = CreateBlock(page.Id, block, index++);
                page.Blocks.Add(created);
                db.Blocks.Add(created);
            }

            contentChanged = true;
        }

        if (changes.Tags is not null)
        {
            await ApplyTagsAsync(db, page, changes.Tags, cancellationToken);
        }

        if (contentChanged)
        {
            page.SearchText = PageContent.BuildSearchText(page.Title, page.Blocks);
            page.UpdatedAt = now;
            db.Revisions.Add(new Revision
            {
                Id = Guid.NewGuid(),
                PageId = page.Id,
                ContentSnapshot = PageContent.SerializeSnapshot(page),
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        if (contentChanged)
        {
            await TrimRevisionsAsync(db, page.Id, cancellationToken);
        }

        return page;
    }

    /// <summary>Moves a page to a new parent and position within its workspace.</summary>
    /// <returns>The moved page, or <c>null</c> when it does not exist or is trashed.</returns>
    /// <exception cref="PageValidationException">The move would create a cycle or cross workspaces.</exception>
    public async Task<Page?> MoveAsync(
        Guid id,
        Guid? parentId,
        double? position,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (page is null)
        {
            return null;
        }

        if (parentId is { } parent)
        {
            if (parent == id)
            {
                throw new PageValidationException("A page cannot be its own parent.");
            }

            var parentPage = await db.Pages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == parent, cancellationToken);

            if (parentPage is null || parentPage.IsDeleted)
            {
                throw new PageValidationException("The target parent page was not found.");
            }

            if (parentPage.WorkspaceId != page.WorkspaceId)
            {
                throw new PageValidationException("Pages cannot be moved between workspaces.");
            }

            var pages = await db.Pages
                .AsNoTracking()
                .Where(p => p.WorkspaceId == page.WorkspaceId)
                .ToListAsync(cancellationToken);

            if (CollectSubtree(pages, id).Contains(parent))
            {
                throw new PageValidationException("A page cannot be moved inside one of its descendants.");
            }
        }

        page.ParentId = parentId;
        page.Position = position ?? await NextPositionAsync(db, page.WorkspaceId, parentId, cancellationToken);
        page.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    /// <summary>Moves a page and all of its descendants to the trash.</summary>
    /// <returns><c>true</c> when a live page was trashed.</returns>
    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var target = await db.Pages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (target is null || target.IsDeleted)
        {
            return false;
        }

        var pages = await db.Pages
            .Where(p => p.WorkspaceId == target.WorkspaceId)
            .ToListAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var subtree = CollectSubtree(pages, id);
        foreach (var page in pages.Where(p => subtree.Contains(p.Id)))
        {
            if (page.IsDeleted)
            {
                continue;
            }

            page.IsDeleted = true;
            page.DeletedAt = now;
            page.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Restores a trashed page and its descendants.</summary>
    /// <returns><c>true</c> when a trashed page was restored.</returns>
    public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var target = await db.Pages.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (target is null || !target.IsDeleted)
        {
            return false;
        }

        var pages = await db.Pages
            .Where(p => p.WorkspaceId == target.WorkspaceId)
            .ToListAsync(cancellationToken);

        var subtree = CollectSubtree(pages, id);
        var now = _timeProvider.GetUtcNow();

        foreach (var page in pages.Where(p => subtree.Contains(p.Id)))
        {
            page.IsDeleted = false;
            page.DeletedAt = null;
            page.UpdatedAt = now;
        }

        if (target.ParentId is { } parentId)
        {
            var parent = pages.FirstOrDefault(p => p.Id == parentId);
            if (parent is null || parent.IsDeleted)
            {
                target.ParentId = null;
                target.Position = pages
                    .Where(p => p.ParentId is null)
                    .Select(p => p.Position)
                    .DefaultIfEmpty(0d)
                    .Max() + PositionSpacing;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Permanently deletes a trashed page, its descendants, and its revisions.</summary>
    /// <returns><c>true</c> when a trashed page was deleted.</returns>
    public async Task<bool> DeletePermanentlyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken);
        if (page is null)
        {
            return false;
        }

        db.Pages.Remove(page);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Runs a full-text search over live pages.</summary>
    public async Task<IReadOnlyList<PageSearchHit>> SearchAsync(
        string? query,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var matchQuery = PageContent.BuildSearchQuery(query);
        if (matchQuery is null)
        {
            return Array.Empty<PageSearchHit>();
        }

        limit = Math.Clamp(limit, 1, 50);

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.Database
                .SqlQueryRaw<SearchRow>(SearchSql, matchQuery, limit)
                .ToListAsync(cancellationToken);

            return rows
                .Select(row => new PageSearchHit(row.Id, row.Title, row.Icon, row.Snippet ?? string.Empty, row.UpdatedAt))
                .ToArray();
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex, "The full-text search query could not be executed.");
            return Array.Empty<PageSearchHit>();
        }
    }

    private async Task<Guid> ResolveWorkspaceAsync(
        NotedXDbContext db,
        Guid? requestedWorkspaceId,
        CancellationToken cancellationToken)
    {
        if (requestedWorkspaceId is { } workspaceId)
        {
            if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken))
            {
                throw new PageValidationException("The workspace was not found.");
            }

            return workspaceId;
        }

        var existing = (await db.Workspaces
                .AsNoTracking()
                .Select(w => new { w.Id, w.CreatedAt })
                .ToListAsync(cancellationToken))
            .OrderBy(w => w.CreatedAt)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefault();

        if (existing is { } id)
        {
            return id;
        }

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "My Workspace",
            CreatedAt = _timeProvider.GetUtcNow()
        };

        db.Workspaces.Add(workspace);
        return workspace.Id;
    }

    private static async Task<Guid?> ValidateParentAsync(
        NotedXDbContext db,
        Guid? parentId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (parentId is not { } parent)
        {
            return null;
        }

        var parentPage = await db.Pages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == parent, cancellationToken);

        if (parentPage is null || parentPage.IsDeleted)
        {
            throw new PageValidationException("The parent page was not found.");
        }

        if (parentPage.WorkspaceId != workspaceId)
        {
            throw new PageValidationException("The parent page belongs to another workspace.");
        }

        return parent;
    }

    private static async Task<double> NextPositionAsync(
        NotedXDbContext db,
        Guid workspaceId,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        var maximum = await db.Pages
            .Where(p => p.WorkspaceId == workspaceId && p.ParentId == parentId)
            .MaxAsync(p => (double?)p.Position, cancellationToken);

        return (maximum ?? 0d) + PositionSpacing;
    }

    private static Block CreateBlock(Guid pageId, BlockDraft draft, int position) => new()
    {
        Id = Guid.NewGuid(),
        PageId = pageId,
        Type = draft.Type,
        Content = string.IsNullOrWhiteSpace(draft.Content) ? "{}" : draft.Content,
        Position = position
    };

    private static string NormalizeTitle(string? title)
    {
        var trimmed = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
    }

    private static string? NormalizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var trimmed = icon.Trim();
        return trimmed.Length <= MaxIconLength ? trimmed : trimmed[..MaxIconLength];
    }

    private static HashSet<Guid> CollectSubtree(IReadOnlyList<Page> pages, Guid rootId)
    {
        var children = pages
            .Where(p => p.ParentId.HasValue)
            .ToLookup(p => p.ParentId!.Value);

        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var child in children[current])
            {
                stack.Push(child.Id);
            }
        }

        return seen;
    }

    private static async Task ApplyTagsAsync(
        NotedXDbContext db,
        Page page,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var normalized = names
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > MaxTagsPerPage)
        {
            throw new PageValidationException($"A page cannot have more than {MaxTagsPerPage} tags.");
        }

        if (normalized.Any(name => name.Length > MaxTagNameLength))
        {
            throw new PageValidationException($"Tag names cannot exceed {MaxTagNameLength} characters.");
        }

        db.PageTags.RemoveRange(page.PageTags);
        page.PageTags.Clear();

        foreach (var name in normalized)
        {
            var tag = await db.Tags.FirstOrDefaultAsync(
                t => t.WorkspaceId == page.WorkspaceId && t.Name == name,
                cancellationToken);

            if (tag is null)
            {
                tag = new Tag
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = page.WorkspaceId,
                    Name = name
                };

                db.Tags.Add(tag);
            }

            var pageTag = new PageTag
            {
                PageId = page.Id,
                TagId = tag.Id,
                Tag = tag
            };

            page.PageTags.Add(pageTag);
            db.PageTags.Add(pageTag);
        }
    }

    private static async Task TrimRevisionsAsync(
        NotedXDbContext db,
        Guid pageId,
        CancellationToken cancellationToken)
    {
        var revisions = await db.Revisions
            .Where(r => r.PageId == pageId)
            .Select(r => new { r.Id, r.CreatedAt })
            .ToListAsync(cancellationToken);

        if (revisions.Count <= MaxRevisionsPerPage)
        {
            return;
        }

        var staleIds = revisions
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(MaxRevisionsPerPage)
            .Select(r => r.Id)
            .ToArray();

        await db.Revisions
            .Where(r => staleIds.Contains(r.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private sealed class SearchRow
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Icon { get; set; }

        public string? Snippet { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
