using System.Text;

namespace ExamOps.Infra.Ui;

/// <summary>
/// Dependency-free local QR Code (Model 2, Byte mode, ECC Level L, Version 1–6) SVG generator.
/// Works 100% offline with zero external network calls or NuGet dependencies.
/// </summary>
public static class QrSvgGenerator
{
    private static readonly int[] DataCodewordsL = [0, 19, 34, 55, 80, 108, 136];
    private static readonly int[] EccCodewordsL = [0, 7, 10, 15, 20, 26, 18]; // V6 uses 2 blocks of 18
    private static readonly int[] BlockCountL = [0, 1, 1, 1, 1, 1, 2];
    private static readonly int[][] AlignCoords =
    [
        [],
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34]
    ];

    public static string GenerateSvg(string text, int quietZone = 3)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var version = 1;
        while (version <= 6 && DataCodewordsL[version] < bytes.Length + 2)
            version++;
        if (version > 6)
        {
            bytes = Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, 120)]);
            version = 6;
        }

        var size = 17 + 4 * version;
        var dataCapacity = DataCodewordsL[version];
        var eccPerBlock = EccCodewordsL[version];
        var blocks = BlockCountL[version];

        // 1. Encode byte-mode bit stream
        var bits = new List<int>(dataCapacity * 8);
        AppendBits(bits, 0b0100, 4); // Byte mode
        AppendBits(bits, bytes.Length, 8);
        foreach (var b in bytes) AppendBits(bits, b, 8);
        var maxBits = dataCapacity * 8;
        AppendBits(bits, 0, Math.Min(4, maxBits - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(0);
        var padBytes = new[] { 0xEC, 0x11 };
        var padIdx = 0;
        while (bits.Count < maxBits) AppendBits(bits, padBytes[padIdx++ & 1], 8);

        var dataBytes = new byte[dataCapacity];
        for (var i = 0; i < dataCapacity; i++)
        {
            int val = 0;
            for (var b = 0; b < 8; b++) val = (val << 1) | bits[i * 8 + b];
            dataBytes[i] = (byte)val;
        }

        // 2. Reed-Solomon ECC & interleave
        var dataPerBlock = dataCapacity / blocks;
        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        var genPoly = RsGeneratorPoly(eccPerBlock);
        for (var b = 0; b < blocks; b++)
        {
            dataBlocks[b] = dataBytes[(b * dataPerBlock)..((b + 1) * dataPerBlock)];
            eccBlocks[b] = RsRemainder(dataBlocks[b], genPoly);
        }

        var finalBytes = new List<byte>(dataCapacity + eccPerBlock * blocks);
        for (var i = 0; i < dataPerBlock; i++)
            for (var b = 0; b < blocks; b++) finalBytes.Add(dataBlocks[b][i]);
        for (var i = 0; i < eccPerBlock; i++)
            for (var b = 0; b < blocks; b++) finalBytes.Add(eccBlocks[b][i]);

        // 3. Place function patterns
        var modules = new bool[size, size];
        var isFunc = new bool[size, size];
        DrawFinder(modules, isFunc, 0, 0);
        DrawFinder(modules, isFunc, size - 7, 0);
        DrawFinder(modules, isFunc, 0, size - 7);

        for (var i = 8; i < size - 8; i++)
        {
            SetFunc(modules, isFunc, i, 6, i % 2 == 0);
            SetFunc(modules, isFunc, 6, i, i % 2 == 0);
        }

        var aligns = AlignCoords[version];
        foreach (var ay in aligns)
        {
            foreach (var ax in aligns)
            {
                if ((ax < 9 && ay < 9) || (ax > size - 9 && ay < 9) || (ax < 9 && ay > size - 9)) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        SetFunc(modules, isFunc, ax + dx, ay + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }

        // Reserve format info areas
        for (var i = 0; i <= 8; i++)
        {
            if (i != 6) { isFunc[8, i] = true; isFunc[i, 8] = true; }
        }
        for (var i = 0; i < 8; i++)
        {
            isFunc[8, size - 1 - i] = true;
            isFunc[size - 1 - i, 8] = true;
        }
        SetFunc(modules, isFunc, 8, size - 8, true); // Dark module

        // 4. Place data bits + apply mask 0 ((x + y) % 2 == 0)
        var bitIdx = 0;
        var totalBits = finalBytes.Count * 8;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            var upward = ((right + 1) & 2) == 0;
            for (var vert = 0; vert < size; vert++)
            {
                var y = upward ? size - 1 - vert : vert;
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    if (isFunc[y, x]) continue;
                    var dark = false;
                    if (bitIdx < totalBits)
                    {
                        dark = ((finalBytes[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        bitIdx++;
                    }
                    if (((x + y) & 1) == 0) dark = !dark;
                    modules[y, x] = dark;
                }
            }
        }

        // 5. Write format info for ECC Level L (01) + Mask 0 (000) -> 01000 -> BCH 0x77C4
        const int formatBits = 0x77C4;
        for (var i = 0; i <= 5; i++) modules[8, i] = ((formatBits >> i) & 1) != 0;
        modules[8, 7] = ((formatBits >> 6) & 1) != 0;
        modules[8, 8] = ((formatBits >> 7) & 1) != 0;
        modules[7, 8] = ((formatBits >> 8) & 1) != 0;
        for (var i = 9; i < 15; i++) modules[14 - i, 8] = ((formatBits >> i) & 1) != 0;
        for (var i = 0; i < 8; i++) modules[8, size - 1 - i] = ((formatBits >> (14 - i)) & 1) != 0;
        for (var i = 0; i < 7; i++) modules[size - 1 - i, 8] = ((formatBits >> i) & 1) != 0;

        // 6. Render compact SVG path
        var viewSize = size + quietZone * 2;
        var sb = new StringBuilder(4096);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {viewSize} {viewSize}\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"考试直达二维码\">");
        sb.Append($"<rect width=\"{viewSize}\" height=\"{viewSize}\" fill=\"#FFFFFF\" rx=\"2\"/>");
        sb.Append("<path fill=\"#0F172A\" d=\"");
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (modules[y, x])
                    sb.Append($"M{x + quietZone},{y + quietZone}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    private static void DrawFinder(bool[,] m, bool[,] f, int ox, int oy)
    {
        for (var dy = -1; dy <= 7; dy++)
        {
            for (var dx = -1; dx <= 7; dx++)
            {
                var x = ox + dx;
                var y = oy + dy;
                if (x < 0 || y < 0 || x >= m.GetLength(1) || y >= m.GetLength(0)) continue;
                var dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                SetFunc(m, f, x, y, dist != 2 && dist != 4);
            }
        }
    }

    private static void SetFunc(bool[,] m, bool[,] f, int x, int y, bool dark)
    {
        m[y, x] = dark;
        f[y, x] = true;
    }

    private static void AppendBits(List<int> bits, int val, int len)
    {
        for (var i = len - 1; i >= 0; i--) bits.Add((val >> i) & 1);
    }

    private static byte[] RsGeneratorPoly(int degree)
    {
        var poly = new byte[degree];
        poly[^1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                poly[j] = GfMul(poly[j], root);
                if (j + 1 < degree) poly[j] ^= poly[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return poly;
    }

    private static byte[] RsRemainder(byte[] data, byte[] gen)
    {
        var rem = new byte[gen.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, rem.Length - 1);
            rem[^1] = 0;
            for (var i = 0; i < gen.Length; i++) rem[i] ^= GfMul(gen[i], factor);
        }
        return rem;
    }

    private static byte GfMul(byte x, byte y)
    {
        int z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }
}
