using System;
using System.Runtime.InteropServices;
using System.Text;

namespace demo
{
    /// <summary>
    /// 广州致远(ZLG) zlgcan.dll 原生接口 P/Invoke 封装。
    /// 依据《800H常用说明.pdf》(接口函数使用手册 V1.24) 实现，
    /// 覆盖 CANFDNET-800U 打开 / 通道参数下发 / 初始化 / 启动 / 收发 所需的接口与数据结构。
    ///
    /// 关键流程（手册 2.1 开发流程图）：
    ///   ZCAN_OpenDevice → ZCAN_SetValue(通道网络参数) → ZCAN_InitCAN → ZCAN_StartCAN
    ///   → ZCAN_Transmit / ZCAN_TransmitFD → ZCAN_CloseDevice
    /// </summary>
    public static class ZlgCan
    {
        private const string DLL = @".\zlgcan.dll";

        // ------------------------------------------------------------------
        // 设备类型号（手册附录1 表4.1「设备类型号定义」）
        // ------------------------------------------------------------------
        /// <summary>CANFDNET-800U TCP 工作方式 = 57</summary>
        public const uint ZCAN_CANFDNET_800U_TCP = 57;
        /// <summary>CANFDNET-800U UDP 工作方式 = 58</summary>
        public const uint ZCAN_CANFDNET_800U_UDP = 58;
        /// <summary>兼容旧命名（800H 走 TCP 工作方式）</summary>
        public const uint ZCAN_CANFDNET_800H = ZCAN_CANFDNET_800U_TCP;

        /// <summary>通道类型：CAN</summary>
        public const uint TYPE_CAN = 0;
        /// <summary>通道类型：CANFD</summary>
        public const uint TYPE_CANFD = 1;

        /// <summary>接口调用成功返回值（STATUS_OK）</summary>
        public const uint STATUS_OK = 1;

        // ------------------------------------------------------------------
        // 错误码（手册附录3 表4.3）
        // ------------------------------------------------------------------
        public const uint ZCAN_ERROR_CAN_OVERFLOW = 0x0001;
        public const uint ZCAN_ERROR_CAN_ERRALARM = 0x0002;
        public const uint ZCAN_ERROR_CAN_PASSIVE = 0x0004;
        public const uint ZCAN_ERROR_CAN_LOSE = 0x0008;
        public const uint ZCAN_ERROR_CAN_BUSERR = 0x0010;
        public const uint ZCAN_ERROR_CAN_BUSOFF = 0x0020;
        public const uint ZCAN_ERROR_CAN_BUFFER_OVERFLOW = 0x0040;
        public const uint ZCAN_ERROR_DEVICEOPENED = 0x0100;
        public const uint ZCAN_ERROR_DEVICEOPEN = 0x0200;
        public const uint ZCAN_ERROR_DEVICENOTOPEN = 0x0400;
        public const uint ZCAN_ERROR_BUFFEROVERFLOW = 0x0800;
        public const uint ZCAN_ERROR_DEVICENOTEXIST = 0x1000;
        public const uint ZCAN_ERROR_LOADKERNELDLL = 0x2000;
        public const uint ZCAN_ERROR_CMDFAILED = 0x4000;
        public const uint ZCAN_ERROR_BUFFERCREATE = 0x8000;

        // ------------------------------------------------------------------
        // 函数导入（zlgcan.h 中 FUNC_CALL 在 Windows 上为 __stdcall）
        // ------------------------------------------------------------------

        /// <summary>打开设备：device_index 为「设备管理」里的索引，同类型设备从 0 开始编号。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_OpenDevice(uint device_type, uint device_index, uint reserved);

        /// <summary>关闭设备，与 ZCAN_OpenDevice 一一对应。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_CloseDevice(IntPtr device_handle);

        /// <summary>获取设备信息（版本号、通道数、序列号、硬件类型）。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_GetDeviceInf(IntPtr device_handle, ref ZCAN_DEVICE_INFO pInfo);

        /// <summary>设置设备/通道属性，path 形如 "0/work_mode"、"0/ip"（n 为通道号）。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_SetValue(IntPtr device_handle, string path, string value);

        /// <summary>设置动态配置（value 传 ZCAN_DYNAMIC_CONFIG_DATA 指针，本系列不使用）。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_SetValue(IntPtr device_handle, string path, IntPtr value);

        /// <summary>获取属性，返回指针（const void*），失败返回 NULL。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_GetValue(IntPtr device_handle, string path);

