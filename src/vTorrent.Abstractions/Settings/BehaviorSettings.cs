using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// General behavior settings
/// </summary>
public class BehaviorSettings
{
    /// <summary>
    /// Close redundant connections (seed-to-seed)
    /// </summary>
    public bool CloseRedundantConnections { get; set; } = true;

    /// <summary>
    /// Automatically enable sequential mode in seeder swarms
    /// </summary>
    public bool AutoSequentialInSeederSwarm { get; set; } = true;

    /// <summary>
    /// Prioritize partial pieces for completion
    /// </summary>
    public bool PrioritizePartialPieces { get; set; } = false;

    /// <summary>
    /// Use strict endgame mode
    /// </summary>
    public bool StrictEndgameMode { get; set; } = true;

    /// <summary>
    /// Share ratio limit before stopping seed (0 = unlimited)
    /// </summary>
    public float SeedRatioLimit { get; set; } = 0f;

    /// <summary>
    /// Seed time limit in minutes (0 = unlimited)
    /// </summary>
    public int SeedTimeLimit { get; set; } = 0;

    /// <summary>
    /// Seed time ratio limit. Torrent stops seeding when seed_time >= download_time * ratio.
    /// 0 = disabled. libtorrent default: 7.0.
    /// Example: ratio 7.0 means a torrent that took 1 hour to download will seed for up to 7 hours.
    /// </summary>
    public float SeedTimeRatioLimit { get; set; } = 0f;

    /// <summary>
    /// Remove torrent when seed ratio/time reached
    /// </summary>
    public bool RemoveOnSeedComplete { get; set; } = false;

    /// <summary>
    /// Pause torrent when seed ratio/time reached
    /// </summary>
    public bool PauseOnSeedComplete { get; set; } = false;

    /// <summary>
    /// Metadata download timeout in minutes for magnet links (0 = no timeout)
    /// </summary>
    public int MetadataDownloadTimeoutMinutes { get; set; } = 10;

    /// <summary>Send HAVE messages even to peers that already have the piece. libtorrent default: true.</summary>
    public bool SendRedundantHave { get; set; } = true;

    /// <summary>Put peers on parole after they send data failing hash check. libtorrent default: true.</summary>
    public bool UseParoleMode { get; set; } = true;

    /// <summary>Make outgoing connections while seeding. libtorrent default: true.</summary>
    public bool SeedingOutgoingConnections { get; set; } = true;

    /// <summary>Include redundant byte count in tracker announce. libtorrent default: true.</summary>
    public bool ReportRedundantBytes { get; set; } = true;

    /// <summary>Include redundant bytes in the "downloaded" count reported to tracker. libtorrent default: false.</summary>
    public bool ReportTrueDownloaded { get; set; } = false;

    /// <summary>Download unchoking strategy. Default: FixedSlots (libtorrent default)</summary>
    public ChokingAlgorithm ChokingAlgorithm { get; set; } = ChokingAlgorithm.FixedSlots;

    /// <summary>Seed unchoking strategy. Default: FastestUpload (qBittorrent hardcoded choice)</summary>
    public SeedChokingAlgorithm SeedChokingAlgorithm { get; set; } = SeedChokingAlgorithm.FastestUpload;

    /// <summary>Percentage of peers to disconnect per turnover interval (0-100). libtorrent default: 4</summary>
    public int PeerTurnover { get; set; } = 4;

    /// <summary>Only trigger turnover when connected to more than this percentage of peer limit (0-100). libtorrent default: 90</summary>
    public int PeerTurnoverCutoff { get; set; } = 90;

    /// <summary>Seconds between peer turnover cycles. libtorrent default: 300</summary>
    public int PeerTurnoverInterval { get; set; } = 300;

    /// <summary>Seed/peer ratio threshold for auto-sequential. When seeds/peers exceeds this, switch to sequential.</summary>
    public double AutoSequentialRatio { get; set; } = 0.8;

    /// <summary>
    /// Number of completed pieces before switching from sequential to rarest-first picking.
    /// 0 = always rarest-first. libtorrent: initial_picker_threshold. Default: 4.
    /// </summary>
    public int InitialPickerThreshold { get; set; } = 4;

    /// <summary>
    /// If a peer can download a whole piece in fewer than this many seconds,
    /// assign entire pieces to that peer instead of individual blocks.
    /// libtorrent: whole_pieces_threshold. Default: 20.
    /// </summary>
    public int WholePiecesThreshold { get; set; } = 20;

    /// <summary>
    /// Global cap on total unchoked peers across all torrents.
    /// Distributed via ResourceAllocator. libtorrent: unchoke_slots_limit. Default: 8.
    /// </summary>
    public int UnchokeSlots { get; set; } = 8;
}
