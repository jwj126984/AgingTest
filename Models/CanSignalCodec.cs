using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgingTest.Models
{
    /// <summary>一条 CAN 信号定义（对应 DBC 中 SG_ 一行）。</summary>
    public class CanSignalDef
    {
        public string Name { get; set; } = "";
        /// <summary>起始位。Intel：从字节 0 的 LSB 起算；Motorola：从字节 0 的 MSB 起算（DBC 习惯）。</summary>
        public int StartBit { get; set; }
        public int Length { get; set; }
        public double Factor { get; set; } = 1.0;
        public double Offset { get; set; }
        public string Unit { get; set; } = "";
        /// <summary>true = Intel（小端），false = Motorola（大端）。</summary>
        public bool Intel { get; set; } = true;
        public bool Signed { get; set; }
        public double Min { get; set; } = double.NaN;
        public double Max { get; set; } = double.NaN;
        /// <summary>枚举值文本，例如 0=断开;1=闭合。</summary>
        public Dictionary<long, string>? ValueMap { get; set; }

        public CanSignalDef() { }

        public CanSignalDef(string name, int startBit, int length, double factor = 1, double offset = 0,
            string unit = "", bool intel = true, bool signed = false)
        {
            Name = name;
            StartBit = startBit;
            Length = length;
            Factor = factor;
            Offset = offset;
            Unit = unit;
            Intel = intel;
            Signed = signed;
        }
    }

    /// <summary>
    /// CAN / CANFD 报文信号解析：位域提取、物理值换算、ASCII 解码。
    /// 附录1 的 DID 解析直接用到这里的字节读取；整车 CAN 信号可传入 <see cref="CanSignalDef"/> 列表批量解析。
    /// </summary>
    public static class CanSignalCodec
    {
        /// <summary>按起始位/长度提取无符号原始值（Intel 小端 / Motorola 大端）。</summary>
        public static ulong ExtractRaw(byte[] data, int startBit, int length, bool intel = true)
        {
            if (data == null || length <= 0 || length > 64 || startBit < 0) return 0;
            ulong raw = 0;

            if (intel)
            {
                for (int i = 0; i < length; i++)
                {
                    int bitIndex = startBit + i;
                    int byteIndex = bitIndex / 8;
                    int bitInByte = bitIndex % 8;
                    if (byteIndex >= data.Length) break;
                    if (((data[byteIndex] >> bitInByte) & 0x01) != 0)
                        raw |= 1UL << i;
                }
            }
            else
            {
                int byteIndex = startBit / 8;
                int bitInByte = startBit % 8;
                for (int i = 0; i < length; i++)
                {
                    if (byteIndex >= data.Length) break;
                    int bit = (data[byteIndex] >> bitInByte) & 0x01;   // DBC 锯齿位号：byte0 的 bit7 为 MSB
                    raw = (raw << 1) | (uint)bit;
                    bitInByte--;
                    if (bitInByte < 0)
                    {
                        bitInByte = 7;
                        byteIndex++;
                    }
                }
            }
            return raw;
        }

        /// <summary>提取有符号原始值（按位宽做符号扩展）。</summary>
        public static long ExtractSigned(byte[] data, int startBit, int length, bool intel = true)
        {
            ulong raw = ExtractRaw(data, startBit, length, intel);
            if (length > 0 && length < 64 && ((raw >> (length - 1)) & 1UL) != 0)
                return (long)(raw | (~0UL << length));
            return (long)raw;
        }

        /// <summary>物理值 = 原始值 × Factor + Offset。</summary>
        public static double ToPhysical(double raw, double factor, double offset) => raw * factor + offset;

        /// <summary>按信号定义解出物理值。</summary>
        public static double Decode(byte[] data, CanSignalDef def)
        {
            long raw = def.Signed
                ? ExtractSigned(data, def.StartBit, def.Length, def.Intel)
                : (long)ExtractRaw(data, def.StartBit, def.Length, def.Intel);
            return ToPhysical(raw, def.Factor, def.Offset);
        }

        /// <summary>解码并格式化为 "名称=值 单位"（带枚举文本时优先显示枚举）。</summary>
        public static string DecodeText(byte[] data, CanSignalDef def)
        {
            long raw = def.Signed
                ? ExtractSigned(data, def.StartBit, def.Length, def.Intel)
                : (long)ExtractRaw(data, def.StartBit, def.Length, def.Intel);

            if (def.ValueMap != null && def.ValueMap.TryGetValue(raw, out string? text))
                return $"{def.Name}={text}";

            double value = ToPhysical(raw, def.Factor, def.Offset);
            string number = Math.Abs(value) >= 1000 ? value.ToString("F1") : value.ToString("F3").TrimEnd('0').TrimEnd('.');
            return string.IsNullOrEmpty(def.Unit) ? $"{def.Name}={number}" : $"{def.Name}={number} {def.Unit}";
        }

        /// <summary>批量解析一帧报文，返回 "名称=值" 列表。</summary>
        public static List<string> DecodeAll(byte[] data, IEnumerable<CanSignalDef> defs)
            => defs.Select(d => DecodeText(data, d)).ToList();

        /// <summary>取单 bit（bitIndex 从字节 0 的 LSB 起算）。</summary>
        public static bool Bit(byte[] data, int bitIndex)
        {
            int byteIndex = bitIndex / 8;
            if (data == null || byteIndex < 0 || byteIndex >= data.Length) return false;
            return ((data[byteIndex] >> (bitIndex % 8)) & 0x01) != 0;
        }

        public static byte U8(byte[] data, int offset)
            => offset >= 0 && offset < data.Length ? data[offset] : (byte)0;

        public static ushort U16(byte[] data, int offset, bool bigEndian = true)
        {
            if (offset < 0 || offset + 1 >= data.Length) return 0;
            return bigEndian
                ? (ushort)((data[offset] << 8) | data[offset + 1])
                : (ushort)((data[offset + 1] << 8) | data[offset]);
        }

        public static uint U32(byte[] data, int offset, bool bigEndian = true)
        {
            if (offset < 0 || offset + 3 >= data.Length) return 0;
            return bigEndian
                ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
                : ((uint)data[offset + 3] << 24) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset];
        }

        public static int S32(byte[] data, int offset, bool bigEndian = true) => unchecked((int)U32(data, offset, bigEndian));

        /// <summary>ASCII 解码（去掉 0x00、空格与不可见字符）。</summary>
        public static string Ascii(byte[] data, int offset = 0, int length = -1)
        {
            if (data == null) return "";
            int start = Math.Max(0, offset);
            int count = length < 0 ? data.Length - start : Math.Min(length, data.Length - start);
            if (count <= 0) return "";

            var sb = new StringBuilder(count);
            for (int i = 0; i < count; i++)
            {
                byte b = data[start + i];
                if (b == 0x00) break;
                if (b is >= 0x20 and <= 0x7E) sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        /// <summary>ASCII 编码（不足补 0x00，超长抛异常由调用方截断）。</summary>
        public static byte[] AsciiBytes(string text, int length)
        {
            byte[] result = new byte[length];
            if (string.IsNullOrEmpty(text)) return result;
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, result, 0, Math.Min(bytes.Length, length));
            return result;
        }

        /// <summary>可打印文本：非 ASCII 字节显示为 '.'。</summary>
        public static string Printable(byte[] data)
        {
            if (data == null) return "";
            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            return sb.ToString();
        }

        public static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));
    }
}
