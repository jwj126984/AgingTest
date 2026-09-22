using System;

namespace AgingTest.Models
{
    /// <summary>工位状态</summary>
    public enum WorkStationState
    {
        /// <summary>空闲（未绑定，空白色）</summary>
        Idle,
        /// <summary>已绑定（等待测试）</summary>
        Bound,
        /// <summary>常温测试中</summary>
        RTAging,
        /// <summary>老化测试中</summary>
        Aging,
        /// <summary>测试通过</summary>
        Passed,
        /// <summary>测试NG</summary>
        Failed
    }

    /// <summary>
    /// 老化测试步骤（按《BMS老化测试项》流程 序号1~12 循环，13=重复1-12）：
    /// 上电 → 带载20min → 一级休眠 → 静置1min → 唤醒 → 带载20min → 二级休眠 → 静置1min → 唤醒 → 带载20min → 下电 → 静置1min → 回到上电
    /// </summary>
    public enum AgingStep
    {
        /// <summary>1-BMS上电（设单体电压/温度模拟量，低电平唤醒线拉低，发网络管理报文）</summary>
        PowerOn,
        /// <summary>2-带载20min（MOS+HSD闭合，P2带载5A，OUT1-OUT4各1A）</summary>
        Load1,
        /// <summary>3-一级休眠（停止电流输出，发休眠指令，MOS保持闭合）</summary>
        Sleep1,
        /// <summary>4-静置1min</summary>
        Rest1,
        /// <summary>5-唤醒（发网络管理报文唤醒）</summary>
        Wake1,
        /// <summary>6-带载20min</summary>
        Load2,
        /// <summary>7-二级休眠（停止电流，强制断开MOS及HSD，发休眠指令）</summary>
        Sleep2,
        /// <summary>8-静置1min</summary>
        Rest2,
        /// <summary>9-唤醒（低电平唤醒线拉低唤醒）</summary>
        Wake2,
        /// <summary>10-带载20min</summary>
        Load3,
        /// <summary>11-BMS下电（停止电流输出，给BMS下电）</summary>
        PowerOff,
        /// <summary>12-静置1min → 返回1重复</summary>
        Rest3
    }

    /// <summary>
    /// 老化工位模型：6层 × 8列 = 48工位
    /// </summary>
    public class WorkStation
    {
        public WorkStation(int layer, int column)
        {
            Layer = layer;
            Column = column;
        }

        /// <summary>层（1~6，纵向）</summary>
        public int Layer { get; }

        /// <summary>列（1~8，横向）</summary>
        public int Column { get; }

        /// <summary>工位编号，如 "1-1"</summary>
        public string Code => $"{Layer}-{Column}";

        /// <summary>工位条码（默认规则：POS + 编号，如 POS1-1），规则待客户确认</summary>
        public string Barcode => $"POS{Layer}-{Column}";

        /// <summary>当前状态</summary>
        public WorkStationState State { get; set; } = WorkStationState.Idle;

        /// <summary>产品二维码</summary>
        public string ProductCode { get; set; } = "";

        /// <summary>绑定时间</summary>
        public DateTime? BindTime { get; set; }

        /// <summary>测试开始时间</summary>
        public DateTime? TestStartTime { get; set; }

        /// <summary>计划测试时长</summary>
        public TimeSpan? TestDuration { get; set; }

        /// <summary>测试类型：常温 / 老化</summary>
        public string TestType { get; set; } = "";

        /// <summary>测试剩余时间</summary>
        public TimeSpan? RemainTime { get; set; }

        // ---------- 实时模拟采集数据（设备通讯接入后替换为真实数据） ----------
        /// <summary>单体电压 V（4串，取第1串演示）</summary>
        public double CellVoltage { get; set; }

        /// <summary>电芯温度 ℃</summary>
        public double CellTemp { get; set; }

        /// <summary>总电流 A</summary>
        public double Current { get; set; }

        /// <summary>内总压 V</summary>
        public double InnerVoltage { get; set; }

        /// <summary>外总压 V</summary>
        public double OuterVoltage { get; set; }

        /// <summary>故障信息（空=正常）</summary>
        public string FaultInfo { get; set; } = "";

        /// <summary>DTC故障码（无=正常；出现异常时置故障码，如 P0560）</summary>
        public string DtcInfo { get; set; } = "无";

        // ---------- 详情监控数据（设备通讯接入后替换为真实数据） ----------
        /// <summary>4串单体电压 V</summary>
        public double[] CellVoltages { get; } = new double[4];

        /// <summary>2路电芯温度 ℃</summary>
        public double[] CellTemps { get; } = new double[2];

        /// <summary>MOS温度 ℃</summary>
        public double MosTemp { get; set; }

        /// <summary>SOC %</summary>
        public double Soc { get; set; }

        // ---------- 按文档《产品功能说明》扩展的采集/控制状态 ----------
        /// <summary>工作电压 V（6~18V）</summary>
        public double WorkVoltage { get; set; }

        /// <summary>工作温度 ℃（-40~105℃）</summary>
        public double WorkTemp { get; set; }

        /// <summary>工作功耗 mA（&lt;100mA）</summary>
        public double WorkPower { get; set; }

        /// <summary>SOH %（精度8%）</summary>
        public double Soh { get; set; }

        /// <summary>4路被动均衡开关状态</summary>
        public bool[] BalanceOn { get; } = new bool[4];

        /// <summary>4路均衡电流 mA（40~120mA）</summary>
        public double[] BalanceCurrents { get; } = new double[4];

        /// <summary>均衡失效自诊断（true=失效）</summary>
        public bool BalanceFault { get; set; }

        /// <summary>MOS闭合状态（true=闭合/吸合，带载条件）</summary>
        public bool MosClosed { get; set; }

        /// <summary>休眠模式：0=正常唤醒 1=浅休眠 2=深度休眠</summary>
        public int SleepMode { get; set; }

        // ---------- 老化测试流程（按《BMS老化测试项》步骤循环） ----------
        /// <summary>当前老化步骤</summary>
        public AgingStep AgingStep { get; set; }

        /// <summary>当前老化步骤剩余秒数</summary>
        public int AgingStepRemainSec { get; set; }

        /// <summary>老化步骤显示名（含序号）</summary>
        public string AgingStepText => AgingStep switch
        {
            AgingStep.PowerOn => "1-BMS上电",
            AgingStep.Load1 or AgingStep.Load2 or AgingStep.Load3 => "带载20min",
            AgingStep.Sleep1 => "3-一级休眠",
            AgingStep.Rest1 or AgingStep.Rest2 or AgingStep.Rest3 => "静置1min",
            AgingStep.Wake1 or AgingStep.Wake2 => "唤醒",
            AgingStep.Sleep2 => "7-二级休眠",
            AgingStep.PowerOff => "11-BMS下电",
            _ => "--"
        };
    }
}
