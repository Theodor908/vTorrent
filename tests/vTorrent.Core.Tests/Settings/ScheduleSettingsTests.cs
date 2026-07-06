using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using vTorrent.Abstractions.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class ScheduleSettingsTests
{
    [Fact]
    public void DefaultGrid_Is7x24_AllBalancedProfile()
    {
        var grid = ScheduleSettings.CreateDefaultGrid();

        grid.Should().HaveCount(7, "7 days in a week");

        for (int day = 0; day < 7; day++)
        {
            grid[day].Should().HaveCount(24, $"day {day} should have 24 hours");

            for (int hour = 0; hour < 24; hour++)
            {
                var cell = grid[day][hour];
                cell.Mode.Should().Be(ScheduleCellMode.Profile);
                cell.ProfileName.Should().Be("Balanced");
            }
        }
    }

    [Fact]
    public void ScheduleCell_DefaultValues()
    {
        var cell = new ScheduleCell();

        cell.Mode.Should().Be(ScheduleCellMode.Profile);
        cell.ProfileName.Should().Be("Balanced");
    }

    [Fact]
    public void ScheduleCell_SeedOnlyMode_NullProfileName()
    {
        var cell = new ScheduleCell
        {
            Mode = ScheduleCellMode.SeedOnly,
            ProfileName = null
        };

        cell.Mode.Should().Be(ScheduleCellMode.SeedOnly);
        cell.ProfileName.Should().BeNull();
    }

    [Fact]
    public void Grid_Serialization_RoundTrips()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var original = new ScheduleSettings();
        // Modify one cell to ensure it survives round-trip
        original.Grid[2][10] = new ScheduleCell
        {
            Mode = ScheduleCellMode.SeedOnly,
            ProfileName = null
        };
        original.Grid[5][23] = new ScheduleCell
        {
            Mode = ScheduleCellMode.Paused,
            ProfileName = null
        };

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<ScheduleSettings>(json, options);

        deserialized.Should().NotBeNull();
        deserialized!.Enabled.Should().Be(original.Enabled);
        deserialized.Grid.Should().HaveCount(7);

        for (int day = 0; day < 7; day++)
        {
            deserialized.Grid[day].Should().HaveCount(24);
            for (int hour = 0; hour < 24; hour++)
            {
                deserialized.Grid[day][hour].Mode.Should().Be(original.Grid[day][hour].Mode,
                    $"day {day} hour {hour} mode should match");
                deserialized.Grid[day][hour].ProfileName.Should().Be(original.Grid[day][hour].ProfileName,
                    $"day {day} hour {hour} profileName should match");
            }
        }

        // Verify the specifically modified cells
        deserialized.Grid[2][10].Mode.Should().Be(ScheduleCellMode.SeedOnly);
        deserialized.Grid[2][10].ProfileName.Should().BeNull();
        deserialized.Grid[5][23].Mode.Should().Be(ScheduleCellMode.Paused);
        deserialized.Grid[5][23].ProfileName.Should().BeNull();
    }
}
