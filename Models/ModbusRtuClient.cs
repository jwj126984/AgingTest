using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace AgingTest.Models
{
    /// <summary>
    /// 通用 Modbus RTU 客户端（RS485 串口 + CRC16）。
    /// 覆盖 AGI 老化板卡协议所需功能码：FC03 读保持寄存器、FC05 写单线圈、
    /// FC0F 写多线圈、FC10 写多寄存器。CRC16 poly=0xA001，初始 0xFFFF，校验低字节在前。
    /// </summary>
    public class ModbusRtuClient : IDisposable
    {
        private SerialPort? _sp;

        /// <summary>当前 Modbus 从站地址（电阻卡=0x02，继电器卡=0x07）。</summary>
        public byte Slave { get; set; } = 1;
        public bool IsOpen => _sp != null && _sp.IsOpen;

        /// <summary>打开串口（8N1）。</summary>
        public void Open(string portName, int baud, byte slave)
        {
            if (IsOpen) return;
            Slave = slave;
            _sp = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1500,
                WriteTimeout = 1000
            };
            _sp.Open();
        }

        public void Close()
        {
            if (_sp != null && _sp.IsOpen) _sp.Close();
            _sp?.Dispose();
            _sp = null;
        }

        public void Dispose() => Close();

        // ================================================================
        // 高层业务接口
        // ================================================================

        /// <summary>FC03 读保持寄存器（返回大端 ushort 数组）。</summary>
        public ushort[] ReadRegisters(ushort start, ushort count)
        {
            var req = BuildFrame(0x03, new[] { (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count });
            var resp = Transact(req, expectedLen: 3 + count * 2);
            var data = new ushort[count];
            for (int i = 0; i < count; i++)
                data[i] = (ushort)((resp[3 + i * 2] << 8) | resp[4 + i * 2]);
            return data;
        }

        /// <summary>FC05 写单个线圈（relayOn=开）。</summary>
        public void WriteSingleCoil(ushort coil, bool on)
        {
            byte v = on ? (byte)0xFF : (byte)0x00;
            var req = BuildFrame(0x05, new[] { (byte)(coil >> 8), (byte)coil, v, (byte)0x00 });
            Transact(req, expectedLen: 8);
        }

        /// <summary>FC0F 写多线圈（values 低位在前，values[0].bit0 对应 start）。</summary>
        public void WriteMultipleCoils(ushort start, bool[] values)
        {
            int nBits = values.Length;
            int nBytes = (nBits + 7) / 8;
            var data = new byte[nBytes];
            for (int i = 0; i < nBits; i++)
                if (values[i]) data[i / 8] |= (byte)(1 << (i % 8));
            var payload = new List<byte> { (byte)(start >> 8), (byte)start, (byte)(nBits >> 8), (byte)nBits, (byte)nBytes };
            payload.AddRange(data);
            var req = BuildFrame(0x0F, payload.ToArray());
            Transact(req, expectedLen: 8);
        }

        /// <summary>FC10 写多个保持寄存器（values 大端字节）。</summary>
        public void WriteRegisters(ushort start, ushort[] values)
        {
            var payload = new List<byte> { (byte)(start >> 8), (byte)start, (byte)(values.Length >> 8), (byte)values.Length, (byte)(values.Length * 2) };
            foreach (var v in values) { payload.Add((byte)(v >> 8)); payload.Add((byte)v); }
            var req = BuildFrame(0x10, payload.ToArray());
            Transact(req, expectedLen: 8);
        }

        // ================================================================
        // 底层帧与校验
        // ================================================================

        /// <summary>构造一帧：从站地址 + 功能码 + 数据 + CRC16(低字节在前)。</summary>
        private byte[] BuildFrame(byte func, byte[] data)
        {
            var buf = new List<byte> { Slave, func };
            buf.AddRange(data);
            var crc = Crc16(buf.ToArray());
            buf.Add((byte)(crc & 0xFF));
            buf.Add((byte)((crc >> 8) & 0xFF));
            return buf.ToArray();
        }

        /// <summary>发送请求并接收应答（校验功能码回显与 CRC）。expectedLen=期望总帧长（不含即可按格式校验）。</summary>
        private byte[] Transact(byte[] req, int expectedLen)
        {
            if (!IsOpen) throw new InvalidOperationException("Modbus 串口未打开");
            _sp!.DiscardInBuffer();
            _sp.Write(req, 0, req.Length);

            var recv = new List<byte>();
            DateTime start = DateTime.Now;
            while ((DateTime.Now - start).TotalMilliseconds < 1500)
            {
                if (_sp.BytesToRead > 0)
                {
                    recv.Add((byte)_sp.ReadByte());
                    if (recv.Count >= expectedLen) break;
                }
                else Thread.Sleep(1);
            }
            if (recv.Count < expectedLen)
                throw new TimeoutException($"Modbus 通信超时（应收 {expectedLen} 字节，实收 {recv.Count}），请检查 485 接线/波特率/从站地址");

            if (recv[1] >= 0x80)
                throw new Exception($"Modbus 异常应答：从站返回功能码 0x{recv[1]:X2}");

            // CRC 校验（末2字节）
            var body = recv.Take(recv.Count - 2).ToArray();
            ushort expect = Crc16(body);
            byte crcLo = recv[recv.Count - 2], crcHi = recv[recv.Count - 1];
            if (crcLo != (expect & 0xFF) || crcHi != ((expect >> 8) & 0xFF))
                throw new Exception("Modbus CRC16 校验失败");
            return recv.ToArray();
        }

        /// <summary>Modbus CRC16：poly 0xA001，初始 0xFFFF。</summary>
        public static ushort Crc16(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }
    }
}
