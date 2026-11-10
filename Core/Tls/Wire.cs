using System;
using System.Text;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// Сборка байтов «в стиле» TLS: все длины в сетевом порядке, вложенные
    /// структуры удобно писать с конца. Нужен для ClientHello и HTTP-запросов.
    /// </summary>
    public sealed class ByteWriter
    {
        private byte[] _data = new byte[256];
        private int _length;

        public int Length { get { return _length; } }

        private void Ensure(int extra)
        {
            if (_length + extra <= _data.Length) return;

            int size = _data.Length;
            while (size < _length + extra) size *= 2;

            var bigger = new byte[size];
            Buffer.BlockCopy(_data, 0, bigger, 0, _length);
            _data = bigger;
        }

        public ByteWriter WriteByte(byte value)
        {
            Ensure(1);
            _data[_length++] = value;
            return this;
        }

        public ByteWriter WriteU16(int value)
        {
            Ensure(2);
            _data[_length++] = (byte)(value >> 8);
            _data[_length++] = (byte)value;
            return this;
        }

        public ByteWriter WriteU24(int value)
        {
            Ensure(3);
            _data[_length++] = (byte)(value >> 16);
            _data[_length++] = (byte)(value >> 8);
            _data[_length++] = (byte)value;
            return this;
        }

        public ByteWriter WriteU32(uint value)
        {
            Ensure(4);
            _data[_length++] = (byte)(value >> 24);
            _data[_length++] = (byte)(value >> 16);
            _data[_length++] = (byte)(value >> 8);
            _data[_length++] = (byte)value;
            return this;
        }

        public ByteWriter WriteBytes(byte[] data)
        {
            return WriteBytes(data, 0, data.Length);
        }

        public ByteWriter WriteBytes(byte[] data, int offset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(data, offset, _data, _length, count);
            _length += count;
            return this;
        }

        public ByteWriter WriteAscii(string text)
        {
            return WriteBytes(Encoding.ASCII.GetBytes(text));
        }

        /// <summary>Блок с 2-байтовой длиной впереди.</summary>
        public ByteWriter WriteBlock16(byte[] data)
        {
            WriteU16(data.Length);
            return WriteBytes(data);
        }

        /// <summary>Блок с 3-байтовой длиной впереди.</summary>
        public ByteWriter WriteBlock24(byte[] data)
        {
            WriteU24(data.Length);
            return WriteBytes(data);
        }

        /// <summary>Блок с 1-байтовой длиной впереди.</summary>
        public ByteWriter WriteBlock8(byte[] data)
        {
            WriteByte((byte)data.Length);
            return WriteBytes(data);
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_data, 0, result, 0, _length);
            return result;
        }
    }

    /// <summary>Чтение байтов TLS-структур с проверкой границ.</summary>
    public sealed class ByteReader
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _end;

        public ByteReader(byte[] data) : this(data, 0, data.Length) { }

        public ByteReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = offset + length;
        }

        public int Remaining { get { return _end - _pos; } }

        public int Position { get { return _pos; } }

        public byte ReadByte()
        {
            Need(1);
            return _data[_pos++];
        }

        public int ReadU16()
        {
            Need(2);
            int value = (_data[_pos] << 8) | _data[_pos + 1];
            _pos += 2;
            return value;
        }

        public int ReadU24()
        {
            Need(3);
            int value = (_data[_pos] << 16) | (_data[_pos + 1] << 8) | _data[_pos + 2];
            _pos += 3;
            return value;
        }

        public uint ReadU32()
        {
            Need(4);
            uint value = ((uint)_data[_pos] << 24) | ((uint)_data[_pos + 1] << 16) |
                         ((uint)_data[_pos + 2] << 8) | _data[_pos + 3];
            _pos += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            Need(count);
            var result = new byte[count];
            Buffer.BlockCopy(_data, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        /// <summary>Блок с 1-, 2- или 3-байтовой длиной.</summary>
        public byte[] ReadBlock8()
        {
            return ReadBytes(ReadByte());
        }

        public byte[] ReadBlock16()
        {
            return ReadBytes(ReadU16());
        }

        public byte[] ReadBlock24()
        {
            return ReadBytes(ReadU24());
        }

        public void Skip(int count)
        {
            Need(count);
            _pos += count;
        }

        private void Need(int count)
        {
            if (count < 0 || _pos + count > _end)
                throw new TlsException("Данные TLS закончились неожиданно");
        }
    }
}
