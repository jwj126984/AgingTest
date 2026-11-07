using demo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AgingTest.Models
{
    /// <summary>
    /// 【CAN 通讯管理】共享已打开的 NetCAN-800H 设备/通道句柄（最多6卡×8通道），
    /// 提供发送(CAN/CANFD)、清空缓冲、重置CAN、读接收数、在线状态，并广播接收帧与连接状态。
    /// 设备由主界面 ReadFullConfig 打开后 Register 到本类；硬件控制“CAN-800H 调试”页订阅接收帧。
    /// </summary>
    public static class CanComm
    {
        public const int CardCount = 6;   // CAN卡数（CanIndex 1~6）
        public const int ChannelCount = 8; // 每卡通道数 ch0~ch7

        /// <summary>单台 CAN 卡运行时句柄。</summary>
        public class CardRuntime
        {
            public int CanIndex { get; set; }
            public IntPtr DeviceHandle { get; set; } = IntPtr.Zero;
            public IntPtr[] ChannelHandles { get; } = new IntPtr[ChannelCount];
            public bool[] ChannelStarted { get; } = new bool[ChannelCount];
            public bool IsOnline => DeviceHandle != IntPtr.Zero;
        }

        /// <summary>已打开的 CAN 卡运行时集合。</summary>
        public static readonly List<CardRuntime> Cards = new();

        /// <summary>接收到的 CAN/CANFD 帧信息。</summary>
        public class CanFrameInfo
        {
            public int CanIndex { get; set; }
            public int Channel { get; set; }
            public uint Id { get; set; }          // 纯净ID（去除标志位）
            public bool IsExt { get; set; }       // 扩展帧
            public bool IsRemote { get; set; }    // 远程帧
            public bool IsFd { get; set; }        // CANFD
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public DateTime Time { get; set; }

            /// <summary>ID 十六进制显示。</summary>
            public string IdText => Id.ToString("X8");
            /// <summary>帧类型显示。</summary>
            public string Kind => IsRemote ? "RTR" : (IsFd ? "CANFD" : "CAN");
            /// <summary>数据长度。</summary>
            public int Len => Data.Length;
            /// <summary>数据十六进制显示。</summary>
            public string DataText => string.Join(" ", Data.Select(b => b.ToString("X2")));
        }

        /// <summary>接收帧广播（来自主界面接收线程）。</summary>
        public static event Action<CanFrameInfo>? FrameReceived;
        /// <summary>CAN 连接/通道状态变化。</summary>
        public static event Action? StateChanged;

        public static void RaiseStateChanged() => StateChanged?.Invoke();

        /// <summary>打开成功后将一张卡注册进共享管理（重复则替换）。</summary>
        public static void Register(int canIndex, IntPtr deviceHandle, IntPtr[] channelHandles, bool[] channelStarted)
        {
            var card = Cards.FirstOrDefault(c => c.CanIndex == canIndex);
            if (card == null)
            {
                card = new CardRuntime { CanIndex = canIndex };
                Cards.Add(card);
            }
            card.DeviceHandle = deviceHandle;
            for (int i = 0; i < ChannelCount; i++)
            {
                card.ChannelHandles[i] = channelHandles != null && i < channelHandles.Length ? channelHandles[i] : IntPtr.Zero;
                card.ChannelStarted[i] = channelStarted != null && i < channelStarted.Length && channelStarted[i];
            }
            RaiseStateChanged();
        }

        /// <summary>清空全部注册（主界面关闭时调用）。</summary>
        public static void Clear()
        {
            foreach (var c in Cards)
            {
                if (c.DeviceHandle != IntPtr.Zero)
                {
                    try { ZlgCan.ZCAN_CloseDevice(c.DeviceHandle); } catch { }
                }
            }
            Cards.Clear();
            RaiseStateChanged();
        }

        // ================================================================
        // 单卡连接 / 断开（按《800H常用说明》流程：打开→网络参数→InitCAN→StartCAN→动态配置波特率）
        // ================================================================

        /// <summary>
        /// 打开并初始化单张 CAN-800H 卡（8通道），按手册完成波特率动态配置后注册进共享管理。
        /// 已连接则直接返回成功（避免重复打开）。
        /// </summary>
        public static (bool ok, string err) OpenCard(int canIndex, CanCardItem card, Action<string>? log = null)
        {
            if (IsOnline(canIndex)) return (true, "已连接");
            try
            {
                uint type = ZlgCan.ZCAN_CANFDNET_800H;   // 57 = CANFDNET-800U/800H TCP 工作方式（手册附录1）
                uint devIndex = (uint)(canIndex - 1);
                IntPtr dev = ZlgCan.OpenDevice(type, devIndex);
                if (dev == IntPtr.Zero) return (false, $"打开设备失败 type={type}, devIndex={devIndex}");
                log?.Invoke($"[CAN{canIndex}] 打开设备成功 handle=0x{dev.ToInt64():X}");

                // 网络参数（设备级，一次性下发，须在 InitCAN 前完成）
                if (!SetNetParams(dev, card, log))
                {
                    try { ZlgCan.ZCAN_CloseDevice(dev); } catch { }
                    return (false, "网络参数设置失败");
                }

                byte canType = (byte)(card.IsCanFd ? 1 : 0);   // 0=CAN，1=CANFD（手册：can_type 由协议决定）
                byte chMode = card.ListenOnly ? (byte)1 : (byte)0;
                var chHandles = new IntPtr[ChannelCount];
                var chStarted = new bool[ChannelCount];

                for (int ch = 0; ch < ChannelCount; ch++)
                {
                    try
                    {
                        var cfg = new ZlgCan.ZCAN_CHANNEL_INIT_CONFIG
                        {
                            can_type = canType,
                            acc_code = 0,
                            acc_mask = 0xFFFFFFFF,
                            reserved = 0,
                            filter = 0,
                            timing0 = 0x00,
                            timing1 = 0x1C,
                            mode = chMode
                        };
                        IntPtr h = ZlgCan.ZCAN_InitCAN(dev, (uint)ch, ref cfg);
                        if (h == IntPtr.Zero) { log?.Invoke($"[CAN{canIndex}] CH{ch} 初始化失败，跳过"); continue; }
                        chHandles[ch] = h;

                        uint ret = ZlgCan.ZCAN_StartCAN(h);
                        if (ret != ZlgCan.STATUS_OK) { log?.Invoke($"[CAN{canIndex}] CH{ch} 启动失败 ret={ret}，跳过"); continue; }
                        chStarted[ch] = true;

                        ApplyChannelBaud(dev, ch, card, log);   // 波特率动态配置必须在 StartCAN 之后
                        log?.Invoke($"[CAN{canIndex}] CH{ch} 启动成功 {(card.IsCanFd ? "CANFD" : "CAN")}");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[CAN{canIndex}] CH{ch} 通道异常：{ex.Message}");
                    }
                }

                // 替换或新增运行时记录
                Cards.RemoveAll(c => c.CanIndex == canIndex);
                var rt = new CardRuntime { CanIndex = canIndex, DeviceHandle = dev };
                Array.Copy(chHandles, rt.ChannelHandles, ChannelCount);
                Array.Copy(chStarted, rt.ChannelStarted, ChannelCount);
                Cards.Add(rt);
                RaiseStateChanged();
                int started = 0;
                foreach (var b in chStarted) if (b) started++;
                return (true, $"已连接（成功启动 {started}/8 通道）");
            }
            catch (Exception ex)
            {
                return (false, "连接异常：" + ex.Message);
            }
        }

        /// <summary>断开并关闭单张 CAN 卡（关闭设备句柄、移除注册）。</summary>
        public static bool CloseCard(int canIndex)
        {
            var rt = GetCard(canIndex);
            if (rt == null) return false;
            if (rt.DeviceHandle != IntPtr.Zero)
            {
                try { ZlgCan.ZCAN_CloseDevice(rt.DeviceHandle); } catch { }
            }
            Cards.Remove(rt);
            RaiseStateChanged();
            return true;
        }

        /// <summary>下发设备网络参数（work_mode / work_port / ip / local_port），一次性。</summary>
        private static bool SetNetParams(IntPtr dev, CanCardItem card, Action<string>? log)
        {
            try
            {
                bool ok = ZlgCan.ZCAN_SetValue(dev, "0/work_mode", card.WorkMode == 1 ? "1" : "0") == ZlgCan.STATUS_OK;
                if (ok) ok = ZlgCan.ZCAN_SetValue(dev, "0/work_port", card.RemotePort.ToString()) == ZlgCan.STATUS_OK;
                if (ok && !string.IsNullOrWhiteSpace(card.IpAddress))
                    ok = ZlgCan.ZCAN_SetValue(dev, "0/ip", card.IpAddress) == ZlgCan.STATUS_OK;
                if (ok) ok = ZlgCan.ZCAN_SetValue(dev, "0/local_port", card.LocalPort.ToString()) == ZlgCan.STATUS_OK;
                if (ok) log?.Invoke($"[CAN{card.CanIndex}] 网络配置下发成功 模式={card.WorkMode} IP={card.IpAddress} 工作端口={card.RemotePort} 本地端口={card.LocalPort}");
                return ok;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按《800H常用说明》动态配置接口下发该通道波特率（须在 StartCAN 之后）。
        /// 仅下发用户已配置的值（>0），未配置则保持设备默认。
        /// </summary>
        private static void ApplyChannelBaud(IntPtr dev, int ch, CanCardItem card, Action<string>? log)
        {
            try
            {
                if (card.BaudArbitration > 0)
                    ZlgCan.SetDynamicData(dev, ZlgCan.ChannelKey(ZlgCan.DYNAMIC_CONFIG_CAN_NOMINALBAUD, ch), card.BaudArbitration.ToString());
                if (card.IsCanFd && card.BaudData > 0)
                    ZlgCan.SetDynamicData(dev, ZlgCan.ChannelKey(ZlgCan.DYNAMIC_CONFIG_CAN_DATABAUD, ch), card.BaudData.ToString());
                ZlgCan.SetDynamicData(dev, ZlgCan.ChannelKey(ZlgCan.DYNAMIC_CONFIG_CAN_USERES, ch), card.TermResistorOn ? "1" : "0");
                ZlgCan.SetDynamicData(dev, ZlgCan.ChannelKey(ZlgCan.DYNAMIC_CONFIG_CAN_MODE, ch), card.ListenOnly ? "1" : "0");
                ZlgCan.SetDynamicData(dev, ZlgCan.ChannelKey(ZlgCan.DYNAMIC_CONFIG_CAN_ENABLE, ch), "1");
                ZlgCan.ApplyDynamicData(dev, false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[CAN{card.CanIndex}] CH{ch} 波特率动态配置异常：{ex.Message}");
            }
        }

        public static CardRuntime? GetCard(int canIndex) => Cards.FirstOrDefault(c => c.CanIndex == canIndex);

        public static bool IsOnline(int canIndex) => GetCard(canIndex)?.IsOnline == true;

        public static bool IsChannelStarted(int canIndex, int ch)
            => GetCard(canIndex) is { } c && ch >= 0 && ch < ChannelCount && c.ChannelStarted[ch];

        public static IntPtr GetChannelHandle(int canIndex, int ch)
        {
            var c = GetCard(canIndex);
            if (c == null || ch < 0 || ch >= ChannelCount) return IntPtr.Zero;
            return c.ChannelHandles[ch];
        }

        // ================================================================
        // 发送
        // ================================================================
        /// <summary>发送 CAN/CANFD 帧。id 请传纯净ID；isExt 扩展帧、isRemote 远程帧、isFd CANFD。</summary>
        public static bool Send(int canIndex, int ch, uint id, byte[] data, bool isExt, bool isRemote, bool isFd)
        {
            IntPtr h = GetChannelHandle(canIndex, ch);
            if (h == IntPtr.Zero) return false;
            if (isRemote) data = Array.Empty<byte>();

            try
            {
                if (isFd)
                {
                    var txf = new ZlgCan.ZCAN_TransmitFD_Data
                    {
                        frame = new ZlgCan.canfd_frame
                        {
                            can_id = id | (isExt ? 0x80000000u : 0u),
                            len = (byte)data.Length,
                            flags = 0,
                            data = new byte[64]
                        },
                        transmit_type = 0
                    };
                    Array.Copy(data, txf.frame.data, Math.Min(data.Length, 64));
                    return TransmitFD(h, ref txf);
                }
                else
                {
                    var tx = new ZlgCan.ZCAN_Transmit_Data
                    {
                        frame = new ZlgCan.can_frame
                        {
                            can_id = id | (isExt ? 0x80000000u : 0u) | (isRemote ? 0x40000000u : 0u),
                            can_dlc = (byte)data.Length,
                            data = new byte[8]
                        },
                        transmit_type = 0
                    };
                    Array.Copy(data, tx.frame.data, Math.Min(data.Length, 8));
                    return Transmit(h, ref tx);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool Transmit(IntPtr ch, ref ZlgCan.ZCAN_Transmit_Data tx)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ZlgCan.ZCAN_Transmit_Data)));
            try
            {
                Marshal.StructureToPtr(tx, p, false);
                return ZlgCan.ZCAN_Transmit(ch, p, 1) != 0;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static bool TransmitFD(IntPtr ch, ref ZlgCan.ZCAN_TransmitFD_Data txf)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ZlgCan.ZCAN_TransmitFD_Data)));
            try
            {
                Marshal.StructureToPtr(txf, p, false);
                return ZlgCan.ZCAN_TransmitFD(ch, p, 1) != 0;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ================================================================
        // 通道工具
        // ================================================================
        /// <summary>清空收发缓冲。</summary>
        public static bool ClearBuffer(int canIndex, int ch)
        {
            IntPtr h = GetChannelHandle(canIndex, ch);
            if (h == IntPtr.Zero) return false;
            try { return ZlgCan.ZCAN_ClearBuffer(h) == ZlgCan.STATUS_OK; } catch { return false; }
        }

        /// <summary>重置 CAN 通道。</summary>
        public static bool ResetCan(int canIndex, int ch)
        {
            IntPtr h = GetChannelHandle(canIndex, ch);
            if (h == IntPtr.Zero) return false;
            try { return ZlgCan.ZCAN_ResetCAN(h) == ZlgCan.STATUS_OK; } catch { return false; }
        }

        /// <summary>读取通道待收帧数（0=CAN，1=CANFD）。</summary>
        public static uint GetReceiveNum(int canIndex, int ch, byte type = 0)
        {
            IntPtr h = GetChannelHandle(canIndex, ch);
            if (h == IntPtr.Zero) return 0;
            try { return ZlgCan.ZCAN_GetReceiveNum(h, type); } catch { return 0; }
        }

        // ================================================================
        // 接收帧广播（由主界面接收线程调用）
        // ================================================================
        public static void RaiseFrameReceived(int canIndex, int ch, uint rawId, byte[] data, int len, bool isFd)
        {
            bool remote = (rawId & 0x40000000) != 0;
            bool ext = (rawId & 0x80000000) != 0;
            var info = new CanFrameInfo
            {
                CanIndex = canIndex,
                Channel = ch,
                Id = rawId & 0x1FFFFFFF,
                IsExt = ext,
                IsRemote = remote,
                IsFd = isFd,
                Data = data.Take(Math.Max(0, len)).ToArray(),
                Time = DateTime.Now
            };
            FrameReceived?.Invoke(info);
        }

        /// <summary>便捷：C# 可空引用提示辅助。</summary>
        public static string FormatData(CanFrameInfo f)
            => string.Join(" ", f.Data.Select(b => b.ToString("X2")));
    }
}
