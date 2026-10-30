using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NotedX.Core.Domain;

namespace NotedX.Desktop.ViewModels;

/// <summary>What happens when a slash command is chosen.</summary>
public enum SlashCommandAction
{
    Convert,
    Image,
    File
}

/// <summary>An entry of the editor's slash menu.</summary>
/// <param name="Title">Primary label.</param>
/// <param name="Description">Short explanation.</param>
/// <param name="Glyph">Small text glyph shown in the menu.</param>
/// <param name="Type">Block type produced by the command.</param>
/// <param name="Action">Whether the command converts the block or imports a file.</param>
public sealed record SlashCommandItem(
    string Title,
    string Description,
    string Glyph,
    BlockType Type,
    SlashCommandAction Action)
{
    /// <summary>Whether the command matches a filter typed after the slash.</summary>
    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A row of editable cells inside a table block.</summary>
public sealed class TableRowViewModel
{
    /// <summary>Raised when any cell text changes.</summary>
    public event EventHandler? CellChanged;

    /// <summary>Creates a row with the supplied cells, padded to the requested column count.</summary>
    public TableRowViewModel(IEnumerable<string>? cells = null, int columnCount = 0)
    {
        var values = cells?.ToArray() ?? Array.Empty<string>();
        var count = Math.Max(Math.Max(values.Length, columnCount), 1);

        for (var i = 0; i < count; i++)
        {
            AddCell(i < values.Length ? values[i] : string.Empty);
        }
    }

    /// <summary>Cells in display order.</summary>
    public ObservableCollection<TableCellViewModel> Cells { get; } = new();

    /// <summary>Appends a cell.</summary>
    public void AddCell(string text)
    {
        var cell = new TableCellViewModel(text);
        cell.PropertyChanged += OnCellPropertyChanged;
        Cells.Add(cell);
    }

    /// <summary>Removes the last cell while keeping at least one.</summary>
    public void RemoveLastCell()
    {
        if (Cells.Count <= 1)
        {
            return;
        }

        var cell = Cells[^1];
        cell.PropertyChanged -= OnCellPropertyChanged;
        Cells.RemoveAt(Cells.Count - 1);
    }

    private void OnCellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableCellViewModel.Text))
        {
            CellChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>One editable cell of a table block.</summary>
public sealed class TableCellViewModel : ObservableObject
{
    private string _text;

    /// <summary>Creates a cell with initial text.</summary>
    public TableCellViewModel(string text)
    {
        _text = text ?? string.Empty;
    }

    /// <summary>Cell contents.</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }
}
