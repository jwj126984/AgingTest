using System;
using System.IO;
using System.Net.Sockets;

namespace AgingTest.Models
{
    /// <summary>
    /// 通用 Modbus TCP 客户端（仅实现烘箱协议用到的 01/03/04/05/06 功能码）。
    /// 帧格式（MBAP 头 6 字节 + PDU）：
    ///   事务标识(2) + 协议标识(2=0) + 长度(2=站号1字节+PDU) + 站号(1) + 功能码 + 数据
    /// 与《X4MCGSLDETYA120MV104.05WA通讯协议》1.2.2 网口通讯举例一致。
    /// </summary>
    public class ModbusTcpClient : IDisposable
    {
        private TcpClient? _tcp;
        private NetworkStream? _ns;
        private ushort _tid;
        private readonly object _sync = new object();
        private string? _lastIp;
        private int _lastPort;
        private byte _lastUnitId;
        private bool _manualClose;

        /// <summary>从站地址（设备地址码），默认 1。</summary>
        public byte UnitId { get; set; } = 1;

        /// <summary>读写超时（毫秒）。</summary>
        public int TimeoutMs { get; set; } = 2000;

        public bool IsOpen
        {
            get
            {
                if (_tcp is not { Connected: true } || _ns == null) return false;
                try
                {
                    // TcpClient.Connected 可能是过期状态：远端关闭后 Poll 读到 0 字节即判定断开。
                    if (_tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0)
                        return false;
                }
                catch { return false; }
                return true;
            }
        }

        public void Open(string ip, int port, byte unitId)
        {
            lock (_sync)
            {
                CloseCore();
                _lastIp = ip;
                _lastPort = port;
                _lastUnitId = unitId;
                _manualClose = false;
                OpenCore(ip, port, unitId);
            }
        }

        private void OpenCore(string ip, int port, byte unitId)
        {
            var tcp = new TcpClient
            {
                ReceiveTimeout = TimeoutMs,
                SendTimeout = 1000,
                NoDelay = true
            };
            try
            {
                tcp.Connect(ip, port);
                _tcp = tcp;
                _ns = tcp.GetStream();
                UnitId = unitId;
            }
            catch
            {
                try { tcp.Close(); } catch { }
                throw;
            }
        }

        public void Close()
        {
            _manualClose = true;
            lock (_sync) CloseCore();
        }

        private void CloseCore()
        {
            try { _ns?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _ns = null;
            _tcp = null;
        }

        public void Dispose() => Close();

        // ==================== 读 ====================

        /// <summary>功能码 03：读保持寄存器（最多 125 个）。</summary>
        public ushort[] ReadHoldingRegisters(int startAddress, int count) => ReadRegisters(0x03, startAddress, count);

        /// <summary>功能码 04：读输入寄存器。</summary>
        public ushort[] ReadInputRegisters(int startAddress, int count) => ReadRegisters(0x04, startAddress, count);

        private ushort[] ReadRegisters(byte func, int startAddress, int count)
        {
            if (count < 1 || count > 125) throw new ArgumentOutOfRangeException(nameof(count), "寄存器数量需 1~125");
            byte[] pdu = new byte[5];
            pdu[0] = func;
            pdu[1] = (byte)(startAddress >> 8);
            pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(count >> 8);
            pdu[4] = (byte)count;

            byte[] resp = Transact(pdu);
            if (resp.Length < 2 + count * 2) throw new IOException("Modbus 响应长度不足");

            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int idx = 2 + i * 2;   // 跳过功能码 + 字节数
                values[i] = (ushort)((resp[idx] << 8) | resp[idx + 1]);
            }
            return values;
        }

        /// <summary>功能码 01：读线圈（最多 2000 个）。</summary>
        public bool[] ReadCoils(int startAddress, int count)
        {
            if (count < 1 || count > 2000) throw new ArgumentOutOfRangeException(nameof(count), "线圈数量需 1~2000");
            byte[] pdu = new byte[5];
            pdu[0] = 0x01;
            pdu[1] = (byte)(startAddress >> 8);
            pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(count >> 8);
            pdu[4] = (byte)count;

            byte[] resp = Transact(pdu);
            bool[] bits = new bool[count];
            for (int i = 0; i < count; i++)
                bits[i] = ((resp[2 + i / 8] >> (i % 8)) & 0x01) != 0;
            return bits;
        }

        // ==================== 写 ====================

