using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Core.Settings;
using vTorrent.Desktop.ViewModels.Dialogs;
using vTorrent.Desktop.ViewModels.Settings;
using vTorrent.Desktop.ViewModels.Tools;
using vTorrent.Desktop.Views;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Extracts dialog creation logic from MainWindow code-behind.
/// Each method creates and shows a specific dialog as a modal window.
/// </summary>
public class DialogService : IDialogService
{
    private readonly ITorrentManagerService? _torrentManager;
    private readonly IServiceProvider? _serviceProvider;

    public DialogService(ITorrentManagerService? torrentManager, IServiceProvider? serviceProvider = null)
    {
        _torrentManager = torrentManager;
        _serviceProvider = serviceProvider;
    }

    public async Task<bool> ShowAddTorrentDialogAsync(Window owner, string filePath)
    {
        var viewModel = new AddTorrentViewModel(_torrentManager);
        return await AddTorrentDialog.ShowDialogAsync(owner, viewModel, filePath);
    }

    public async Task<bool> ShowAddMagnetDialogAsync(Window owner, string? magnetUri = null)
    {
        var viewModel = new AddMagnetLinkViewModel(_torrentManager);

        if (!string.IsNullOrEmpty(magnetUri))
        {
            return await AddMagnetLinkDialog.ShowDialogAsync(owner, viewModel, magnetUri);
        }

        return await AddMagnetLinkDialog.ShowDialogAsync(owner, viewModel);
    }

    public async Task ShowSettingsDialogAsync(Window owner)
    {
        var settingsManager = _torrentManager?.SettingsManager;
        var themeService = _torrentManager?.ThemeService;

        // Resolve server dependencies from DI
        var serverHost = _serviceProvider?.GetService<ServerHostService>();
        var bundleScanner = _serviceProvider?.GetService<WebUIBundleScanner>();
        var profileManager = _serviceProvider?.GetService<ProfileManager>();
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vTorrent");
        var bundlesDir = Path.Combine(dataDir, "webui-bundles");

        var viewModel = new SettingsWindowViewModel(
            settingsManager,
            _torrentManager,
            themeService,
            serverHost,
            bundleScanner,
            bundlesDir,
            profileManager);

        var settingsWindow = new SettingsWindow
        {
            DataContext = viewModel
        };

        await viewModel.InitializeAsync();

        var vpnStatus = _serviceProvider?.GetService<IVpnStatusService>();
        viewModel.AdvancedTab.InitializeVpnStatus(vpnStatus);
        viewModel.AdvancedTab.SubscribeNetworkChanges();

        await settingsWindow.ShowDialog(owner);
    }

    public async Task ShowToolsWindowAsync(Window owner, string? preselectedInfoHash = null, int initialTab = 0)
    {
        var viewModel = new ToolsWindowViewModel(_torrentManager)
        {
            SelectedTabIndex = initialTab,
            PreselectedInfoHash = preselectedInfoHash,
        };

        viewModel.Initialize();

        var toolsWindow = new ToolsWindow
        {
            DataContext = viewModel
        };

        await toolsWindow.ShowDialog(owner);
    }

    public async Task<CategoryEditorResult?> ShowEditCategoryDialogAsync(
        Window owner, int databaseId, string name, string? savePath, string? color)
    {
        return await CategoryEditorDialog.ShowEditDialogAsync(owner, databaseId, name, savePath, color);
    }

    public async Task<CategoryEditorResult?> ShowCreateCategoryDialogAsync(Window owner)
    {
        return await CategoryEditorDialog.ShowCreateDialogAsync(owner);
    }

    public async Task<TagEditorResult?> ShowEditTagDialogAsync(
        Window owner, int databaseId, string name, string? color)
    {
        return await TagEditorDialog.ShowEditDialogAsync(owner, databaseId, name, color);
    }

    public async Task<TagEditorResult?> ShowCreateTagDialogAsync(Window owner)
    {
        return await TagEditorDialog.ShowCreateDialogAsync(owner);
    }
}
