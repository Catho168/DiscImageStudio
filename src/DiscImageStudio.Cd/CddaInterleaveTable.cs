namespace DiscImageStudio.Cd;

/// <summary>
/// CD-DA CIRC delay table shared by the generator's interleaver and the read-back preview.
/// D = 4 is the CD-DA depth: 24 audio bytes per frame against a 28-frame delay buffer.
/// The interleaver stores the logical byte at index i in the frame delay line and rotates it
/// by the column of its position, so the emitted track holds it at index
/// i + FileOffsetFor(i % 24). Reading back therefore needs that many bytes of look-ahead.
/// </summary>
internal static class CddaInterleaveTable
{
    internal const int Depth = 4;
    internal const int FramesPerCycle = 28 * Depth;
    internal const int BytesPerFrame = 24;

    private static readonly int[] DelayTable = BuildDelays();
    private static readonly int[] FileOffsetTable = BuildFileOffsets();

    internal static int DelayFor(int position) => DelayTable[position];

    internal static int FileOffsetFor(int position) => FileOffsetTable[position];

    internal static int MaximumFileOffset { get; } = FileOffsetTable.Max();

    private static int[] BuildDelays()
    {
        int[] k0Even = [0, 8, 16, 2, 10, 18];
        int[] k0Odd = [4, 12, 20, 6, 14, 22];
        int[] delays = new int[BytesPerFrame];
        for (int index = 0; index < 12; index++)
        {
            bool odd = (index & 1) != 0;
            delays[index] = (odd ? 1 : 0)
                - (BytesPerFrame * ((index * Depth) + (odd ? 2 : 3)))
                + k0Even[index / 2];
        }

        for (int index = 12; index < BytesPerFrame; index++)
        {
            bool odd = ((index - 12) & 1) != 0;
            delays[index] = (odd ? 1 : 0)
                - (BytesPerFrame * (((index + 4) * Depth) + (odd ? 0 : 1)))
                + k0Odd[(index - 12) / 2];
        }

        return delays;
    }

    /// <summary>
    /// Inverse of the frame delay: the logical byte at index i is emitted at file index
    /// i + FileOffsetFor(i % 24). The interleaver pushes a logical frame
    /// (FramesPerCycle - 1 - encoderDelay) frames forward and moves each byte inside its
    /// frame to the delay column, which is what the offset combines.
    /// </summary>
    private static int[] BuildFileOffsets()
    {
        int[] offsets = new int[BytesPerFrame];
        for (int position = 0; position < BytesPerFrame; position++)
        {
            int delay = DelayTable[position];
            int column = ((delay % BytesPerFrame) + BytesPerFrame) % BytesPerFrame;
            int frameDelay = (column - delay) / BytesPerFrame;
            offsets[position] = (BytesPerFrame * (FramesPerCycle - 1))
                - (BytesPerFrame * frameDelay)
                + column
                - position;
        }

        return offsets;
    }
}
