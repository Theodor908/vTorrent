using System.Threading.Tasks;
using Avalonia.Controls;
using vTorrent.Desktop.Views;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Abstracts dialog creation for the main window.
/// All dialogs require an owner Window for modal display.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Show the Add Torrent dialog for a given .torrent file path.
    /// Returns true if the torrent was added successfully.
    /// </summary>
    Task<bool> ShowAddTorrentDialogAsync(Window owner, string filePath);

    /// <summary>
    /// Show the Add Magnet Link dialog.
    /// Returns true if a magnet link was added successfully.
    /// </summary>
    Task<bool> ShowAddMagnetDialogAsync(Window owner, string? magnetUri = null);

    /// <summary>
    /// Show the Settings window as a dialog.
    /// </summary>
    Task ShowSettingsDialogAsync(Window owner);

    /// <summary>
    /// Show the Tools window (Creator/Editor) as a dialog.
    /// </summary>
    Task ShowToolsWindowAsync(Window owner, string? preselectedInfoHash = null, int initialTab = 0);

    /// <summary>
    /// Show the Category Editor dialog in edit mode.
    /// </summary>
    Task<CategoryEditorResult?> ShowEditCategoryDialogAsync(Window owner, int databaseId, string name, string? savePath, string? color);

    /// <summary>
    /// Show the Category Editor dialog in create mode.
    /// </summary>
    Task<CategoryEditorResult?> ShowCreateCategoryDialogAsync(Window owner);

    /// <summary>
    /// Show the Tag Editor dialog in edit mode.
    /// </summary>
    Task<TagEditorResult?> ShowEditTagDialogAsync(Window owner, int databaseId, string name, string? color);

    /// <summary>
    /// Show the Tag Editor dialog in create mode.
    /// </summary>
    Task<TagEditorResult?> ShowCreateTagDialogAsync(Window owner);
}