        /// <summary>初始化 CAN 通道（CAN 模式配置结构）。</summary>
        [DllImport(DLL, EntryPoint = "ZCAN_InitCAN", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_InitCAN(IntPtr device_handle, uint can_index, ref ZCAN_CHANNEL_INIT_CONFIG_CAN pInitConfig);

        /// <summary>初始化 CANFD 通道（CANFD 模式配置结构，比 CAN 模式多 12 字节，绝不能复用 CAN 结构）。</summary>
        [DllImport(DLL, EntryPoint = "ZCAN_InitCAN", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_InitCAN_Fd(IntPtr device_handle, uint can_index, ref ZCAN_CHANNEL_INIT_CONFIG_CANFD pInitConfig);

        /// <summary>启动 CAN 通道。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_StartCAN(IntPtr channel_handle);

        /// <summary>复位 CAN 通道（停止收发），可通过 ZCAN_StartCAN 恢复。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ResetCAN(IntPtr channel_handle);

        /// <summary>清空通道接收缓冲区。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ClearBuffer(IntPtr channel_handle);

        /// <summary>读取通道待收帧数，type：0=CAN，1=CANFD。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_GetReceiveNum(IntPtr channel_handle, byte type);

        /// <summary>发送 CAN 报文，返回实际发送成功的报文数。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_Transmit(IntPtr channel_handle, IntPtr pTransmit, uint len);

        /// <summary>发送 CANFD 报文，返回实际发送成功的报文数。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_TransmitFD(IntPtr channel_handle, IntPtr pTransmit, uint len);

        /// <summary>接收 CAN 报文。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_Receive(IntPtr channel_handle, IntPtr pReceive, uint len, int wait_time);

        /// <summary>接收 CANFD 报文。</summary>
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ReceiveFD(IntPtr channel_handle, IntPtr pReceive, uint len, int wait_time);

        // ------------------------------------------------------------------
        // 数据结构（严格按 zlgcan.h 定义）
        // ------------------------------------------------------------------

        /// <summary>CAN 帧（can_frame）：4+1+1+1+1+8 = 16 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct can_frame
        {
            public uint can_id;             // 位31=扩展帧，位30=远程帧，位29=错误帧，低29位为实际 ID
            public byte can_dlc;            // 数据长度（0~8）
            public byte __pad;              // 保留（CANFD 系列用于队列发送标志位）
            public byte __res0;
            public byte __res1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] data;
        }

        /// <summary>CANFD 帧（canfd_frame）：4+1+1+1+1+64 = 72 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct canfd_frame
        {
            public uint can_id;
            public byte len;                // 数据长度（0~64）
            public byte flags;              // 位0=CANFD 加速(BRS)，位5=发送回显，位7=队列发送
            public byte __res0;
            public byte __res1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] data;
        }

        /// <summary>CAN 发送帧：can_frame + transmit_type = 20 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_Transmit_Data
        {
            public can_frame frame;
            public uint transmit_type;      // 0 正常发送，1 单次发送，2 自发自收，3 单次自发自收
        }

        /// <summary>CANFD 发送帧：canfd_frame + transmit_type = 76 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_TransmitFD_Data
        {
            public canfd_frame frame;
            public uint transmit_type;
        }

        /// <summary>CAN 接收帧：can_frame + timestamp = 24 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_Receive_Data
        {
            public can_frame frame;
            public ulong timestamp;         // 单位 us，基于设备启动时间
        }

        /// <summary>CANFD 接收帧：canfd_frame + timestamp = 80 字节。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_ReceiveFD_Data
        {
            public canfd_frame frame;
            public ulong timestamp;
        }

        /// <summary>
        /// 通道初始化配置（CAN 模式，20 字节）：
        /// can_type / acc_code / acc_mask / reserved / filter / timing0 / timing1 / mode
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_CHANNEL_INIT_CONFIG_CAN
        {
            public uint can_type;           // 0 = CAN
            public uint acc_code;           // 验收码，推荐 0
            public uint acc_mask;           // 屏蔽码，推荐 0xFFFFFFFF（全收）
            public uint reserved;
            public byte filter;
            public byte timing0;
            public byte timing1;
            public byte mode;               // 0 正常模式，1 只听模式
        }

        /// <summary>
        /// 通道初始化配置（CANFD 模式，32 字节）：
        /// can_type / acc_code / acc_mask / abi_timing / dbit_timing / brp / filter / mode / pad / reserved
        /// 注意：CANFDNET 系列波特率由设备端配置，这里的 timing 参数保持 0 即可。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_CHANNEL_INIT_CONFIG_CANFD
        {
            public uint can_type;           // 1 = CANFD
            public uint acc_code;
            public uint acc_mask;
            public uint abi_timing;         // 仲裁段定时（0 = 采用设备已有配置）
            public uint dbit_timing;        // 数据段定时
            public uint brp;
            public byte filter;
            public byte mode;               // 0 正常模式，1 只听模式
            public ushort pad;
            public uint reserved;
        }

        /// <summary>
        /// 设备信息（ZCAN_DEVICE_INFO）。C 结构含 USHORT 对齐，用显式偏移保证跨平台一致（共 80 字节）。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 80)]
        public struct ZCAN_DEVICE_INFO
        {
            [FieldOffset(0)] public ushort hw_Version;
            [FieldOffset(2)] public ushort fw_Version;
            [FieldOffset(4)] public ushort dr_Version;
            [FieldOffset(6)] public ushort in_Version;
            [FieldOffset(8)] public ushort irq_Num;
            [FieldOffset(10)] public byte can_Num;
            [FieldOffset(11), MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] str_Serial_Num;
            [FieldOffset(31), MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public byte[] str_hw_Type;
            [FieldOffset(72), MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] reserved;
        }

        /// <summary>动态配置数据结构（key/value 各 64 字节，CANFDNET 系列不使用）。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_DYNAMIC_CONFIG_DATA
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] key;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] value;
        }

