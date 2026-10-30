using System.Text.Json;
using NotedX.Core.Domain;
using NotedX.Core.Notes;

namespace NotedX.Server.Api;

internal static class PageContractMapper
{
    public static PageSummaryDto ToSummary(Page page) => new(
        page.Id,
        page.WorkspaceId,
        page.ParentId,
        page.Title,
        page.Icon,
        page.Position,
        page.IsFavorite,
        page.IsDeleted,
        page.CreatedAt,
        page.UpdatedAt);

    public static PageDetailDto ToDetail(Page page) => new(
        page.Id,
        page.WorkspaceId,
        page.ParentId,
        page.Title,
        page.Icon,
        page.Position,
        page.IsFavorite,
        page.IsDeleted,
        page.CreatedAt,
        page.UpdatedAt,
        page.Blocks
            .OrderBy(block => block.Position)
            .Select(ToBlock)
            .ToArray(),
        page.PageTags
            .Select(pageTag => pageTag.Tag)
            .Where(tag => tag is not null)
            .Select(tag => new TagDto(tag!.Id, tag.Name, tag.Color))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        page.Attachments
            .OrderByDescending(attachment => attachment.CreatedAt)
            .Select(ToAttachment)
            .ToArray());

    public static BlockDto ToBlock(Block block) =>
        new(block.Id, ToApiType(block.Type), block.Content, block.Position);

    public static AttachmentDto ToAttachment(Attachment attachment) => new(
        attachment.Id,
        attachment.PageId,
        attachment.FileName,
        attachment.MimeType,
        attachment.SizeBytes,
        attachment.UploadedBy,
        attachment.CreatedAt,
        attachment.BlobHash,
        $"/api/files/{attachment.Id}");

    public static string ToApiType(BlockType type) =>
        JsonNamingPolicy.CamelCase.ConvertName(type.ToString());

    public static bool TryMapBlocks(
        IReadOnlyList<BlockInput>? inputs,
        out IReadOnlyList<BlockDraft>? drafts,
        out string? error)
    {
        drafts = null;
        error = null;

        if (inputs is null)
        {
            return true;
        }

        var mapped = new List<BlockDraft>(inputs.Count);
        foreach (var input in inputs)
        {
            var type = BlockType.Paragraph;
            if (!string.IsNullOrWhiteSpace(input.Type))
            {
                if (!Enum.TryParse<BlockType>(input.Type, ignoreCase: true, out type) ||
                    !Enum.IsDefined(type))
                {
                    error = $"Unknown block type '{input.Type}'.";
                    return false;
                }
            }

            mapped.Add(new BlockDraft(type, input.Content ?? "{}"));
        }

        drafts = mapped;
        return true;
    }
}
