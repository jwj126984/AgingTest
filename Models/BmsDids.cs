using System;
using System.Collections.Generic;
using System.Linq;

namespace AgingTest.Models
{
    /// <summary>一条 DID 的解析结果。</summary>
    public class DidValue
    {
        public ushort Did { get; set; }
        public string Name { get; set; } = "";
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>物理值/数值（无换算时等于原始整数；文本型 DID 为 NaN）。</summary>
        public double Numeric { get; set; } = double.NaN;
        public string Unit { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Ok { get; set; } = true;
        public List<string> Details { get; set; } = new();

        public string RawHex => CanSignalCodec.Hex(Data);
        public string DidText => $"0x{Did:X4}";

        public override string ToString()
            => string.IsNullOrEmpty(Text) ? $"{DidText} {Name}: {RawHex}" : $"{DidText} {Name}: {Text}";
    }

    /// <summary>
    /// 附录1-数据解析 的 DID 定义与解析规则。读取用 22 DID，解析结果见 <see cref="Parse"/>。
    /// </summary>
    public static class BmsDids
    {
        // 版本 / 标识
        public const ushort AppVersion = 0x73B3;
        public const ushort BootVersion = 0x73B2;
        public const ushort EcuSerial = 0xF18C;
        public const ushort HardwareVersion = 0xF089;      // 只读，24 字节 ASCII，前 5 字节有效
        public const ushort HardwareVersionWrite = 0x73B4; // 可写，3 字节 ASCII，如 "0.2.3"

        // 模拟量
        public const ushort Current = 0x7396;              // uint32，×0.001 − 2000 A
        public const ushort InnerVoltage = 0x739B;         // uint8 ×0.1 V（B+）
        public const ushort OuterVoltage = 0x739C;         // uint8 ×0.1 V（P+）
        public const ushort MidPointVoltage = 0x739E;      // uint8 ×0.1 V（US）
        public const ushort CellVoltage1 = 0x7397;
        public const ushort CellVoltage2 = 0x7398;
        public const ushort CellVoltage3 = 0x7399;
        public const ushort CellVoltage4 = 0x739A;
        public const ushort CellTemp0 = 0x73A3;
        public const ushort CellTemp1 = 0x73A4;
        public const ushort DischargeMosTemp = 0x73A5;
        public const ushort ChargeMosTemp = 0x73A6;
        public const ushort BuckTemp = 0x73A7;             // 限流模块温度
        public const ushort BoardTemp = 0x73A8;
        public const ushort Soc = 0x7391;                  // Bit0~6 SOC，Bit7 有效位

        // 状态
        public const ushort MosBuckStatus = 0x738F;
        public const ushort DischargeMosGateVoltage = 0x73A1;
        public const ushort ChargeMosGateVoltage = 0x73A2;
        public const ushort WakeupSource = 0x73AA;
        public const ushort LockStatus = 0x73B0;

        // 配置 / 标定
        public const ushort CurrentType = 0x73B5;
        public const ushort CurrentCalibration = 0x73B6;   // 40 字节 = 10 × uint32，值 − 1,000,000 → mA
        public const ushort StrongMosBuck = 0x73BA;        // 强控 MOS / BUCK
        public const ushort StrongBalance = 0x73BB;        // 强控均衡
        public const ushort SocCalibrationRoutine = 0x7410; // 31 01 74 10 xx

        private static readonly Dictionary<ushort, string> Names = new()
        {
            [AppVersion] = "APP 版本",
            [BootVersion] = "BOOT 版本",
            [EcuSerial] = "ECU 序列号",
            [HardwareVersion] = "硬件版本（读）",
            [HardwareVersionWrite] = "硬件版本（写）",
            [Current] = "电流",
            [InnerVoltage] = "内总压 B+",
            [OuterVoltage] = "外总压 P+",
            [MidPointVoltage] = "中间点电压 US",
            [CellVoltage1] = "单体电压 1",
            [CellVoltage2] = "单体电压 2",
            [CellVoltage3] = "单体电压 3",
            [CellVoltage4] = "单体电压 4",
            [CellTemp0] = "电池温度 0",
            [CellTemp1] = "电池温度 1",
            [DischargeMosTemp] = "放电 MOS 温度",
            [ChargeMosTemp] = "充电 MOS 温度",
            [BuckTemp] = "限流模块温度",
            [BoardTemp] = "板子温度",
            [Soc] = "SOC",
            [MosBuckStatus] = "充放电 MOS / BUCK 状态",
            [DischargeMosGateVoltage] = "放电 MOS G 极电压",
            [ChargeMosGateVoltage] = "充电 MOS G 极电压",
            [WakeupSource] = "唤醒源",
            [LockStatus] = "锁包状态",
            [CurrentType] = "电流类型",
            [CurrentCalibration] = "电流标定点",
            [StrongMosBuck] = "强控 MOS / BUCK",
            [StrongBalance] = "强控均衡",
        };

