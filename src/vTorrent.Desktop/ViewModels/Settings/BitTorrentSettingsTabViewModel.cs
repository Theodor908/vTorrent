using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// BitTorrent tab: protocol features (DHT, PEX, LSD, holepunch), encryption, and anonymous mode.
/// Replaces ProtocolSettingsTabViewModel + absorbs relevant Privacy settings.
/// </summary>
public partial class BitTorrentSettingsTabViewModel : SettingsTabViewModelBase
{
    public override string TabName => "BitTorrent";
    public override string TabIcon => "\uE582";

    [ObservableProperty]
    private bool _enableDht = true;

    [ObservableProperty]
    private bool _enablePex = true;

    [ObservableProperty]
    private bool _enableLsd = true;

    [ObservableProperty]
    private bool _enableHolepunch = true;

    [ObservableProperty]
    private bool _enableDhtDosBlocker = true;

    public IReadOnlyList<EncryptionPolicy> EncryptionPolicies { get; } =
        Enum.GetValues<EncryptionPolicy>();

    public IReadOnlyList<EncryptionLevel> EncryptionLevels { get; } =
        Enum.GetValues<EncryptionLevel>();

    [ObservableProperty]
    private EncryptionPolicy _outEncryptionPolicy = EncryptionPolicy.Enabled;

    [ObservableProperty]
    private EncryptionPolicy _inEncryptionPolicy = EncryptionPolicy.Enabled;

    [ObservableProperty]
    private EncryptionLevel _allowedEncryptionLevel = EncryptionLevel.Both;

    [ObservableProperty]
    private bool _anonymousMode;

    public override void LoadFromSettings(GlobalSettings settings)
    {
        EnableDht = settings.Protocol.EnableDht;
        EnablePex = settings.Protocol.EnablePex;
        EnableLsd = settings.Protocol.EnableLsd;
        EnableHolepunch = settings.Protocol.EnableHolepunch;
        EnableDhtDosBlocker = settings.Dht.EnableDosBlocker;

        OutEncryptionPolicy = settings.Encryption.OutPolicy;
        InEncryptionPolicy = settings.Encryption.InPolicy;
        AllowedEncryptionLevel = settings.Encryption.AllowedLevel;

        AnonymousMode = settings.Privacy.AnonymousMode;
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        settings.Protocol.EnableDht = EnableDht;
        settings.Dht.Enabled = EnableDht;
        settings.Protocol.EnablePex = EnablePex;
        settings.Protocol.EnableLsd = EnableLsd;
        settings.Protocol.EnableHolepunch = EnableHolepunch;
        settings.Dht.EnableDosBlocker = EnableDhtDosBlocker;

        settings.Encryption.OutPolicy = OutEncryptionPolicy;
        settings.Encryption.InPolicy = InEncryptionPolicy;
        settings.Encryption.AllowedLevel = AllowedEncryptionLevel;

        settings.Privacy.AnonymousMode = AnonymousMode;
    }
}
