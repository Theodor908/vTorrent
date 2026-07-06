using System.Collections.Generic;
using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Settings;
using vTorrent.Core.TrackerCommunication;
using Xunit;

namespace vTorrent.Tests.Unit.TrackerCommunication;

public class MseTrackerParamsTests
{
    private static readonly byte[] TestInfoHash = new byte[20];

    [Fact]
    public void Apply_WhenBothDisabled_AddsNothing()
    {
        var settings = new EncryptionSettings
        {
            OutPolicy = EncryptionPolicy.Disabled,
            InPolicy = EncryptionPolicy.Disabled
        };

        var @params = new Dictionary<string, string>();
        MseTrackerParams.Apply(@params, TestInfoHash, settings);

        @params.Should().BeEmpty();
    }

    [Fact]
    public void Apply_WhenOutEnabled_AddsSupportcrypto()
    {
        var settings = new EncryptionSettings
        {
            OutPolicy = EncryptionPolicy.Enabled,
            InPolicy = EncryptionPolicy.Disabled
        };

        var @params = new Dictionary<string, string>();
        MseTrackerParams.Apply(@params, TestInfoHash, settings);

        @params.Should().ContainKey("supportcrypto").WhoseValue.Should().Be("1");
        @params.Should().NotContainKey("requirecrypto");
    }

    [Fact]
    public void Apply_WhenBothForced_AddsRequirecrypto()
    {
        var settings = new EncryptionSettings
        {
            OutPolicy = EncryptionPolicy.Forced,
            InPolicy = EncryptionPolicy.Forced
        };

        var @params = new Dictionary<string, string>();
        MseTrackerParams.Apply(@params, TestInfoHash, settings);

        @params.Should().ContainKey("supportcrypto").WhoseValue.Should().Be("1");
        @params.Should().ContainKey("requirecrypto").WhoseValue.Should().Be("1");
    }
}