        public static string NameOf(ushort did) => Names.TryGetValue(did, out var name) ? name : "未定义 DID";

        /// <summary>EOL 常读的 DID 集合（版本、电流、电压、温度、状态、SOC、序列号）。</summary>
        public static readonly ushort[] DefaultReadDids =
        {
            AppVersion, BootVersion, HardwareVersion, EcuSerial,
            Current, InnerVoltage, OuterVoltage, MidPointVoltage,
            CellVoltage1, CellVoltage2, CellVoltage3, CellVoltage4,
            CellTemp0, CellTemp1, DischargeMosTemp, ChargeMosTemp, BuckTemp, BoardTemp,
            MosBuckStatus, Soc, WakeupSource, LockStatus, CurrentType,
        };

        /// <summary>按附录1 规则解析某一 DID 的原始数据（不含 62/DID 头）。</summary>
        public static DidValue Parse(ushort did, byte[] data)
        {
            var v = new DidValue { Did = did, Name = NameOf(did), Data = data ?? Array.Empty<byte>() };
            if (data == null || data.Length == 0)
            {
                v.Ok = false;
                v.Text = "无数据";
                return v;
            }

            switch (did)
            {
                case AppVersion:
                case BootVersion:
                case EcuSerial:
                    v.Text = CanSignalCodec.Ascii(data);
                    break;

                case HardwareVersion:
                    v.Text = CanSignalCodec.Ascii(data, 0, 5);   // 前 5 字节有效，如 0.2.2
                    break;

                case HardwareVersionWrite:
                    v.Text = CanSignalCodec.Ascii(data);
                    break;

                case Current:
                {
                    // uint32 原始值 × 0.001 − 2000（A），示例 0x1E8482 → 0.002 A
                    if (data.Length < 4) { v.Ok = false; v.Text = "长度不足 4 字节"; break; }
                    uint raw = CanSignalCodec.U32(data, 0);
                    v.Numeric = raw * 0.001 - 2000.0;
                    v.Unit = "A";
                    v.Text = $"{v.Numeric:F3} A（原始 0x{raw:X8}）";
                    break;
                }

                case InnerVoltage:
                case OuterVoltage:
                case MidPointVoltage:
                {
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = CanSignalCodec.U8(data, 0) * 0.1;
                    v.Unit = "V";
                    v.Text = $"{v.Numeric:F1} V";
                    break;
                }

                case CellVoltage1:
                case CellVoltage2:
                case CellVoltage3:
                case CellVoltage4:
                {
                    if (data.Length < 2) { v.Ok = false; v.Text = "长度不足 2 字节"; break; }
                    v.Numeric = CanSignalCodec.U16(data, 0) * 0.001;
                    v.Unit = "V";
                    v.Text = $"{v.Numeric:F3} V";
                    break;
                }

                case CellTemp0:
                case CellTemp1:
                case DischargeMosTemp:
                case ChargeMosTemp:
                case BuckTemp:
                case BoardTemp:
                {
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = CanSignalCodec.U8(data, 0) - 40.0;
                    v.Unit = "℃";
                    v.Text = $"{v.Numeric:F0} ℃";
                    break;
                }

                case Soc:
                {
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    byte raw = data[0];
                    bool valid = (raw & 0x80) != 0;
                    int percent = raw & 0x7F;
                    v.Numeric = percent;
                    v.Unit = "%";
                    v.Text = $"{percent} %（Bit7 有效位={(valid ? "1 有效" : "0 无效")}，原始 0x{raw:X2}）";
                    break;
                }

                case MosBuckStatus:
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Text = MosBuckText(data[0]);
                    v.Details.AddRange(MosBuckDetails(data[0]));
                    break;

                case DischargeMosGateVoltage:
                case ChargeMosGateVoltage:
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = data[0];
                    v.Unit = "V";
                    v.Text = $"{v.Numeric:F0} V";
                    break;

                case WakeupSource:
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = data[0];
                    v.Text = WakeupText(data[0]);
                    break;

                case LockStatus:
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = data[0];
                    v.Text = data[0] switch { 0 => "未锁包（NotLock）", 1 => "锁包（Lock）", _ => $"未定义（0x{data[0]:X2}）" };
                    break;

                case CurrentType:
                    if (data.Length < 1) { v.Ok = false; v.Text = "长度不足 1 字节"; break; }
                    v.Numeric = data[0];
                    v.Text = CurrentTypeText(data[0]);
                    break;

                case CurrentCalibration:
                {
                    if (data.Length < 40) { v.Ok = false; v.Text = $"长度 {data.Length} 字节，应为 40 字节"; break; }
                    var list = new List<string>();
                    for (int i = 0; i < 10; i++)
                    {
                        // 附录1：读取原始值后 −1,000,000 得到 mA
                        int mA = unchecked((int)CanSignalCodec.U32(data, i * 4) - 1000000);
                        list.Add($"标定点{i + 1}={mA} mA（原始 {mA + 1000000}）");
                    }
                    v.Text = string.Join("；", list);
                    v.Details.AddRange(list);
                    break;
                }

                case StrongMosBuck:
                    if (data.Length < 3) { v.Ok = false; v.Text = $"长度 {data.Length} 字节，应为 3 字节"; break; }
                    v.Text = $"控制参数=0x{data[0]:X2}，状态位=0x{data[1]:X2}，使能=0x{data[2]:X2}" + "；" +
                             MosBuckDetails(data[1]).Aggregate((a, b) => a + "、" + b);
                    v.Details.AddRange(MosBuckDetails(data[1]));
                    break;

                case StrongBalance:
                    if (data.Length < 3) { v.Ok = false; v.Text = $"长度 {data.Length} 字节，应为 3 字节"; break; }
                    v.Text = $"控制参数=0x{data[0]:X2}，均衡位图=0x{data[1]:X2}，使能=0x{data[2]:X2}；" + BalanceText(data[1]);
                    v.Details.Add(BalanceText(data[1]));
                    break;

                default:
                    v.Ok = false;
                    v.Text = $"未定义解析规则，原始：{v.RawHex}";
                    break;
            }
            return v;
        }

