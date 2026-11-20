using System;
using System.Collections.Generic;

namespace AgingTest.Models
{
    /// <summary>
    /// 烘箱控制器（X4MCGSLDETYA120MV104.05WA，Modbus TCP Server）业务封装。
    /// 地址全部取自协议文档「二、通讯地址」，报文地址 = 表中数值（协议起始地址为 0）。
    ///
    /// 关键约定：
    ///   温度/湿度的值均为「实际值 ×10」的 16 位整数，负数按补码存放（如 -23℃ = 65536-230）。
    ///   启动/停止/复位等按钮为线圈，写 1 后设备自动复位。
    /// </summary>
    public class OvenController
    {
        private readonly ModbusTcpClient _mb = new();

        public bool IsConnected => _mb.IsOpen;
        public ModbusTcpClient Modbus => _mb;

        public void Connect(string ip, int port, int unitId)
        {
            _mb.TimeoutMs = 2000;
            _mb.Open(ip, port, (byte)unitId);
        }

        public void Disconnect() => _mb.Close();

        // ================================================================
        // 地址常量（协议表）
        // ================================================================
        // 只读（03/04）
        public const int RegRunStatus = 7990;          // 运行状态：0 停止 1 运行
        public const int RegCurrentTemp = 7991;        // 当前温度（一位小数，×10）
        public const int RegHumidity = 7992;           // 当前湿度（一位小数，×10）
        public const int RegSetTempRead = 8024;        // 温度设定（只读，×10）
        public const int RegSetHumidityRead = 8025;    // 湿度设定（只读，×10）
        public const int RegEstimateMin = 8012;        // 预计完成时间 分/时/日/月/年（8012~8016）
        public const int RegTotalRunMinute = 8042;     // 运行总时间 分
        public const int RegTotalRunHour = 8043;       // 运行总时间 时
        public const int RegProgramSection = 8034;     // 段次

        // 可写（06）
        public const int RegSetTempWrite = 8100;       // 定值设定温度（×10）
        public const int RegSetHumidityWrite = 8101;   // 定值设定湿度（×10）
        public const int RegControlType = 8107;        // 0 温湿度 / 1 温度
        public const int RegOperationMode = 8108;      // 0 定值控制 / 1 程式控制
        public const int RegTempLowLimit = 8112;
        public const int RegTempHighLimit = 8113;
        public const int RegPowerDownMode = 10299;

        // 线圈（01 读 / 05 写）
        public const int CoilRun = 8000;               // 运行按钮（置 1 启动，自动复位）
        public const int CoilStop = 8001;              // 停止按钮
        public const int CoilFaultReset = 8002;        // 故障复位按钮
        public const int CoilProgramSkip = 8006;       // 程式跳段
        public const int CoilHold = 8007;              // 保持
        public const int CoilLight = 8010;             // 照明 0 关 1 开
        public const int CoilAutoTuneTemp = 8014;      // 温度自整定
        public const int CoilAutoTuneHumidity = 8015;  // 湿度自整定
        public const int CoilPause = 8029;             // 暂停
        public const int CoilContinue = 8103;          // 继续运行
        public const int CoilReservation = 3010;       // 预约功能

        // ================================================================
        // 状态读取
        // ================================================================
        public class OvenStatus
        {
            public double CurrentTemp { get; set; } = double.NaN;
            public double SetTemp { get; set; } = double.NaN;
            public double Humidity { get; set; } = double.NaN;
            public double SetHumidity { get; set; } = double.NaN;
            public bool? Running { get; set; }
            public int TotalRunHours { get; set; }
            public int TotalRunMinutes { get; set; }
            public int ProgramSection { get; set; }
            public DateTime? EstimatedFinish { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;

            public string RunningText => Running == true ? "运行中" : Running == false ? "已停止" : "未知";
            public string TempText => Format1(CurrentTemp);
            public string SetTempText => Format1(SetTemp);
            public string HumidityText => double.IsNaN(Humidity) ? "—" : $"{Humidity:0.0} %";

            private static string Format1(double v) => double.IsNaN(v) ? "—" : $"{v:0.0} ℃";
        }

