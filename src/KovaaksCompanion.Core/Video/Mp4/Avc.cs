namespace KovaaksCompanion.Core.Video.Mp4;

/// <summary>H.264 byte-stream helpers: libobs hands out Annex B (start-code) data, MP4 wants length-prefixed NAL units.</summary>
public static class Avc
{
    public const int NalSps = 7, NalPps = 8, NalAud = 9;

    /// <summary>Splits an Annex B buffer into NAL units (without start codes).</summary>
    public static List<ArraySegment<byte>> SplitNals(byte[] d)
    {
        var nals = new List<ArraySegment<byte>>();
        int i = 0, start = -1;
        while (i + 2 < d.Length)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                if (start >= 0) nals.Add(Trim(d, start, i));
                start = i + 3; i += 3;
            }
            else i++;
        }
        if (start >= 0 && start < d.Length) nals.Add(Trim(d, start, d.Length));
        return nals;
    }

    // the 3-byte start code may be preceded by a zero (4-byte form), which belongs to the previous NAL's trailing zeros
    static ArraySegment<byte> Trim(byte[] d, int from, int to)
    {
        while (to > from && d[to - 1] == 0) to--;
        return new ArraySegment<byte>(d, from, to - from);
    }

    /// <summary>First SPS and PPS found in an Annex B buffer (the encoder's extra data); null when either is missing.</summary>
    public static (byte[] Sps, byte[] Pps)? ParameterSets(byte[] annexB)
    {
        byte[]? sps = null, pps = null;
        foreach (var n in SplitNals(annexB))
        {
            if (n.Count == 0) continue;
            var type = n[0] & 0x1F;
            if (type == NalSps && sps == null) sps = n.ToArray();
            else if (type == NalPps && pps == null) pps = n.ToArray();
        }
        return sps != null && pps != null && sps.Length >= 4 ? (sps, pps) : null;
    }

    /// <summary>Annex B access unit -> 4-byte length-prefixed NAL units, dropping access-unit delimiters and in-band SPS/PPS (they live in avcC).</summary>
    public static byte[] ToLengthPrefixed(byte[] annexB)
    {
        var nals = SplitNals(annexB).Where(n => n.Count > 0 && (n[0] & 0x1F) is not (NalSps or NalPps or NalAud)).ToList();
        var o = new byte[nals.Sum(n => 4 + n.Count)];
        int p = 0;
        foreach (var n in nals)
        {
            o[p] = (byte)(n.Count >> 24); o[p + 1] = (byte)(n.Count >> 16); o[p + 2] = (byte)(n.Count >> 8); o[p + 3] = (byte)n.Count;
            Buffer.BlockCopy(n.Array!, n.Offset, o, p + 4, n.Count);
            p += 4 + n.Count;
        }
        return o;
    }
}
