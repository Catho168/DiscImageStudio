using System.Buffers.Binary;
using DiscImageStudio.BluRay.Encoding;

internal static class BluRayEncodingTests
{
    internal static void Run()
    {
        TestEdcAndScrambling();
        TestEccAndInterleaving();
        Test17Pp();
        TestFrames();
        TestRecordingUnit();
        TestValidation();
        Console.WriteLine("blu-ray-encoding: passed (EDC vectors, scrambler vectors, independent RS syndromes, LDC/BIS reversal, 17PP tables/RLL/parity, sync/NRZI, automatic DC, complete RUB replay)");
    }

    private static void TestEdcAndScrambling()
    {
        byte[] ramp = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        // Reference values calculated independently using integer polynomial long division.
        Require(BluRayEdc.Compute(ramp) == 0xb0b77f5f, "ramp EDC vector");
        Require(BluRayEdc.Compute(new byte[2048]) == 0, "zero EDC vector");
        Require(BluRayEdc.Compute(Enumerable.Repeat((byte)255, 2048).ToArray()) == 0x0ac0f2b6, "all-ones EDC vector");
        byte[] single = new byte[2048];
        single[0] = 1;
        Require(BluRayEdc.Compute(single) == 0x461f4110, "leading-one EDC vector");
        byte[] frame = new byte[2052];
        BluRayEdc.WriteDataFrame(ramp, frame);
        Require(frame.AsSpan(0, 2048).SequenceEqual(ramp), "EDC leaves payload intact");
        Require(frame.AsSpan(2048).SequenceEqual(Convert.FromHexString("B0B77F5F")), "EDC is big-endian");
        Require(IndependentEdc(frame) == 0, "data plus EDC has zero polynomial remainder");

        // Independent shift-register bit-array reference, seeded before the first byte is read.
        byte[] reference = Convert.FromHexString("00888D0D2DA45E2CDEFC51F9E3FA92F4");
        Require(BluRayScrambler.CreateMask(0x100000, 16).SequenceEqual(reference), "first-cluster scrambler vector");
        Require(BluRayScrambler.CreateMask(0x100020, 16).SequenceEqual(Convert.FromHexString("0199971776ECE2756304F20A240FB71C")),
            "following-cluster scrambler vector");
        for (uint offset = 0; offset < 32; offset++)
            Require(BluRayScrambler.CreateMask(0x100000 + offset, 16).SequenceEqual(reference), "all 32 frames reset to the same mask");
        byte[] before = (byte[])frame.Clone();
        BluRayScrambler.Transform(frame, 0x100020);
        Require(!frame.SequenceEqual(before), "scrambler changes nonempty frame");
        BluRayScrambler.Transform(frame, 0x100020);
        Require(frame.SequenceEqual(before), "scrambler XOR is reversible");
    }

