namespace ScaleBridge.Core.Protocol;

/// <summary>按小端顺序读取字节的轻量读取器；任何读取越界都返回 false，不抛异常。</summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ByteReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly int Position => _pos;
    public readonly int Remaining => _data.Length - _pos;

    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1) { value = 0; return false; }
        value = _data[_pos++];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2) { value = 0; return false; }
        value = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
        _pos += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4) { value = 0; return false; }
        value = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
        _pos += 4;
        return true;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        if (Remaining < count) { value = default; return false; }
        value = _data.Slice(_pos, count);
        _pos += count;
        return true;
    }
}
