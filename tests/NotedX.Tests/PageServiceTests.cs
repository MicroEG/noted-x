using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NotedX.Core.Data;
using NotedX.Core.Domain;
using NotedX.Core.Notes;

namespace NotedX.Tests;

public sealed class PageServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_applies_defaults_and_indexes_content()
    {
        await using var fixture = await Fixture.CreateAsync();

        var page = await fixture.Pages.CreateAsync(new PageDraft(
            WorkspaceId: null,
            ParentId: null,
            Title: "  Meeting notes  ",
            Icon: null,
            Blocks: [new BlockDraft(BlockType.Paragraph, """{"text":"Budget review"}""")]));

        Assert.Equal("Meeting notes", page.Title);
        Assert.Null(page.Icon);
        Assert.Null(page.ParentId);
        Assert.Equal(1024d, page.Position);
        Assert.Equal(Start, page.CreatedAt);
        Assert.Equal(Start, page.UpdatedAt);
        Assert.Contains("Budget review", page.SearchText);
        Assert.Single(page.Blocks);
        Assert.Equal("""{"text":"Budget review"}""", page.Blocks.Single().Content);
        Assert.Equal(0, page.Blocks.Single().Position);

        await using var db = await fixture.Database.CreateDbContextAsync();
        Assert.Equal(1, await db.Workspaces.CountAsync());
        Assert.Equal(1, await db.Pages.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_defaults_empty_titles_and_clamps_long_values()
    {
        await using var fixture = await Fixture.CreateAsync();

        var untitled = await fixture.Pages.CreateAsync(
            new PageDraft(null, null, "   ", null, null));
        Assert.Equal("Untitled", untitled.Title);

        var longTitle = await fixture.Pages.CreateAsync(
            new PageDraft(null, null, new string('t', 600), null, null));
        Assert.Equal(500, longTitle.Title.Length);

        var longIcon = await fixture.Pages.CreateAsync(
            new PageDraft(null, null, "Icon", new string('i', 100), null));
        Assert.Equal(64, longIcon.Icon!.Length);

        var blankIcon = await fixture.Pages.CreateAsync(
            new PageDraft(null, null, "No icon", "   ", null));
        Assert.Null(blankIcon.Icon);
    }

    [Fact]
    public async Task CreateAsync_spaces_sibling_positions_and_orders_root_pages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Pages.CreateAsync(new PageDraft(null, null, "First", null, null));
        var second = await fixture.Pages.CreateAsync(new PageDraft(null, null, "Second", null, null));
        var child = await fixture.Pages.CreateAsync(
            new PageDraft(null, first.Id, "Child", null, null));

        Assert.Equal(1024d, first.Position);
        Assert.Equal(2048d, second.Position);
        Assert.Equal(1024d, child.Position);

        var rootPages = (await fixture.Pages.ListAsync())
            .Where(page => page.ParentId is null)
            .ToArray();
        Assert.Equal(new[] { "First", "Second" }, rootPages.Select(page => page.Title));

        var children = await fixture.Pages.ListAsync(parentId: first.Id);
        Assert.Single(children);
        Assert.Equal(child.Id, children[0].Id);
    }

    [Fact]
    public async Task CreateAsync_validates_blocks_workspaces_and_parents()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(null, null, "Bad JSON", null,
                [new BlockDraft(BlockType.Paragraph, "{")])));
        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(null, null, "Not an object", null,
                [new BlockDraft(BlockType.Paragraph, "[]")])));
        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(null, null, "Unknown type", null,
                [new BlockDraft((BlockType)99, "{}")])));
        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(Guid.NewGuid(), null, "Missing workspace", null, null)));
        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(null, Guid.NewGuid(), "Missing parent", null, null)));

        var empty = await fixture.Pages.CreateAsync(
            new PageDraft(null, null, "Empty", null, null));
        Assert.Empty(empty.Blocks);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_parent_from_another_workspace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var foreignPage = await fixture.InsertForeignPageAsync();

        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.CreateAsync(
            new PageDraft(null, foreignPage.Id, "Child", null, null)));
    }

    [Fact]
    public async Task ListAsync_filters_by_parent_favorites_and_trash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.CreateAsync("Parent");
        var child = await fixture.CreateAsync("Child", parent.Id);
        var other = await fixture.CreateAsync("Other");

        await fixture.Pages.UpdateAsync(
            child.Id,
            new PageChanges(null, null, true, null, null));

        Assert.Equal(3, (await fixture.Pages.ListAsync()).Count);

        var children = await fixture.Pages.ListAsync(parentId: parent.Id);
        Assert.Single(children);
        Assert.Equal(child.Id, children[0].Id);

        var favorites = await fixture.Pages.ListAsync(favoritesOnly: true);
        Assert.Single(favorites);
        Assert.Equal(child.Id, favorites[0].Id);

        Assert.True(await fixture.Pages.SoftDeleteAsync(other.Id));

        Assert.Equal(2, (await fixture.Pages.ListAsync()).Count);
        Assert.Equal(3, (await fixture.Pages.ListAsync(includeDeleted: true)).Count);
        Assert.Equal(3, (await fixture.Pages.ListAsync(
            workspaceId: parent.WorkspaceId,
            includeDeleted: true)).Count);
        Assert.Empty(await fixture.Pages.ListAsync(workspaceId: Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAsync_returns_ordered_blocks_and_hides_trash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreateAsync(
            "Blocks",
            blocks:
            [
                new BlockDraft(BlockType.Paragraph, """{"text":"one"}"""),
                new BlockDraft(BlockType.Paragraph, """{"text":"two"}""")
            ]);

        var loaded = await fixture.Pages.GetAsync(page.Id);

        Assert.NotNull(loaded);
        Assert.Equal(
            new[] { "one", "two" },
            loaded!.Blocks.Select(block => PageContent.ExtractText(block.Content)));
        Assert.Equal(new[] { 0, 1 }, loaded.Blocks.Select(block => block.Position));

        Assert.True(await fixture.Pages.SoftDeleteAsync(page.Id));
        Assert.Null(await fixture.Pages.GetAsync(page.Id));
        Assert.NotNull(await fixture.Pages.GetAsync(page.Id, includeDeleted: true));
        Assert.Null(await fixture.Pages.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateAsync_records_revisions_and_refreshes_search()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreateAsync("Draft");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await fixture.Pages.UpdateAsync(page.Id, new PageChanges(
            "Final",
            null,
            null,
            [new BlockDraft(BlockType.Paragraph, """{"text":"Quarterly numbers"}""")],
            null));

        Assert.NotNull(updated);
        Assert.Equal("Final", updated!.Title);
        Assert.Equal(Start.AddMinutes(1), updated.UpdatedAt);
        Assert.Contains("Quarterly numbers", updated.SearchText);

        var revisions = await fixture.Pages.GetRevisionsAsync(page.Id);
        Assert.Single(revisions);
        Assert.Contains("Quarterly numbers", revisions[0].ContentSnapshot);

        var hits = await fixture.Pages.SearchAsync("Quarterly");
        Assert.Single(hits);
        Assert.Equal(page.Id, hits[0].Id);
        Assert.Empty(await fixture.Pages.SearchAsync("Draft"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var favorited = await fixture.Pages.UpdateAsync(
            page.Id,
            new PageChanges(null, null, true, null, null));

        Assert.True(favorited!.IsFavorite);
        Assert.Equal(Start.AddMinutes(1), favorited.UpdatedAt);
        Assert.Single(await fixture.Pages.GetRevisionsAsync(page.Id));
    }

    [Fact]
    public async Task UpdateAsync_returns_null_for_missing_or_trashed_pages()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(await fixture.Pages.UpdateAsync(
            Guid.NewGuid(),
            new PageChanges("Gone", null, null, null, null)));

        var page = await fixture.CreateAsync("Trashed");
        await fixture.Pages.SoftDeleteAsync(page.Id);

        Assert.Null(await fixture.Pages.UpdateAsync(
            page.Id,
            new PageChanges("Gone", null, null, null, null)));
    }

    [Fact]
    public async Task UpdateAsync_replaces_blocks_and_renumbers_them()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreateAsync(
            "Blocks",
            blocks:
            [
                new BlockDraft(BlockType.Paragraph, """{"text":"one"}"""),
                new BlockDraft(BlockType.Paragraph, """{"text":"two"}""")
            ]);

        var updated = await fixture.Pages.UpdateAsync(page.Id, new PageChanges(
            null,
            null,
            null,
            [new BlockDraft(BlockType.BulletedList, """{"text":"only"}""")],
            null));

        Assert.NotNull(updated);
        Assert.Single(updated!.Blocks);

        await using var db = await fixture.Database.CreateDbContextAsync();
        Assert.Equal(1, await db.Blocks.CountAsync());

        var loaded = await fixture.Pages.GetAsync(page.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Blocks);
        Assert.Equal(BlockType.BulletedList, loaded.Blocks.Single().Type);
        Assert.Equal(0, loaded.Blocks.Single().Position);
    }

    [Fact]
    public async Task UpdateAsync_manages_tags_without_duplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateAsync("Tagged one");
        var second = await fixture.CreateAsync("Tagged two");

        var tagged = await fixture.Pages.UpdateAsync(first.Id, new PageChanges(
            null,
            null,
            null,
            null,
            ["Work", "work", " ", "Ideas"]));

        Assert.NotNull(tagged);
        Assert.Equal(
            new[] { "Ideas", "Work" },
            tagged!.PageTags
                .Select(pageTag => pageTag.Tag!.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        var retagged = await fixture.Pages.UpdateAsync(first.Id, new PageChanges(
            null,
            null,
            null,
            null,
            ["Work"]));

        Assert.NotNull(retagged);
        Assert.Single(retagged!.PageTags);

        var secondPage = await fixture.Pages.UpdateAsync(second.Id, new PageChanges(
            null,
            null,
            null,
            null,
            ["work"]));

        Assert.NotNull(secondPage);

        await using (var db = await fixture.Database.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.Tags.CountAsync());
            var workTag = await db.Tags.SingleAsync(tag => tag.Name == "Work");
            Assert.Equal(2, await db.PageTags.CountAsync(pt => pt.TagId == workTag.Id));
        }

        var cleared = await fixture.Pages.UpdateAsync(first.Id, new PageChanges(
            null,
            null,
            null,
            null,
            Array.Empty<string>()));
        Assert.Empty(cleared!.PageTags);

        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.UpdateAsync(
            first.Id,
            new PageChanges(
                null,
                null,
                null,
                null,
                Enumerable.Range(0, 21).Select(index => $"tag-{index}").ToArray())));
        await Assert.ThrowsAsync<PageValidationException>(() => fixture.Pages.UpdateAsync(
            first.Id,
            new PageChanges(null, null, null, null, [new string('x', 65)])));
    }

    [Fact]
    public async Task MoveAsync_reparents_and_validates_the_tree()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.CreateAsync("Parent");
        var child = await fixture.CreateAsync("Child");
        var grandchild = await fixture.CreateAsync("Grandchild", child.Id);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var moved = await fixture.Pages.MoveAsync(child.Id, parent.Id, null);

        Assert.NotNull(moved);
        Assert.Equal(parent.Id, moved!.ParentId);
        Assert.Equal(1024d, moved.Position);
        Assert.Equal(Start.AddSeconds(5), moved.UpdatedAt);

        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Pages.MoveAsync(parent.Id, grandchild.Id, null));
        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Pages.MoveAsync(parent.Id, parent.Id, null));
        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Pages.MoveAsync(child.Id, Guid.NewGuid(), null));
        Assert.Null(await fixture.Pages.MoveAsync(Guid.NewGuid(), null, null));

        var reparented = await fixture.Pages.MoveAsync(child.Id, null, 500d);
        Assert.NotNull(reparented);
        Assert.Null(reparented!.ParentId);
        Assert.Equal(500d, reparented.Position);

        var foreignPage = await fixture.InsertForeignPageAsync();
        await Assert.ThrowsAsync<PageValidationException>(
            () => fixture.Pages.MoveAsync(child.Id, foreignPage.Id, null));
    }

    [Fact]
    public async Task SoftDeleteAsync_trashes_the_subtree_and_RestoreAsync_reparents_orphans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.CreateAsync("Root");
        var child = await fixture.CreateAsync("Child", parent.Id);
        var grandchild = await fixture.CreateAsync("Grandchild", child.Id);

        Assert.True(await fixture.Pages.SoftDeleteAsync(child.Id));
        Assert.False(await fixture.Pages.SoftDeleteAsync(child.Id));

        var live = await fixture.Pages.ListAsync();
        Assert.Single(live);
        Assert.Equal(parent.Id, live[0].Id);
        Assert.Null(await fixture.Pages.GetAsync(child.Id));
        Assert.NotNull(await fixture.Pages.GetAsync(child.Id, includeDeleted: true));
        Assert.NotNull(await fixture.Pages.GetAsync(grandchild.Id, includeDeleted: true));

        Assert.False(await fixture.Pages.RestoreAsync(parent.Id));
        Assert.True(await fixture.Pages.RestoreAsync(child.Id));
        Assert.NotNull(await fixture.Pages.GetAsync(grandchild.Id));
        Assert.Equal(parent.Id, (await fixture.Pages.GetAsync(child.Id))!.ParentId);

        Assert.True(await fixture.Pages.SoftDeleteAsync(parent.Id));
        Assert.True(await fixture.Pages.RestoreAsync(child.Id));

        var orphan = await fixture.Pages.GetAsync(child.Id);
        Assert.NotNull(orphan);
        Assert.Null(orphan!.ParentId);
        Assert.True(orphan.Position > parent.Position);
        Assert.Null(await fixture.Pages.GetAsync(parent.Id));

        Assert.False(await fixture.Pages.SoftDeleteAsync(Guid.NewGuid()));
        Assert.False(await fixture.Pages.RestoreAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeletePermanentlyAsync_only_removes_trashed_pages_and_their_data()
    {
        await using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.CreateAsync("Parent");
        var child = await fixture.CreateAsync("Child", parent.Id);
        await fixture.Pages.UpdateAsync(
            parent.Id,
            new PageChanges("Parent v2", null, null, null, null));

        Assert.False(await fixture.Pages.DeletePermanentlyAsync(parent.Id));
        Assert.True(await fixture.Pages.SoftDeleteAsync(parent.Id));
        Assert.True(await fixture.Pages.DeletePermanentlyAsync(parent.Id));

        await using var db = await fixture.Database.CreateDbContextAsync();
        Assert.Equal(0, await db.Pages.CountAsync());
        Assert.Equal(0, await db.Blocks.CountAsync());
        Assert.Equal(0, await db.Revisions.CountAsync());

        Assert.False(await fixture.Pages.DeletePermanentlyAsync(child.Id));
    }

    [Fact]
    public async Task GetRevisionsAsync_orders_newest_first_and_clamps_the_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreateAsync("Revisions");

        for (var version = 1; version <= 3; version++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Pages.UpdateAsync(
                page.Id,
                new PageChanges($"Version {version}", null, null, null, null));
        }

        var revisions = await fixture.Pages.GetRevisionsAsync(page.Id);
        Assert.Equal(3, revisions.Count);
        Assert.Contains("Version 3", revisions[0].ContentSnapshot);
        Assert.Contains("Version 1", revisions[2].ContentSnapshot);

        Assert.Equal(2, (await fixture.Pages.GetRevisionsAsync(page.Id, limit: 2)).Count);
        Assert.Single(await fixture.Pages.GetRevisionsAsync(page.Id, limit: 0));
        Assert.Equal(3, (await fixture.Pages.GetRevisionsAsync(page.Id, limit: 500)).Count);
    }

    [Fact]
    public async Task UpdateAsync_trims_history_to_fifty_revisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreateAsync("History");

        for (var version = 1; version <= 55; version++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Pages.UpdateAsync(
                page.Id,
                new PageChanges($"Revision {version}", null, null, null, null));
        }

        var revisions = await fixture.Pages.GetRevisionsAsync(page.Id);
        Assert.Equal(PageService.MaxRevisionsPerPage, revisions.Count);
        Assert.Contains("Revision 55", revisions[0].ContentSnapshot);
        Assert.Contains("Revision 6", revisions[^1].ContentSnapshot);

        await using var db = await fixture.Database.CreateDbContextAsync();
        Assert.Equal(PageService.MaxRevisionsPerPage, await db.Revisions.CountAsync());
    }

    [Fact]
    public async Task SearchAsync_matches_titles_and_body_text_and_skips_trash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var titled = await fixture.CreateAsync("Garden plans");
        var body = await fixture.CreateAsync(
            "Shopping",
            blocks: [new BlockDraft(BlockType.Paragraph, """{"text":"Garden hose and pots"}""")]);

        Assert.Equal(2, (await fixture.Pages.SearchAsync("garden")).Count);

        var basil = await fixture.Pages.SearchAsync("basil");
        Assert.Empty(basil);

        await fixture.Pages.UpdateAsync(
            titled.Id,
            new PageChanges(
                null,
                null,
                null,
                [new BlockDraft(BlockType.Paragraph, """{"text":"Tomatoes and basil"}""")],
                null));

        basil = await fixture.Pages.SearchAsync("basil");
        Assert.Single(basil);
        Assert.Equal(titled.Id, basil[0].Id);

        await fixture.Pages.SoftDeleteAsync(body.Id);
        Assert.Single(await fixture.Pages.SearchAsync("garden"));

        Assert.Empty(await fixture.Pages.SearchAsync(null));
        Assert.Empty(await fixture.Pages.SearchAsync("   "));
        Assert.Single(await fixture.Pages.SearchAsync("garden", limit: 1));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TestDatabase database)
        {
            Database = database;
            Clock = new ManualTimeProvider(Start);
            Pages = new PageService(database, NullLogger<PageService>.Instance, Clock);
        }

        public TestDatabase Database { get; }

        public ManualTimeProvider Clock { get; }

        public PageService Pages { get; }

        public static async Task<Fixture> CreateAsync() =>
            new(await TestDatabase.CreateAsync());

        public Task<Page> CreateAsync(
            string title,
            Guid? parentId = null,
            params BlockDraft[] blocks) =>
            Pages.CreateAsync(new PageDraft(
                null,
                parentId,
                title,
                null,
                blocks.Length == 0 ? null : blocks));

        public async Task<Page> InsertForeignPageAsync()
        {
            await using var db = await Database.CreateDbContextAsync();
            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = "Other workspace",
                CreatedAt = Start.AddYears(10)
            };

            var page = new Page
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                Title = "Foreign",
                SearchText = "Foreign",
                CreatedAt = Start,
                UpdatedAt = Start
            };

            db.Workspaces.Add(workspace);
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            return page;
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
