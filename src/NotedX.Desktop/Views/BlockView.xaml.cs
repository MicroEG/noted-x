using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NotedX.Desktop.ViewModels;

namespace NotedX.Desktop.Views;

/// <summary>Renders one block and forwards text editing gestures to the page editor.</summary>
public partial class BlockView : UserControl
{
    private BlockEditorViewModel? _block;

    /// <summary>Creates the block view.</summary>
    public BlockView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private TextBox? ActiveTextBox => _block?.IsCode == true ? CodeBox : _block?.IsTextual == true ? EditorBox : null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
        FocusPendingCaret();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (IsLoaded)
        {
            Attach();
        }
    }

    private void Attach()
    {
        if (_block is not null)
        {
            return;
        }

        _block = DataContext as BlockEditorViewModel;
        if (_block is not null)
        {
            _block.FocusRequested += OnFocusRequested;
        }
    }

    private void Detach()
    {
        if (_block is null)
        {
            return;
        }

        _block.FocusRequested -= OnFocusRequested;
        _block = null;
    }

    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(FocusPendingCaret, DispatcherPriority.Input);

    private void FocusPendingCaret()
    {
        if (_block is null)
        {
            return;
        }

        var box = ActiveTextBox;
        if (box is null)
        {
            return;
        }

        var caret = _block.ConsumePendingCaret();
        box.Focus();

        if (caret is { } position)
        {
            box.CaretIndex = Math.Clamp(position, 0, box.Text.Length);
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_block is not null && sender is TextBox box)
        {
            _block.Owner.HandleTextChanged(_block, box.Text, box.CaretIndex);
        }
    }

    private async void OnBlockPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_block is null || sender is not TextBox box)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when !_block.IsCode && (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                e.Handled = true;
                await _block.Owner.HandleEnterAsync(_block, box.CaretIndex);
                break;

            case Key.Back when box.CaretIndex == 0 && box.SelectionLength == 0:
                if (_block.Owner.HandleBackspace(_block))
                {
                    e.Handled = true;
                }

                break;

            case Key.Up when box.CaretIndex == 0:
                if (_block.Owner.HandleArrowNavigation(_block, moveUp: true))
                {
                    e.Handled = true;
                }

                break;

            case Key.Down when box.CaretIndex >= box.Text.Length:
                if (_block.Owner.HandleArrowNavigation(_block, moveUp: false))
                {
                    e.Handled = true;
                }

                break;

            case Key.Escape:
                _block.IsSlashMenuOpen = false;
                break;
        }
    }

    private async void OnSlashItemClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null &&
            sender is FrameworkElement { DataContext: SlashCommandItem command })
        {
            await _block.Owner.ApplySlashCommandAsync(_block, command);
        }
    }

    private async void OnOpenMediaClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            await _block.Owner.OpenAttachmentAsync(_block);
        }
    }

    private async void OnReplaceImageClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            await _block.Owner.ReplaceImageAsync(_block);
        }
    }

    private void OnRemoveMediaClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            _block.Owner.RemoveMedia(_block);
        }
    }

    private void OnAddTableRowClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            _block.Owner.AddTableRow(_block);
        }
    }

    private void OnRemoveTableRowClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null && sender is FrameworkElement { DataContext: TableRowViewModel row })
        {
            _block.Owner.RemoveTableRow(_block, row);
        }
    }

    private void OnAddTableColumnClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            _block.Owner.AddTableColumn(_block);
        }
    }

    private void OnRemoveTableColumnClick(object sender, RoutedEventArgs e)
    {
        if (_block is not null)
        {
            _block.Owner.RemoveTableColumn(_block);
        }
    }
}
