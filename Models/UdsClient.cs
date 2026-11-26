using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AgingTest.Models
{
    /// <summary>UDS 负响应（0x7F SID NRC）。</summary>
    public class UdsNegativeResponseException : Exception
    {
        public byte ServiceId { get; }
        public byte Nrc { get; }

        public UdsNegativeResponseException(byte serviceId, byte nrc)
            : base($"UDS 负响应：服务 0x{serviceId:X2}，NRC 0x{nrc:X2}（{NrcText(nrc)}）")
        {
            ServiceId = serviceId;
            Nrc = nrc;
        }

        public static string NrcText(byte nrc) => nrc switch
        {
            0x10 => "通用拒绝",
            0x11 => "服务不支持",
            0x12 => "子功能不支持",
            0x13 => "报文长度或格式错误",
            0x22 => "条件不满足",
            0x24 => "请求序列错误",
            0x31 => "请求超出范围",
            0x33 => "安全访问被拒绝",
            0x35 => "密钥无效",
            0x36 => "尝试次数超限",
            0x37 => "延时未到期",
            0x72 => "编程失败",
            0x78 => "响应挂起",
            0x7E => "当前会话下子功能不支持",
            0x7F => "当前会话下服务不支持",
            _ => "未知 NRC",
        };
    }

    /// <summary>一条故障码记录（19 02 09 响应）。</summary>
    public class UdsDtc
    {
        public byte[] Raw { get; init; } = Array.Empty<byte>();   // 3 字节
        public byte Status { get; init; }

        /// <summary>原始十六进制，例如 96 D4 01。</summary>
        public string RawHex => string.Join(" ", Raw.Select(b => b.ToString("X2")));

        /// <summary>标准 DTC 文本，例如 96D401 → B16D401。</summary>
        public string Code
        {
            get
            {
                if (Raw.Length < 3) return RawHex;
                int sysBits = (Raw[0] >> 6) & 0x03;
                char sys = sysBits switch { 0 => 'P', 1 => 'C', 2 => 'B', _ => 'U' };
                int d1 = (Raw[0] >> 4) & 0x03;
                int d2 = Raw[0] & 0x0F;
                int d3 = (Raw[1] >> 4) & 0x0F;
                int d4 = Raw[1] & 0x0F;
                return $"{sys}{d1}{d2:X}{d3:X}{d4:X}{Raw[2]:X2}";
            }
        }
    }

    /// <summary>
    /// UDS 客户端（ISO 14229）。请求 ID / 响应 ID / 是否 CANFD 均可配置，默认按附录1：0x775 / 0x77D / CANFD。
    /// 所有方法直接返回正响应字节（含 SID+0x40），负响应抛出 <see cref="UdsNegativeResponseException"/>。
    /// </summary>
    public class UdsClient : IDisposable
    {
        public IsoTpTransport Transport { get; }

        /// <summary>单次请求超时（ms）。</summary>
        public int TimeoutMs { get; set; } = 1000;

        /// <summary>当前会话是否处于非默认会话（10 03 之后）。</summary>
        public bool IsSessionActive { get; private set; }
        public byte ActiveSession { get; private set; } = 0x01;

        /// <summary>安全访问是否已通过（27 02 正响应后置位）。</summary>
        public bool IsSecurityUnlocked { get; private set; }

        /// <summary>进入扩展会话后是否自动起 3E 80 保活线程。多工位场景由管理器的单线程统一保活时设为 false。</summary>
        public bool AutoKeepAlive { get; set; } = true;

        public event Action<string>? Log
        {
            add => Transport.Log += value;
            remove => Transport.Log -= value;
        }

        private Thread? _keepAliveThread;
        private volatile bool _keepAliveRunning;

        public UdsClient(int canIndex, int channel, uint requestId = 0x775, uint responseId = 0x77D,
            bool isFd = true, bool isExt = false)
        {
            Transport = new IsoTpTransport(canIndex, channel, requestId, responseId, isFd, isExt);
        }

        // ================================================================
        // 基础收发
        // ================================================================
        public byte[] Request(params byte[] pdu) => Request((int?)null, pdu);

        public byte[] Request(int? timeoutMs, params byte[] pdu)
        {
            byte[] resp = Transport.Request(pdu, timeoutMs ?? TimeoutMs);
            if (resp.Length == 0) throw new UdsNegativeResponseException(pdu[0], 0x10);
            if (resp[0] == 0x7F)
            {
                byte serviceId = resp.Length > 1 ? resp[1] : pdu[0];
                byte nrc = resp.Length > 2 ? resp[2] : (byte)0x10;
                throw new UdsNegativeResponseException(serviceId, nrc);
            }
            return resp;
        }

        // ================================================================
        // 0x10 诊断会话
        // ================================================================
        public byte[] DiagnosticSessionControl(byte sessionType)
        {
            var resp = Request(0x10, sessionType);
            ActiveSession = sessionType;
            IsSessionActive = sessionType != 0x01;
            if (IsSessionActive)
            {
                if (AutoKeepAlive) StartKeepAlive();
            }
            else StopKeepAlive();
            return resp;
        }

        /// <summary>10 03：扩展会话（附录1 要求安全访问、例程控制前必须先进入）。</summary>
        public byte[] EnterExtendedSession() => DiagnosticSessionControl(0x03);

        /// <summary>10 01：回默认会话。</summary>
        public byte[] ReturnToDefaultSession() => DiagnosticSessionControl(0x01);

        // ================================================================
        // 0x27 安全访问
        // ================================================================
        public byte[] SecurityAccessRequestSeed(byte level = 0x01) => Request(0x27, level);

        public byte[] SecurityAccessSendKey(byte level, byte[] key)
        {
            byte[] pdu = new byte[2 + key.Length];
            pdu[0] = 0x27;
            pdu[1] = (byte)(level + 1);   // 请求种子 01 → 发送密钥 02
            Array.Copy(key, 0, pdu, 2, key.Length);
            return Request(pdu);
        }

        /// <summary>
        /// 完整解锁流程：27 01 取种子 → 算法库算密钥 → 27 02 发送。
        /// 附录1 只写"通过算法库计算"，算法本身不在文档内；seedToKey 由调用方提供（厂商算法脚本/库）。
        /// </summary>
        public void Unlock(byte level, Func<byte[], byte[]> seedToKey)
        {
            var seedResp = SecurityAccessRequestSeed(level);
            byte[] seed = seedResp.Skip(2).ToArray();
            if (seed.Length == 0) throw new UdsNegativeResponseException(0x27, 0x12);
            if (seed.All(b => b == 0x00) || seed.All(b => b == 0xFF))
            {
                // 全 0/全 FF 表示 ECU 已解锁，无需发送密钥
                IsSecurityUnlocked = true;
                return;
            }

            byte[] key = seedToKey(seed)
                ?? throw new InvalidOperationException("密钥算法返回空，无法完成 27 02");
            SecurityAccessSendKey(level, key);
            IsSecurityUnlocked = true;
        }

        /// <summary>已由外部算好密钥时直接发送。</summary>
        public void UnlockWithKey(byte level, byte[] key)
        {
            SecurityAccessRequestSeed(level);
            SecurityAccessSendKey(level, key);
            IsSecurityUnlocked = true;
        }

        // ================================================================
        // 0x22 / 0x2E 数据读写
        // ================================================================
        /// <summary>22 DID，返回 62 DID Data。</summary>
        public byte[] ReadDataByIdentifier(ushort did)
            => Request(0x22, (byte)(did >> 8), (byte)did);

        /// <summary>只取 22 响应中的数据部分（去掉 62 + DID 三个字节）。</summary>
        public byte[] ReadDidData(ushort did)
        {
            var resp = ReadDataByIdentifier(did);
            return resp.Length <= 3 ? Array.Empty<byte>() : resp.Skip(3).ToArray();
        }

        /// <summary>2E DID Data，返回 6E DID。</summary>
        public byte[] WriteDataByIdentifier(ushort did, byte[] data)
        {
            byte[] pdu = new byte[3 + data.Length];
            pdu[0] = 0x2E;
            pdu[1] = (byte)(did >> 8);
            pdu[2] = (byte)did;
            Array.Copy(data, 0, pdu, 3, data.Length);
            return Request(pdu);
        }

        /// <summary>
        /// 2F DID ControlOption Data：输入输出控制（强控 MOS/BUCK、均衡等）。
        /// 附录1 中 73BA / 73BB 均为 2F 服务，不是 2E；请求示例 2F 73 BA 03 04 07。
        /// </summary>
        public byte[] InputOutputControlByIdentifier(ushort did, byte[] control)
        {
            byte[] pdu = new byte[3 + control.Length];
            pdu[0] = 0x2F;
            pdu[1] = (byte)(did >> 8);
            pdu[2] = (byte)did;
            Array.Copy(control, 0, pdu, 3, control.Length);
            return Request(pdu);
        }

        // ================================================================
        // 0x31 例程控制
        // ================================================================
        /// <summary>31 01 RoutineId [参数]；controlType 01=启动，02=停止，03=查询结果。</summary>
        public byte[] RoutineControl(ushort routineId, byte controlType = 0x01, byte[]? parameters = null)
        {
            var list = new List<byte> { 0x31, controlType, (byte)(routineId >> 8), (byte)routineId };
            if (parameters != null) list.AddRange(parameters);
            return Request(list.ToArray());
        }

        // ================================================================
        // 0x14 / 0x19 故障码
        // ================================================================
        /// <summary>14 FF FF FF 清除全部故障码。</summary>
        public byte[] ClearDiagnosticInformation(uint group = 0x00FFFFFF)
            => Request(0x14, (byte)(group >> 16), (byte)(group >> 8), (byte)group);

        /// <summary>19 02 09：按状态掩码读取故障码（附录1 功能测试项要求）。</summary>
        public List<UdsDtc> ReadDtcByStatusMask(byte statusMask = 0x09)
        {
            var resp = Request(0x19, 0x02, statusMask);
            var list = new List<UdsDtc>();
            if (resp.Length < 3) return list;

            // 59 02 [availabilityMask] 然后每 3 字节一个 DTC + 1 字节状态
            int index = 3;
            while (index + 4 <= resp.Length)
            {
                byte[] raw = { resp[index], resp[index + 1], resp[index + 2] };
                if (raw[0] == 0 && raw[1] == 0 && raw[2] == 0) break;
                list.Add(new UdsDtc { Raw = raw, Status = resp[index + 3] });
                index += 4;
            }
            return list;
        }

        // ================================================================
        // 其它常用服务
        // ================================================================
        /// <summary>3E 80：保持会话（S3=5000ms，附录1）。抑制正响应时只发不等回。</summary>
        public byte[] TesterPresent(bool suppressPositiveResponse = true)
        {
            if (suppressPositiveResponse)
            {
                Transport.SendOnly(new byte[] { 0x3E, 0x80 });
                return new byte[] { 0x7E, 0x00 };
            }
            return Request(0x3E, 0x00);
        }

        /// <summary>11 01：硬复位；11 02：钥匙关 ON-OFF；11 03：软复位。</summary>
        public byte[] EcuReset(byte resetType = 0x01) => Request(0x11, resetType);

        /// <summary>28 00 01：恢复正常通讯。</summary>
        public byte[] CommunicationControl(byte controlType = 0x00, byte communicationType = 0x01)
            => Request(0x28, controlType, communicationType);

        // ================================================================
        // 会话保持（S3 = 5000 ms）
        // ================================================================
        public void StartKeepAlive(int intervalMs = 2000)
        {
            if (_keepAliveRunning) return;
            _keepAliveRunning = true;
            _keepAliveThread = new Thread(() =>
            {
                while (_keepAliveRunning)
                {
                    Thread.Sleep(intervalMs);
                    if (!_keepAliveRunning) break;
                    try { TesterPresent(); }
                    catch { /* 会话已掉线或 ECU 不回 3E，忽略 */ }
                }
            })
            { IsBackground = true, Name = "UdsKeepAlive" };
            _keepAliveThread.Start();
        }

        public void StopKeepAlive()
        {
            _keepAliveRunning = false;
            _keepAliveThread?.Join(1000);
            _keepAliveThread = null;
        }

        public void Dispose()
        {
            StopKeepAlive();
            Transport.Dispose();
        }
    }
}
