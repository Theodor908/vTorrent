using System;

namespace vTorrent.Bench.Config;

public static class Presets
{
    public static ScenarioConfig Get(ScenarioPreset preset) => preset switch
    {
        ScenarioPreset.HomeDSL => new ScenarioConfig
        {
            PeerCount = 30,
            MaxUploadRatePerPeer = 524_288,
            RoundTripTimeMs = 80,
            ChokeProbability = 0.15f,
            ChokeIntervalSec = 30,
            PacketLossPercent = 0.01f,
            PeerBitfieldFill = 0.7f,
            BandwidthFluctuation = true,
            FluctuationAmplitude = 0.15f,
        },
        ScenarioPreset.Seedbox => new ScenarioConfig
        {
            PeerCount = 10,
            MaxUploadRatePerPeer = 10_485_760,
            RoundTripTimeMs = 10,
            ChokeProbability = 0.02f,
            ChokeIntervalSec = 60,
            PacketLossPercent = 0f,
            PeerBitfieldFill = 1.0f,
        },
        ScenarioPreset.MobileHotspot => new ScenarioConfig
        {
            PeerCount = 15,
            MaxUploadRatePerPeer = 131_072,
            RoundTripTimeMs = 200,
            ChokeProbability = 0.30f,
            ChokeIntervalSec = 20,
            PacketLossPercent = 0.05f,
            PeerBitfieldFill = 0.5f,
            BandwidthFluctuation = true,
            FluctuationAmplitude = 0.3f,
        },
        ScenarioPreset.SeederSwarm => new ScenarioConfig
        {
            PeerCount = 50,
            MaxUploadRatePerPeer = 2_097_152,
            RoundTripTimeMs = 30,
            ChokeProbability = 0.05f,
            ChokeIntervalSec = 45,
            PacketLossPercent = 0f,
            PeerBitfieldFill = 1.0f,
        },
        ScenarioPreset.LeecherHeavy => new ScenarioConfig
        {
            PeerCount = 40,
            MaxUploadRatePerPeer = 262_144,
            RoundTripTimeMs = 100,
            ChokeProbability = 0.25f,
            ChokeIntervalSec = 25,
            PacketLossPercent = 0.02f,
            PeerBitfieldFill = 0.2f,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };
}
