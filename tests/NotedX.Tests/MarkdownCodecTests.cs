using System.Text.Json;
using NotedX.Core.Domain;
using NotedX.Core.Notes;

namespace NotedX.Tests;

public sealed class MarkdownCodecTests
{
    [Fact]
    public void Parse_extracts_a_leading_heading_as_the_title()
    {
        const string markdown = "\uFEFF# Roadmap\r\n\r\nFirst paragraph.\r\n\r\n## Phase 1\r\n- Ship\r\n";

        var document = MarkdownCodec.Parse(markdown, "Fallback");

        Assert.Equal("Roadmap", document.Title);
        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(BlockType.Paragraph, document.Blocks[0].Type);
        Assert.Equal("First paragraph.", Text(document.Blocks[0]));
        Assert.Equal(BlockType.Heading2, document.Blocks[1].Type);
        Assert.Equal("Phase 1", Text(document.Blocks[1]));
        Assert.Equal(BlockType.BulletedList, document.Blocks[2].Type);
        Assert.Equal("Ship", Text(document.Blocks[2]));
    }

    [Fact]
    public void Parse_uses_the_fallback_title_when_no_leading_heading_exists()
    {
        var document = MarkdownCodec.Parse("Intro line.\n\n# Late heading\n", "Fallback");

        Assert.Equal("Fallback", document.Title);
        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(BlockType.Paragraph, document.Blocks[0].Type);
        Assert.Equal("Intro line.", Text(document.Blocks[0]));
        Assert.Equal(BlockType.Heading1, document.Blocks[1].Type);
        Assert.Equal("Late heading", Text(document.Blocks[1]));

        Assert.Equal("Imported note", MarkdownCodec.Parse("   \n\n").Title);
        Assert.Equal("My note", MarkdownCodec.Parse(string.Empty, "  My note  ").Title);

        var empty = MarkdownCodec.Parse("   \n\n");
        Assert.Empty(empty.Blocks);
    }

    [Fact]
    public void Parse_recognizes_every_block_type()
    {
        const string markdown = """
            ## Heading two
            ### Heading three
            #### Not a heading
            - one
            * two
            + three
            1. first
            2) second
            - [ ] open
            - [x] done
            - [X] also done
            - [y] not a todo
            > quoted
            > second line
            ---
            ***
            _ _ _
            """;

        var document = MarkdownCodec.Parse(markdown);

        Assert.Equal(16, document.Blocks.Count);
        Assert.Equal(BlockType.Heading2, document.Blocks[0].Type);
        Assert.Equal(BlockType.Heading3, document.Blocks[1].Type);
        Assert.Equal(BlockType.Paragraph, document.Blocks[2].Type);
        Assert.Equal("#### Not a heading", Text(document.Blocks[2]));
        Assert.Equal(BlockType.BulletedList, document.Blocks[3].Type);
        Assert.Equal(BlockType.BulletedList, document.Blocks[4].Type);
        Assert.Equal(BlockType.BulletedList, document.Blocks[5].Type);
        Assert.Equal(BlockType.NumberedList, document.Blocks[6].Type);
        Assert.Equal(BlockType.NumberedList, document.Blocks[7].Type);
        Assert.Equal(BlockType.ToDo, document.Blocks[8].Type);
        Assert.False(IsChecked(document.Blocks[8]));
        Assert.True(IsChecked(document.Blocks[9]));
        Assert.True(IsChecked(document.Blocks[10]));
        Assert.Equal(BlockType.BulletedList, document.Blocks[11].Type);
        Assert.Equal("[y] not a todo", Text(document.Blocks[11]));
        Assert.Equal(BlockType.Quote, document.Blocks[12].Type);
        Assert.Equal("quoted\nsecond line", Text(document.Blocks[12]));
        Assert.All(
            document.Blocks.Skip(13),
            block => Assert.Equal(BlockType.Divider, block.Type));

        Assert.Equal(
            BlockType.Paragraph,
            Assert.Single(MarkdownCodec.Parse("#NoSpace\n").Blocks).Type);
    }

    [Fact]
    public void Parse_reads_code_fences_with_their_language_and_blank_lines()
    {
        var document = MarkdownCodec.Parse("```csharp\nvar x = 1;\n\nvar y = 2;\n```\n");

        var block = Assert.Single(document.Blocks);
        Assert.Equal(BlockType.Code, block.Type);
        Assert.Equal("csharp", StringProperty(block, "language"));
        Assert.Equal("var x = 1;\n\nvar y = 2;", Text(block));

        var unclosed = MarkdownCodec.Parse("~~~\nline one\nline two");

        var code = Assert.Single(unclosed.Blocks);
        Assert.Equal(BlockType.Code, code.Type);
        Assert.Equal(string.Empty, StringProperty(code, "language"));
        Assert.Equal("line one\nline two", Text(code));
    }

