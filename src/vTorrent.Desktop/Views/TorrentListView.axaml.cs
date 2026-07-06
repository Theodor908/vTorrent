using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.ViewModels.Dialogs;

namespace vTorrent.Desktop.Views;

public partial class TorrentListView : UserControl
{
    private TorrentViewModel? _rightClickedTorrent;
    private readonly Dictionary<string, DataGridColumn> _columnMap = new();
    private ContextMenu? _headerContextMenu;
    private bool _hasAutoFitted;

    public TorrentListView()
    {
        InitializeComponent();

        // Handle right-click to select the row before context menu opens
        TorrentDataGrid.AddHandler(PointerPressedEvent, OnDataGridPointerPressed, RoutingStrategies.Tunnel);

        // Handle right-click on column headers for visibility context menu
        TorrentDataGrid.AddHandler(PointerReleasedEvent, OnDataGridHeaderPointerReleased, RoutingStrategies.Tunnel);

        // Handle double-click to open torrent folder
        TorrentDataGrid.DoubleTapped += OnDataGridDoubleTapped;

        // Handle selection changed to sync with ViewModel
        TorrentDataGrid.SelectionChanged += OnDataGridSelectionChanged;

        // Handle context menu opening to prevent it on empty space
        if (TorrentDataGrid.ContextMenu != null)
        {
            TorrentDataGrid.ContextMenu.Opening += OnContextMenuOpening;
        }

        // Wire up menu item click handlers
        ResumeMenuItem.Click += OnResumeMenuItemClick;
        PauseMenuItem.Click += OnPauseMenuItemClick;
        ForceStartMenuItem.Click += OnForceStartMenuItemClick;
        ForceRecheckMenuItem.Click += OnForceRecheckMenuItemClick;
        SuperSeedingMenuItem.Click += OnSuperSeedingMenuItemClick;
        MoveToTopMenuItem.Click += OnMoveToTopMenuItemClick;
        OptionsMenuItem.Click += OnOptionsMenuItemClick;
        EditTorrentMenuItem.Click += OnEditTorrentMenuItemClick;
        DetailsMenuItem.Click += OnDetailsMenuItemClick;
        DeleteMenuItem.Click += OnDeleteMenuItemClick;

        // Build columns when DataContext is set
        DataContextChanged += OnDataContextChanged;
    }