    private static void TestEccAndInterleaving()
    {
        const uint psn = 0x100040;
        byte[] payload = new byte[65536];
        new Random(9713).NextBytes(payload);
        byte[] controls = Enumerable.Range(0, 576).Select(i => (byte)(i * 19)).ToArray();
        byte[] flags = Enumerable.Range(0, 16).Select(i => (byte)(i << 4)).ToArray();
        byte[] ldc = BluRayEccEncoder.EncodeLdcCluster(payload, psn);
        byte[] bis = BluRayEccEncoder.EncodeBisCluster(psn, flags, controls);
        byte[] ecc = BluRayEccEncoder.Encode(payload, psn, flags, controls);
        Require(ldc.Length == 75392 && bis.Length == 1488 && ecc.Length == 76880, "cluster sizes");
        Require(ecc.SequenceEqual(BluRayEccEncoder.Encode(payload, psn, flags, controls)), "encoding is deterministic");

        // Reverse the two LDC interleavers from destination coordinates.
        byte[][] words = Enumerable.Range(0, 304).Select(_ => new byte[248]).ToArray();
        for (int row = 0; row < 496; row++)
            for (int column = 0; column < 152; column++)
            {
                int originalColumn = 2 * ((column + 3 * (row / 2)) % 152) + row % 2;
                words[originalColumn][row / 2] = ldc[row * 152 + column];
            }
        foreach (byte[] word in words) CheckSyndromes(word, 32);
        byte[] frames = words.SelectMany(word => word.Take(216)).ToArray();
        for (int sector = 0; sector < 32; sector++)
        {
            Span<byte> frame = frames.AsSpan(sector * 2052, 2052);
            IndependentDescramble(frame, psn);
            Require(frame[..2048].SequenceEqual(payload.AsSpan(sector * 2048, 2048)), "LDC inversion recovers sector order");
            Require(IndependentEdc(frame) == 0, "LDC data frames retain valid EDC");
        }

        // BIS places each complete address field in the first three rows of its own unit.
        for (int unit = 0; unit < 16; unit++)
        {
            ReadOnlySpan<byte> address = bis.AsSpan(unit * 31 * 3, 9);
            Require(BinaryPrimitives.ReadUInt32BigEndian(address) == psn + 2 * unit, "BIS address unit number");
            Require(address[4] == flags[unit], "BIS status flag ownership");
            CheckSyndromes(address, 4);
        }
        byte[][] bisWords = Enumerable.Range(0, 24).Select(_ => new byte[62]).ToArray();
        for (int unit = 0; unit < 16; unit++)
            for (int row = 0; row < 31; row++)
                for (int column = 0; column < 3; column++)
                {
                    int sourceColumn = 3 * ((row + 8 - unit % 8) % 8) + (column + 3 - row % 3) % 3;
                    bisWords[sourceColumn][2 * row + unit / 8] = bis[(unit * 31 + row) * 3 + column];
                }
        foreach (byte[] word in bisWords) CheckSyndromes(word, 32);
        byte[] recoveredControl = bisWords.SelectMany(word => word.Skip(6).Take(24)).ToArray();
        Require(recoveredControl.SequenceEqual(controls), "BIS user-control column ordering");

        for (int row = 0; row < 496; row++)
            for (int column = 0; column < 155; column++)
            {
                bool isBis = column is 38 or 77 or 116;
                byte expected = isBis ? bis[row * 3 + column / 39]
                    : ldc[row * 152 + column - column / 39];
                Require(ecc[row * 155 + column] == expected, "ECC multiplexes four 38-byte groups and three BIS bytes");
            }
    }

    private static void Test17Pp()
    {
        (string Data, string Channel)[] vectors =
        [
            ("00000000", "010100100100"), ("00001000", "000100100100"),
            ("000000", "010100000"), ("000001", "010100100"),
            ("000010", "000100000"), ("000011", "000100100"),
            ("0001", "000100"), ("0010", "010000"), ("0011", "010100"),
            ("01", "010"), ("10", "001"), ("11", "101"),
            ("0000", "010100"), ("00", "000"),
            ("11011101", "001000000010"), ("11011110", "101010101001"),
            ("01011001", "010010001010"),
        ];
        foreach ((string data, string channel) in vectors)
            Require(BluRay17PpEncoder.Encode(Bits(data)).ModulationBits.SequenceEqual(Bits(channel)), $"17PP table {data}");
        Require(BluRay17PpEncoder.Encode(Bits("11"), 1).ModulationBits.SequenceEqual(Bits("000")), "11 uses preceding modulation bit");
        Require(BluRay17PpEncoder.Encode(Bits("00")).UsedTermination, "two-bit termination");
        Require(BluRay17PpEncoder.Encode(Bits("0000")).UsedTermination, "four-bit termination");
        Require(!BluRay17PpEncoder.Encode(Bits("000000")).UsedTermination, "normal six-bit codeword is not termination");

        // Exhaust every even-length input through 16 bits, including both context states.
        for (int length = 2; length <= 16; length += 2)
            for (int value = 0; value < 1 << length; value++)
            {
                byte[] bits = new byte[length];
                for (int i = 0; i < length; i++) bits[i] = (byte)((value >> (length - 1 - i)) & 1);
                for (byte context = 0; context <= 1; context++)
                {
                    byte[] channel = BluRay17PpEncoder.Encode(bits, context).ModulationBits;
                    Require(channel.Length == length / 2 * 3, "17PP code rate 2/3");
                    Require(bits.Sum(bit => bit) % 2 == channel.Sum(bit => bit) % 2, "17PP preserves parity");
                    CheckRll(channel);
                }
            }
        Random random = new(3190);
        for (int sample = 0; sample < 64; sample++)
        {
            byte[] bits = Enumerable.Range(0, 1268).Select(_ => (byte)random.Next(2)).ToArray();
            CheckRll(BluRay17PpEncoder.Encode(bits).ModulationBits);
        }
    }

