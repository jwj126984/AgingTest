using System;
using System.Runtime.InteropServices;
using System.Text;

namespace demo
{
    /// <summary>
    /// 广州致远(ZLG) zlgcan.dll 原生接口 P/Invoke 封装。
    /// 依据《800H常用说明.pdf》(接口函数使用手册 V1.24) 实现，
    /// 覆盖 CANFDNET-800H 打开 / 配置 / 启动 / 收发 所需的接口与数据结构。
    /// </summary>
    public static class ZlgCan
    {
        // ------------------------------------------------------------------
        // 设备类型（类型号：type，见手册附录1，zlgcan.h 中 ZCAN_XXX 宏）
        // 注意：CANFDNET-800H 为较新设备，本地 SDK 头文件未收录其类型号，
        //       以下为预留常量。若实际运行打开失败，请按所用 zlgcan.h 版本核对
        //       并修改该值（或直接在界面“设备类型”输入框填写）。
        // ------------------------------------------------------------------
        public static uint ZCAN_CANFDNET_800H = 57;   // TODO: 按所用 SDK 的 zlgcan.h 核对确认

        public const uint TYPE_CAN = 0;            // 通道类型：CAN
        public const uint TYPE_CANFD = 1;            // 通道类型：CANFD

        public const uint STATUS_OK = 1;             // 接口调用成功返回值
        public const uint INVALID_DEVICE_HANDLE = 0;
        public const uint INVALID_CHANNEL_HANDLE = 0;

        // ------------------------------------------------------------------
        // 动态配置 key（对应 zlgcan.h 中 ZCAN_DYNAMIC_CONFIG_* 宏字符串，
        // 其中 %d 占位符需用通道号替换）
        // ------------------------------------------------------------------
        public const string DYNAMIC_CONFIG_DEVNAME = "DYNAMIC_CONFIG_DEVNAME";
        public const string DYNAMIC_CONFIG_CAN_ENABLE = "DYNAMIC_CONFIG_CAN{0}_ENABLE";
        public const string DYNAMIC_CONFIG_CAN_MODE = "DYNAMIC_CONFIG_CAN{0}_MODE";
        public const string DYNAMIC_CONFIG_CAN_TXATTEMPTS = "DYNAMIC_CONFIG_CAN{0}_TXATTEMPTS";
        public const string DYNAMIC_CONFIG_CAN_NOMINALBAUD = "DYNAMIC_CONFIG_CAN{0}_NOMINALBAUD";
        public const string DYNAMIC_CONFIG_CAN_DATABAUD = "DYNAMIC_CONFIG_CAN{0}_DATABAUD";
        public const string DYNAMIC_CONFIG_CAN_USERES = "DYNAMIC_CONFIG_CAN{0}_USERES";
        public const string DYNAMIC_CONFIG_CAN_SNDCFG_INTERVAL = "DYNAMIC_CONFIG_CAN{0}_SNDCFG_INTERVAL";
        public const string DYNAMIC_CONFIG_CAN_BUSRATIO_ENABLE = "DYNAMIC_CONFIG_CAN{0}_BUSRATIO_ENABLE";
        public const string DYNAMIC_CONFIG_CAN_BUSRATIO_PERIOD = "DYNAMIC_CONFIG_CAN{0}_BUSRATIO_PERIOD";

        // ------------------------------------------------------------------
        // 函数导入（StdCall，DLL 与程序同级目录）
        // ------------------------------------------------------------------
        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_OpenDevice(uint device_type, uint device_index, uint reserved);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_CloseDevice(IntPtr device_handle);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_SetValue(IntPtr device_handle, string path, string value);

        // 动态配置：value 为 ZCAN_DYNAMIC_CONFIG_DATA 结构体指针
        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_SetValue(IntPtr device_handle, string path, IntPtr value);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr ZCAN_InitCAN(IntPtr device_handle, uint can_index, ref ZCAN_CHANNEL_INIT_CONFIG config);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_StartCAN(IntPtr chn_handle);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ResetCAN(IntPtr chn_handle);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ClearBuffer(IntPtr chn_handle);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_GetReceiveNum(IntPtr channel_handle, byte type); // type:0-CAN,1-CANFD

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_Transmit(IntPtr channel_handle, IntPtr pTransmit, uint len);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_TransmitFD(IntPtr channel_handle, IntPtr pTransmit, uint len);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_Receive(IntPtr channel_handle, IntPtr pReceive, uint len, int wait_time);

