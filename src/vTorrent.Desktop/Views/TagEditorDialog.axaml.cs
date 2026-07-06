using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace vTorrent.Desktop.Views;

public partial class TagEditorDialog : Window
{
    public string TagName => NameTextBox.Text ?? string.Empty;
    public string? Color => string.IsNullOrWhiteSpace(ColorTextBox.Text) ? null : ColorTextBox.Text;
    public bool IsDeleted { get; private set; }

    private readonly int _tagId;
    private readonly bool _isNewTag;

    public TagEditorDialog()
    {
        InitializeComponent();
        _isNewTag = true;
        TitleText.Text = "New Tag";
        DeleteButton.IsVisible = false;

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public TagEditorDialog(int tagId, string name, string? color) : this()
    {
        _tagId = tagId;
        _isNewTag = false;
        TitleText.Text = "Edit Tag";
        DeleteButton.IsVisible = true;

        NameTextBox.Text = name;
        ColorTextBox.Text = color ?? string.Empty;
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

    /// <summary>
    /// Show the tag editor dialog for an existing tag
    /// </summary>
    public static async Task<TagEditorResult?> ShowEditDialogAsync(Window owner, int tagId, string name, string? color)
    {
        var dialog = new TagEditorDialog(tagId, name, color);
        var result = await dialog.ShowDialog<bool?>(owner);

        if (result != true)
            return null;

        return new TagEditorResult
        {
            TagId = tagId,
            Name = dialog.TagName,
            Color = dialog.Color,
            IsDeleted = dialog.IsDeleted
        };
    }

    /// <summary>
    /// Show the tag editor dialog for creating a new tag
    /// </summary>
    public static async Task<TagEditorResult?> ShowCreateDialogAsync(Window owner)
    {
        var dialog = new TagEditorDialog();
        var result = await dialog.ShowDialog<bool?>(owner);

        if (result != true)
            return null;

        return new TagEditorResult
        {
            TagId = 0,
            Name = dialog.TagName,
            Color = dialog.Color,
            IsDeleted = false
        };
    }
}

public class TagEditorResult
{
    public int TagId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public bool IsDeleted { get; set; }
}
