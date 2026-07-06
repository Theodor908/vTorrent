using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace vTorrent.Desktop.Services;

public record WebUIBundle(string Name, string Path);

public class WebUIBundleScanner
{
    /// <summary>
    /// Scans the bundles directory for valid WebUI bundles (folders containing index.html).
    /// Always returns "Default (built-in)" as the first entry.
    /// </summary>
    public List<WebUIBundle> ScanBundles(string bundlesDirectory)
    {
        var bundles = new List<WebUIBundle>
        {
            new("Default (built-in)", "")
        };

        if (!Directory.Exists(bundlesDirectory))
            return bundles;

        foreach (var dir in Directory.GetDirectories(bundlesDirectory).OrderBy(d => d))
        {
            var indexPath = System.IO.Path.Combine(dir, "index.html");
            if (File.Exists(indexPath))
            {
                bundles.Add(new WebUIBundle(
                    System.IO.Path.GetFileName(dir),
                    dir));
            }
        }

        return bundles;
    }
}
