using System;
using System.Collections.Generic;

namespace AgingTest.Models
{
    /// <summary>
    /// 充电测试单个工位的设备映射（默认拓扑，见 ChargeTestSetting 注释）：
    ///   层 1~6 = 485 列 1~6；
    ///   列 1~8 → 继电器地址 17/17/18/18/19/19/20/20，电阻地址 1/1/2/2/3/3/4/4；
    ///   奇数列 = 单工位通道组，偶数列 = 双工位通道组；
    ///   负载 1/2 各负责 3 层，层内使用本负载 CH1~8；
    ///   大/小电源按层对应 1~6 号机；CAN 按 8 工位/盒映射。
    /// </summary>
    public class ChargeStationMap
    {
        public WorkStation Station { get; }
        public int StationNo => Station.StationNo;
        public int Layer => Station.Layer;
        public int Column => Station.Column;
        public bool IsDual { get; }

        public int RelayPort => Layer;
        public int RelayAddr => 17 + (Column - 1) / 2;
        public int ResistorPort => Layer;
        public int ResistorAddr => 1 + (Column - 1) / 2;

        public int LoadDevId => Layer <= 3 ? 1 : 2;
        public int LoadCh => ((Layer - 1) % 3) * 8 + Column;

        public int BigSupplyIndex => Layer;
        public int SmallSupplyIndex => Layer;

        public int CanIndex => (StationNo - 1) / 8 + 1;
        public int CanChannel => (StationNo - 1) % 8;

        public int WakeCtl => IsDual ? 20 : 18;
        public int[] PowerCtls => IsDual ? new[] { 6, 8, 13, 15 } : new[] { 2, 4, 9, 11 };
        public int[] TempResistorRegisters => IsDual ? new[] { 0x0204, 0x0206 } : new[] { 0x0200, 0x0202 };
        public string TempChannelText => IsDual ? "CH5~7" : "CH1~3";

        public string MapText =>
            $"继电器 485-{RelayPort}/地址{RelayAddr} CTL{WakeCtl}+{string.Join("/", PowerCtls)}；" +
            $"电阻 地址{ResistorAddr} {TempChannelText}；负载{LoadDevId}/CH{LoadCh}；" +
            $"CAN{CanIndex}/CH{CanChannel}；电源{BigSupplyIndex}";

        public ChargeStationMap(WorkStation station, bool isDual)
        {
            Station = station;
            IsDual = isDual;
        }

        public override string ToString() => $"工位{Station.Code} {MapText}";
    }
}
