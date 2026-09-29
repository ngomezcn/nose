using System.Buffers.Binary;
using System.Text;

namespace AICopilotCore.Connector;

// Serializacion de los mensajes. Gemelo de proto::Writer/proto::Reader en
// src/connector/Protocol.h.
//
// Las primitivas de BinaryPrimitives son explicitamente little-endian, y no
// BitConverter, que sigue el endianness de la maquina: hoy los dos lados
// son x86 y daria igual, pero el formato esta definido como little-endian y
// el codigo deberia decirlo, no dar por hecho el hardware.

public sealed class MessageWriter
{
    private byte[] _buf = new byte[64];
    private int _len;

    public MessageWriter(Op op)
    {
        _len = 4;           // hueco para el prefijo de longitud
        U8((byte)op);
    }

    public void U8(byte v) { Ensure(1); _buf[_len++] = v; }

    public void U16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len), v);
        _len += 2;
    }

    public void U32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
    }

    public void I32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
    }

    public void F32(float v)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
    }

    public void F64(double v)
    {
        Ensure(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(_len), v);
        _len += 8;
    }

    public void Str(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue) Array.Resize(ref bytes, ushort.MaxValue);
        U16((ushort)bytes.Length);
        Ensure(bytes.Length);
        bytes.CopyTo(_buf.AsSpan(_len));
        _len += bytes.Length;
    }

    // Deja el prefijo de longitud puesto y devuelve el mensaje listo para
    // escribir en el pipe tal cual.
    public ReadOnlyMemory<byte> Finish()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(0), (uint)(_len - 4));
        return _buf.AsMemory(0, _len);
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int cap = _buf.Length;
        while (cap < _len + extra) cap *= 2;
        Array.Resize(ref _buf, cap);
    }
}

// Igual de paranoico que el Reader de C++: leer mas alla del final marca el
// reader como invalido en vez de lanzar. Un mensaje corrupto tiene que
// acabar en "descartado", no en una excepcion que tire el hilo de lectura y
// con el la conexion.
public ref struct MessageReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private bool _ok;

    public MessageReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _ok = true;
    }

    public bool Ok => _ok;

    public byte U8()
    {
        if (!Take(1)) return 0;
        return _data[_pos - 1];
    }

    public ushort U16()
    {
        if (!Take(2)) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos - 2));
    }

    public uint U32()
    {
        if (!Take(4)) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos - 4));
    }

    public int I32()
    {
        if (!Take(4)) return 0;
        return BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos - 4));
    }

    public float F32()
    {
        if (!Take(4)) return 0f;
        return BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_pos - 4));
    }

    public double F64()
    {
        if (!Take(8)) return 0d;
        return BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_pos - 8));
    }

    public string Str()
    {
        ushort len = U16();
        if (!Take(len)) return string.Empty;
        return Encoding.UTF8.GetString(_data.Slice(_pos - len, len));
    }

    private bool Take(int n)
    {
        if (!_ok || _pos + n > _data.Length) { _ok = false; return false; }
        _pos += n;
        return true;
    }
}
