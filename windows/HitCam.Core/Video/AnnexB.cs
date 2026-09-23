namespace HitCam.Core.Video;

/// <summary>H.264 NAL unit types we care about.</summary>
public enum H264NalType : byte
{
    Slice = 1,
    Idr = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    AccessUnitDelimiter = 9,
}

/// <summary>Helpers for H.264 Annex-B byte streams (start-code delimited NAL units).</summary>
public static class AnnexB
{
    /// <summary>Returns the (offset, length) of each NAL unit payload, excluding start codes.</summary>
    public static List<Range> SplitNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<Range>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                    units.Add(new Range(start, TrimTrailingZeros(data, start, i)));
                i += 3;
                start = i;
                continue;
            }
            i++;
        }

        if (start >= 0 && start < data.Length)
            units.Add(new Range(start, data.Length));
        return units;
    }

    public static H264NalType NalType(byte firstByte) => (H264NalType)(firstByte & 0x1F);

    /// <summary>True if the access unit contains an IDR slice and both SPS and PPS, i.e. decoding can start here.</summary>
    public static bool IsDecodableKeyframe(ReadOnlySpan<byte> data)
    {
        bool sps = false, pps = false, idr = false;
        foreach (var range in SplitNalUnits(data))
        {
            var nal = data[range];
            if (nal.IsEmpty)
                continue;
            switch (NalType(nal[0]))
            {
                case H264NalType.Sps: sps = true; break;
                case H264NalType.Pps: pps = true; break;
                case H264NalType.Idr: idr = true; break;
            }
        }
        return sps && pps && idr;
    }

    // A 4-byte start code (00 00 00 01) leaves one zero at the end of the previous unit.
    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
            end--;
        return end;
    }
}
