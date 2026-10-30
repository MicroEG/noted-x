using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotedX.Core.Domain;

namespace NotedX.Core.Notes;

/// <summary>Validation, search-index text extraction, and revision snapshots for page content.</summary>
public static class PageContent
{
    /// <summary>Largest number of blocks accepted for a single page.</summary>
    public const int MaxBlocksPerPage = 5000;

    /// <summary>Largest accepted JSON payload size for a single block.</summary>
    public const int MaxBlockContentCharacters = 256 * 1024;

    /// <summary>Largest searchable body indexed per page.</summary>
    public const int MaxSearchTextCharacters = 200_000;

    /// <summary>Largest accepted search query length.</summary>
    public const int MaxSearchQueryCharacters = 200;

    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly HashSet<string> TextProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "title",
        "caption"
    };

    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "checked",
        "language",
        "url",
        "src",
        "color"
    };

    /// <summary>Validates a block list before it is persisted.</summary>
    /// <exception cref="PageValidationException">A block is invalid.</exception>
    public static void ValidateBlocks(IReadOnlyList<BlockDraft> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count > MaxBlocksPerPage)
        {
            throw new PageValidationException(
                $"A page cannot contain more than {MaxBlocksPerPage} blocks.");
        }

        foreach (var block in blocks)
        {
            if (!Enum.IsDefined(block.Type))
            {
                throw new PageValidationException("A block has an unknown type.");
            }

            var content = block.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (content.Length > MaxBlockContentCharacters)
            {
                throw new PageValidationException(
                    $"A block cannot be larger than {MaxBlockContentCharacters} characters.");
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new PageValidationException("Block content must be a JSON object.");
                }
            }
            catch (JsonException)
            {
                throw new PageValidationException("Block content is not valid JSON.");
            }
        }
    }

    /// <summary>Builds the denormalized text stored in <see cref="Page.SearchText"/>.</summary>
    public static string BuildSearchText(string title, IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var text = new StringBuilder();
        AppendText(text, title);
        foreach (var block in blocks.OrderBy(b => b.Position))
        {
            AppendText(text, ExtractText(block.Content));
        }

        return Normalize(text);
    }

    /// <summary>Builds searchable text from not-yet-persisted blocks.</summary>
    public static string BuildSearchText(string title, IEnumerable<BlockDraft> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var text = new StringBuilder();
        AppendText(text, title);
        foreach (var block in blocks)
        {
            AppendText(text, ExtractText(block.Content));
        }

        return Normalize(text);
    }

    /// <summary>
    /// Converts user input into a safe FTS5 phrase-prefix query, or <c>null</c> when
    /// the input contains no searchable characters.
    /// </summary>
    public static string? BuildSearchQuery(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return null;
        }

        var cleaned = rawQuery.Trim();
        if (cleaned.Length > MaxSearchQueryCharacters)
        {
            cleaned = cleaned[..MaxSearchQueryCharacters];
        }

        var builder = new StringBuilder(cleaned.Length + 4);
        foreach (var character in cleaned)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            if (character == '"')
            {
                builder.Append("\"\"");
            }
            else
            {
                builder.Append(character);
            }
        }

        var phrase = builder.ToString().Trim();
        return phrase.Length == 0 ? null : $"\"{phrase}\"*";
    }

    /// <summary>Serializes the current page content into a revision snapshot.</summary>
    public static string SerializeSnapshot(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var snapshot = new
        {
            title = page.Title,
            icon = page.Icon,
            blocks = page.Blocks
                .OrderBy(b => b.Position)
                .Select(b => new
                {
                    type = b.Type.ToString(),
                    content = b.Content,
                    position = b.Position
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(snapshot, SnapshotOptions);
    }

    /// <summary>Extracts human-readable text from a block's JSON content.</summary>
    public static string ExtractText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            CollectText(document.RootElement, builder, insideArray: false);
            return Normalize(builder);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static void CollectText(JsonElement element, StringBuilder builder, bool insideArray)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (TextProperties.Contains(property.Name))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            AppendText(builder, property.Value.GetString());
                        }
                        else
                        {
                            CollectText(property.Value, builder, insideArray);
                        }
                    }
                    else if (!IgnoredProperties.Contains(property.Name))
                    {
                        CollectText(property.Value, builder, insideArray: false);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectText(item, builder, insideArray: true);
                }

                break;

            case JsonValueKind.String when insideArray:
                AppendText(builder, element.GetString());
                break;
        }
    }

    private static void AppendText(StringBuilder builder, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(text);
    }

    private static string Normalize(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(builder.Length);
        var pendingSpace = false;

        for (var i = 0; i < builder.Length; i++)
        {
            var character = builder[i];
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(character);
            if (normalized.Length >= MaxSearchTextCharacters)
            {
                break;
            }
        }

        return normalized.ToString();
    }
}
