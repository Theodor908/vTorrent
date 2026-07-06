using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Input;

namespace vTorrent.Desktop.Views;

public partial class CategoryEditorDialog : Window
{
    public string CategoryName => NameTextBox.Text ?? string.Empty;
    public string? SavePath => string.IsNullOrWhiteSpace(SavePathTextBox.Text) ? null : SavePathTextBox.Text;
    public string? Color => string.IsNullOrWhiteSpace(ColorTextBox.Text) ? null : ColorTextBox.Text;
    public bool IsDeleted { get; private set; }

    private readonly int _categoryId;
    private readonly bool _isNewCategory;

    public CategoryEditorDialog()
    {
        InitializeComponent();
        _isNewCategory = true;
        TitleText.Text = "New Category";
        DeleteButton.IsVisible = false;

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);
    }

    public CategoryEditorDialog(int categoryId, string name, string? savePath, string? color) : this()
    {
        _categoryId = categoryId;
        _isNewCategory = false;
        TitleText.Text = "Edit Category";
        DeleteButton.IsVisible = true;

        NameTextBox.Text = name;
        SavePathTextBox.Text = savePath ?? string.Empty;
        ColorTextBox.Text = color ?? string.Empty;
    }
    
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            return;
        }
        Close(true);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        IsDeleted = true;
        Close(true);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Default Save Path",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            SavePathTextBox.Text = folders[0].Path.LocalPath;
        }
    }

    /// <summary>
    /// Show the category editor dialog for an existing category
    /// </summary>
    public static async Task<CategoryEditorResult?> ShowEditDialogAsync(Window owner, int categoryId, string name, string? savePath, string? color)
    {
        var dialog = new CategoryEditorDialog(categoryId, name, savePath, color);
        var result = await dialog.ShowDialog<bool?>(owner);

        if (result != true)
            return null;

        return new CategoryEditorResult
        {
            CategoryId = categoryId,
            Name = dialog.CategoryName,
            SavePath = dialog.SavePath,
            Color = dialog.Color,
            IsDeleted = dialog.IsDeleted
        };
    }

    /// <summary>
    /// Show the category editor dialog for creating a new category
    /// </summary>
    public static async Task<CategoryEditorResult?> ShowCreateDialogAsync(Window owner)
    {
        var dialog = new CategoryEditorDialog();
        var result = await dialog.ShowDialog<bool?>(owner);

        if (result != true)
            return null;

        return new CategoryEditorResult
        {
            CategoryId = 0,
            Name = dialog.CategoryName,
            SavePath = dialog.SavePath,
            Color = dialog.Color,
            IsDeleted = false
        };
    }
}

public class CategoryEditorResult
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SavePath { get; set; }
    public string? Color { get; set; }
    public bool IsDeleted { get; set; }
}
