using System;
using System.Collections.Generic;

namespace vTorrent.Abstractions.Events;

public class MissingFilesEventArgs : EventArgs
{
    public string Message { get; }
    public IReadOnlyList<(string Path, long ExpectedSize, long ActualSize)> Files { get; }

    public MissingFilesEventArgs(string message, List<(string path, long expectedSize, long actualSize)> files)
    {
        Message = message;
        Files = files;
    }
}
