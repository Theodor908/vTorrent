using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using vTorrent.Abstractions.Settings;
using vTorrent.Desktop.ViewModels.Settings;

namespace vTorrent.Desktop.Views;

/// <summary>
/// Code-behind for the Settings Window.
/// Handles view-specific events like folder browsing.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Wire up events when DataContext is set
        DataContextChanged += OnDataContextChanged;

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Enable window dragging from title bar
        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += OnTitleBarPointerPressed;
        }

        // Wire Change Password button
        var changePasswordBtn = this.FindControl<Button>("ChangePasswordButton");
        if (changePasswordBtn != null)
            changePasswordBtn.Click += OnChangePasswordClicked;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel)
        {
            // Unsubscribe from any previous handlers
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.BrowseDefaultSavePathRequested -= OnBrowseDefaultSavePath;
            viewModel.BrowseIncompleteSavePathRequested -= OnBrowseIncompleteSavePath;
            viewModel.BrowseLogFilePathRequested -= OnBrowseLogFilePath;
            viewModel.PropagationRequested -= OnPropagationRequested;
            viewModel.SaveAsProfileRequested -= OnSaveAsProfileRequested;

            // Subscribe to events
            viewModel.CloseRequested += OnCloseRequested;
            viewModel.BrowseDefaultSavePathRequested += OnBrowseDefaultSavePath;
            viewModel.BrowseIncompleteSavePathRequested += OnBrowseIncompleteSavePath;
            viewModel.BrowseLogFilePathRequested += OnBrowseLogFilePath;
            viewModel.PropagationRequested += OnPropagationRequested;
            viewModel.SaveAsProfileRequested += OnSaveAsProfileRequested;

            // Wire schedule grid painting after layout
            WireScheduleGridEvents();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private async void OnBrowseDefaultSavePath(object? sender, EventArgs e)
    {
        try
        {
            var folder = await SelectFolderAsync("Select default download location");
            if (folder != null && DataContext is SettingsWindowViewModel vm)
            {
                vm.SetDefaultSavePath(folder);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnBrowseDefaultSavePath: {ex.Message}");
        }
    }

    private async void OnBrowseIncompleteSavePath(object? sender, EventArgs e)
    {
        try
        {
            var folder = await SelectFolderAsync("Select incomplete downloads location");
            if (folder != null && DataContext is SettingsWindowViewModel vm)
            {
                vm.SetIncompleteSavePath(folder);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnBrowseIncompleteSavePath: {ex.Message}");
        }
    }

    private async void OnBrowseLogFilePath(object? sender, EventArgs e)
    {
        try
        {
            var folder = await SelectFolderAsync("Select log file location");
            if (folder != null && DataContext is SettingsWindowViewModel vm)
            {
                vm.SetLogFilePath(folder);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnBrowseLogFilePath: {ex.Message}");
        }
    }

    private async void OnSaveAsProfileRequested(object? sender, EventArgs e)
    {
        try
        {
            var dialog = new Settings.SaveProfileDialog();
            await dialog.ShowDialog(this);

            if (dialog.DataContext is SaveProfileDialogViewModel vm && vm.IsConfirmed && vm.Result != null)
            {
                if (DataContext is SettingsWindowViewModel settingsVm)
                {
                    // Save the profile via ProfilesTab
                    await settingsVm.ProfilesTab.SaveNewProfileAsync(vm.Result);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnSaveAsProfileRequested: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task<string?> SelectFolderAsync(string title)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return null;

        var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            return result[0].Path.LocalPath;
        }

        return null;
    }

    private async Task<SettingsPropagationMode> OnPropagationRequested(
        string settingName, string displayName, string oldVal, string newVal)
    {
        return await SettingsChangeDialog.ShowDialogAsync(this, displayName, oldVal, newVal);
    }

    private async void OnExportProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;
        if (vm.ProfilesTab.SelectedProfile == null) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Profile",
            SuggestedFileName = vm.ProfilesTab.SelectedProfile.Name + ".vtprofile.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("vTorrent Profile") { Patterns = new[] { "*.vtprofile.json" } }
            }
        });

        if (result != null)
        {
            await vm.ProfilesTab.ExportProfileCommand.ExecuteAsync(result.Path.LocalPath);
        }
    }

    private async void OnImportProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Profile",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("vTorrent Profile") { Patterns = new[] { "*.vtprofile.json" } }
            }
        });

        if (result.Count > 0)
        {
            await vm.ProfilesTab.ImportProfileCommand.ExecuteAsync(result[0].Path.LocalPath);
        }
    }

    private async void OnExportSchedule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Schedule",
            SuggestedFileName = "schedule.vtschedule.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("vTorrent Schedule") { Patterns = new[] { "*.vtschedule.json" } }
            }
        });

        if (result != null)
        {
            await vm.ProfilesTab.ExportScheduleCommand.ExecuteAsync(result.Path.LocalPath);
        }
    }

    private async void OnImportSchedule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Schedule",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("vTorrent Schedule") { Patterns = new[] { "*.vtschedule.json" } }
            }
        });

        if (result.Count > 0)
        {
            await vm.ProfilesTab.ImportScheduleCommand.ExecuteAsync(result[0].Path.LocalPath);
        }
    }

    private async void OnChangePasswordClicked(object? sender, RoutedEventArgs e)
    {
        var result = await Settings.ChangePasswordDialog.ShowDialogAsync(this);
        if (result != null && DataContext is SettingsWindowViewModel vm)
        {
            vm.ServerTab.LocalPasswordHash = result;
        }
    }

    // ── Schedule Grid Painting ──

    private bool _isPaintingSchedule;

    private void OnScheduleGridCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isPaintingSchedule = true;
            PaintScheduleCellFromSender(sender);
            e.Handled = true;
        }
    }

    private void OnScheduleGridCellPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPaintingSchedule)
        {
            PaintScheduleCellFromSender(sender);
        }
    }

    private void OnScheduleGridCellPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPaintingSchedule = false;
    }

    private void PaintScheduleCellFromSender(object? sender)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not ScheduleCellViewModel cellVm) return;
        if (DataContext is not SettingsWindowViewModel settingsVm) return;

        UpdatePaintMode(settingsVm.ProfilesTab);

        int cellIndex = cellVm.DayIndex * 24 + cellVm.HourIndex;
        settingsVm.ProfilesTab.PaintCell(cellIndex);
    }

    private void UpdatePaintMode(ProfilesSettingsTabViewModel profilesTab)
    {
        // Paint mode is now driven by SelectedPaintOption binding via OnSelectedPaintOptionChanged.
        // No manual update needed — the ComboBox binding handles it.
    }

    private async void OnPaintAllDays(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;
        vm.ProfilesTab.PaintAllDays();
        try { await vm.ProfilesTab.FlushScheduleAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FlushScheduleAsync failed: {ex.Message}"); }
    }

    private async void OnPaintDay(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel vm) return;
        var dayCombo = this.FindControl<ComboBox>("PaintDayCombo");
        if (dayCombo?.SelectedIndex is int dayIndex and >= 0 and < 7)
        {
            vm.ProfilesTab.PaintDay(dayIndex);
            try { await vm.ProfilesTab.FlushScheduleAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FlushScheduleAsync failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Wire up schedule grid cell events after the visual tree is rendered.
    /// Called when the DataContext changes (scheduleEnabled toggled or initial load).
    /// </summary>
    private void WireScheduleGridEvents()
    {
        var gridControl = this.FindControl<ItemsControl>("ScheduleGridCells");
        if (gridControl == null) return;

        // Attach events to the ItemsControl itself for capture
        gridControl.AddHandler(PointerPressedEvent, OnScheduleGridPointerPressed, RoutingStrategies.Tunnel);
        gridControl.AddHandler(PointerMovedEvent, OnScheduleGridPointerMoved, RoutingStrategies.Tunnel);
        gridControl.AddHandler(PointerReleasedEvent, OnScheduleGridPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnScheduleGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isPaintingSchedule = true;
        PaintCellAtPointer(e);
    }

    private void OnScheduleGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPaintingSchedule) return;
        PaintCellAtPointer(e);
    }

    private async void OnScheduleGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPaintingSchedule) return;
        _isPaintingSchedule = false;

        try
        {
            if (DataContext is SettingsWindowViewModel vm)
                await vm.ProfilesTab.FlushScheduleAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FlushScheduleAsync failed: {ex.Message}");
        }
    }

    private void PaintCellAtPointer(PointerEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel settingsVm) return;

        var gridControl = this.FindControl<ItemsControl>("ScheduleGridCells");
        if (gridControl == null) return;

        // Find the Border under the pointer
        var position = e.GetPosition(gridControl);
        var hit = gridControl.InputHitTest(position);

        // Walk up to find a Border whose DataContext is ScheduleCellViewModel
        if (hit is Avalonia.Visual visual)
        {
            var current = visual;
            while (current != null)
            {
                if (current is Border border && border.DataContext is ScheduleCellViewModel cellVm)
                {
                    UpdatePaintMode(settingsVm.ProfilesTab);
                    int cellIndex = cellVm.DayIndex * 24 + cellVm.HourIndex;
                    settingsVm.ProfilesTab.PaintCell(cellIndex);
                    return;
                }
                current = current.GetVisualParent() as Avalonia.Visual;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is SettingsWindowViewModel viewModel)
        {
            // Always flush on close: PaintCell schedules a debounced 300 ms flush, and
            // the user may close before it fires. Bounded wait per project rule against
            // unbounded sync-over-async on shutdown. Idempotent — cancels any pending
            // debounce and writes the current grid once.
            _isPaintingSchedule = false;
            try { viewModel.ProfilesTab.FlushScheduleAsync().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FlushScheduleAsync on close failed: {ex.Message}"); }

            viewModel.ProfilesTab.Dispose();

            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.BrowseDefaultSavePathRequested -= OnBrowseDefaultSavePath;
            viewModel.BrowseIncompleteSavePathRequested -= OnBrowseIncompleteSavePath;
            viewModel.BrowseLogFilePathRequested -= OnBrowseLogFilePath;
            viewModel.PropagationRequested -= OnPropagationRequested;
            viewModel.SaveAsProfileRequested -= OnSaveAsProfileRequested;
            viewModel.AdvancedTab.UnsubscribeNetworkChanges();
        }

        base.OnClosed(e);
    }
}