        /// <summary>功能码 06：写单个保持寄存器。</summary>
        public void WriteSingleRegister(int address, ushort value)
        {
            byte[] pdu =
            {
                0x06,
                (byte)(address >> 8), (byte)address,
                (byte)(value >> 8), (byte)value
            };
            Transact(pdu);
        }

        /// <summary>功能码 05：写单个线圈（FF00=ON，0000=OFF）。</summary>
        public void WriteSingleCoil(int address, bool on)
        {
            byte[] pdu =
            {
                0x05,
                (byte)(address >> 8), (byte)address,
                (byte)(on ? 0xFF : 0x00), 0x00
            };
            Transact(pdu);
        }

        // ==================== 事务 ====================

        private byte[] Transact(byte[] pdu)
        {
            try
            {
                return TransactOnce(pdu);
            }
            catch (Exception ex) when (IsRetryable(ex) && !_manualClose && !string.IsNullOrWhiteSpace(_lastIp))
            {
                // 多工位长时间运行时，烘箱 TCP 可能被设备端主动断开或残留半帧；
                // 关闭旧 socket 后按原 IP/端口/站号重连一次，避免整个老化流程因一次断链失败。
                if (!TryReconnect()) throw;
                return TransactOnce(pdu);
            }
        }

        private byte[] TransactOnce(byte[] pdu)
        {
            lock (_sync)
            {
                if (!IsOpen || _ns == null) throw new InvalidOperationException("Modbus TCP 未连接");

                ushort tid = ++_tid;
                byte[] frame = new byte[6 + 1 + pdu.Length];
                frame[0] = (byte)(tid >> 8);
                frame[1] = (byte)tid;
                frame[2] = 0;                       // 协议标识
                frame[3] = 0;
                frame[4] = (byte)((pdu.Length + 1) >> 8);
                frame[5] = (byte)(pdu.Length + 1);
                frame[6] = UnitId;
                Array.Copy(pdu, 0, frame, 7, pdu.Length);

                _ns.Write(frame, 0, frame.Length);

                // 读 MBAP 头
                byte[] head = ReadExactly(6);
                ushort respTid = (ushort)((head[0] << 8) | head[1]);
                ushort proto = (ushort)((head[2] << 8) | head[3]);
                ushort len = (ushort)((head[4] << 8) | head[5]);

                if (proto != 0) throw new IOException("Modbus 协议标识非 0");
                if (respTid != tid) throw new IOException($"事务标识不匹配（请求 {tid}，响应 {respTid}）");
                if (len < 2) throw new IOException("Modbus 响应长度非法");

                byte[] body = ReadExactly(len);     // 站号 + PDU
                byte unit = body[0];
                if (unit != UnitId) throw new IOException($"从站地址不匹配（请求 {UnitId}，响应 {unit}）");

                byte func = body[1];
                if ((func & 0x80) != 0)
                {
                    byte code = body.Length > 2 ? body[2] : (byte)0;
                    throw new IOException($"Modbus 异常响应：功能码 0x{func:X2}，异常码 {code}（{ExceptionText(code)}）");
                }

                byte[] respPdu = new byte[body.Length - 1];
                Array.Copy(body, 1, respPdu, 0, respPdu.Length);
                return respPdu;
            }
        }

        private bool TryReconnect()
        {
            if (_manualClose || string.IsNullOrWhiteSpace(_lastIp)) return false;
            lock (_sync)
            {
                try
                {
                    CloseCore();
                    OpenCore(_lastIp, _lastPort, _lastUnitId);
                    return true;
                }
                catch
                {
                    CloseCore();
                    return false;
                }
            }
        }

        private static bool IsRetryable(Exception ex)
        {
            if (ex is InvalidOperationException) return true;
            if (ex is SocketException) return true;
            // Modbus 异常响应（非法地址/值/忙）不能靠重连解决，直接抛给业务层。
            if (ex is IOException io)
                return !io.Message.StartsWith("Modbus 异常响应", StringComparison.Ordinal);
            return false;
        }

        private byte[] ReadExactly(int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = _ns!.Read(buf, offset, count - offset);
                if (n <= 0) throw new IOException("Modbus 连接被关闭");
                offset += n;
            }
            return buf;
        }

        private static string ExceptionText(byte code) => code switch
        {
            1 => "非法功能码",
            2 => "非法数据地址",
            3 => "非法数据值",
            4 => "从站设备故障",
            5 => "确认（处理中）",
            6 => "从站设备忙",
            _ => "未知异常"
        };
    }
}
