using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NotedX.Desktop.ViewModels;

namespace NotedX.Desktop.Views;

/// <summary>Command palette over the full-text search index.</summary>
public partial class SearchOverlay : UserControl
{
    private MainViewModel? _main;

    /// <summary>Creates the search overlay.</summary>
    public SearchOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_main is not null)
        {
            _main.Search.Results.CollectionChanged -= OnResultsChanged;
        }

        _main = DataContext as MainViewModel;

        if (_main is not null)
        {
            _main.Search.Results.CollectionChanged += OnResultsChanged;
        }
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SelectFirstIfNeeded();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    SelectFirstIfNeeded();
                },
                DispatcherPriority.Input);
        }
        else
        {
            ResultsList.SelectedIndex = -1;
        }
    }

    private void SelectFirstIfNeeded()
    {
        if (_main is null)
        {
            return;
        }

        var count = _main.Search.Results.Count;

        if (count == 0)
        {
            ResultsList.SelectedIndex = -1;
        }
        else if (ResultsList.SelectedIndex < 0 || ResultsList.SelectedIndex >= count)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _main.CloseSearchCommand.Execute(null);
                break;

            case Key.Enter:
            {
                e.Handled = true;
                var result = ResultsList.SelectedItem as SearchResultViewModel;
                if (result is null && _main.Search.Results.Count > 0)
                {
                    result = _main.Search.Results[0];
                }

                if (result is not null)
                {
                    _main.Search.ChooseCommand.Execute(result);
                }

                break;
            }

            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int offset)
    {
        if (_main is null)
        {
            return;
        }

        var count = _main.Search.Results.Count;
        if (count == 0)
        {
            return;
        }

        var next = Math.Clamp(ResultsList.SelectedIndex + offset, 0, count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }
}
