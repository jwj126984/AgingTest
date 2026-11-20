using System.Collections.Generic;

namespace AgingTest.Models
{
    /// <summary>
    /// 【全局配置存储主类】合并CAN卡配置 + GJDA电子负载配置
    /// 对应配置文件：CanAppConfig.json
    /// </summary>
    public class Config
    {
        /// <summary>ZLG NetCAN800H 多块CAN卡配置列表</summary>
        public List<CanCardItem> CanCardList { get; set; } = new List<CanCardItem>();

        /// <summary>GJDA‑100‑32电子负载设备集合</summary>
        public List<GjdaDeviceItem> GjdaDevices { get; set; } = new List<GjdaDeviceItem>();

        /// <summary>设备通讯设置（电阻卡/继电器板串口、程控电源网口）</summary>
        public CommunicationConfig Comm { get; set; } = new CommunicationConfig();
    }

    #region 通讯设置实体
    /// <summary>设备通讯参数集合（485串口 + 程控电源网口）。</summary>
    public class CommunicationConfig
    {
        /// <summary>电子负载共享 485 串口（GJDA-100-32 ×2 共用一路 485，靠总线地址区分）。</summary>
        public RrPortSetting LoadPort { get; set; } = new();

        /// <summary>485总线端口（电阻模拟卡+继电器板共用），共6个；每端口挂 电阻卡地址1~4、继电器板地址17~20。</summary>
        public List<RrPortSetting> RrPorts { get; set; } = new List<RrPortSetting>();

        /// <summary>大电流程控电源 N36100 设备（6台，各自独立IP/端口）。</summary>
        public List<NetDeviceSetting> N36100 { get; set; } = new List<NetDeviceSetting>();

        /// <summary>高精度程控电源 N36600 设备（6台，各自独立IP/端口）。</summary>
        public List<NetDeviceSetting> N36600 { get; set; } = new List<NetDeviceSetting>();

        /// <summary>烘箱控制器（Modbus TCP Server，网口）。</summary>
        public OvenSetting Oven { get; set; } = new OvenSetting();
    }

    /// <summary>
    /// 烘箱控制器网口参数（依据《X4MCGSLDETYA120MV104.05WA通讯协议》Modbus Server）。
    /// 触摸屏/控制器默认 IP 与电脑同网段（例 192.168.1.10），端口默认 8000，站号 1。
    /// </summary>
    public class OvenSetting
    {
        public string Ip { get; set; } = "192.168.1.10";
        public int Port { get; set; } = 8000;
        /// <summary>Modbus 从站地址（报文中的设备地址码，默认 1）</summary>
        public int UnitId { get; set; } = 1;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>485总线串口设置（电阻/继电器板共用）。</summary>
    public class RrPortSetting
    {
        public string ComPort { get; set; } = "COM1";
        public int Baud { get; set; } = 9600;
    }

    /// <summary>程控电源网口设置（IP/端口）。</summary>
    public class NetDeviceSetting
    {
        public string Ip { get; set; } = "192.168.0.123";
        public int Port { get; set; } = 7000;
    }
    #endregion

    #region CAN卡实体
    /// <summary>
    /// CANFDNET-800U 单通道启动参数（对应 ZCANPRO「启动」界面，以及手册 3.3.7 的 n/work_mode、n/ip、n/work_port、n/local_port 属性）。
    /// 800H 一台设备 8 个通道，每通道是独立的网络连接，因此每个通道都要单独配置、单独启动。
    /// </summary>
    public class CanChannelItem
    {
        /// <summary>通道号 0~7</summary>
        public int ChannelIndex { get; set; }

        /// <summary>是否参与启动（连接时跳过未勾选通道）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>目标设备 IP（客户端模式生效）</summary>
        public string IpAddress { get; set; } = "192.168.0.10";

        /// <summary>工作模式：0=客户端（PC 连设备），1=服务器（PC 监听）</summary>
        public int WorkMode { get; set; }

        /// <summary>本地端口（服务器 / UDP 模式生效）</summary>
        public int LocalPort { get; set; } = 4001;

        /// <summary>工作端口（客户端模式生效，即设备该通道的监听端口）</summary>
        public int WorkPort { get; set; } = 4001;

        /// <summary>协议：false=CAN，true=CANFD（对应 InitCAN 的 can_type）</summary>
        public bool IsCanFd { get; set; }

        /// <summary>CANFD 加速：0=否，1=是（发送帧 flags 第 0 位）</summary>
        public int CanFdAcc { get; set; }

        /// <summary>只听模式：1=只听（控制器不发任何数据，含应答位）</summary>
        public bool ListenOnly { get; set; }

        /// <summary>端口显示，便于日志/界面展示。</summary>
        public string EndpointText => WorkMode == 1 ? $"本地端口 {LocalPort}" : $"{IpAddress}:{WorkPort}";
    }

    /// <summary>NetCAN800H单张CAN卡配置实体</summary>
    public class CanCardItem
    {
        /// <summary>CAN卡索引编号（1~6）</summary>
        public int CanIndex { get; set; }

        /// <summary>设备类型名称，对应「设备管理」类型下拉：CANFDNET-800U-TCP / CANFDNET-800U-UDP</summary>
        public string DeviceType { get; set; } = "CANFDNET-800U-TCP";