    #region Programmatic Column Building

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TorrentListViewModel viewModel)
        {
            BuildColumnsFromDefinitions(viewModel);
            BuildHeaderContextMenu(viewModel);

            // Restore persisted widths or auto-fit on first data load
            if (!RestoreColumnWidths(viewModel.ViewState?.ColumnWidths))
            {
                INotifyCollectionChanged items = viewModel.FilteredTorrents;
                void OnFirstData(object? s, NotifyCollectionChangedEventArgs e)
                {
                    if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
                    {
                        items.CollectionChanged -= OnFirstData;
                        AutoFitColumns();
                    }
                };
                items.CollectionChanged += OnFirstData;
            }

            // Persist column widths when user finishes resizing (HeaderPointerReleased fires on resize-end)
            foreach (var column in _columnMap.Values)
            {
                column.HeaderPointerReleased += (_, _) => SaveColumnWidths();
            }
        }
    }

    private void BuildColumnsFromDefinitions(TorrentListViewModel viewModel)
    {
        TorrentDataGrid.Columns.Clear();
        _columnMap.Clear();

        foreach (var colDef in viewModel.ColumnDefinitions)
        {
            DataGridColumn column;

            if (colDef.Key == "Name")
            {
                column = CreateNameColumn(colDef);
            }
            else if (colDef.Key == "Progress")
            {
                column = CreateProgressColumn(colDef);
            }
            else
            {
                column = CreateTextColumn(colDef);
            }

            column.IsVisible = colDef.IsVisible;
            column.CanUserSort = true;

            // Subscribe to visibility changes
            colDef.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModels.ColumnDefinition.IsVisible))
                {
                    column.IsVisible = colDef.IsVisible;
                }
            };

            TorrentDataGrid.Columns.Add(column);
            _columnMap[colDef.Key] = column;
        }
    }

    private DataGridTemplateColumn CreateNameColumn(ViewModels.ColumnDefinition colDef)
    {
        return new DataGridTemplateColumn
        {
            Header = colDef.Header,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = colDef.MinWidth,
            SortMemberPath = colDef.SortMemberPath,
            CellTemplate = new FuncDataTemplate<TorrentViewModel>((_, _) =>
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var icon = new Label
                {
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(0)
                };
                icon.Bind(Label.ContentProperty, new Binding("State")
                {
                    Converter = StateToIconConverter.Instance
                });
                icon.Bind(Label.ForegroundProperty, new Binding("StatusColor")
                {
                    Converter = StringToBrushConverter.Instance
                });
                icon.Bind(ToolTip.TipProperty, new Binding("StatusTooltip"));

                // Apply Phosphor font from resources
                if (Application.Current!.TryFindResource("Phosphor", out var font) && font is FontFamily ff)
                    icon.FontFamily = ff;

                var text = new TextBlock
                {
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                text.Bind(TextBlock.TextProperty, new Binding("EffectiveDisplayName"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextPrimary"));
                text.Bind(ToolTip.TipProperty, new Binding("EffectiveDisplayName"));

                panel.Children.Add(icon);
                panel.Children.Add(text);
                return panel;
            })
        };
    }

    private DataGridTemplateColumn CreateProgressColumn(ViewModels.ColumnDefinition colDef)
    {
        return new DataGridTemplateColumn
        {
            Header = colDef.Header,
            Width = new DataGridLength(150),
            MinWidth = colDef.MinWidth,
            SortMemberPath = colDef.SortMemberPath,
            CellTemplate = new FuncDataTemplate<TorrentViewModel>((_, _) =>
            {
                var bar = new ProgressBar
                {
                    Maximum = 100,
                    Height = 6,
                    MinWidth = 100,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(3)
                };
                bar.Bind(ProgressBar.ValueProperty, new Binding("ProgressPercent"));
                bar.Bind(ProgressBar.ForegroundProperty, new Binding("State")
                {
                    Converter = StateToProgressColorConverter.Instance
                });
                bar.Bind(ProgressBar.BackgroundProperty, bar.GetResourceObservable("CardBackground"));
                bar.Bind(ToolTip.TipProperty, new Binding("ProgressTooltip"));
                return bar;
            })
        };
    }

    private DataGridTextColumn CreateTextColumn(ViewModels.ColumnDefinition colDef)
    {
        var col = new DataGridTextColumn
        {
            Header = colDef.Header,
            Binding = new Binding(colDef.BindingPath),
            Width = DataGridLength.Auto,
            SortMemberPath = colDef.SortMemberPath
        };

        if (colDef.MinWidth > 0)
            col.MinWidth = colDef.MinWidth;

        return col;
    }

    /// <summary>
    /// Auto-fit column widths to visible content. Runs once on first data load.
    /// </summary>
    private void AutoFitColumns()
    {
        if (_hasAutoFitted) return;
        _hasAutoFitted = true;

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (key, column) in _columnMap)
            {
                if (key == "Name" || key == "Progress") continue;

                if (column is DataGridTextColumn textCol)
                {
                    var minWidth = textCol.MinWidth;

                    // Measure header text width to ensure headers are never clipped
                    var headerWidth = MeasureHeaderWidth(column);

                    textCol.Width = DataGridLength.SizeToCells;

                    Dispatcher.UIThread.Post(() =>
                    {
                        var actualWidth = column.ActualWidth;
                        if (actualWidth > 0)
                        {
                            var fitWidth = Math.Max(Math.Max(actualWidth + 8, headerWidth), minWidth);
                            column.Width = new DataGridLength(fitWidth);
                        }
                    }, DispatcherPriority.Background);
                }
            }
        }, DispatcherPriority.Background);
    }

    private static double MeasureHeaderWidth(DataGridColumn column)
    {
        if (column.Header is not string headerText || string.IsNullOrEmpty(headerText))
            return 0;

        var formatted = new FormattedText(
            headerText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            null);

        // Add padding for sort indicator and cell margins
        return formatted.Width + 28;
    }

    /// <summary>
    /// Restore column widths from persisted ViewState.
    /// </summary>
    private bool RestoreColumnWidths(Dictionary<string, double>? columnWidths)
    {
        if (columnWidths == null || columnWidths.Count == 0)
            return false;

        foreach (var (key, width) in columnWidths)
        {
            if (_columnMap.TryGetValue(key, out var column) && key != "Name")
            {
                var effectiveWidth = Math.Max(width, column.MinWidth);
                column.Width = new DataGridLength(effectiveWidth);
            }
        }
        return true;
    }

    /// <summary>
    /// Save current column widths via ViewState persistence.
    /// </summary>
    private void SaveColumnWidths()
    {
        if (DataContext is not TorrentListViewModel viewModel)
            return;

        var widths = new Dictionary<string, double>();
        foreach (var (key, column) in _columnMap)
        {
            if (key == "Name") continue;
            if (column.ActualWidth > 0)
                widths[key] = column.ActualWidth;
        }

        viewModel.UpdateColumnWidths(widths);
    }

    #endregion

    #region Header Context Menu

    private void BuildHeaderContextMenu(TorrentListViewModel viewModel)
    {
        var contextMenu = new ContextMenu();

        // Style to match the row context menu
        contextMenu.Styles.Add(CreateHeaderContextMenuStyle());

        foreach (var colDef in viewModel.ColumnDefinitions)
        {
            var menuItem = new MenuItem
            {
                Header = colDef.Header,
                Icon = new CheckBox
                {
                    IsChecked = colDef.IsVisible,
                    IsEnabled = !colDef.IsNameColumn,
                    IsHitTestVisible = false
                }
            };

            if (colDef.IsNameColumn)
            {
                menuItem.IsEnabled = false;
            }

            var def = colDef;
            menuItem.Click += (s, e) =>
            {
                if (!def.IsNameColumn)
                {
                    viewModel.ToggleColumnVisibilityCommand.Execute(def.Key);
                    if (menuItem.Icon is CheckBox cb)
                        cb.IsChecked = def.IsVisible;
                }
            };

            colDef.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModels.ColumnDefinition.IsVisible) && menuItem.Icon is CheckBox cb)
                    cb.IsChecked = colDef.IsVisible;
            };

            contextMenu.Items.Add(menuItem);
        }

        _headerContextMenu = contextMenu;
    }

    private Style CreateHeaderContextMenuStyle()
    {
        var style = new Style(x => x.OfType<ContextMenu>());
        style.Setters.Add(new Setter(ContextMenu.PaddingProperty, new Thickness(4)));
        return style;
    }

    private void OnDataGridHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;

        // Walk up the visual tree to find a DataGridColumnHeader
        var source = e.Source as Control;
        while (source != null)
        {
            if (source is DataGridColumnHeader)
            {
                if (_headerContextMenu != null)
                {
                    _headerContextMenu.Open(source);
                    e.Handled = true;
                }
                return;
            }
            source = source.Parent as Control;
        }
    }

    #endregion

    #region Row Context Menu & Selection

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel the context menu if the right-click was not on a torrent row
        if (_rightClickedTorrent == null)
        {
            e.Cancel = true;
        }
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is TorrentListViewModel viewModel)
        {
            viewModel.SelectedTorrents.Clear();
            foreach (var item in TorrentDataGrid.SelectedItems)
            {
                if (item is TorrentViewModel torrent)
                {
                    viewModel.SelectedTorrents.Add(torrent);
                }
            }
        }
    }

    private List<TorrentViewModel> GetSelectedTorrents()
    {
        var selected = new List<TorrentViewModel>();
        foreach (var item in TorrentDataGrid.SelectedItems)
        {
            if (item is TorrentViewModel torrent)
            {
                selected.Add(torrent);
            }
        }
        return selected;
    }

    private void OnDataGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only handle right-click
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        // Find the row that was clicked
        var source = e.Source as Control;
        while (source != null && source is not DataGridRow)
        {
            source = source.Parent as Control;
        }

        if (source is DataGridRow row && row.DataContext is TorrentViewModel torrent)
        {
            // Store the right-clicked torrent for menu actions
            _rightClickedTorrent = torrent;

            // Only change selection if the right-clicked item is not already selected
            // This preserves multi-selection when right-clicking on a selected item
            if (!TorrentDataGrid.SelectedItems.Contains(torrent))
            {
                TorrentDataGrid.SelectedItem = torrent;
            }

            // Update the ViewModel's SelectedTorrent
            if (DataContext is TorrentListViewModel viewModel)
            {
                viewModel.SelectedTorrent = torrent;
            }
        }
        else
        {
            _rightClickedTorrent = null;
        }
    }

    #endregion

    #region Menu Item Handlers

    private void OnResumeMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0)
            return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
            {
                viewModel.ResumeTorrentCommand.Execute(torrent);
            }
        }
    }

    private void OnPauseMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0)
            return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
            {
                viewModel.PauseTorrentCommand.Execute(torrent);
            }
        }
    }

    private void OnForceStartMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0) return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
                viewModel.ForceStartTorrentCommand.Execute(torrent);
        }
    }

    private void OnForceRecheckMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0) return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
                viewModel.ForceRecheckTorrentCommand.Execute(torrent);
        }
    }

    private void OnSuperSeedingMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0) return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
            {
                viewModel.ToggleSuperSeedingCommand.Execute(torrent);
            }
        }
    }

    private void OnMoveToTopMenuItemClick(object? sender, RoutedEventArgs e)
    {
        var selectedTorrents = GetSelectedTorrents();
        if (selectedTorrents.Count == 0) return;

        if (DataContext is TorrentListViewModel viewModel)
        {
            foreach (var torrent in selectedTorrents)
                viewModel.SetQueuePositionTopCommand.Execute(torrent);
        }
    }

    private async void OnOptionsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selectedTorrents = GetSelectedTorrents();
            if (selectedTorrents.Count == 0)
                return;

            if (DataContext is not TorrentListViewModel viewModel)
                return;

            // Find the parent window
            var window = this.FindAncestorOfType<Window>();
            if (window == null)
                return;

            // Get the torrent manager service from the ViewModel
            var torrentManager = viewModel.TorrentManager;
            if (torrentManager == null)
                return;

            // Create the ViewModel with services
            var optionsViewModel = new TorrentOptionsViewModel(torrentManager, torrentManager.SettingsManager);

            // Show the dialog with all selected torrents
            await TorrentOptionsDialog.ShowDialogAsync(window, optionsViewModel, selectedTorrents);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnOptionsMenuItemClick: {ex.Message}");
        }
    }

    private void OnEditTorrentMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not TorrentListViewModel vm) return;
        var selected = vm.SelectedTorrent;
        if (selected == null) return;

        vm.RaiseEditTorrentRequested(selected.InfoHash);
    }

    private void OnDetailsMenuItemClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selectedTorrents = GetSelectedTorrents();
            if (selectedTorrents.Count == 0) return;

            if (DataContext is not TorrentListViewModel viewModel) return;

            var torrentManager = viewModel.TorrentManager;
            if (torrentManager == null) return;

            // Open details for the first selected torrent
            var selected = selectedTorrents[0];
            var vm = new TorrentDetailsViewModel(selected.InfoHash, torrentManager);
            var window = new TorrentDetailsWindow { DataContext = vm };
            window.Closed += (_, _) => vm.Dispose();

            var parent = this.FindAncestorOfType<Window>();
            if (parent != null)
                window.Show(parent);
            else
                window.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening torrent details: {ex.Message}");
        }
    }

    private async void OnDeleteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selectedTorrents = GetSelectedTorrents();
            if (selectedTorrents.Count == 0)
                return;

            if (DataContext is not TorrentListViewModel viewModel)
                return;

            var window = this.FindAncestorOfType<Window>();
            if (window == null)
                return;

            var settingsManager = viewModel.TorrentManager?.SettingsManager;
            var defaultSecureWipe = settingsManager?.Current?.Privacy.SecureDeletion ?? false;
            var defaultWipeMetadata = settingsManager?.Current?.Privacy.SecureDeletionIncludeMetadata ?? false;

            var (confirmed, deleteFiles, secureWipe, wipeMetadata) = await DeleteTorrentDialog.ShowDialogAsync(
                window, selectedTorrents, defaultSecureWipe, defaultWipeMetadata);

            if (!confirmed)
                return;

            if (deleteFiles)
            {
                // Remove all from UI instantly
                var snapshot = selectedTorrents.ToList();
                viewModel.RemoveFromGrid(snapshot);

                // Backend removal with files must be sequential (ExtraFilesDialog per torrent)
                foreach (var torrent in snapshot)
                {
                    try
                    {
                        var result = await Task.Run(() =>
                            viewModel.RemoveFromBackendWithFilesAsync(
                                torrent.InfoHash, secureWipe, wipeMetadata)).ConfigureAwait(false);

                        if (secureWipe)
                        {
                            viewModel.TorrentManager?.NotificationService?.Show(
                                "Secure Deletion Complete",
                                $"Securely deleted torrent '{torrent.Name}'",
                                NotificationType.Success);
                        }

                        if (result?.HasExtraFiles == true && result.TorrentDirectory != null)
                        {
                            // ExtraFilesDialog must run on UI thread
                            var deleteAll = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                ExtraFilesDialog.ShowDialogAsync(
                                    window, torrent.Name, result.ExtraFiles));

                            if (deleteAll && viewModel.TorrentManager != null && result.SavePath != null)
                            {
                                await Task.Run(() =>
                                    viewModel.TorrentManager.Service.DeleteRemainingFilesAsync(
                                        result.TorrentDirectory, result.SavePath)).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error removing torrent {torrent.Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Remove all from UI instantly, then fire backend removals concurrently
                var snapshot = selectedTorrents.ToList();
                viewModel.RemoveFromGrid(snapshot);

                // Run backend removals on thread pool to keep UI thread free.
                // Without Task.Run, the synchronous preamble of each RemoveTorrentAsync
                // (engine stop → SetPhase → StatusChanged → InvokeOnUIThread) runs
                // on the UI thread before yielding.
                await Task.Run(() => Task.WhenAll(snapshot.Select(torrent =>
                    viewModel.RemoveFromBackendAsync(torrent.InfoHash)))).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnDeleteMenuItemClick: {ex.Message}");
        }
    }

    #endregion

    #region Double-click / Open Folder

    private void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Find the row that was double-clicked
        var source = e.Source as Control;
        while (source != null && source is not DataGridRow)
        {
            source = source.Parent as Control;
        }

        if (source is DataGridRow row && row.DataContext is TorrentViewModel torrent)
        {
            OpenTorrentFolder(torrent);
        }
    }

    private void OpenTorrentFolder(TorrentViewModel torrent)
    {
        if (string.IsNullOrEmpty(torrent.SavePath))
            return;

        var folderPath = torrent.SavePath;

        // For multi-file torrents, navigate into the torrent's root folder if it exists
        if (!string.IsNullOrEmpty(torrent.Name))
        {
            var torrentDir = Path.Combine(folderPath, torrent.Name);
            if (Directory.Exists(torrentDir))
                folderPath = torrentDir;
        }

        // Check if the folder exists
        if (!Directory.Exists(folderPath))
            return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Silently fail if folder cannot be opened
        }
    }

    #endregion
}
