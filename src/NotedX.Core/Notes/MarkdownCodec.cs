using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotedX.Core.Domain;

namespace NotedX.Core.Notes;

/// <summary>A page parsed from Markdown.</summary>
/// <param name="Title">Title taken from the leading level-one heading, or a fallback.</param>
/// <param name="Blocks">Parsed content blocks.</param>
public sealed record MarkdownDocument(string Title, IReadOnlyList<BlockDraft> Blocks);

/// <summary>Converts between the block model and portable Markdown.</summary>
public static partial class MarkdownCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Parses Markdown into a title and blocks.</summary>
    /// <param name="markdown">Markdown source; a leading BOM and CRLF line endings are accepted.</param>
    /// <param name="fallbackTitle">Title used when the document has no leading level-one heading.</param>
    public static MarkdownDocument Parse(string markdown, string? fallbackTitle = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        if (lines.Length > 0)
        {
            lines[0] = lines[0].TrimStart('\uFEFF');
        }

        var blocks = new List<BlockDraft>();
        string? title = null;
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                blocks.Add(ParseCodeFence(lines, ref index));
                continue;
            }

            if (TryParseHeading(trimmed, out var headingLevel, out var headingText))
            {
                if (headingLevel == 1 && title is null && blocks.Count == 0)
                {
                    title = headingText;
                }
                else
                {
                    var type = headingLevel switch
                    {
                        1 => BlockType.Heading1,
                        2 => BlockType.Heading2,
                        _ => BlockType.Heading3
                    };

                    blocks.Add(new BlockDraft(type, TextContent(headingText)));
                }

                index++;
                continue;
            }

            if (IsDivider(trimmed))
            {
                blocks.Add(new BlockDraft(BlockType.Divider, "{}"));
                index++;
                continue;
            }

            if (index + 1 < lines.Length &&
                line.Contains('|') &&
                IsTableSeparator(lines[index + 1]))
            {
                blocks.Add(ParseTable(lines, ref index));
                continue;
            }

            if (TryParseToDo(trimmed, out var toDoText, out var isChecked))
            {
                blocks.Add(new BlockDraft(BlockType.ToDo, ToDoContent(toDoText, isChecked)));
                index++;
                continue;
            }

            if (TryParseBullet(trimmed, out var bulletText))
            {
                blocks.Add(new BlockDraft(BlockType.BulletedList, TextContent(bulletText)));
                index++;
                continue;
            }

            if (TryParseNumbered(trimmed, out var numberedText))
            {
                blocks.Add(new BlockDraft(BlockType.NumberedList, TextContent(numberedText)));
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(new BlockDraft(BlockType.Quote, TextContent(ParseQuote(lines, ref index))));
                continue;
            }

            var image = ImagePattern().Match(line.Trim());
            if (image.Success)
            {
                blocks.Add(ParseImage(image));
                index++;
                continue;
            }

            var file = FileLinkPattern().Match(line.Trim());
            if (file.Success)
            {
                var attachmentId = ParseAttachmentId(file.Groups["url"].Value);
                if (attachmentId is not null)
                {
                    blocks.Add(new BlockDraft(
                        BlockType.File,
                        FileContent(attachmentId.Value, file.Groups["name"].Value)));
                    index++;
                    continue;
                }
            }

            blocks.Add(new BlockDraft(BlockType.Paragraph, TextContent(ParseParagraph(lines, ref index))));
        }

        return new MarkdownDocument(
            string.IsNullOrWhiteSpace(title)
                ? (string.IsNullOrWhiteSpace(fallbackTitle) ? "Imported note" : fallbackTitle.Trim())
                : title,
            blocks);
    }

    /// <summary>Serializes a page title and blocks as Markdown.</summary>
    public static string ToMarkdown(string? title, IEnumerable<BlockDraft> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("# ").AppendLine(title.ReplaceLineEndings(" ").Trim());
            builder.AppendLine();
        }

        var first = true;
        foreach (var block in blocks)
        {
            if (!first)
            {
                builder.AppendLine();
            }

            first = false;
            AppendBlock(builder, block);
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendBlock(StringBuilder builder, BlockDraft block)
    {
        switch (block.Type)
        {
            case BlockType.Heading1:
                builder.Append("# ").AppendLine(GetText(block.Content));
                break;
            case BlockType.Heading2:
                builder.Append("## ").AppendLine(GetText(block.Content));
                break;
            case BlockType.Heading3:
                builder.Append("### ").AppendLine(GetText(block.Content));
                break;
            case BlockType.BulletedList:
                AppendLines(builder, GetText(block.Content), "- ");
                break;
            case BlockType.NumberedList:
                AppendNumbered(builder, GetText(block.Content));
                break;
            case BlockType.ToDo:
                AppendToDos(builder, GetText(block.Content), GetBoolean(block.Content, "checked"));
                break;
            case BlockType.Quote:
                AppendLines(builder, GetText(block.Content), "> ");
                break;
            case BlockType.Code:
                AppendCode(builder, block.Content);
                break;
            case BlockType.Divider:
                builder.AppendLine("---");
                break;
            case BlockType.Image:
                AppendImage(builder, block.Content);
                break;
            case BlockType.File:
                AppendFile(builder, block.Content);
                break;
            case BlockType.Table:
                AppendTable(builder, block.Content);
                break;
            default:
                builder.AppendLine(GetText(block.Content));
                break;
        }
    }

    private static BlockDraft ParseCodeFence(string[] lines, ref int index)
    {
        var opening = lines[index].TrimStart();
        var marker = opening[..3];
        var language = opening[3..].Trim();
        index++;

        var content = new List<string>();
        while (index < lines.Length &&
               !lines[index].TrimStart().StartsWith(marker, StringComparison.Ordinal))
        {
            content.Add(lines[index]);
            index++;
        }

        if (index < lines.Length)
        {
            index++;
        }

        return new BlockDraft(
            BlockType.Code,
            Serialize(new { text = string.Join("\n", content), language }));
    }

    private static BlockDraft ParseTable(string[] lines, ref int index)
    {
        var rows = new List<List<string>>
        {
            SplitTableRow(lines[index])
        };

        index += 2;

        while (index < lines.Length &&
               !string.IsNullOrWhiteSpace(lines[index]) &&
               lines[index].Contains('|'))
        {
            rows.Add(SplitTableRow(lines[index]));
            index++;
        }

        return new BlockDraft(BlockType.Table, Serialize(new { rows }));
    }

    private static string ParseQuote(string[] lines, ref int index)
    {
        var content = new StringBuilder();

        while (index < lines.Length)
        {
            var trimmed = lines[index].TrimStart();
            if (!trimmed.StartsWith('>'))
            {
                break;
            }

            var text = trimmed[1..];
            if (text.StartsWith(' '))
            {
                text = text[1..];
            }

            if (content.Length > 0)
            {
                content.Append('\n');
            }

            content.Append(text);
            index++;
        }

        return content.ToString();
    }

    private static string ParseParagraph(string[] lines, ref int index)
    {
        var content = new StringBuilder();

        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
        {
            if (content.Length > 0 && StartsBlock(lines[index]))
            {
                break;
            }

            if (content.Length > 0)
            {
                content.Append('\n');
            }

            content.Append(lines[index].TrimEnd());
            index++;
        }

        return content.ToString();
    }

    private static bool StartsBlock(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
               trimmed.StartsWith("~~~", StringComparison.Ordinal) ||
               TryParseHeading(trimmed, out _, out _) ||
               IsDivider(trimmed) ||
               TryParseToDo(trimmed, out _, out _) ||
               TryParseBullet(trimmed, out _) ||
               TryParseNumbered(trimmed, out _) ||
               trimmed.StartsWith('>');
    }

    private static bool TryParseHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        while (level < trimmed.Length && level < 4 && trimmed[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 3 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return true;
    }

    private static bool IsDivider(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var character = trimmed[0];
        if (character is not ('-' or '*' or '_'))
        {
            return false;
        }

        return trimmed.All(c => c == character || c == ' ');
    }

    private static bool TryParseToDo(string trimmed, out string text, out bool isChecked)
    {
        text = string.Empty;
        isChecked = false;

        if (trimmed.Length < 6 || trimmed[0] is not ('-' or '*' or '+') || trimmed[1] != ' ')
        {
            return false;
        }

        var remainder = trimmed[2..];
        if (remainder.Length < 3 || remainder[0] != '[' || remainder[2] != ']')
        {
            return false;
        }

        var marker = char.ToLowerInvariant(remainder[1]);
        if (marker is not (' ' or 'x'))
        {
            return false;
        }

        if (remainder.Length > 3 && remainder[3] != ' ')
        {
            return false;
        }

        isChecked = marker == 'x';
        text = remainder.Length > 4 ? remainder[4..].Trim() : string.Empty;
        return true;
    }

    private static bool TryParseBullet(string trimmed, out string text)
    {
        text = string.Empty;
        if (trimmed.Length < 3 || trimmed[0] is not ('-' or '*' or '+') || trimmed[1] != ' ')
        {
            return false;
        }

        if (TryParseToDo(trimmed, out _, out _))
        {
            return false;
        }

        text = trimmed[2..].Trim();
        return true;
    }

    private static bool TryParseNumbered(string trimmed, out string text)
    {
        text = string.Empty;
        var digits = 0;

        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= trimmed.Length)
        {
            return false;
        }

        if (trimmed[digits] is not ('.' or ')') || trimmed[digits + 1] != ' ')
        {
            return false;
        }

        text = trimmed[(digits + 2)..].Trim();
        return true;
    }

    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.Contains('-') || !trimmed.Contains('|'))
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (character is not ('-' or '|' or ':' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed
            .Split('|')
            .Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal))
            .ToList();
    }

    private static BlockDraft ParseImage(Match match)
    {
        var name = match.Groups["alt"].Value.Trim();
        var url = match.Groups["url"].Value.Trim();
        var attachmentId = ParseAttachmentId(url);

        return attachmentId is not null
            ? new BlockDraft(BlockType.Image, ImageContent(attachmentId, name, null))
            : new BlockDraft(BlockType.Image, ImageContent(null, name, url));
    }

    private static Guid? ParseAttachmentId(string value)
    {
        const string prefix = "notedx-attachment:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Guid.TryParse(value[prefix.Length..], out var id) ? id : null;
    }

    private static void AppendLines(StringBuilder builder, string text, string prefix)
    {
        var lines = ToLines(text);
        if (lines.Length == 0)
        {
            builder.AppendLine(prefix.TrimEnd());
            return;
        }

        foreach (var line in lines)
        {
            builder.Append(prefix).AppendLine(line);
        }
    }

    private static void AppendNumbered(StringBuilder builder, string text)
    {
        var lines = ToLines(text);
        if (lines.Length == 0)
        {
            builder.AppendLine("1.");
            return;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append(i + 1).Append(". ").AppendLine(lines[i]);
        }
    }

    private static void AppendToDos(StringBuilder builder, string text, bool isChecked)
    {
        var marker = isChecked ? "- [x] " : "- [ ] ";
        var lines = ToLines(text);
        if (lines.Length == 0)
        {
            builder.AppendLine(marker.TrimEnd());
            return;
        }

        foreach (var line in lines)
        {
            builder.Append(marker).AppendLine(line);
        }
    }

    private static void AppendCode(StringBuilder builder, string content)
    {
        var language = GetString(content, "language");
        builder.Append("```").AppendLine(language);
        builder.AppendLine(GetText(content));
        builder.AppendLine("```");
    }

    private static void AppendImage(StringBuilder builder, string content)
    {
        var name = GetString(content, "name");
        var attachmentId = GetString(content, "attachmentId");
        var url = GetString(content, "url");
        var target = !string.IsNullOrWhiteSpace(attachmentId)
            ? $"notedx-attachment:{attachmentId}"
            : url;

        builder.Append("![").Append(name).Append("](").Append(target).AppendLine(")");
    }

    private static void AppendFile(StringBuilder builder, string content)
    {
        var name = GetString(content, "name");
        var attachmentId = GetString(content, "attachmentId");
        var target = !string.IsNullOrWhiteSpace(attachmentId)
            ? $"notedx-attachment:{attachmentId}"
            : name;

        builder.Append('[').Append(name).Append("](").Append(target).AppendLine(")");
    }

    private static void AppendTable(StringBuilder builder, string content)
    {
        var rows = ReadTableRows(content);
        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(row => row.Count);
        AppendTableRow(builder, rows[0], columns);
        builder.Append('|');
        for (var column = 0; column < columns; column++)
        {
            builder.Append(" --- |");
        }

        builder.AppendLine();

        for (var row = 1; row < rows.Count; row++)
        {
            AppendTableRow(builder, rows[row], columns);
        }
    }

    private static void AppendTableRow(StringBuilder builder, IReadOnlyList<string> cells, int columns)
    {
        builder.Append('|');
        for (var column = 0; column < columns; column++)
        {
            var value = column < cells.Count ? cells[column] : string.Empty;
            builder.Append(' ')
                .Append(value.Replace('|', '/').ReplaceLineEndings(" "))
                .Append(" |");
        }

        builder.AppendLine();
    }

    private static List<List<string>> ReadTableRows(string content)
    {
        var rows = new List<List<string>>();

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rows", out var rowsElement) ||
                rowsElement.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (var row in rowsElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                rows.Add(row.EnumerateArray()
                    .Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : string.Empty)
                    .ToList());
            }
        }
        catch (JsonException)
        {
            return rows;
        }

        return rows;
    }

    private static string GetText(string content) => GetString(content, "text");

    private static string GetString(string content, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static bool GetBoolean(string content, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out var value))
            {
                return value.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string[] ToLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static string TextContent(string text) => Serialize(new { text });

    private static string ToDoContent(string text, bool isChecked) =>
        Serialize(new { text, @checked = isChecked });

    private static string ImageContent(Guid? attachmentId, string name, string? url) =>
        Serialize(new
        {
            attachmentId = attachmentId?.ToString(),
            name,
            url = string.IsNullOrWhiteSpace(url) ? null : url
        });

    private static string FileContent(Guid attachmentId, string name) =>
        Serialize(new { attachmentId = attachmentId.ToString(), name });

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^\[(?<name>[^\]]+)\]\((?<url>[^)\s]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileLinkPattern();
}