    private static void TestFrames()
    {
        byte[] ecc = Enumerable.Range(0, 155).Select(i => (byte)i).ToArray();
        byte[] dc = Enumerable.Range(0, 28).Select(i => (byte)(i % 2)).ToArray();
        byte[] bits = BluRayRecordingFrameEncoder.CreateDataBits(ecc, dc);
        Require(bits.Length == 1268, "recording data bits exclude 20-bit sync placeholder");
        int source = 0;
        int cursor = 0;
        for (int block = 0; block < 28; block++)
        {
            int count = block == 0 ? 25 : 45;
            for (int bit = 0; bit < count; bit++, source++)
                Require(bits[cursor++] == ((ecc[source / 8] >> (7 - source % 8)) & 1), "recording data MSB-first order");
            Require(bits[cursor++] == dc[block], "dc-control insertion");
        }
        string body = "001010000000010000000010";
        Require(BluRayRecordingFrameEncoder.CreateFrameSync(0).SequenceEqual(Bits(body + "000001")), "FS0 vector");
        Require(BluRayRecordingFrameEncoder.CreateFrameSync(30).SequenceEqual(Bits(body + "101000")), "last recording frame uses FS2");
        for (int frame = 0; frame < 31; frame++)
        {
            byte[] regular = BluRayRecordingFrameEncoder.CreateFrameSync(frame);
            byte[] terminated = BluRayRecordingFrameEncoder.CreateFrameSync(frame, true);
            Require(regular[0] == 0 && terminated[0] == 1 && regular.AsSpan(1).SequenceEqual(terminated.AsSpan(1)), "sync termination flag only changes bit zero");
            BluRayRecordingFrameResult result = BluRayRecordingFrameEncoder.Encode(ecc, frame, dc, false, 1);
            Require(result.ChannelLevels.Length == 1932 && result.ModulationBits.Length == 1932, "complete recording frame length");
            Require(result.ModulationBits.AsSpan(0, 30).SequenceEqual(regular), "recording frame begins with correct sync");
            byte previous = 1;
            for (int i = 0; i < result.ChannelLevels.Length; i++)
            {
                Require((previous ^ result.ChannelLevels[i]) == result.ModulationBits[i], "NRZI transition correspondence");
                previous = result.ChannelLevels[i];
            }
            CheckRll(result.ModulationBits.AsSpan(30));
        }
        Require(BluRay17PpEncoder.ToChannelLevels(Bits("010010001010")).SequenceEqual(Bits("011100001100")), "Figure 45 NRZI example");
        foreach (byte fill in new byte[] { 0, 255, 0x55, 0xaa })
        {
            byte[] row = Enumerable.Repeat(fill, 155).ToArray();
            byte[] controls = Enumerable.Repeat((byte)(fill & 1), 28).ToArray();
            bool termination = false;
            int lastTransition = -1;
            for (int frame = 0; frame < 31; frame++)
            {
                BluRayRecordingFrameResult result = BluRayRecordingFrameEncoder.Encode(row, frame, controls, termination);
                for (int bit = 0; bit < result.ModulationBits.Length; bit++)
                {
                    if (result.ModulationBits[bit] == 0) continue;
                    int position = frame * 1932 + bit;
                    if (lastTransition >= 0)
                        Require(position - lastTransition is >= 2 and <= 9, "repeated-pattern frame/sync boundaries respect run lengths");
                    lastTransition = position;
                }
                termination = result.UsedTermination;
            }
        }
    }

