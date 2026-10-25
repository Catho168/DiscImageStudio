using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.Imaging;

namespace DvdGrayProbe;

internal sealed class ProbePattern
{
    public const int Size = 6000;
    public const double CellMm = .02;
    public static readonly byte[] Codes = [0xA5, 0x66, 0x64, 0xE4, 0xB9, 0x92];
    public static readonly byte[] PanelCandidates = [0x66, 0x64, 0xE4, 0x64, 0xE4, 0x66, 0xE4, 0x66, 0x64, 0x66, 0xE4, 0x64];
    public readonly byte[] Atlas;
    private readonly byte[] _crop;
    private readonly byte[] _ramp;
    private readonly byte[][] _mixes;
    private readonly double[] _sin = Enumerable.Range(0, 12).Select(i => Math.Sin((i + .5) * Math.PI / 6)).ToArray();
    private readonly double[] _cos = Enumerable.Range(0, 12).Select(i => Math.Cos((i + .5) * Math.PI / 6)).ToArray();

    public ProbePattern(string source)
    {
        BitmapSource original = FourLevelDither.LoadImage(source);
        if (original.PixelWidth != Size || original.PixelHeight != Size)
            throw new ArgumentException("This experiment uses the supplied 6000 x 6000 four-level image.");
        BitmapSource gray = new FormatConvertedBitmap(original, PixelFormats.Gray8, null, 0);
        _crop = new byte[640 * 640];
        gray.CopyPixels(new Int32Rect(2880, 1920, 640, 640), _crop, 640, 0);
        if (_crop.Any(value => value is not (0 or 85 or 170 or 255)))
            throw new ArgumentException("The source crop must already contain only 0/85/170/255.");
        _ramp = Diffuse(640, 105, (x, _) => x / 639.0, 4);
        _mixes = new[] { .25, .5, .75 }.Select(fraction => Diffuse(256, 128, (_, _) => fraction, 2)).ToArray();
        foreach (byte[] mix in _mixes)
        {
            double fraction = mix.Average(value => (double)value);
            if (Math.Abs(fraction - new[] { .25, .5, .75 }[Array.IndexOf(_mixes, mix)]) > .01)
                throw new InvalidOperationException("Binary error diffusion did not preserve requested pixel occupancy.");
        }
        Atlas = new byte[Size * Size];
        Parallel.For(0, Size, y =>
        {
            double mmY = (y + .5) * CellMm - 60;
            for (int x = 0; x < Size; x++) Atlas[y * Size + x] = SamplePattern((x + .5) * CellMm - 60, mmY);
        });
        for (int panel = 0; panel < 12; panel++) DrawLabel($"{panel + 1}-{PanelCandidates[panel]:X2}", (panel + .5) * Math.PI / 6, 45.15, .13);
        for (int patch = 0; patch < 24; patch++) DrawLabel($"{Codes[patch % 6]:X2}", (patch + .5) * Math.PI / 12, 56.2, .17);
        // Distinctive orientation mark at 12 o'clock, before panel 1.
        for (int y = 100; y < 150; y++)
            for (int x = Size / 2 - 15; x < Size / 2 + 15; x++) Atlas[y * Size + x] = 0xA5;
    }

    private ProbePattern(byte[] atlas)
    {
        Atlas = atlas;
        _crop = [];
        _ramp = [];
        _mixes = [];
        if (atlas.Length != Size * Size || atlas.Any(value => !Codes.Contains(value)))
            throw new ArgumentException("Invalid direct-Q atlas.");
    }

    public static ProbePattern Load(string path) => new(File.ReadAllBytes(path));

    public byte Sample(double x, double y)
    {
        int ix = (int)Math.Floor((x + 60) / CellMm);
        int iy = (int)Math.Floor((y + 60) / CellMm);
        return ix >= 0 && iy >= 0 && ix < Size && iy < Size ? Atlas[iy * Size + ix] : (byte)0x92;
    }

    private byte SamplePattern(double x, double y)
    {
        double radius = Math.Sqrt(x * x + y * y);
        if (radius < 24.5 || radius >= 57.3) return 0x92;
        double angle = Math.Atan2(x, -y);
        if (angle < 0) angle += 2 * Math.PI;
        double position = angle * 6 / Math.PI;
        int panel = Math.Min(11, (int)position);
        double t = position - panel;
        double u = x * _cos[panel] + y * _sin[panel];
        double v = x * _sin[panel] - y * _cos[panel];
        byte candidate = PanelCandidates[panel];
        if (radius < 25.6 || radius >= 52.2)
        {
            double patchPosition = angle * 12 / Math.PI;
            double fraction = patchPosition - Math.Floor(patchPosition);
            return fraction < .025 || fraction >= .975 ? (byte)0x92 : Codes[(int)patchPosition % 6];
        }
        if (t < .04 || t >= .96) return 0x92;
        if (radius >= 26 && radius < 28.1)
        {
            int gx = Math.Clamp((int)((t - .04) / .92 * 640), 0, 639);
            int gy = Math.Clamp((int)((radius - 26) / CellMm), 0, 104);
            return ForLevel(_ramp[gy * 640 + gx], candidate);
        }
        if (radius >= 28.5 && radius < 30.5) return Mixed(u, radius - 28.5, t, 0x64, 0xE4);
        if (u >= -6.4 && u < 6.4 && v >= 31.1 && v < 43.9)
        {
            int ix = Math.Clamp((int)((u + 6.4) / CellMm), 0, 639);
            int iy = Math.Clamp((int)((43.9 - v) / CellMm), 0, 639);
            return ForLevel((byte)(_crop[iy * 640 + ix] / 85), candidate);
        }
        if (radius >= 45.8 && radius < 47.5) return Mixed(u, radius - 45.8, t, candidate, 0xA5);
        if (radius >= 47.8 && radius < 49.5) return Mixed(u, radius - 47.8, t, candidate, 0xB9);
        if (radius >= 49.8 && radius < 51.5) return Mixed(u, radius - 49.8, t, candidate, 0x92);
        return 0x92;
    }