        [DllImport(".\\zlgcan.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern uint ZCAN_ReceiveFD(IntPtr channel_handle, IntPtr pReceive, uint len, int wait_time);

        // ------------------------------------------------------------------
        // 数据结构（严格按 zlgcan.h，Pack=1 对齐）
        // ------------------------------------------------------------------

        /// <summary>CAN 帧</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct can_frame
        {
            public uint can_id;             // 报文 ID（含扩展帧/远端帧标志位）
            public byte can_dlc;            // 数据长度
            public byte __pad;
            public byte __res0;
            public byte __res1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] data;             // 数据（CAN 最多 8 字节）
        }

        /// <summary>CANFD 帧</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct canfd_frame
        {
            public uint can_id;
            public byte len;                // 数据长度（最多 64）
            public byte flags;
            public byte __res0;
            public byte __res1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] data;
        }

        /// <summary>CAN 发送帧</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_Transmit_Data
        {
            public can_frame frame;
            public uint transmit_type;      // 0 正常发送，1 单次发送，2 自发自收，3 单次自发自收
        }

        /// <summary>CANFD 发送帧</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_TransmitFD_Data
        {
            public canfd_frame frame;
            public uint transmit_type;
        }

        /// <summary>CAN 接收帧（带时间戳）</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_Receive_Data
        {
            public can_frame frame;
            public UInt64 timestamp;
        }

        /// <summary>CANFD 接收帧（带时间戳）</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_ReceiveFD_Data
        {
            public canfd_frame frame;
            public UInt64 timestamp;
        }

        /// <summary>通道初始化配置（CAN/CANFD 共用）</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_CHANNEL_INIT_CONFIG
        {
            public uint can_type;           // TYPE_CAN / TYPE_CANFD
            public uint acc_code;           // 验收码
            public uint acc_mask;           // 屏蔽码
            public uint reserved;
            public byte filter;             // 滤波
            public byte timing0;
            public byte timing1;
            public byte mode;               // 0 正常模式，1 只听模式
        }

        /// <summary>动态配置数据结构：key 与 value 各 64 字节</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ZCAN_DYNAMIC_CONFIG_DATA
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] key;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] value;

            public static ZCAN_DYNAMIC_CONFIG_DATA Create(string keyStr, string valueStr)
            {
                var d = new ZCAN_DYNAMIC_CONFIG_DATA
                {
                    key = new byte[64],
                    value = new byte[64]
                };
                byte[] k = Encoding.ASCII.GetBytes(keyStr ?? "");
                byte[] v = Encoding.ASCII.GetBytes(valueStr ?? "");
                Array.Copy(k, 0, d.key, 0, Math.Min(k.Length, 64));
                Array.Copy(v, 0, d.value, 0, Math.Min(v.Length, 64));
                return d;
            }
        }

        /// <summary>
        /// 构造指定通道的动态配置 key（替换 %d 占位符）
        /// </summary>
        public static string ChannelKey(string keyTemplate, int channel) => string.Format(keyTemplate, channel);

        // ------------------------------------------------------------------
        // 便捷工具
        // ------------------------------------------------------------------

        /// <summary>打开设备，返回句柄；失败返回 IntPtr.Zero</summary>
        public static IntPtr OpenDevice(uint deviceType, uint deviceIndex) =>
            ZCAN_OpenDevice(deviceType, deviceIndex, 0);

        /// <summary>
        /// 下发一条动态配置并返回是否成功（内部完成结构体内存分配与释放）
        /// </summary>
        public static bool SetDynamicData(IntPtr device, string key, string value)
        {
            if (device == IntPtr.Zero) return false;
            var data = ZCAN_DYNAMIC_CONFIG_DATA.Create(key, value);
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ZCAN_DYNAMIC_CONFIG_DATA)));
            try
            {
                Marshal.StructureToPtr(data, ptr, false);
                return ZCAN_SetValue(device, "0/add_dynamic_data", ptr) == STATUS_OK;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>应用动态配置：0-临时配置，1-持久配置</summary>
        public static bool ApplyDynamicData(IntPtr device, bool persistent)
        {
            if (device == IntPtr.Zero) return false;
            return ZCAN_SetValue(device, "0/apply_dynamic_data", persistent ? "1" : "0") == STATUS_OK;
        }
    }
}
