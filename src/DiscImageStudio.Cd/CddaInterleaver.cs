using System.IO;

namespace DiscImageStudio.Cd;

internal sealed class CddaInterleaver(CdAudioByteOrder audioByteOrder)
{
    private readonly byte[] _interleaveSequence =
        new byte[CddaInterleaveTable.FramesPerCycle * CddaInterleaveTable.BytesPerFrame];
    private readonly byte[] _sector = new byte[CdDiscParameters.BytesPerSector];
    private int _head = CddaInterleaveTable.FramesPerCycle - 1;
    private int _inputPosition;
    private int _sectorPosition;

    internal void Add(byte value, Stream output)
    {
        _interleaveSequence[MapIndex(CddaInterleaveTable.DelayFor(_inputPosition++))] = value;
        if (_inputPosition < CddaInterleaveTable.BytesPerFrame)
        {
            return;
        }

        _inputPosition = 0;
        _head++;
        if (_head >= CddaInterleaveTable.FramesPerCycle)
        {
            _head = 0;
        }

        for (int index = 0; index < CddaInterleaveTable.BytesPerFrame; index++)
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
        int value = (_head * CddaInterleaveTable.BytesPerFrame) + position;
        int modulus = _interleaveSequence.Length;
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
