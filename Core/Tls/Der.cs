using System;
using System.Text;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// Минимальный DER-читатель: ровно столько, сколько нужно для X.509.
    /// CryptoAPI на XP не умеет проверять цепочки с SHA-256, поэтому сертификаты
    /// разбираем и проверяем сами.
    /// </summary>
    public sealed class DerReader
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _end;

        public DerReader(byte[] data) : this(data, 0, data.Length) { }

        public DerReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = offset + length;
        }

        public bool HasData { get { return _pos < _end; } }

        public byte[] Data { get { return _data; } }

        public int PeekTag()
        {
            if (_pos >= _end) return -1;
            return _data[_pos];
        }

        /// <summary>Входит внутрь SEQUENCE и отдаёт читатель на его содержимое.</summary>
        public DerReader ReadSequence()
        {
            int valueStart, valueLength;
            ReadHeader(0x30, out valueStart, out valueLength);
            return new DerReader(_data, valueStart, valueLength);
        }

        /// <summary>Входит внутрь SET и отдаёт читатель на его содержимое.</summary>
        public DerReader ReadSet()
        {
            int valueStart, valueLength;
            ReadHeader(0x31, out valueStart, out valueLength);
            return new DerReader(_data, valueStart, valueLength);
        }

        /// <summary>Читает элемент с заданным тегом и отдаёт его содержимое.</summary>
        public byte[] ReadValue(int expectedTag)
        {
            int valueStart, valueLength;
            ReadHeader(expectedTag, out valueStart, out valueLength);

            var result = new byte[valueLength];
            Buffer.BlockCopy(_data, valueStart, result, 0, valueLength);
            return result;
        }

        /// <summary>Читает элемент и отдаёт весь TLV целиком (для сравнения имён).</summary>
        public byte[] ReadRaw(int expectedTag)
        {
            int start = _pos;
            int valueStart, valueLength;
            ReadHeader(expectedTag, out valueStart, out valueLength);

            var result = new byte[valueStart + valueLength - start];
            Buffer.BlockCopy(_data, start, result, 0, result.Length);
            return result;
        }

        /// <summary>Читает элемент с неизвестным заранее тегом и отдаёт TLV.</summary>
        public byte[] ReadAnyRaw(out int tag)
        {
            int start = _pos;
            tag = _data[_pos];
            int valueStart, valueLength;
            ReadHeader(tag, out valueStart, out valueLength);

            var result = new byte[valueStart + valueLength - start];
            Buffer.BlockCopy(_data, start, result, 0, result.Length);
            return result;
        }

        public void Skip()
        {
            int valueStart, valueLength;
            ReadHeader(PeekTag(), out valueStart, out valueLength);
        }

        public string ReadOid()
        {
            byte[] raw = ReadValue(0x06);
            if (raw.Length == 0) return "";

            var sb = new StringBuilder();
            int first = raw[0];
            sb.Append(first / 40);
            sb.Append('.');
            sb.Append(first % 40);

            long value = 0;
            for (int i = 1; i < raw.Length; i++)
            {
                value = (value << 7) | (uint)(raw[i] & 0x7f);
                if ((raw[i] & 0x80) == 0)
                {
                    sb.Append('.');
                    sb.Append(value.ToString());
                    value = 0;
                }
            }
            return sb.ToString();
        }

        public byte[] ReadBitString()
        {
            byte[] raw = ReadValue(0x03);
            if (raw.Length == 0) return new byte[0];

            int unused = raw[0];
            if (unused == 0) 
            {
                var result = new byte[raw.Length - 1];
                Buffer.BlockCopy(raw, 1, result, 0, result.Length);
                return result;
            }

            var trimmed = new byte[raw.Length - 1];
            Buffer.BlockCopy(raw, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        public DateTime ReadTime()
        {
            int tag = PeekTag();
            byte[] raw = ReadValue(tag);
            string text = Encoding.ASCII.GetString(raw);

            try
            {
                if (tag == 0x17)   // UTCTime: YYMMDDHHMMSSZ
                {
                    int year = int.Parse(text.Substring(0, 2));
                    year += year >= 50 ? 1900 : 2000;
                    return new DateTime(year,
                        int.Parse(text.Substring(2, 2)), int.Parse(text.Substring(4, 2)),
                        int.Parse(text.Substring(6, 2)), int.Parse(text.Substring(8, 2)),
                        int.Parse(text.Substring(10, 2)), DateTimeKind.Utc);
                }

                // GeneralizedTime: YYYYMMDDHHMMSSZ
                return new DateTime(
                    int.Parse(text.Substring(0, 4)), int.Parse(text.Substring(4, 2)),
                    int.Parse(text.Substring(6, 2)), int.Parse(text.Substring(8, 2)),
                    int.Parse(text.Substring(10, 2)), int.Parse(text.Substring(12, 2)),
                    DateTimeKind.Utc);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public byte[] ReadInteger()
        {
            byte[] raw = ReadValue(0x02);
            int start = 0;
            while (start < raw.Length - 1 && raw[start] == 0) start++;

            var result = new byte[raw.Length - start];
            Buffer.BlockCopy(raw, start, result, 0, result.Length);
            return result;
        }

        private void ReadHeader(int expectedTag, out int valueStart, out int valueLength)
        {
            if (_pos >= _end) throw new FormatException("DER: неожиданный конец данных");
            if (_data[_pos] != expectedTag)
                throw new FormatException("DER: ожидался тег 0x" + expectedTag.ToString("x2")
                                        + ", получен 0x" + _data[_pos].ToString("x2"));

            _pos++;
            if (_pos >= _end) throw new FormatException("DER: нет длины");

            int length = _data[_pos++];
            if ((length & 0x80) != 0)
            {
                int bytes = length & 0x7f;
                if (bytes == 0 || bytes > 4) throw new FormatException("DER: неверная длина");

                length = 0;
                for (int i = 0; i < bytes; i++) length = (length << 8) | _data[_pos++];
            }

            if (length < 0 || _pos + length > _end) throw new FormatException("DER: длина за границей данных");

            valueStart = _pos;
            valueLength = length;
            _pos += length;
        }
    }
}
