using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

public partial class SaveProfileDialogViewModel : ObservableObject
{
    // ── Observable Properties ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _profileName = "";

    [ObservableProperty]
    private string _selectedColor = "#2196F3";

    // ── Computed Properties ──

    public bool CanCreate => !string.IsNullOrWhiteSpace(ProfileName);

    // ── Static Colors ──

    public static List<string> AvailableColors { get; } = new()
    {
        "#F44336", "#E91E63", "#9C27B0", "#2196F3", "#009688",
        "#4CAF50", "#FF9800", "#795548", "#607D8B", "#78909C"
    };

    // ── Result ──

    public bool IsConfirmed { get; private set; }

    public ProfileSettings? Result { get; private set; }

    // ── Commands ──

    [RelayCommand]
    private void Create()
    {
        if (!CanCreate) return;

        Result = new ProfileSettings
        {
            Name = ProfileName.Trim(),
            Color = SelectedColor,
            Scope = "performance",
            Settings = new ProfileSettingsValues()
        };

        IsConfirmed = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        Result = null;
    }
}