        // ================================================================
        // 状态位文本
        // ================================================================
        /// <summary>738F：Bit0~1 充电 MOS，Bit2~3 放电 MOS，Bit4~7 BUCK。</summary>
        public static List<string> MosBuckDetails(byte raw)
        {
            var list = new List<string>();
            int charge = raw & 0x03;
            int discharge = (raw >> 2) & 0x03;
            int buck = (raw >> 4) & 0x0F;

            list.Add("充电MOS=" + (charge switch { 0 => "Unknown", 1 => "Close(闭合)", 2 => "Open(断开)", _ => "Reserved" }));
            list.Add("放电MOS=" + (discharge switch { 0 => "Unknown", 1 => "Close(闭合)", 2 => "Open(断开)", _ => "Reserved" }));
            list.Add("BUCK=" + (buck switch { 0 => "Open(断开)", 1 => "Close(闭合)", _ => $"未定义(0x{buck:X})" }));
            return list;
        }

        public static string MosBuckText(byte raw) => string.Join("，", MosBuckDetails(raw));

        public static string WakeupText(byte raw) => raw switch
        {
            0x00 => "PowerOn（上电）",
            0x01 => "RTC（定时唤醒）",
            0x02 => "HardwareLine（硬线唤醒）",
            0x03 => "CAN NM（CAN 唤醒）",
            0x04 => "LIN",
            0x05 => "DisChrgCurrent（放电电流）",
            0x06 => "OV（过压）",
            0x07 => "OT（过温）",
            0x08 => "UV（欠压）",
            0x09 => "UT（低温）",
            0x0A => "Shortcircuit（短路）",
            0x0B => "JumpStart（借电启动）",
            _ => $"未定义（0x{raw:X2}）",
        };

        /// <summary>73B5 电流类型：11 原始 / 22 平均 / 33 20A / 44 100A / 55 200A / 66 400A 标定。</summary>
        public static string CurrentTypeText(byte raw) => raw switch
        {
            0x11 => "原始电流",
            0x22 => "平均电流",
            0x33 => "标定电流 1（±0.2/1/10/20A）",
            0x44 => "标定电流 2（±0.2/1/10/100A）",
            0x55 => "标定电流 3（±0.2/1/10/100/200A）",
            0x66 => "标定电流 4（±0.2/1/10/100/200/400A）",
            _ => $"未定义（0x{raw:X2}）",
        };

        public static string BalanceText(byte mask)
        {
            var on = new List<string>();
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0) on.Add($"电芯{i + 1}");
            return on.Count == 0 ? "均衡全关" : "均衡开启：" + string.Join("、", on);
        }
    }
}
