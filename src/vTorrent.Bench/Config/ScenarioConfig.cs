using System.Text.Json.Serialization;

namespace vTorrent.Bench.Config;

public class ScenarioConfig
{
    [JsonPropertyName("pieceCount")]
    public int PieceCount { get; set; } = 1000;

    [JsonPropertyName("pieceSize")]
    public int PieceSize { get; set; } = 262_144; // 256 KB

    [JsonPropertyName("torrentFilePath")]
    public string? TorrentFilePath { get; set; }

    [JsonPropertyName("dataPath")]
    public string? DataPath { get; set; }

    [JsonPropertyName("peerCount")]
    public int PeerCount { get; set; } = 30;

    [JsonPropertyName("maxUploadRatePerPeer")]
    public int MaxUploadRatePerPeer { get; set; } = 1_048_576; // 1 MB/s

    [JsonPropertyName("roundTripTimeMs")]
    public int RoundTripTimeMs { get; set; } = 50;

    [JsonPropertyName("chokeProbability")]
    public float ChokeProbability { get; set; } = 0.1f;

    [JsonPropertyName("chokeIntervalSec")]
    public int ChokeIntervalSec { get; set; } = 30;

    [JsonPropertyName("packetLossPercent")]
    public float PacketLossPercent { get; set; } = 0f;

    [JsonPropertyName("peerBitfieldFill")]
    public float PeerBitfieldFill { get; set; } = 1.0f;

    [JsonPropertyName("bandwidthFluctuation")]
    public bool BandwidthFluctuation { get; set; } = false;

    [JsonPropertyName("fluctuationAmplitude")]
    public float FluctuationAmplitude { get; set; } = 0.2f;
}

public enum ScenarioPreset
{
    HomeDSL,
    Seedbox,
    MobileHotspot,
    SeederSwarm,
    LeecherHeavy
}