        /// <summary>设备索引（ZCAN_OpenDevice 的 device_index），-1 表示沿用 CanIndex-1</summary>
        public int DeviceIndex { get; set; } = -1;

        /// <summary>8 个通道的启动参数</summary>
        public List<CanChannelItem> Channels { get; set; } = new List<CanChannelItem>();

        /// <summary>起始端口（通道0端口），通道 n 的端口 = BasePort + n，默认 4001</summary>
        public int BasePort { get; set; } = 4001;

        /// <summary>设备类型号：CANFDNET-800U TCP=57，UDP=58（手册附录1，表4.1）</summary>
        public uint DeviceTypeCode => (DeviceType ?? "").Contains("UDP", StringComparison.OrdinalIgnoreCase) ? 58u : 57u;

        /// <summary>实际使用的设备索引。</summary>
        public int ResolvedDeviceIndex => DeviceIndex >= 0 ? DeviceIndex : CanIndex - 1;

        /// <summary>
        /// 补齐 8 个通道配置：缺失通道按卡片级旧字段派生，默认通道 n 的端口为 4001+n（ZLG 设备默认通道端口）。
        /// 兼容旧 Config.json（旧文件只有卡片级 IpAddress/WorkMode/LocalPort/RemotePort）。
        /// </summary>
        public List<CanChannelItem> EnsureChannels(int count = 8)
        {
            if (Channels == null) Channels = new List<CanChannelItem>();
            for (int ch = 0; ch < count; ch++)
            {
                if (Channels.Any(c => c.ChannelIndex == ch)) continue;
                Channels.Add(new CanChannelItem
                {
                    ChannelIndex = ch,
                    Enabled = true,
                    IpAddress = string.IsNullOrWhiteSpace(IpAddress) ? "192.168.0.10" : IpAddress,
                    WorkMode = WorkMode,
                    LocalPort = BasePort + ch,
                    WorkPort = BasePort + ch,
                    IsCanFd = IsCanFd,
                    CanFdAcc = CanFdAcc,
                    ListenOnly = ListenOnly
                });
            }
            Channels.Sort((a, b) => a.ChannelIndex.CompareTo(b.ChannelIndex));
            return Channels;
        }

        /// <summary>设备是否已经连接</summary>
        public bool IsConnected { get; set; }

        /// <summary>设备IP地址（客户端模式生效）</summary>
        public string IpAddress { get; set; } = "192.168.0.100";

        /// <summary>工作模式：0=客户端，1=服务器</summary>
        public int WorkMode { get; set; } = 0;

        /// <summary>本地监听端口</summary>
        public int LocalPort { get; set; } = 4001;

        /// <summary>远端设备工作端口（客户端模式生效）</summary>
        public int RemotePort { get; set; } = 8000;

        /// <summary>CANFD加速：0=否，1=是；仅CAN‑FD协议下生效</summary>
        public int CanFdAcc { get; set; } = 0;

        /// <summary>是否启用CAN‑FD协议；false=普通CAN，true=CAN‑FD</summary>
        public bool IsCanFd { get; set; }

        #region 保留旧字段，兼容历史旧代码
        /// <summary>底层SDK设备编号</summary>
        public int DevIndex { get; set; }

        /// <summary>CAN通道编号 0=CH0，1=CH1</summary>
        public int CanChannel { get; set; }

        /// <summary>仲裁段波特率</summary>
        public uint BaudArbitration { get; set; }

        /// <summary>CAN‑FD数据段波特率</summary>
        public uint BaudData { get; set; }

        /// <summary>是否开启120Ω终端电阻</summary>
        public bool TermResistorOn { get; set; }

        /// <summary>是否开启只听模式（只读不发送报文）</summary>
        public bool ListenOnly { get; set; }=false;
        #endregion
    }
    #endregion




    #region GJDA电子负载实体
    /// <summary>GJDA‑100‑32电子负载设备实体，单台设备包含32路通道</summary>
    public class GjdaDeviceItem
    {
        /// <summary>电子负载设备ID编号</summary>
        public int DevId { get; set; }

        /// <summary>RS485串口名称 COM1/COM2……</summary>
        public string ComPort { get; set; } = "COM1";

        /// <summary>串口波特率</summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>RS485从站设备地址</summary>
        public byte SlaveAddr { get; set; } = 1;

        /// <summary>32路通道配置集合</summary>
        public List<GjdaChannelItem> Channels { get; set; } = new List<GjdaChannelItem>();
    }

    /// <summary>GJDA电子负载单通道配置实体</summary>
    public class GjdaChannelItem
    {
        /// <summary>通道号 1‑32</summary>
        public int ChNo { get; set; }

        /// <summary>通道工作模式 0=恒流，1=恒压，2=恒阻</summary>
        public int Mode { get; set; }

        /// <summary>开启电压Von(V)，达到该电压负载才投入工作</summary>
        public double VonVolt { get; set; }

        /// <summary>模式设定值：电流/电压/电阻</summary>
        public double SetValue { get; set; }

        /// <summary>模式扩展附加参数</summary>
        public byte ExtraParam { get; set; }
    }
    #endregion
}
