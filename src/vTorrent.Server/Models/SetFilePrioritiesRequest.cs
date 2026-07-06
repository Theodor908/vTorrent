using System.Collections.Generic;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Server.Models;

public record SetFilePrioritiesRequest
{
    public IList<FilePriorityEntry> Priorities { get; init; } = [];
}

public record FilePriorityEntry(int FileIndex, FilePriority Priority);
