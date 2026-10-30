using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NotedX.Core.Domain;
using NotedX.Core.Notes;

namespace NotedX.Desktop.ViewModels;

/// <summary>One editable block inside the page editor.</summary>
public sealed class BlockEditorViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly PageEditorViewModel _owner;
    private BlockType _type;
    private string _text = string.Empty;
    private bool _isChecked;
    private string _language = string.Empty;
    private Guid? _attachmentId;
    private string _attachmentName = string.Empty;
    private string? _remoteUrl;
    private string _attachmentSizeText = string.Empty;
    private ImageSource? _previewImage;
    private bool _isPreviewLoading;
    private string? _previewError;
    private int _listNumber;
    private bool _isSlashMenuOpen;
    private int? _pendingCaret;

    internal BlockEditorViewModel(PageEditorViewModel owner, BlockType type)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _type = type;
    }

    /// <summary>Raised when the editor should move keyboard focus to this block.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>The page editor that owns this block.</summary>
    public PageEditorViewModel Owner => _owner;

    /// <summary>Block kind.</summary>
    public BlockType Type
    {
        get => _type;
        set
        {
            if (!SetProperty(ref _type, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTextual));
            OnPropertyChanged(nameof(IsCode));
            OnPropertyChanged(nameof(IsDivider));
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(IsTable));
            OnPropertyChanged(nameof(IsTodo));
            OnPropertyChanged(nameof(ShowsBullet));
            OnPropertyChanged(nameof(ShowsNumber));
            OnPropertyChanged(nameof(ShowsQuoteBar));
            OnPropertyChanged(nameof(Placeholder));
        }
    }

    /// <summary>Text for paragraph, heading, list, quote, to-do, and code blocks.</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    /// <summary>Completion state of a to-do block.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>Language label of a code block.</summary>
    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value ?? string.Empty);
    }

    /// <summary>Attachment behind an image or file block.</summary>
    public Guid? AttachmentId
    {
        get => _attachmentId;
        set => SetProperty(ref _attachmentId, value);
    }

    /// <summary>Original file name of the attachment.</summary>
    public string AttachmentName
    {
        get => _attachmentName;
        set => SetProperty(ref _attachmentName, value ?? string.Empty);
    }

    /// <summary>Remote URL kept for images imported from Markdown.</summary>
    public string? RemoteUrl
    {
        get => _remoteUrl;
        set => SetProperty(ref _remoteUrl, value);
    }

    /// <summary>Human-readable attachment size.</summary>
    public string AttachmentSizeText
    {
        get => _attachmentSizeText;
        set => SetProperty(ref _attachmentSizeText, value ?? string.Empty);
    }

    /// <summary>Decoded image shown in the editor.</summary>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
            }
        }
    }

    /// <summary>Whether an image preview is being decoded.</summary>
    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        set => SetProperty(ref _isPreviewLoading, value);
    }

    /// <summary>Reason the preview could not be shown.</summary>
    public string? PreviewError
    {
        get => _previewError;
        set
        {
            if (SetProperty(ref _previewError, value))
            {
                OnPropertyChanged(nameof(HasPreviewError));
            }
        }
    }

    /// <summary>Position shown next to a numbered list item.</summary>
    public int ListNumber
    {
        get => _listNumber;
        set => SetProperty(ref _listNumber, value);
    }

    /// <summary>Whether the slash command menu is open.</summary>
    public bool IsSlashMenuOpen
    {
        get => _isSlashMenuOpen;
        set => SetProperty(ref _isSlashMenuOpen, value);
    }

    /// <summary>Commands matching the text typed after the slash.</summary>
    public ObservableCollection<SlashCommandItem> SlashItems { get; } = new();

    /// <summary>Rows of a table block.</summary>
    public ObservableCollection<TableRowViewModel> TableRows { get; } = new();

    /// <summary>Whether the block is edited through the main text box.</summary>
    public bool IsTextual => Type is BlockType.Paragraph
        or BlockType.Heading1
        or BlockType.Heading2
        or BlockType.Heading3
        or BlockType.BulletedList
        or BlockType.NumberedList
        or BlockType.ToDo
        or BlockType.Quote;

    /// <summary>Whether this is a code block.</summary>
    public bool IsCode => Type == BlockType.Code;

    /// <summary>Whether this is a divider.</summary>
    public bool IsDivider => Type == BlockType.Divider;

    /// <summary>Whether this is an image block.</summary>
    public bool IsImage => Type == BlockType.Image;

    /// <summary>Whether this is a file attachment block.</summary>
    public bool IsFile => Type == BlockType.File;

    /// <summary>Whether this is a table block.</summary>
    public bool IsTable => Type == BlockType.Table;

    /// <summary>Whether this block has a checkbox.</summary>
    public bool IsTodo => Type == BlockType.ToDo;

    /// <summary>Whether a bullet is displayed.</summary>
    public bool ShowsBullet => Type == BlockType.BulletedList;

    /// <summary>Whether a list number is displayed.</summary>
    public bool ShowsNumber => Type == BlockType.NumberedList;

    /// <summary>Whether the quote accent bar is displayed.</summary>
    public bool ShowsQuoteBar => Type == BlockType.Quote;

    /// <summary>Whether a decoded image is available.</summary>
    public bool HasPreviewImage => PreviewImage is not null;

    /// <summary>Whether decoding the image failed.</summary>
    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);

    /// <summary>Whether an image points at an external URL that is not downloaded.</summary>
    public bool IsRemoteImage => IsImage && AttachmentId is null && !string.IsNullOrWhiteSpace(RemoteUrl);

    /// <summary>Placeholder shown when the block has no text.</summary>
    public string Placeholder
    {
        get
        {
            if (IsTodo)
            {
                return "To-do";
            }

            return Type switch
            {
                BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 => "Heading",
                BlockType.BulletedList or BlockType.NumberedList => "List item",
                BlockType.Quote => "Quote",
                BlockType.Code => "Code",
                _ => "Type something, or press / for commands"
            };
        }
    }

    /// <summary>Asks the view to focus this block and place the caret.</summary>
    public void RequestFocus(int? caret = null)
    {
        _pendingCaret = caret;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns a pending caret position and clears it.</summary>
    public int? ConsumePendingCaret()
    {
        var caret = _pendingCaret;
        _pendingCaret = null;
        return caret;
    }

    /// <summary>Replaces the slash command results.</summary>
    internal void SetSlashItems(IEnumerable<SlashCommandItem> items)
    {
        SlashItems.Clear();
        foreach (var item in items)
        {
            SlashItems.Add(item);
        }
    }

    /// <summary>Builds the JSON payload persisted for this block.</summary>
    public BlockDraft ToDraft()
    {
        var content = Type switch
        {
            BlockType.ToDo => JsonSerializer.Serialize(new { text = Text, @checked = IsChecked }, JsonOptions),
            BlockType.Code => JsonSerializer.Serialize(new { text = Text, language = Language }, JsonOptions),
            BlockType.Image => JsonSerializer.Serialize(
                new
                {
                    attachmentId = AttachmentId?.ToString(),
                    name = AttachmentName,
                    url = string.IsNullOrWhiteSpace(RemoteUrl) ? null : RemoteUrl
                },
                JsonOptions),
            BlockType.File => JsonSerializer.Serialize(
                new { attachmentId = AttachmentId?.ToString(), name = AttachmentName },
                JsonOptions),
            BlockType.Table => JsonSerializer.Serialize(
                new { rows = TableRows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()).ToArray() },
                JsonOptions),
            BlockType.Divider => "{}",
            _ => JsonSerializer.Serialize(new { text = Text }, JsonOptions)
        };

        return new BlockDraft(Type, content);
    }

    /// <summary>Creates a block view model from a stored block.</summary>
    public static BlockEditorViewModel FromBlock(PageEditorViewModel owner, Block block)
    {
        var model = new BlockEditorViewModel(owner, block.Type);

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(block.Content) ? "{}" : block.Content);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                model.Text = GetString(root, "text");
                model.IsChecked = GetBoolean(root, "checked");
                model.Language = GetString(root, "language");
                model.AttachmentName = GetString(root, "name");
                model.RemoteUrl = GetString(root, "url");

                if (Guid.TryParse(GetString(root, "attachmentId"), out var attachmentId))
                {
                    model.AttachmentId = attachmentId;
                }

                if (block.Type == BlockType.Table && root.TryGetProperty("rows", out var rows) &&
                    rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        model.AddTableRow(row.EnumerateArray()
                            .Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : string.Empty));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Corrupted payloads start as empty blocks rather than failing the whole page.
        }

        if (block.Type == BlockType.Table && model.TableRows.Count == 0)
        {
            model.SetupTable(new[] { new[] { "Column 1", "Column 2", "Column 3" } });
        }

        return model;
    }

    internal void SetupTable(IEnumerable<IEnumerable<string>> rows)
    {
        ClearTable();
        var materialized = rows.Select(row => row.ToArray()).ToArray();
        var columnCount = Math.Max(2, materialized.Select(row => row.Length).DefaultIfEmpty(2).Max());

        foreach (var row in materialized)
        {
            AddTableRow(row);
        }

        if (TableRows.Count == 0)
        {
            AddTableRow(null, columnCount);
        }
        else
        {
            foreach (var row in TableRows)
            {
                while (row.Cells.Count < columnCount)
                {
                    row.AddCell(string.Empty);
                }
            }
        }

        foreach (var row in TableRows)
        {
            row.CellChanged -= OnTableCellChanged;
            row.CellChanged += OnTableCellChanged;
        }
    }

    internal TableRowViewModel AddTableRow(IEnumerable<string>? cells = null, int columnCount = 0)
    {
        var row = new TableRowViewModel(cells, columnCount);
        row.CellChanged += OnTableCellChanged;
        TableRows.Add(row);
        return row;
    }

    internal void RemoveTableRow(TableRowViewModel row)
    {
        row.CellChanged -= OnTableCellChanged;
        TableRows.Remove(row);
    }

    internal void AddTableColumn()
    {
        foreach (var row in TableRows)
        {
            row.AddCell(string.Empty);
        }
    }

    internal void RemoveTableColumn()
    {
        if (TableRows.Count == 0 || TableRows[0].Cells.Count <= 2)
        {
            return;
        }

        foreach (var row in TableRows)
        {
            row.RemoveLastCell();
        }
    }

    internal void ClearTable()
    {
        foreach (var row in TableRows)
        {
            row.CellChanged -= OnTableCellChanged;
        }

        TableRows.Clear();
    }

    private void OnTableCellChanged(object? sender, EventArgs e) => _owner.MarkContentDirty();

    private static string GetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool GetBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
}
