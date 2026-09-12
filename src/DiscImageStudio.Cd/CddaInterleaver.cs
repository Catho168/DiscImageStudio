using System.IO;

namespace DiscImageStudio.Cd;

internal sealed class CddaInterleaver(CdAudioByteOrder audioByteOrder)
{
    private const int D = 4;

    private static readonly int[] Delays =
    [
        -24 * 3, -24 * ((1 * D) + 2) + 1, 8 - (24 * ((2 * D) + 3)), 8 - (24 * ((3 * D) + 2)) + 1,
        16 - (24 * ((4 * D) + 3)), 16 - (24 * ((5 * D) + 2)) + 1, 2 - (24 * ((6 * D) + 3)), 2 - (24 * ((7 * D) + 2)) + 1,
        10 - (24 * ((8 * D) + 3)), 10 - (24 * ((9 * D) + 2)) + 1, 18 - (24 * ((10 * D) + 3)), 18 - (24 * ((11 * D) + 2)) + 1,
        4 - (24 * ((16 * D) + 1)), 4 - (24 * (17 * D)) + 1, 12 - (24 * ((18 * D) + 1)), 12 - (24 * (19 * D)) + 1,
        20 - (24 * ((20 * D) + 1)), 20 - (24 * (21 * D)) + 1, 6 - (24 * ((22 * D) + 1)), 6 - (24 * (23 * D)) + 1,
        14 - (24 * ((24 * D) + 1)), 14 - (24 * (25 * D)) + 1, 22 - (24 * ((26 * D) + 1)), 22 - (24 * (27 * D)) + 1,
    ];

    private readonly byte[] _interleaveSequence = new byte[24 * 28 * D];
    private readonly byte[] _sector = new byte[CdDiscParameters.BytesPerSector];
    private int _head = (28 * D) - 1;
    private int _inputPosition;
    private int _sectorPosition;

    internal void Add(byte value, Stream output)
    {
        _interleaveSequence[MapIndex(Delays[_inputPosition++])] = value;
        if (_inputPosition < 24)
        {
            return;
        }

        _inputPosition = 0;
        _head++;
        if (_head >= 28 * D)
        {
            _head = 0;
        }

        for (int index = 0; index < 24; index++)
        {
            WriteByte(_interleaveSequence[MapIndex(index)], output);
        }
    }

    internal void Flush(Stream output)
    {
        if (_sectorPosition == 0)
        {
            return;
        }

        Array.Clear(_sector, _sectorPosition, _sector.Length - _sectorPosition);
        CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
        _sectorPosition = 0;
    }

    private int MapIndex(int position)
    {
        int value = (_head * 24) + position;
        int modulus = 28 * D * 24;
        if (value >= modulus)
        {
            return value - modulus;
        }

        return value < 0 ? value + modulus : value;
    }

    private void WriteByte(byte value, Stream output)
    {
        _sector[_sectorPosition++] = value;
        if (_sectorPosition < _sector.Length)
        {
            return;
        }

        CdAudioSamples.WriteSector(output, _sector, audioByteOrder);
        _sectorPosition = 0;
    }
}