    [Fact]
    public void Parse_reads_tables_with_alignment_markers()
    {
        const string markdown = """
            | Name | Size |
            |:---|---:|
            | apple | 1 |
            | pear | 2 |
            """;

        var block = Assert.Single(MarkdownCodec.Parse(markdown).Blocks);

        Assert.Equal(BlockType.Table, block.Type);

        var rows = Rows(block);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "Name", "Size" }, rows[0]);
        Assert.Equal(new[] { "apple", "1" }, rows[1]);
        Assert.Equal(new[] { "pear", "2" }, rows[2]);
    }

    [Fact]
    public void Parse_maps_attachment_links_to_image_and_file_blocks()
    {
        var attachmentId = Guid.NewGuid();
        var markdown = $"""
            ![diagram](notedx-attachment:{attachmentId})

            ![photo](https://example.com/photo.png)

            [report](notedx-attachment:{attachmentId})

            [site](https://example.com)

            ![broken](notedx-attachment:not-a-guid)

            [broken](notedx-attachment:not-a-guid)
            """;

        var document = MarkdownCodec.Parse(markdown);

        Assert.Equal(6, document.Blocks.Count);

        var localImage = document.Blocks[0];
        Assert.Equal(BlockType.Image, localImage.Type);
        Assert.Equal(attachmentId.ToString(), StringProperty(localImage, "attachmentId"));
        Assert.Equal("diagram", StringProperty(localImage, "name"));
        Assert.Null(StringProperty(localImage, "url"));

        var externalImage = document.Blocks[1];
        Assert.Equal(BlockType.Image, externalImage.Type);
        Assert.Null(StringProperty(externalImage, "attachmentId"));
        Assert.Equal("https://example.com/photo.png", StringProperty(externalImage, "url"));

        var file = document.Blocks[2];
        Assert.Equal(BlockType.File, file.Type);
        Assert.Equal(attachmentId.ToString(), StringProperty(file, "attachmentId"));
        Assert.Equal("report", StringProperty(file, "name"));

        Assert.Equal(BlockType.Paragraph, document.Blocks[3].Type);
        Assert.Equal("[site](https://example.com)", Text(document.Blocks[3]));

        Assert.Equal(BlockType.Image, document.Blocks[4].Type);
        Assert.Null(StringProperty(document.Blocks[4], "attachmentId"));
        Assert.Equal(
            "notedx-attachment:not-a-guid",
            StringProperty(document.Blocks[4], "url"));

        Assert.Equal(BlockType.Paragraph, document.Blocks[5].Type);
        Assert.Equal("[broken](notedx-attachment:not-a-guid)", Text(document.Blocks[5]));
    }

    [Fact]
    public void Parse_joins_paragraph_lines_until_a_blank_line_or_block_start()
    {
        const string markdown = "line one\nline two\n# Heading\n- bullet\n\nsecond paragraph";

        var document = MarkdownCodec.Parse(markdown);

        Assert.Equal(4, document.Blocks.Count);
        Assert.Equal(BlockType.Paragraph, document.Blocks[0].Type);
        Assert.Equal("line one\nline two", Text(document.Blocks[0]));
        Assert.Equal(BlockType.Heading1, document.Blocks[1].Type);
        Assert.Equal("Heading", Text(document.Blocks[1]));
        Assert.Equal(BlockType.BulletedList, document.Blocks[2].Type);
        Assert.Equal(BlockType.Paragraph, document.Blocks[3].Type);
        Assert.Equal("second paragraph", Text(document.Blocks[3]));
    }

    [Fact]
    public void ToMarkdown_serializes_every_block_type()
    {
        var attachmentId = Guid.NewGuid();
        var blocks = new[]
        {
            new BlockDraft(BlockType.Heading1, """{"text":"Heading"}"""),
            new BlockDraft(BlockType.Heading2, """{"text":"Section"}"""),
            new BlockDraft(BlockType.Heading3, """{"text":"Detail"}"""),
            new BlockDraft(BlockType.Paragraph, """{"text":"Body"}"""),
            new BlockDraft(BlockType.BulletedList, """{"text":"bullet"}"""),
            new BlockDraft(BlockType.NumberedList, """{"text":"first"}"""),
            new BlockDraft(BlockType.ToDo, """{"text":"done","checked":true}"""),
            new BlockDraft(BlockType.Quote, """{"text":"quoted"}"""),
            new BlockDraft(BlockType.Code, """{"text":"var x = 1;","language":"cs"}"""),
            new BlockDraft(BlockType.Divider, "{}"),
            new BlockDraft(
                BlockType.Image,
                $$"""{"attachmentId":"{{attachmentId}}","name":"diagram"}"""),
            new BlockDraft(
                BlockType.File,
                $$"""{"attachmentId":"{{attachmentId}}","name":"report.pdf"}"""),
            new BlockDraft(BlockType.Table, """{"rows":[["A","B"],["1","2"]]}""")
        };

        var markdown = MarkdownCodec.ToMarkdown(null, blocks)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var expected = $"""
            # Heading

            ## Section

            ### Detail

            Body

            - bullet

            1. first

            - [x] done

            > quoted

            ```cs
            var x = 1;
            ```

            ---

            ![diagram](notedx-attachment:{attachmentId})

            [report.pdf](notedx-attachment:{attachmentId})

            | A | B |
            | --- | --- |
            | 1 | 2 |
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        Assert.Equal(expected, markdown);
    }

    [Fact]
    public void Markdown_round_trips_titles_and_blocks_including_unicode()
    {
        var attachmentId = Guid.NewGuid();
        var blocks = new[]
        {
            new BlockDraft(BlockType.Heading2, """{"text":"المقدمة"}"""),
            new BlockDraft(BlockType.Paragraph, """{"text":"نص عربي بسيط"}"""),
            new BlockDraft(BlockType.BulletedList, """{"text":"بند أول"}"""),
            new BlockDraft(BlockType.NumberedList, """{"text":"خطوة واحدة"}"""),
            new BlockDraft(BlockType.ToDo, """{"text":"مهمة مكتملة","checked":true}"""),
            new BlockDraft(BlockType.ToDo, """{"text":"مهمة متبقية","checked":false}"""),
            new BlockDraft(BlockType.Quote, """{"text":"اقتباس"}"""),
            new BlockDraft(
                BlockType.Code,
                """{"text":"Console.WriteLine(1);","language":"csharp"}"""),
            new BlockDraft(BlockType.Divider, "{}"),
            new BlockDraft(
                BlockType.Image,
                $$"""{"attachmentId":"{{attachmentId}}","name":"رسم"}"""),
            new BlockDraft(
                BlockType.File,
                $$"""{"attachmentId":"{{attachmentId}}","name":"تقرير.pdf"}"""),
            new BlockDraft(BlockType.Table, """{"rows":[["الاسم","الحجم"],["أ","1"]]}""")
        };

        var markdown = MarkdownCodec.ToMarkdown("خطة المشروع", blocks);
        var parsed = MarkdownCodec.Parse(markdown, "خطة المشروع");

        Assert.Equal("خطة المشروع", parsed.Title);
        Assert.Equal(blocks.Length, parsed.Blocks.Count);
        Assert.Equal(
            blocks.Select(block => block.Type),
            parsed.Blocks.Select(block => block.Type));

        for (var index = 0; index < blocks.Length; index++)
        {
            Assert.Equal(Text(blocks[index]), Text(parsed.Blocks[index]));
        }

        Assert.True(IsChecked(parsed.Blocks[4]));
        Assert.False(IsChecked(parsed.Blocks[5]));
        Assert.Equal(attachmentId.ToString(), StringProperty(parsed.Blocks[9], "attachmentId"));
        Assert.Equal(attachmentId.ToString(), StringProperty(parsed.Blocks[10], "attachmentId"));
        Assert.Equal("csharp", StringProperty(parsed.Blocks[7], "language"));
        Assert.Equal(Rows(blocks[11]), Rows(parsed.Blocks[11]));
    }

    [Fact]
    public void ToMarkdown_tolerates_malformed_block_content()
    {
        var blocks = new[]
        {
            new BlockDraft(BlockType.Paragraph, "not json"),
            new BlockDraft(BlockType.Code, "not json"),
            new BlockDraft(BlockType.ToDo, "not json"),
            new BlockDraft(BlockType.NumberedList, "not json"),
            new BlockDraft(BlockType.BulletedList, "not json"),
            new BlockDraft(BlockType.Quote, "not json"),
            new BlockDraft(BlockType.Table, "not json"),
            new BlockDraft(BlockType.Image, "not json"),
            new BlockDraft(BlockType.File, "not json")
        };

        var markdown = MarkdownCodec.ToMarkdown(null, blocks);

        Assert.False(string.IsNullOrWhiteSpace(markdown));
    }

    private static string Text(BlockDraft block) => StringProperty(block, "text") ?? string.Empty;

    private static string? StringProperty(BlockDraft block, string propertyName)
    {
        using var document = JsonDocument.Parse(block.Content);
        return document.RootElement.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsChecked(BlockDraft block)
    {
        using var document = JsonDocument.Parse(block.Content);
        return document.RootElement.TryGetProperty("checked", out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static List<string[]> Rows(BlockDraft block)
    {
        using var document = JsonDocument.Parse(block.Content);
        return document.RootElement
            .GetProperty("rows")
            .EnumerateArray()
            .Select(row => row.EnumerateArray()
                .Select(cell => cell.GetString() ?? string.Empty)
                .ToArray())
            .ToList();
    }
}