        // ------------------------------------------------------------------
        // 便捷工具
        // ------------------------------------------------------------------

        /// <summary>打开设备，失败（含 DLL 缺失）返回 IntPtr.Zero。</summary>
        public static IntPtr OpenDevice(uint deviceType, uint deviceIndex)
        {
            try
            {
                return ZCAN_OpenDevice(deviceType, deviceIndex, 0);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>判断 zlgcan.dll 是否可加载，返回失败原因（成功返回 null）。</summary>
        public static string? CheckSdk()
        {
            try
            {
                ZCAN_GetReceiveNum(IntPtr.Zero, 0);   // 仅触发 DLL 加载，不关心返回值
                return null;
            }
            catch (DllNotFoundException)
            {
                return "未找到 zlgcan.dll。请把 ZLG 驱动（ZCANPRO 安装目录）中的 zlgcan.dll 与 kerneldlls 文件夹复制到程序 exe 同目录。";
            }
            catch (Exception ex)
            {
                return $"zlgcan.dll 加载失败：{ex.Message}";
            }
        }

        /// <summary>设置通道网络属性，失败时返回 false。</summary>
        public static bool SetValue(IntPtr device, string path, string value)
        {
            try
            {
                if (device == IntPtr.Zero) return false;
                return ZCAN_SetValue(device, path, value) == STATUS_OK;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读取字符串属性（如设备 IP、工作模式）。失败返回 null。</summary>
        public static string? GetValueString(IntPtr device, string path)
        {
            try
            {
                if (device == IntPtr.Zero) return null;
                IntPtr p = ZCAN_GetValue(device, path);
                if (p == IntPtr.Zero) return null;
                byte[] raw = new byte[64];
                Marshal.Copy(p, raw, 0, raw.Length);
                string s = Encoding.ASCII.GetString(raw);
                int end = s.IndexOf('\0');
                return end >= 0 ? s.Substring(0, end) : s;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>GetValue 返回指向 int 的属性（如待收数量）。</summary>
        public static int? GetValueInt(IntPtr device, string path)
        {
            try
            {
                if (device == IntPtr.Zero) return null;
                IntPtr p = ZCAN_GetValue(device, path);
                if (p == IntPtr.Zero) return null;
                return Marshal.ReadInt32(p);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把返回码翻译成可读文本（附录3 错误码表）。</summary>
        public static string ErrorText(uint ret)
        {
            switch (ret)
            {
                case STATUS_OK: return "成功";
                case ZCAN_ERROR_CAN_ERRALARM: return "CAN 控制器错误报警";
                case ZCAN_ERROR_CAN_PASSIVE: return "CAN 控制器消极错误";
                case ZCAN_ERROR_CAN_LOSE: return "CAN 控制器仲裁丢失";
                case ZCAN_ERROR_CAN_BUSERR: return "CAN 控制器总线错误";
                case ZCAN_ERROR_CAN_BUSOFF: return "CAN 控制器总线关闭";
                case ZCAN_ERROR_CAN_BUFFER_OVERFLOW: return "CAN 缓存溢出";
                case ZCAN_ERROR_DEVICEOPENED: return "设备已经打开";
                case ZCAN_ERROR_DEVICEOPEN: return "打开设备错误";
                case ZCAN_ERROR_DEVICENOTOPEN: return "设备没有打开";
                case ZCAN_ERROR_BUFFEROVERFLOW: return "缓冲区溢出";
                case ZCAN_ERROR_DEVICENOTEXIST: return "此设备不存在";
                case ZCAN_ERROR_LOADKERNELDLL: return "装载动态库失败";
                case ZCAN_ERROR_CMDFAILED: return "执行命令失败";
                case ZCAN_ERROR_BUFFERCREATE: return "内存不足";
                default: return $"未知错误 0x{ret:X}";
            }
        }
    }
}
