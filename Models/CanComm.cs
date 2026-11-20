using demo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AgingTest.Models
{
    /// <summary>
    /// 【CAN 通讯管理】共享已打开的 CANFDNET-800U 设备/通道句柄（最多 6 台 × 8 通道），
    /// 提供发送(CAN/CANFD)、清空缓冲、复位通道、读接收数、在线状态，并广播接收帧与连接状态。
    ///
    /// 连接模型（依据《800H常用说明》3.3.7 以太网系列 2 与 ZCANPRO「设备管理/启动」界面）：
    ///   1) 一台设备只打开一次：ZCAN_OpenDevice(类型号, 设备索引, 0)
    ///      类型号：CANFDNET-800U-TCP = 57，CANFDNET-800U-UDP = 58（手册附录1）。
    ///   2) 8 个通道各自是独立网络连接，用 "n/属性" 逐通道下发 work_mode / ip / work_port / local_port，
    ///      其中 n 为通道号 0~7；这些参数必须在 ZCAN_StartCAN 之前设置。
    ///   3) 每通道 ZCAN_InitCAN(设备句柄, 通道号, 配置) 取通道句柄，再 ZCAN_StartCAN 启动。
    ///   4) 波特率/终端电阻/控制器类型属于设备端配置（ZCANPRO 网络设备配置工具，密码 88888），
    ///      不在本接口库可设置范围内，故启动流程中不下发波特率。
    /// </summary>
    public static class CanComm
    {
        public const int CardCount = 6;    // CAN 卡（设备）数，CanIndex 1~6
        public const int ChannelCount = 8; // 每台设备通道数 ch0~ch7

        /// <summary>单台 CAN 设备的运行时句柄。</summary>
        public class CardRuntime
        {
            public int CanIndex { get; set; }
            public uint DeviceTypeCode { get; set; } = ZlgCan.ZCAN_CANFDNET_800U_TCP;
            public int DeviceIndex { get; set; }
            public IntPtr DeviceHandle { get; set; } = IntPtr.Zero;
            public IntPtr[] ChannelHandles { get; } = new IntPtr[ChannelCount];
            public bool[] ChannelStarted { get; } = new bool[ChannelCount];
            public bool[] ChannelCanFd { get; } = new bool[ChannelCount];
            /// <summary>本次连接实际使用的通道参数（IP/端口/协议），供界面展示与重连复用。</summary>
            public CanChannelItem?[] ChannelConfigs { get; } = new CanChannelItem?[ChannelCount];

            public bool IsOnline => DeviceHandle != IntPtr.Zero;

            /// <summary>已启动通道数。</summary>
            public int StartedCount => ChannelStarted.Count(b => b);

            /// <summary>设备类型显示名。</summary>
            public string DeviceTypeName => DeviceTypeCode == ZlgCan.ZCAN_CANFDNET_800U_UDP
                ? "CANFDNET-800U-UDP" : "CANFDNET-800U-TCP";
        }

        /// <summary>已打开的 CAN 设备运行时集合。</summary>
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

            public string IdText => Id.ToString("X8");
            public string Kind => IsRemote ? "RTR" : (IsFd ? "CANFD" : "CAN");
            public int Len => Data.Length;
            public string DataText => string.Join(" ", Data.Select(b => b.ToString("X2")));
        }

        /// <summary>接收帧广播（来自主界面接收线程）。</summary>
        public static event Action<CanFrameInfo>? FrameReceived;
        /// <summary>CAN 设备/通道状态变化。</summary>
        public static event Action? StateChanged;

        public static void RaiseStateChanged() => StateChanged?.Invoke();

        // ================================================================
        // 设备管理（对应 ZCANPRO「设备管理」：类型 + 索引 + 打开设备）
        // ================================================================

        /// <summary>
        /// 打开一台 CANFDNET-800U 设备（仅打开设备句柄，不启动通道）。
        /// 已打开则直接返回成功。
        /// </summary>
        public static (bool ok, string err) OpenDevice(int canIndex, uint deviceTypeCode, int deviceIndex, Action<string>? log = null)
        {
            var exist = GetCard(canIndex);
            if (exist != null && exist.IsOnline) return (true, "设备已打开");

            string? sdkErr = ZlgCan.CheckSdk();
            if (sdkErr != null) return (false, sdkErr);

            try
            {
                IntPtr dev = ZlgCan.OpenDevice(deviceTypeCode, (uint)deviceIndex);
                if (dev == IntPtr.Zero)
                {
                    return (false, $"打开设备失败：类型号 {deviceTypeCode}，索引 {deviceIndex}。" +
                                   "请确认设备类型与设备实际工作模式一致（TCP/UDP），并检查 IP、网线、驱动。");
                }

                var rt = exist ?? new CardRuntime { CanIndex = canIndex };
                rt.DeviceTypeCode = deviceTypeCode;
                rt.DeviceIndex = deviceIndex;
                rt.DeviceHandle = dev;
                if (exist == null) Cards.Add(rt);

                log?.Invoke($"[CAN{canIndex}] 打开设备成功 {rt.DeviceTypeName} 索引={deviceIndex} handle=0x{dev.ToInt64():X}");
                RaiseStateChanged();
                return (true, "设备已打开");
            }
            catch (Exception ex)
            {
                return (false, "打开设备异常：" + ex.Message);
            }
        }

        /// <summary>关闭设备（同时关闭其全部通道）。</summary>
        public static bool CloseDevice(int canIndex)
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

        /// <summary>读取设备信息文本（版本、通道数、序列号、硬件类型）。</summary>
        public static string DescribeDevice(int canIndex)
        {
            var rt = GetCard(canIndex);
            if (rt == null || !rt.IsOnline) return "设备未打开";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"设备类型：{rt.DeviceTypeName}（类型号 {rt.DeviceTypeCode}）");
            sb.AppendLine($"设备索引：{rt.DeviceIndex}");
            sb.AppendLine($"设备句柄：0x{rt.DeviceHandle.ToInt64():X}");
            sb.AppendLine($"已启动通道：{rt.StartedCount}/{ChannelCount}");
            try
            {
                var info = new ZlgCan.ZCAN_DEVICE_INFO
                {
                    str_Serial_Num = new byte[20],
                    str_hw_Type = new byte[40],
                    reserved = new byte[8]
                };
                uint ret = ZlgCan.ZCAN_GetDeviceInf(rt.DeviceHandle, ref info);
                if (ret == ZlgCan.STATUS_OK)
                {
                    sb.AppendLine($"硬件版本：V{info.hw_Version:X4}");
                    sb.AppendLine($"固件版本：V{info.fw_Version:X4}");
                    sb.AppendLine($"驱动版本：V{info.dr_Version:X4}");
                    sb.AppendLine($"接口库版本：V{info.in_Version:X4}");
                    sb.AppendLine($"通道数：{info.can_Num}");
                    sb.AppendLine($"序列号：{Ascii(info.str_Serial_Num)}");
                    sb.AppendLine($"硬件类型：{Ascii(info.str_hw_Type)}");
                }
                else
                {
                    sb.AppendLine($"ZCAN_GetDeviceInf 返回：{ZlgCan.ErrorText(ret)}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"读取设备信息异常：{ex.Message}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string Ascii(byte[]? raw)
        {
            if (raw == null || raw.Length == 0) return "";
            var s = System.Text.Encoding.ASCII.GetString(raw);
            int end = s.IndexOf('\0');
            return (end >= 0 ? s.Substring(0, end) : s).Trim();
        }

        // ================================================================
        // 通道启动 / 停止（对应 ZCANPRO 每通道的「启动」「停止」按钮）
        // ================================================================

        /// <summary>
        /// 启动单个通道：下发该通道网络参数 → InitCAN → StartCAN。
        /// 参数下发必须在 StartCAN 之前（手册 3.3.7 各属性"注意点"）。
        /// </summary>
        public static (bool ok, string err) StartChannel(int canIndex, int ch, CanChannelItem cfg, Action<string>? log = null)
        {
            var rt = GetCard(canIndex);
            if (rt == null || rt.DeviceHandle == IntPtr.Zero)
                return (false, "设备未打开，请先在「设备管理」中打开设备");
            if (ch < 0 || ch >= ChannelCount)
                return (false, $"通道号越界：{ch}");

            try
            {
                IntPtr dev = rt.DeviceHandle;

                // 1) 网络参数（按通道号下发）
                bool udp = rt.DeviceTypeCode == ZlgCan.ZCAN_CANFDNET_800U_UDP;
                if (!ApplyChannelParams(dev, ch, cfg, udp, log))
                    return (false, $"通道{ch} 网络参数设置失败（工作模式/IP/端口）");

                // 2) 通道句柄：已初始化过则复用，否则 InitCAN
                IntPtr h = rt.ChannelHandles[ch];
                if (h == IntPtr.Zero)
                {
                    if (cfg.IsCanFd)
                    {
                        var cfgFd = new ZlgCan.ZCAN_CHANNEL_INIT_CONFIG_CANFD
                        {
                            can_type = ZlgCan.TYPE_CANFD,
                            acc_code = 0,
                            acc_mask = 0xFFFFFFFF,
                            abi_timing = 0,
                            dbit_timing = 0,
                            brp = 0,
                            filter = 0,
                            mode = (byte)(cfg.ListenOnly ? 1 : 0),
                            pad = 0,
                            reserved = 0
                        };
                        h = ZlgCan.ZCAN_InitCAN_Fd(dev, (uint)ch, ref cfgFd);
                    }
                    else
                    {
                        var cfgCan = new ZlgCan.ZCAN_CHANNEL_INIT_CONFIG_CAN
                        {
                            can_type = ZlgCan.TYPE_CAN,
                            acc_code = 0,
                            acc_mask = 0xFFFFFFFF,
                            reserved = 0,
                            filter = 0,
                            timing0 = 0,
                            timing1 = 0,
                            mode = (byte)(cfg.ListenOnly ? 1 : 0)
                        };
                        h = ZlgCan.ZCAN_InitCAN(dev, (uint)ch, ref cfgCan);
                    }
                    if (h == IntPtr.Zero)
                        return (false, $"通道{ch} 初始化失败（ZCAN_InitCAN 返回空句柄）");
                    rt.ChannelHandles[ch] = h;
                }

                // 3) 启动通道
                uint ret = ZlgCan.ZCAN_StartCAN(h);
                if (ret != ZlgCan.STATUS_OK)
                    return (false, $"通道{ch} 启动失败：{ZlgCan.ErrorText(ret)}");

                rt.ChannelStarted[ch] = true;
                rt.ChannelCanFd[ch] = cfg.IsCanFd;
                rt.ChannelConfigs[ch] = cfg;
                log?.Invoke($"[CAN{canIndex}] 通道{ch} 启动成功 {(cfg.IsCanFd ? "CANFD" : "CAN")} " +
                           $"模式={(cfg.WorkMode == 1 ? "服务器" : "客户端")} {cfg.EndpointText}");
                RaiseStateChanged();
                return (true, $"通道{ch} 已启动");
            }
            catch (Exception ex)
            {
                return (false, $"通道{ch} 启动异常：{ex.Message}");
            }
        }

        /// <summary>停止单个通道（ZCAN_ResetCAN 复位，可用 StartChannel 恢复）。</summary>
        public static (bool ok, string err) StopChannel(int canIndex, int ch, Action<string>? log = null)
        {
            var rt = GetCard(canIndex);
            if (rt == null) return (false, "设备未打开");
            IntPtr h = rt.ChannelHandles[ch];
            if (h == IntPtr.Zero || !rt.ChannelStarted[ch]) return (false, $"通道{ch} 未启动");

            try
            {
                uint ret = ZlgCan.ZCAN_ResetCAN(h);
                if (ret != ZlgCan.STATUS_OK)
                    return (false, $"通道{ch} 停止失败：{ZlgCan.ErrorText(ret)}");
                rt.ChannelStarted[ch] = false;
                log?.Invoke($"[CAN{canIndex}] 通道{ch} 已停止");
                RaiseStateChanged();
                return (true, $"通道{ch} 已停止");
            }
            catch (Exception ex)
            {
                return (false, $"通道{ch} 停止异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 按通道下发网络参数，路径为 "n/work_mode"、"n/ip"、"n/work_port"、"n/local_port"（n=通道号）。
        /// 客户端模式：需要 ip + work_port（连设备的该通道端口）；
        /// 服务器模式：需要 local_port（PC 本机监听）；
        /// UDP：同时需要 local_port。
        /// </summary>
        private static bool ApplyChannelParams(IntPtr dev, int ch, CanChannelItem cfg, bool udp, Action<string>? log)
        {
            // 工作模式：0 客户端，1 服务器
            if (!ZlgCan.SetValue(dev, $"{ch}/work_mode", cfg.WorkMode == 1 ? "1" : "0"))
            {
                log?.Invoke($"[通道{ch}] 设置 work_mode 失败");
                return false;
            }

            if (cfg.WorkMode == 0)
            {
                if (string.IsNullOrWhiteSpace(cfg.IpAddress))
                {
                    log?.Invoke($"[通道{ch}] 客户端模式缺少目标 IP");
                    return false;
                }
                if (!ZlgCan.SetValue(dev, $"{ch}/ip", cfg.IpAddress.Trim()))
                {
                    log?.Invoke($"[通道{ch}] 设置 ip 失败");
                    return false;
                }
                if (!ZlgCan.SetValue(dev, $"{ch}/work_port", cfg.WorkPort.ToString()))
                {
                    log?.Invoke($"[通道{ch}] 设置 work_port 失败");
                    return false;
                }
            }

            if (cfg.WorkMode == 1 || udp)
            {
                if (!ZlgCan.SetValue(dev, $"{ch}/local_port", cfg.LocalPort.ToString()))
                {
                    log?.Invoke($"[通道{ch}] 设置 local_port 失败");
                    return false;
                }
            }

            return true;
        }

        // ================================================================
        // 整卡连接（主界面启动时批量调用）
        // ================================================================

        /// <summary>
        /// 打开整卡：先打开设备，再按配置逐个启动通道（跳过未启用通道）。
        /// 通道部分失败不影响其它通道，最终以"成功启动数"作为结果。
        /// </summary>
        public static (bool ok, string err) OpenCard(int canIndex, CanCardItem card, Action<string>? log = null)
        {
            if (card == null) return (false, "配置为空");

            var channels = card.EnsureChannels();
            var (devOk, devErr) = OpenDevice(canIndex, card.DeviceTypeCode, card.ResolvedDeviceIndex, log);
            if (!devOk) return (false, devErr);

            int started = 0;
            int failed = 0;
            foreach (var chCfg in channels)
            {
                if (!chCfg.Enabled) continue;
                var (ok, err) = StartChannel(canIndex, chCfg.ChannelIndex, chCfg, log);
                if (ok) started++;
                else
                {
                    failed++;
                    log?.Invoke($"[CAN{canIndex}] {err}");
                }
            }

            if (started == 0)
            {
                // 一个通道都没起来，释放设备句柄，避免后续重复打开报「设备已经打开」
                CloseDevice(canIndex);
                return (false, $"设备已打开但 0 个通道启动成功（失败 {failed} 个），已释放设备句柄");
            }

            return (true, $"设备已打开，成功启动 {started}/8 通道" + (failed > 0 ? $"，{failed} 个失败" : ""));
        }

        /// <summary>
        /// 【界面唯一入口】按 IP + 起始端口连接整台设备：打开设备 → 逐通道下发参数 → InitCAN → StartCAN。
        /// 通道 n 使用端口 basePort + n（n = 0~7）；工作模式固定为客户端（PC 连设备）；
        /// 协议 / 加速由调用方统一传入，其余细节全部封装在本方法内。
        /// </summary>
        public static (bool ok, string msg) ConnectCard(int canIndex, string ip, int basePort,
            uint deviceTypeCode = ZlgCan.ZCAN_CANFDNET_800U_TCP, bool isCanFd = false, int canFdAcc = 0,
            Action<string>? log = null)
        {
            if (canIndex < 1 || canIndex > CardCount) return (false, $"CAN 序号越界：{canIndex}");
            if (string.IsNullOrWhiteSpace(ip) || !System.Net.IPAddress.TryParse(ip.Trim(), out _))
                return (false, $"IP 地址不正确：{ip}");
            if (basePort < 1 || basePort + ChannelCount - 1 > 65535)
                return (false, $"起始端口需在 1~{65535 - ChannelCount + 1} 之间：{basePort}");

            string ipTrim = ip.Trim();

            // 已连接时先释放，避免「设备已经打开」
            if (GetCard(canIndex)?.IsOnline == true) CloseDevice(canIndex);

            var (devOk, devErr) = OpenDevice(canIndex, deviceTypeCode, canIndex - 1, log);
            if (!devOk) return (false, devErr);

            int started = 0;
            var errors = new List<string>();
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                var cfg = new CanChannelItem
                {
                    ChannelIndex = ch,
                    Enabled = true,
                    IpAddress = ipTrim,
                    WorkMode = 0,                 // 客户端：PC 主动连设备
                    LocalPort = basePort + ch,    // 服务器模式才生效，保持一致便于切换
                    WorkPort = basePort + ch,     // 客户端模式实际生效
                    IsCanFd = isCanFd,
                    CanFdAcc = canFdAcc
                };
                var (ok, err) = StartChannel(canIndex, ch, cfg, log);
                if (ok) started++;
                else
                {
                    errors.Add(err);
                    log?.Invoke($"[CAN{canIndex}] {err}");
                }
            }

            if (started == 0)
            {
                CloseDevice(canIndex);
                return (false, $"连接失败：8 个通道均未启动。{string.Join("；", errors.Take(3))}");
            }

            string summary = $"CAN{canIndex} 已连接：{ipTrim}，端口 {basePort}~{basePort + ChannelCount - 1}，" +
                             $"启动 {started}/{ChannelCount} 通道";
            if (errors.Count > 0) summary += $"（{errors.Count} 个通道失败）";
            return (true, summary);
        }

        /// <summary>断开并关闭单张 CAN 设备（关闭设备句柄会一并关闭全部通道）。</summary>
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
        /// <summary>关闭全部设备（主窗口关闭时调用）。</summary>
        public static void Clear()
        {
            foreach (var c in Cards.ToList())
            {
                if (c.DeviceHandle != IntPtr.Zero)
                {
                    try { ZlgCan.ZCAN_CloseDevice(c.DeviceHandle); } catch { }
                }
            }
            Cards.Clear();
            RaiseStateChanged();
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
        /// <summary>
        /// 发送 CAN/CANFD 帧。id 传纯净 ID；isExt 扩展帧、isRemote 远程帧、isFd CANFD、brs CANFD 加速位。
        /// </summary>
        public static bool Send(int canIndex, int ch, uint id, byte[] data, bool isExt, bool isRemote, bool isFd, bool brs = false)
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
                            len = (byte)Math.Min(data.Length, 64),
                            flags = (byte)(brs ? 0x01 : 0x00),   // 位0：CANFD 加速
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
                            can_dlc = (byte)Math.Min(data.Length, 8),
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

        /// <summary>复位 CAN 通道。</summary>
        public static bool ResetCan(int canIndex, int ch)
        {
            IntPtr h = GetChannelHandle(canIndex, ch);
            if (h == IntPtr.Zero) return false;
            try
            {
                bool ok = ZlgCan.ZCAN_ResetCAN(h) == ZlgCan.STATUS_OK;
                var rt = GetCard(canIndex);
                if (rt != null) rt.ChannelStarted[ch] = false;
                RaiseStateChanged();
                return ok;
            }
            catch { return false; }
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

        /// <summary>便捷：帧数据十六进制文本。</summary>
        public static string FormatData(CanFrameInfo f)
            => string.Join(" ", f.Data.Select(b => b.ToString("X2")));
    }
}
