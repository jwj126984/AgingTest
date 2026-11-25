using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AgingTest.Models
{
    /// <summary>
    /// ISO 15765-2 传输层（物理寻址、单通道）。报文格式依据《B2510-EHV-EOL测试用例》附录1：
    ///   请求 CANFD ID 0x775，响应 CANFD ID 0x77D；
    ///   单帧 PCI 0x0N（N 字节），CANFD 长单帧用 0x00 + 长度字节；
    ///   多帧：首帧 0x1N + 长度，连续帧 0x2N，流控 0x3N（示例中测试仪回 30 00 00）。
    /// </summary>
    public class IsoTpTransport : IDisposable
    {
        public int CanIndex { get; }
        public int Channel { get; }
        public uint RequestId { get; set; }
        public uint ResponseId { get; set; }
        /// <summary>true = CANFD（附录1 为 CANFD），false = 经典 CAN。</summary>
        public bool IsFd { get; set; }
        /// <summary>true = 29 位扩展帧。</summary>
        public bool IsExt { get; set; }

        /// <summary>等待 ECU 响应的时间（附录用例要求 100ms 级响应，留余量）。</summary>
        public int P2TimeoutMs { get; set; } = 1000;
        /// <summary>等待流控帧的时间。</summary>
        public int FlowControlTimeoutMs { get; set; } = 500;

        public event Action<string>? Log;

        private readonly object _reqLock = new();
        private readonly AutoResetEvent _rxDone = new(false);
        private readonly AutoResetEvent _fcDone = new(false);

        private volatile bool _waiting;        // 正在等待一次请求的响应
        private bool _rxStarted;               // 已收到首帧
        private int _rxExpected;               // 期望总字节数
        private int _rxReceived;               // 已收字节数
        private byte[]? _rxAssembled;
        private int _rxNextSeq;

        private byte _fcStatus;
        private byte _fcBlockSize;
        private byte _fcStMin;

        private volatile bool _disposed;

        public IsoTpTransport(int canIndex, int channel, uint requestId = 0x775, uint responseId = 0x77D,
            bool isFd = true, bool isExt = false)
        {
            CanIndex = canIndex;
            Channel = channel;
            RequestId = requestId;
            ResponseId = responseId;
            IsFd = isFd;
            IsExt = isExt;
            CanComm.FrameReceived += OnFrameReceived;
        }

        // ================================================================
        // 请求 / 响应
        // ================================================================
        /// <summary>发送一帧 UDS PDU 并等待完整响应（自动处理 0x78 响应挂起）。</summary>
        public byte[] Request(byte[] pdu, int? timeoutMs = null)
        {
            if (pdu == null || pdu.Length == 0) throw new ArgumentException("PDU 不能为空");
            lock (_reqLock)
            {
                int timeout = timeoutMs ?? P2TimeoutMs;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    PrepareReceive();
                    SendPayload(pdu);

                    if (!_rxDone.WaitOne(attempt == 0 ? timeout : Math.Max(timeout, 5000)))
                        throw new TimeoutException($"等待 UDS 响应超时（{timeout} ms，响应 ID 0x{ResponseId:X}）");

                    var resp = _rxAssembled;
                    if (resp == null || resp.Length == 0) throw new IOException("UDS 响应为空");

                    // 7F SID 78 = responsePending，继续等待最终响应
                    if (resp.Length >= 3 && resp[0] == 0x7F && resp[2] == 0x78)
                    {
                        Log?.Invoke("ECU 返回 0x78（responsePending），继续等待最终响应");
                        continue;
                    }
                    return resp;
                }
                throw new TimeoutException("ECU 持续返回 0x78（responsePending），未给出最终响应");
            }
        }

        private void PrepareReceive()
        {
            _rxAssembled = null;
            _rxStarted = false;
            _rxExpected = 0;
            _rxReceived = 0;
            _rxNextSeq = 1;
            _rxDone.Reset();
            _fcDone.Reset();
            _waiting = true;
        }

        /// <summary>只发送、不等响应（3E 80 这类抑制正响应的报文用）。</summary>
        public void SendOnly(byte[] pdu)
        {
            if (pdu == null || pdu.Length == 0) throw new ArgumentException("PDU 不能为空");
            lock (_reqLock)
            {
                _waiting = false;
                SendPayload(pdu);
            }
        }

        // ================================================================
        // 发送
        // ================================================================
        private void SendPayload(byte[] pdu)
        {
            int len = pdu.Length;

            // 经典 CAN 单帧：≤7 字节；CANFD 长单帧：8~62 字节用 0x00 + 长度（附录 1 的实际写法）
            if (len <= 7)
            {
                byte[] sf = new byte[len + 1];
                sf[0] = (byte)len;
                Array.Copy(pdu, 0, sf, 1, len);
                SendFrame(sf, "SF");
                return;
            }
            if (IsFd && len <= 62)
            {
                byte[] sf = new byte[len + 2];
                sf[0] = 0x00;
                sf[1] = (byte)len;
                Array.Copy(pdu, 0, sf, 2, len);
                SendFrame(sf, "SF(FD)");
                return;
            }

            // 多帧：首帧携带 6 字节，随后连续帧每帧 8 字节
            const int firstCap = 6;
            byte[] ff = new byte[2 + firstCap];
            ff[0] = (byte)(0x10 | ((len >> 8) & 0x0F));
            ff[1] = (byte)(len & 0xFF);
            int copied = Math.Min(firstCap, len);
            Array.Copy(pdu, 0, ff, 2, copied);
            SendFrame(ff, "FF");

            int offset = copied;
            WaitFlowControl();

            int seq = 1;
            int blockSent = 0;
            while (offset < len)
            {
                byte[] cf = new byte[9];
                cf[0] = (byte)(0x20 | (seq & 0x0F));
                int n = Math.Min(8, len - offset);
                Array.Copy(pdu, offset, cf, 1, n);
                SendFrame(cf, "CF");
                offset += n;
                seq = (seq + 1) & 0x0F;
                blockSent++;

                // 流控块大小 >0 时，每发完 BS 帧等待一次新的流控帧
                if (_fcBlockSize > 0 && blockSent >= _fcBlockSize && offset < len)
                {
                    blockSent = 0;
                    _fcDone.Reset();
                    WaitFlowControl();
                }
                if (_fcStMin is > 0 and <= 0x7F) Thread.Sleep(_fcStMin);
            }
        }

        private void WaitFlowControl()
        {
            _fcDone.Reset();
            if (!_fcDone.WaitOne(FlowControlTimeoutMs))
                throw new TimeoutException("等待流控帧超时（未收到 0x3x）");
            if (_fcStatus == 0x02) throw new IOException("ECU 流控溢出（overflow）");
            if (_fcStatus is 0x01)   // wait：再等一次
                if (!_fcDone.WaitOne(FlowControlTimeoutMs))
                    throw new TimeoutException("ECU 流控 WAIT 后仍未继续");
        }

        private void SendFrame(byte[] frame, string tag)
        {
            byte[] data = frame;
            if (IsFd)
            {
                int target = NormalizeFdLength(frame.Length);
                if (target > frame.Length)
                {
                    data = new byte[target];
                    Array.Copy(frame, data, frame.Length);
                }
            }
            else if (frame.Length < 8)
            {
                data = new byte[8];
                Array.Copy(frame, data, frame.Length);
            }

            if (!CanComm.Send(CanIndex, Channel, RequestId, data, IsExt, false, IsFd))
                throw new IOException($"CAN 发送失败（CAN{CanIndex} 通道{Channel} 未就绪）");

            Log?.Invoke($"{tag} TX 0x{RequestId:X}: {Hex(data)}");
        }

        private void SendFlowControl(byte flowStatus = 0x00, byte blockSize = 0x00, byte stMin = 0x00)
        {
            byte[] fc = { (byte)(0x30 | flowStatus), blockSize, stMin };
            SendFrame(fc, "FC");
        }

        /// <summary>CANFD 只允许 0~8、12、16、20、24、32、48、64 字节的数据长度。</summary>
        private static int NormalizeFdLength(int n)
        {
            if (n <= 8) return 8;
            foreach (int v in new[] { 12, 16, 20, 24, 32, 48, 64 })
                if (n <= v) return v;
            return 64;
        }

        // ================================================================
        // 接收
        // ================================================================
        private void OnFrameReceived(CanComm.CanFrameInfo f)
        {
            if (_disposed || f.IsTx) return;
            if (f.CanIndex != CanIndex || f.Channel != Channel) return;
            if ((f.Id & 0x1FFFFFFF) != ResponseId) return;

            try
            {
                byte[] d = f.Data;
                if (d.Length == 0) return;
                int type = d[0] >> 4;

                // 流控帧（响应方向）
                if (type == 3)
                {
                    _fcStatus = (byte)(d[0] & 0x0F);
                    _fcBlockSize = d.Length > 1 ? d[1] : (byte)0;
                    _fcStMin = d.Length > 2 ? d[2] : (byte)0;
                    _fcDone.Set();
                    return;
                }

                if (!_waiting) return;

                switch (type)
                {
                    case 0: // 单帧
                    {
                        int len, off;
                        if ((d[0] & 0x0F) == 0x00)
                        {
                            if (d.Length < 2) return;
                            len = d[1];
                            off = 2;
                        }
                        else
                        {
                            len = d[0] & 0x0F;
                            off = 1;
                        }
                        if (len <= 0 || off + len > d.Length) return;

                        byte[] payload = new byte[len];
                        Array.Copy(d, off, payload, 0, len);
                        _rxAssembled = payload;
                        Log?.Invoke($"SF RX 0x{ResponseId:X}: {Hex(payload)}");
                        _rxDone.Set();
                        break;
                    }
                    case 1: // 首帧
                    {
                        if (d.Length < 2) return;
                        _rxExpected = ((d[0] & 0x0F) << 8) | d[1];
                        int take = Math.Min(_rxExpected, d.Length - 2);
                        if (_rxExpected <= 0) return;

                        _rxAssembled = new byte[_rxExpected];
                        Array.Copy(d, 2, _rxAssembled, 0, take);
                        _rxReceived = take;
                        _rxStarted = true;
                        _rxNextSeq = 1;

                        SendFlowControl(0x00, 0x00, 0x00);   // 30 00 00
                        if (_rxReceived >= _rxExpected) _rxDone.Set();
                        break;
                    }
                    case 2: // 连续帧
                    {
                        if (!_rxStarted || _rxAssembled == null) return;
                        if ((d[0] & 0x0F) != _rxNextSeq) return;    // 序号不连续，丢弃
                        _rxNextSeq = (_rxNextSeq + 1) & 0x0F;

                        int need = _rxExpected - _rxReceived;
                        int take = Math.Min(need, d.Length - 1);
                        if (take <= 0) return;
                        Array.Copy(d, 1, _rxAssembled, _rxReceived, take);
                        _rxReceived += take;

                        if (_rxReceived >= _rxExpected)
                        {
                            Log?.Invoke($"MF RX 0x{ResponseId:X}: {Hex(_rxAssembled)}");
                            _rxDone.Set();
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke("ISO-TP 接收异常：" + ex.Message);
            }
        }

        public static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));

        public void Dispose()
        {
            _disposed = true;
            CanComm.FrameReceived -= OnFrameReceived;
        }
    }
}