    private static void TestValidation()
    {
        Throws(() => BluRayEccEncoder.Encode(new byte[65535], 0x100000), "partial clusters rejected");
        Throws(() => BluRayEccEncoder.Encode(new byte[65536], 0x100001), "unaligned PSN rejected");
        Throws(() => BluRayEccEncoder.EncodeBisCluster(0x100000, new byte[15]), "partial flags rejected");
        Throws(() => BluRayEccEncoder.EncodeBisCluster(0x100000, default, new byte[575]), "partial control data rejected");
        Throws(() => BluRayScrambler.CreateMask(0, 2053), "oversized scrambler rejected");
        Throws(() => BluRay17PpEncoder.Encode([0]), "odd-length modulation input rejected");
        Throws(() => BluRay17PpEncoder.Encode([0, 2]), "nonbinary modulation input rejected");
        Throws(() => BluRayRecordingFrameEncoder.CreateFrameSync(31), "invalid frame index rejected");
        Throws(() => BluRayGreedyDcControlEncoder.Encode(new byte[155], 0, initialDigitalSum: long.MinValue), "minimum Int64 DSV rejected");
        Throws(() => BluRayGreedyDcControlEncoder.Encode(new byte[155], 0, initialDigitalSum: long.MaxValue), "maximum Int64 DSV rejected");
        Throws(() => BluRayRecordingUnitEncoder.EncodeEccCluster(new byte[76880], 0x100000, initialDigitalSum: long.MaxValue), "RUB rejects overflowing DSV");
        BluRayBalancedFrameResult largeSafeSum = BluRayGreedyDcControlEncoder.Encode(new byte[155], 0,
            initialDigitalSum: long.MaxValue - 2_000_000);
        Require(largeSafeSum.FinalDigitalSum > 0, "large safe DSV does not wrap");
    }

