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
    /// <summary>NetCAN800H单张CAN卡配置实体</summary>
    public class CanCardItem
    {
        /// <summary>CAN卡索引编号（1~6）</summary>
        public int CanIndex { get; set; }

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
