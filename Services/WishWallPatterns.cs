using System.IO;
using System.IO.Compression;

namespace D2MacroNative.Services;

internal static class WishWallPatterns
{
    // GZip-compressed, lossless port of wishes 1-14 from wall_menu4.ahk.
    private const string PatternData =
        "H4sIAAAAAAAEAN2XS7akIAyG56wlA4I8B+ysF9+QoKLGulB1PXW6jyP/D0IICQ/MC1hwkAA1YAREQAPoAeEEWA1gzgAb8AIwFYQzsIAOMIHRzxLcvR4jCLp9vWpE1YtqUKZEtIyw1HGKS1ji4gg5+AXA0dZHwB+jHejue1BVSy5RLPGv4y/V6VBjUZCDE9j7/ZOgzkt/qiqbuYKw/JXcLctHuWNgUC5rPKU7yvMZPVUyIcd3VCo/36quBPtdcJhQoOJO9yBdzYywP8K6lJZRWi/SJfucCAJRLsfqMJdztbml0JN69dU+SgJ8rjfrnK4lyu8R09mWevSxOrVSvmzoNE/fJZimBl8D1c9vAHC9vPlYNb+q7X9L86XfBjcDgqZCposHH31lYWhp6MxLcEfQ3iInkeJx+vVORiIlZK5tzB0iO9RpOREqgrlOdCSLnQ49YiNLg3uPNa6HtmuzilTMNJ/I7oV2pbO154M6UnY9SWxb0R90NjAD4npbHiaaSa0FQVcpY90aY9ua9tK5l/Xl+x9FhTqHdi21a862PcnAFHGc/yPE9E78KJ/V8yw+1BRitu21sL7DtrkmWFGZtD80+B64c2pUTxxpUaYiGNU5hgMqXRREVaHJ/PzkQRxXY3upiaBdFwXCZ8gswvW6MotuDOoXRB/AqUDf0y91/kpWuGTbtrtAN3zf2juY01FPA5wGfgysxWsGwW6IMmkALIcNIr0gu4kD0fdAGF6h5fdMqO3pSmhpN6ydJOD4zfQNQPk1JGtB1YLaf029iucMl7XrmRf/AtZUzlCvEwAA";

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<int[]>> Patterns = Load();

    public static IReadOnlyList<int[]> Get(int wishNumber) =>
        Patterns.TryGetValue(wishNumber, out var stages)
            ? stages
            : throw new ArgumentOutOfRangeException(nameof(wishNumber), "Wish number must be between 1 and 14.");

    private static IReadOnlyDictionary<int, IReadOnlyList<int[]>> Load()
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(PatternData));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var result = new Dictionary<int, IReadOnlyList<int[]>>();

        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var wishNumber = int.Parse(line.AsSpan(0, separator));
            var stages = line[(separator + 1)..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(stage => stage.Split(',').Select(int.Parse).ToArray())
                .ToArray();
            result[wishNumber] = stages;
        }

        return result;
    }
}