    private static void TestRecordingUnit()
    {
        byte[] payload = new byte[65536];
        new Random(80217).NextBytes(payload);
        byte[] ecc = BluRayEccEncoder.Encode(payload, 0x100000);
        BluRayRecordingUnitResult rub = BluRayRecordingUnitEncoder.EncodeEccCluster(ecc, 0x100000);
        Require(rub.ModulationBits.Length == 962676 && rub.ChannelLevels.Length == 962676,
            "single RUB includes 2760 run-in, 958272 physical cluster, 1104 run-out, 540 Guard3 bits");
        Require(rub.DcControlBits.Length == 496 * 28 && rub.DcControlBits.Contains((byte)1), "DC controls were selected");
        byte[] guard = Bits("01001001010100001000");
        for (int i = 0; i < 1100; i++) Require(rub.ModulationBits[i] == guard[i % 20], "Guard1 repetition");
        Require(rub.ModulationBits.AsSpan(2640, 30).SequenceEqual(BluRayRecordingFrameEncoder.CreateFrameSync(6)), "PrA FS4 location");
        Require(rub.ModulationBits.AsSpan(2710, 30).SequenceEqual(BluRayRecordingFrameEncoder.CreateFrameSync(13)), "PrA FS6 location");

        // Replay every selected control through the explicit encoder. This also checks frame boundaries.
        long balancedSum = rub.ChannelLevels.Take(2760).Sum(level => level == 0 ? -1 : 1);
        long zeroSum = balancedSum;
        long balancedMax = 0;
        long zeroMax = 0;
        byte zeroLevel = rub.ChannelLevels[2759];
        bool previousTermination = false;
        bool zeroTermination = false;
        int independentlySuboptimal = 0;
        for (int frame = 0; frame < 496; frame++)
        {
            int start = 2760 + frame * 1932;
            byte initial = rub.ChannelLevels[start - 1];
            BluRayRecordingFrameResult replay = BluRayRecordingFrameEncoder.Encode(ecc.AsSpan(frame * 155, 155), frame % 31,
                rub.DcControlBits.AsSpan(frame * 28, 28), previousTermination, initial);
            Require(replay.ModulationBits.AsSpan().SequenceEqual(rub.ModulationBits.AsSpan(start, 1932)), "RUB explicit-control replay");
            byte[] finalControls = rub.DcControlBits.AsSpan(frame * 28, 28).ToArray();
            for (int control = 0; control < 28; control++)
            {
                finalControls[control] ^= 1;
                BluRayRecordingFrameResult alternative = BluRayRecordingFrameEncoder.Encode(ecc.AsSpan(frame * 155, 155),
                    frame % 31, finalControls, previousTermination, initial);
                finalControls[control] ^= 1;
                if (FinalScore(alternative, control, frame, balancedSum) < FinalScore(replay, control, frame, balancedSum))
                    independentlySuboptimal++;
            }
            previousTermination = replay.UsedTermination;
            BluRayRecordingFrameResult zero = BluRayRecordingFrameEncoder.Encode(ecc.AsSpan(frame * 155, 155), frame % 31,
                new byte[28], zeroTermination, zeroLevel);
            zeroTermination = zero.UsedTermination;
            zeroLevel = zero.FinalChannelLevel;
            for (int bit = 0; bit < 1932; bit++)
            {
                balancedSum += replay.ChannelLevels[bit] == 0 ? -1 : 1;
                zeroSum += zero.ChannelLevels[bit] == 0 ? -1 : 1;
                if ((bit + 1) % 69 == 0)
                {
                    balancedMax = Math.Max(balancedMax, Math.Abs(balancedSum));
                    zeroMax = Math.Max(zeroMax, Math.Abs(zeroSum));
                }
            }
        }
        Require(balancedMax < 150 && balancedMax < zeroMax, "lookahead DC limits accumulated digital sum at block boundaries");
        int runOut = 2760 + 496 * 1932;
        Require(rub.ModulationBits.AsSpan(runOut, 30).SequenceEqual(BluRayRecordingFrameEncoder.CreateFrameSync(0, previousTermination)),
            "PoA FS0 carries final termination state");
        for (int i = 0; i < 54; i++)
            Require(rub.ModulationBits[runOut + 30 + i] == (i % 9 == 1 ? 1 : 0), "PoA six 9T intervals");
        for (int i = runOut + 84; i < rub.ModulationBits.Length; i++)
            Require(rub.ModulationBits[i] == guard[(i - runOut - 84) % 20], "PoA/Guard2/Guard3 repetitions");
        byte lastLevel = 0;
        long digitalSum = 0;
        int lastTransition = -1;
        for (int i = 0; i < rub.ModulationBits.Length; i++)
        {
            Require((lastLevel ^ rub.ChannelLevels[i]) == rub.ModulationBits[i], "RUB-wide NRZI continuity");
            lastLevel = rub.ChannelLevels[i];
            digitalSum += lastLevel == 0 ? -1 : 1;
            if (rub.ModulationBits[i] == 1)
            {
                if (lastTransition >= 0) Require(i - lastTransition is >= 2 and <= 9, "RUB boundaries respect data/sync run lengths");
                lastTransition = i;
            }
        }
        Require(digitalSum == rub.FinalDigitalSum, "RUB DSV includes guards and sync");
        Require(independentlySuboptimal > 0 && independentlySuboptimal == rub.SuboptimalDcControlDecisions,
            "RUB exposes independently checked limitations of greedy DC decisions");
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        try
        {
            BluRayRecordingUnitEncoder.EncodeEccCluster(ecc, 0x100000, cancellationToken: canceled.Token);
            throw new InvalidOperationException("Canceled RUB encoding did not stop.");
        }
        catch (OperationCanceledException) { }
        using CancellationTokenSource duringEncoding = new();
        duringEncoding.CancelAfter(TimeSpan.FromMilliseconds(1));
        try
        {
            BluRayRecordingUnitEncoder.EncodeEccCluster(ecc, 0x100000, cancellationToken: duringEncoding.Token);
            throw new InvalidOperationException("RUB encoding ignored in-progress cancellation.");
        }
        catch (OperationCanceledException) { }

        long FinalScore(BluRayRecordingFrameResult result, int control, int frameNumber, long startingSum)
        {
            int endpoint = Math.Min((control + 2) * 69, 1932);
            long total = startingSum;
            for (int bit = 0; bit < endpoint; bit++) total += result.ChannelLevels[bit] == 0 ? -1 : 1;
            if (control == 27)
            {
                if (frameNumber < 495)
                {
                    BluRayRecordingFrameResult next = BluRayRecordingFrameEncoder.Encode(ecc.AsSpan((frameNumber + 1) * 155, 155),
                        (frameNumber + 1) % 31, rub.DcControlBits.AsSpan((frameNumber + 1) * 28, 28),
                        result.UsedTermination, result.FinalChannelLevel);
                    for (int bit = 0; bit < 69; bit++) total += next.ChannelLevels[bit] == 0 ? -1 : 1;
                }
                else
                {
                    byte[] sync = BluRayRecordingFrameEncoder.CreateFrameSync(0, result.UsedTermination);
                    byte level = result.FinalChannelLevel;
                    for (int bit = 0; bit < 69; bit++)
                    {
                        byte transition = bit < 30 ? sync[bit] : (byte)((bit - 30) % 9 == 1 ? 1 : 0);
                        level ^= transition;
                        total += level == 0 ? -1 : 1;
                    }
                }
            }
            return Math.Abs(total);
        }
    }