        /// <summary>
        /// 读取一次完整状态。分段批量读取，避免 48 个工位同时轮询时占用过多往返：
        ///   7990~7999（运行状态/温度/湿度）、8024~8025（设定值）、8042~8043（运行时间）、8012~8016（预计完成）、8034（段次）
        /// </summary>
        public OvenStatus ReadStatus()
        {
            var st = new OvenStatus { Timestamp = DateTime.Now };

            // 运行状态 + 当前温度 + 当前湿度（首帧失败直接抛出，供界面提示通讯异常）
            var head = _mb.ReadHoldingRegisters(RegRunStatus, 10);        // 7990~7999
            st.Running = head[0] != 0;
            st.CurrentTemp = ToValue(head[1]);
            st.Humidity = ToValue(head[2]);

            // 设定温度 / 设定湿度
            try
            {
                var r = _mb.ReadHoldingRegisters(RegSetTempRead, 2);       // 8024~8025
                st.SetTemp = ToValue(r[0]);
                st.SetHumidity = ToValue(r[1]);
            }
            catch { }

            // 运行总时间
            try
            {
                var r = _mb.ReadHoldingRegisters(RegTotalRunMinute, 2);    // 8042~8043
                st.TotalRunMinutes = r[0];
                st.TotalRunHours = r[1];
            }
            catch { }

            // 预计完成时间
            try
            {
                var r = _mb.ReadHoldingRegisters(RegEstimateMin, 5);       // 8012~8016 分/时/日/月/年
                if (r[4] > 0 && r[3] >= 1 && r[3] <= 12 && r[2] >= 1 && r[2] <= 31 && r[1] <= 23 && r[0] <= 59)
                {
                    int year = r[4] < 100 ? 2000 + r[4] : r[4];   // 年：两位（26）或四位（2026）都兼容
                    try { st.EstimatedFinish = new DateTime(year, r[3], r[2], r[1], r[0], 0); } catch { }
                }
            }
            catch { }

            // 段次
            try { st.ProgramSection = _mb.ReadHoldingRegisters(RegProgramSection, 1)[0]; }
            catch { }

            return st;
        }

        // ================================================================
        // 控制操作
        // ================================================================
        /// <summary>启动设备（线圈 8000，设备收到后自动复位）。</summary>
        public void Start() => _mb.WriteSingleCoil(CoilRun, true);

        /// <summary>停止设备（线圈 8001）。</summary>
        public void Stop() => _mb.WriteSingleCoil(CoilStop, true);

        /// <summary>故障复位（线圈 8002）。</summary>
        public void FaultReset() => _mb.WriteSingleCoil(CoilFaultReset, true);

        /// <summary>暂停（线圈 8029）。</summary>
        public void Pause() => _mb.WriteSingleCoil(CoilPause, true);

        /// <summary>继续运行（线圈 8103）。</summary>
        public void Resume() => _mb.WriteSingleCoil(CoilContinue, true);

        /// <summary>程式跳段（线圈 8006）。</summary>
        public void ProgramSkip() => _mb.WriteSingleCoil(CoilProgramSkip, true);

        /// <summary>保持（线圈 8007）。</summary>
        public void Hold() => _mb.WriteSingleCoil(CoilHold, true);

        /// <summary>照明开/关（线圈 8010）。</summary>
        public void SetLight(bool on) => _mb.WriteSingleCoil(CoilLight, on);

        /// <summary>读照明状态（线圈 8010）。</summary>
        public bool ReadLight()
        {
            var bits = _mb.ReadCoils(CoilLight, 1);
            return bits.Length > 0 && bits[0];
        }

        /// <summary>温度自整定（线圈 8014，一段时间后自动复位）。</summary>
        public void AutoTuneTemperature() => _mb.WriteSingleCoil(CoilAutoTuneTemp, true);

        /// <summary>湿度自整定（线圈 8015）。</summary>
        public void AutoTuneHumidity() => _mb.WriteSingleCoil(CoilAutoTuneHumidity, true);

        /// <summary>预约功能开/关（线圈 3010）。</summary>
        public void SetReservation(bool on) => _mb.WriteSingleCoil(CoilReservation, on);

        /// <summary>
        /// 设定温度（寄存器 8100）：写入值为 ℃ × 10，负数用 16 位补码。
        /// 例：40.0℃ → 400（0x0190）；-23℃ → 65306（0xFF1A）。
        /// </summary>
        public void SetTemperature(double celsius)
        {
            if (celsius < -100.0 || celsius > 300.0)
                throw new ArgumentOutOfRangeException(nameof(celsius), "定值设定温度范围为 -100.0 ~ 300.0 ℃");
            _mb.WriteSingleRegister(RegSetTempWrite, ToRegister(celsius));
        }

        /// <summary>设定湿度（寄存器 8101），写入值为 % × 10。</summary>
        public void SetHumidity(double percent)
        {
            if (percent < 0 || percent > 100)
                throw new ArgumentOutOfRangeException(nameof(percent), "定值设定湿度范围为 0 ~ 100 %");
            _mb.WriteSingleRegister(RegSetHumidityWrite, ToRegister(percent));
        }

        /// <summary>控制类型：false=温湿度，true=仅温度（寄存器 8107）。</summary>
        public void SetControlType(bool temperatureOnly)
            => _mb.WriteSingleRegister(RegControlType, (ushort)(temperatureOnly ? 1 : 0));

        /// <summary>操作模式：false=定值控制，true=程式控制（寄存器 8108）。</summary>
        public void SetOperationMode(bool programMode)
            => _mb.WriteSingleRegister(RegOperationMode, (ushort)(programMode ? 1 : 0));

        // ================================================================
        // 数值换算
        // ================================================================
        /// <summary>寄存器 → 实际值（×10，按有符号 16 位解释）。</summary>
        public static double ToValue(ushort raw) => (short)raw / 10.0;

        /// <summary>实际值 → 寄存器（×10，负数转补码）。</summary>
        public static ushort ToRegister(double value) => (ushort)(short)Math.Round(value * 10, MidpointRounding.AwayFromZero);
    }
}
