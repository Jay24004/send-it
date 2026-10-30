using System;
using System.IO;
using SendIt.Core.Models;

namespace SendIt.App.ViewModels;

public class StagedItem
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }

    public string FormattedSize => IsDirectory ? "Directory" : TransferProgress.FormatBytes(SizeBytes);
    public string Glyph => IsDirectory ? "📁" : "📄";
}