    private static uint IndependentEdc(ReadOnlySpan<byte> bytes)
    {
        uint result = 0;
        foreach (byte value in bytes)
            for (int bit = 7; bit >= 0; bit--)
            {
                bool feedback = ((result >> 31) ^ ((value >> bit) & 1)) != 0;
                result <<= 1;
                if (feedback) result ^= 0x80000011;
            }
        return result;
    }

    private static void IndependentDescramble(Span<byte> bytes, uint psn)
    {
        bool[] state = Enumerable.Range(0, 16).Select(i => ((0x8000 | ((psn >> 5) & 0x7fff)) & (1 << i)) != 0).ToArray();
        for (int index = 0; index < bytes.Length; index++)
        {
            for (int bit = 0; bit < 8; bit++) if (state[bit]) bytes[index] ^= (byte)(1 << bit);
            for (int bit = 0; bit < 8; bit++)
            {
                bool feedback = state[15] ^ state[14] ^ state[12] ^ state[3];
                for (int i = 15; i > 0; i--) state[i] = state[i - 1];
                state[0] = feedback;
            }
        }
    }

    private static void CheckSyndromes(ReadOnlySpan<byte> codeword, int parityCount)
    {
        byte root = 1;
        for (int i = 0; i < parityCount; i++)
        {
            byte syndrome = 0;
            foreach (byte coefficient in codeword) syndrome = (byte)(Multiply(syndrome, root) ^ coefficient);
            Require(syndrome == 0, "independent GF(256) syndrome is zero");
            root = Multiply(root, 2);
        }
    }

    private static byte Multiply(byte left, byte right)
    {
        int product = 0;
        int factor = left;
        for (int bit = 0; bit < 8; bit++)
        {
            if (((right >> bit) & 1) != 0) product ^= factor;
            factor <<= 1;
            if ((factor & 256) != 0) factor ^= 0x11d;
        }
        return (byte)product;
    }

    private static void CheckRll(ReadOnlySpan<byte> channel)
    {
        int last = -1;
        int repeatedMinimum = 0;
        for (int i = 0; i < channel.Length; i++)
        {
            if (channel[i] == 0) continue;
            if (last >= 0)
            {
                int gap = i - last;
                Require(gap is >= 2 and <= 8, "17PP transition distances stay in 2T..8T");
                repeatedMinimum = gap == 2 ? repeatedMinimum + 1 : 0;
                Require(repeatedMinimum <= 6, "17PP prohibits more than six consecutive 2T runs");
            }
            last = i;
        }
    }

    private static byte[] Bits(string value) => value.Select(ch => (byte)(ch - '0')).ToArray();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Blu-ray encoding: " + message);
    }
    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Blu-ray encoding: " + message);
    }
}