    private byte Mixed(double u, double v, double t, byte selected, byte other)
    {
        double columnPosition = (t - .04) / .92 * 3;
        int column = Math.Clamp((int)columnPosition, 0, 2);
        double within = columnPosition - column;
        if (within < .025 || within >= .975) return 0x92;
        int ix = ((int)Math.Floor((u + 60) / CellMm)) % 256;
        int iy = ((int)Math.Floor(v / CellMm)) % 128;
        return _mixes[column][iy * 256 + ix] == 1 ? selected : other;
    }

    private static byte ForLevel(byte level, byte candidate) => level switch { 0 => 0xA5, 1 => candidate, 2 => 0xB9, _ => 0x92 };

    // Identical serpentine FS masks are reused across every codeword palette.
    private static byte[] Diffuse(int width, int height, Func<int, int, double> valueAt, int levels)
    {
        byte[] result = new byte[width * height];
        double[] current = new double[width + 2], next = new double[width + 2];
        for (int y = 0; y < height; y++)
        {
            int direction = (y & 1) == 0 ? 1 : -1;
            int x = direction == 1 ? 0 : width - 1;
            for (int count = 0; count < width; count++, x += direction)
            {
                int slot = x + 1;
                double adjusted = valueAt(x, y) + current[slot];
                int level = Math.Clamp((int)Math.Floor(adjusted * (levels - 1) + .5), 0, levels - 1);
                result[y * width + x] = (byte)level;
                double error = adjusted - level / (double)(levels - 1);
                current[slot + direction] += error * 7 / 16;
                next[slot - direction] += error * 3 / 16;
                next[slot] += error * 5 / 16;
                next[slot + direction] += error / 16;
            }
            (current, next) = (next, current);
            Array.Clear(next);
        }
        return result;
    }

    private void DrawLabel(string text, double angle, double radius, double dotMm)
    {
        // Binary glyphs keep printed panel IDs readable without adding target Q bytes.
        Dictionary<char, string> font = new()
        {
            ['0']="01110/10001/10011/10101/11001/10001/01110", ['1']="00100/01100/00100/00100/00100/00100/01110",
            ['2']="01110/10001/00001/00010/00100/01000/11111", ['3']="11110/00001/00001/01110/00001/00001/11110",
            ['4']="00010/00110/01010/10010/11111/00010/00010", ['5']="11111/10000/10000/11110/00001/00001/11110",
            ['6']="01110/10000/10000/11110/10001/10001/01110", ['7']="11111/00001/00010/00100/01000/01000/01000",
            ['8']="01110/10001/10001/01110/10001/10001/01110", ['9']="01110/10001/10001/01111/00001/00001/01110",
            ['A']="01110/10001/10001/11111/10001/10001/10001", ['B']="11110/10001/10001/11110/10001/10001/11110",
            ['E']="11111/10000/10000/11110/10000/10000/11111", ['-']="00000/00000/00000/11111/00000/00000/00000",
        };
        double width = text.Length * 6 * dotMm;
        double sine = Math.Sin(angle), cosine = Math.Cos(angle);
        for (double dv = -4.5 * dotMm; dv < 4.5 * dotMm; dv += CellMm / 2)
        for (double u = -width / 2 - dotMm; u < width / 2 + dotMm; u += CellMm / 2)
        {
            int gx = (int)Math.Floor((u + width / 2) / dotMm);
            int gy = (int)Math.Floor(3.5 - dv / dotMm);
            bool ink = gx >= 0 && gx / 6 < text.Length && gx % 6 < 5 && gy >= 0 && gy < 7
                && font[text[gx / 6]][gy * 6 + gx % 6] == '1';
            double x = (radius + dv) * sine + u * cosine, y = -(radius + dv) * cosine + u * sine;
            int ix = (int)Math.Floor((x + 60) / CellMm), iy = (int)Math.Floor((y + 60) / CellMm);
            if (ix >= 0 && iy >= 0 && ix < Size && iy < Size) Atlas[iy * Size + ix] = ink ? (byte)0xA5 : (byte)0x92;
        }
    }

    public void SavePreview(string path, bool falseColor)
    {
        const int size = 1800;
        int[] colors = [0x171B22, 0x64748B, 0xE5A33B, 0x995BD5, 0xB9C3CD, 0xFFFFFF];
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double xx = (x + .5) * 120 / size - 60, yy = (y + .5) * 120 / size - 60;
            double r = Math.Sqrt(xx * xx + yy * yy);
            byte q = Sample(xx, yy);
            int index = Array.IndexOf(Codes, q);
            int color = falseColor ? colors[index] : q switch { 0xA5 => 0, 0xB9 => 0xAAAAAA, 0x92 => 0xFFFFFF, _ => 0x555555 };
            if (r < 23.99 || r > 57.965) color = 0xE7EBF0;
            int offset = (y * size + x) * 4;
            pixels[offset] = (byte)color; pixels[offset + 1] = (byte)(color >> 8); pixels[offset + 2] = (byte)(color >> 16); pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = new(path, FileMode.CreateNew);
        encoder.Save(output);
    }
}
