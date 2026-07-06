using System;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

public partial class ScheduleCellViewModel : ObservableObject
{
    [ObservableProperty] private ScheduleCellMode _mode = ScheduleCellMode.Profile;
    [ObservableProperty] private string? _profileName = "Balanced";
    [ObservableProperty] private string _cellColor = "#2196F3";
    [ObservableProperty] private bool _isSeedOnly;
    [ObservableProperty] private bool _isPaused;

    public int DayIndex { get; init; }
    public int HourIndex { get; init; }

    public string DayName => DayIndex switch
    {
        0 => "Mon", 1 => "Tue", 2 => "Wed", 3 => "Thu",
        4 => "Fri", 5 => "Sat", 6 => "Sun", _ => "?"
    };

    public string Tooltip => Mode switch
    {
        ScheduleCellMode.Profile => $"{DayName} {HourIndex:D2}:00\u2013{HourIndex:D2}:59 \u2014 {ProfileName}",
        ScheduleCellMode.SeedOnly => $"{DayName} {HourIndex:D2}:00\u2013{HourIndex:D2}:59 \u2014 Seed Only",
        ScheduleCellMode.Paused => $"{DayName} {HourIndex:D2}:00\u2013{HourIndex:D2}:59 \u2014 Paused",
        _ => ""
    };

    partial void OnModeChanged(ScheduleCellMode value)
    {
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnProfileNameChanged(string? value)
    {
        OnPropertyChanged(nameof(Tooltip));
    }

    public void SetFromCell(ScheduleCell cell, string resolvedColor)
    {
        Mode = cell.Mode;
        ProfileName = cell.ProfileName;
        IsSeedOnly = cell.Mode == ScheduleCellMode.SeedOnly;
        IsPaused = cell.Mode == ScheduleCellMode.Paused;
        CellColor = cell.Mode switch
        {
            ScheduleCellMode.Profile => resolvedColor,
            ScheduleCellMode.SeedOnly => "#FFC107",
            ScheduleCellMode.Paused => "#3C3C3C",
            _ => "#2196F3"
        };
    }

    public ScheduleCell ToCell() => new()
    {
        Mode = Mode,
        ProfileName = Mode == ScheduleCellMode.Profile ? ProfileName : null
    };
}
