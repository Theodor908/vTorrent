using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

public abstract partial class SettingsTabViewModelBase : ObservableObject
{
    public abstract string TabName { get; }
    public abstract string TabIcon { get; }
    public abstract void LoadFromSettings(GlobalSettings settings);
    public abstract void ApplyToSettings(GlobalSettings settings);
}
