using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// 数据监控详情窗口：按文档《产品功能说明》设计。
    /// 数据区：单体电压4通道 / 总压2通道(MOS内外) / 温度2通道 / MOS温度 / 电流 / 工作电压 / 工作温度 / 工作功耗 / SOC / SOH / 均衡电流 / 故障诊断。
    /// 控制区：MOS闭合/断开、4路被动均衡、浅/深休眠、CAN/低电平/帮电唤醒（当前为模拟下发，设备通讯接入后替换为真实指令）。
    /// </summary>
    public partial class DataMonitorWindow : Window
    {
        private const int MaxPoints = 60;

        private readonly WorkStation _ws;
        private readonly DispatcherTimer _timer;
        private readonly Random _rnd = new();

        private readonly List<double> _voltSeries = new();
        private readonly List<double> _tempSeries = new();
        private readonly List<double> _currSeries = new();

        private long _refreshCount;

        public DataMonitorWindow(WorkStation ws)
        {
            InitializeComponent();
            _ws = ws;

            Title = $"数据监控详情 - 工位 {ws.Code}";
            TxtDetailStation.Text = ws.Code;
            TxtDetailProduct.Text = string.IsNullOrEmpty(ws.ProductCode) ? "未绑定" : ws.ProductCode;

            if (ws.Soc < 1) ws.Soc = 85;
            if (ws.Soh < 1) ws.Soh = 95;

            // 初始控制状态同步
            UpdateMosState();
            UpdateSleepState();
            UpdateBalanceState();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            Closed += (_, _) => _timer.Stop();
            Loaded += (_, _) => Tick();
        }

        // ==================== 周期刷新 ====================

        private void Tick()
        {
            _refreshCount++;
            TxtDetailCount.Text = _refreshCount.ToString();

            bool aging = _ws.State == WorkStationState.Aging;
            bool rt = _ws.State == WorkStationState.RTAging;

            // 顶部状态
            TxtDetailState.Text = _ws.State switch
            {
                WorkStationState.Idle => "空闲",
                WorkStationState.Bound => "已绑定",
                WorkStationState.RTAging => "常温测试中",
                WorkStationState.Aging => "老化测试中",
                WorkStationState.Passed => "通过",
                WorkStationState.Failed => "NG",
                _ => "未知"
            };
            TxtDetailState.Foreground = new SolidColorBrush(_ws.State switch
            {
                WorkStationState.Aging or WorkStationState.RTAging => Color.FromRgb(0xED, 0x9A, 0x2C),
                WorkStationState.Failed => Color.FromRgb(0xE5, 0x48, 0x4D),
                WorkStationState.Passed => Color.FromRgb(0x2F, 0xA8, 0x4F),
                _ => Color.FromRgb(0x7E, 0xE0, 0xA0)
            });
            TxtDetailType.Text = _ws.TestType;
            TxtDetailRemain.Text = _ws.RemainTime.HasValue ? $"{_ws.RemainTime.Value:hh\\:mm\\:ss}" : "--";

            // 老化步骤显示（按《BMS老化测试项》流程）
            if (aging)
            {
                TxtDetailAgingStep.Text = $"{_ws.AgingStepText} {_ws.AgingStepRemainSec}s";
                TxtDetailAgingStep.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0xA8));
            }
            else
            {
                TxtDetailAgingStep.Text = "--";
                TxtDetailAgingStep.Foreground = new SolidColorBrush(Color.FromRgb(0xAF, 0xC4, 0xE8));
            }

            // ---- 模拟采集数据（设备通讯接入后替换为真实数据）----
            double baseTemp = aging ? 80 : 25;
            for (int i = 0; i < 4; i++)
            {
                if (_ws.CellVoltages[i] < 3.0) _ws.CellVoltages[i] = 3.2 + _rnd.NextDouble() * 1.0;
                _ws.CellVoltages[i] = Clamp(_ws.CellVoltages[i] + (_rnd.NextDouble() - 0.5) * 0.02, 3.0, 4.5);
            }
            for (int i = 0; i < 2; i++)
            {
                if (_ws.CellTemps[i] < 1) _ws.CellTemps[i] = baseTemp + (_rnd.NextDouble() - 0.5) * 2;
                _ws.CellTemps[i] = Clamp(_ws.CellTemps[i] + (_rnd.NextDouble() - 0.5) * 0.3, -40, 125);
            }
            _ws.MosTemp = _ws.CellTemps[0] + 8 + _rnd.NextDouble() * 4;
            _ws.InnerVoltage = 12 + (_rnd.NextDouble() - 0.5) * 0.4;
            _ws.OuterVoltage = _ws.InnerVoltage + (_rnd.NextDouble() - 0.5) * 0.1;
            // 电流：测试中由主界面老化/常温流程驱动（带载/休眠/唤醒按步骤变化），详情页仅显示
            if (_ws.State is not (WorkStationState.RTAging or WorkStationState.Aging))
                _ws.Current = Clamp(_ws.Current + (_rnd.NextDouble() - 0.5) * 0.6, 0, 8);
            _ws.WorkVoltage = 12 + (_rnd.NextDouble() - 0.5) * 0.6;              // 6~18V
            _ws.WorkTemp = baseTemp + (_rnd.NextDouble() - 0.5) * 2;              // -40~105℃
            _ws.WorkPower = 40 + _rnd.NextDouble() * 50;                          // <100mA
            _ws.Soc = Clamp(_ws.Soc + (_rnd.NextDouble() - 0.5) * 0.1, 5, 100);
            _ws.Soh = Clamp(_ws.Soh + (_rnd.NextDouble() - 0.5) * 0.02, 80, 100);
            _ws.FaultInfo = "";

            double balSum = 0; int balOn = 0;
            for (int i = 0; i < 4; i++)
            {
                if (_ws.BalanceOn[i])
                {
                    _ws.BalanceCurrents[i] = 40 + _rnd.NextDouble() * 80;         // 40~120mA
                    balSum += _ws.BalanceCurrents[i]; balOn++;
                }
                else _ws.BalanceCurrents[i] = 0;
            }

            // ---- 更新数据卡片 ----
            TxtV1.Text = $"{_ws.CellVoltages[0]:F3} V";
            TxtV2.Text = $"{_ws.CellVoltages[1]:F3} V";
            TxtV3.Text = $"{_ws.CellVoltages[2]:F3} V";
            TxtV4.Text = $"{_ws.CellVoltages[3]:F3} V";
            TxtInnerV.Text = $"{_ws.InnerVoltage:F2} V";
            TxtOuterV.Text = $"{_ws.OuterVoltage:F2} V";
            TxtCurr.Text = $"{_ws.Current:F2} A";
            TxtWorkV.Text = $"{_ws.WorkVoltage:F1} V";
            TxtT1.Text = $"{_ws.CellTemps[0]:F1} ℃";
            TxtT2.Text = $"{_ws.CellTemps[1]:F1} ℃";
            TxtMosT.Text = $"{_ws.MosTemp:F1} ℃";
            TxtWorkT.Text = $"{_ws.WorkTemp:F1} ℃";
            TxtSoc.Text = $"{_ws.Soc:F1} %";
            TxtSoh.Text = $"{_ws.Soh:F1} %";
            TxtWorkP.Text = $"{_ws.WorkPower:F0} mA";
            TxtBalance.Text = balOn > 0 ? $"{balSum / balOn:F0} mA (×{balOn})" : "0 mA";

            TxtDetailFault.Text = string.IsNullOrEmpty(_ws.FaultInfo) ? "正常" : _ws.FaultInfo;
            TxtDetailFault.Foreground = new SolidColorBrush(string.IsNullOrEmpty(_ws.FaultInfo)
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xC0, 0x39, 0x2B));

            // DTC 故障码（文档要求实时监控 DTC 信息）
            TxtDetailDtc.Text = string.IsNullOrEmpty(_ws.DtcInfo) || _ws.DtcInfo == "无" ? "无" : _ws.DtcInfo;
            TxtDetailDtc.Foreground = new SolidColorBrush(string.IsNullOrEmpty(_ws.DtcInfo) || _ws.DtcInfo == "无"
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xC0, 0x39, 0x2B));

            // 均衡失效自诊断（模拟：小概率失效演示）
            _ws.BalanceFault = _rnd.NextDouble() < 0.02;
            TxtBalDiag.Text = _ws.BalanceFault ? "失效！" : "正常";
            TxtBalDiag.Foreground = new SolidColorBrush(_ws.BalanceFault
                ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0x2F, 0xA8, 0x4F));

            // ---- 曲线 ----
            Push(_voltSeries, _ws.CellVoltages[0]);
            Push(_tempSeries, _ws.CellTemps[0]);
            Push(_currSeries, _ws.Current);
            UpdatePolyline(LineVoltage, CanvasVoltage, _voltSeries, 3.0, 4.5);
            UpdatePolyline(LineTemp, CanvasTemp, _tempSeries, 0, 100);
            UpdatePolyline(LineCurrent, CanvasCurrent, _currSeries, 0, 10);
            TxtCurV.Text = $"{_ws.CellVoltages[0]:F2}V";
            TxtCurT.Text = $"{_ws.CellTemps[0]:F1}℃";
            TxtCurI.Text = $"{_ws.Current:F2}A";

            // 老化流程自动驱动 MOS/休眠状态时，同步刷新指示灯与状态文本
            UpdateMosState();
            UpdateSleepState();
        }

        // ==================== 控制功能实现 ====================

        private void BtnMosClose_Click(object sender, RoutedEventArgs e)
        {
            _ws.MosClosed = true;
            UpdateMosState();
            Log($"[控制] 工位 {_ws.Code} MOS 闭合（吸合）");
        }

        private void BtnMosOpen_Click(object sender, RoutedEventArgs e)
        {
            _ws.MosClosed = false;
            UpdateMosState();
            Log($"[控制] 工位 {_ws.Code} MOS 断开");
        }

        private void Bal_Changed(object sender, RoutedEventArgs e)
        {
            var tg = (ToggleButton)sender;
            int idx = (int)tg.Tag;
            _ws.BalanceOn[idx] = tg.IsChecked == true;
            UpdateBalanceState();
            Log($"[控制] 工位 {_ws.Code} 均衡{idx + 1} {(tg.IsChecked == true ? "开启" : "关闭")}");
        }

        private void SleepCmd_Click(object sender, RoutedEventArgs e)
        {
            // 测试中禁止休眠（老化流程需保持产品工作状态）
            if (_ws.State is WorkStationState.RTAging or WorkStationState.Aging)
            {
                Hint($"工位 {_ws.Code} 测试中，禁止休眠/唤醒操作");
                return;
            }

            var tag = (string)((Button)sender).Tag;
            switch (tag)
            {
                case "light":
                    _ws.SleepMode = 1;
                    Log($"[控制] 工位 {_ws.Code} 进入浅休眠");
                    break;
                case "deep":
                    _ws.SleepMode = 2;
                    Log($"[控制] 工位 {_ws.Code} 进入深度休眠");
                    break;
                case "wakecan":
                    _ws.SleepMode = 0;
                    Log($"[控制] 工位 {_ws.Code} CAN 唤醒");
                    break;
                case "wakelow":
                    _ws.SleepMode = 0;
                    Log($"[控制] 工位 {_ws.Code} 低电平硬线唤醒（接模拟电池GND）");
                    break;
                case "wakepower":
                    _ws.SleepMode = 0;
                    Log($"[控制] 工位 {_ws.Code} 帮电唤醒");
                    break;
            }
            UpdateSleepState();
        }

        private void UpdateMosState()
        {
            MosLight.Fill = new SolidColorBrush(_ws.MosClosed
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xB0, 0xB6, 0xC0));
            TxtMosState.Text = _ws.MosClosed ? "已闭合" : "已断开";
            TxtMosState.Foreground = new SolidColorBrush(_ws.MosClosed
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0x88, 0x88, 0x88));
        }

        private void UpdateBalanceState()
        {
            for (int i = 0; i < 4; i++)
            {
                var tg = i switch { 0 => TogBal1, 1 => TogBal2, 2 => TogBal3, _ => TogBal4 };
                tg.IsChecked = _ws.BalanceOn[i];
            }
        }

        private void UpdateSleepState()
        {
            TxtSleepState.Text = _ws.SleepMode switch
            {
                1 => "当前状态：浅休眠",
                2 => "当前状态：深度休眠",
                _ => "当前状态：正常唤醒"
            };
            TxtSleepState.Foreground = new SolidColorBrush(_ws.SleepMode == 0
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xED, 0x9A, 0x2C));
        }

        // ==================== 辅助 ====================

        private void Hint(string msg)
        {
            TxtDetailFault.Text = msg;
            TxtDetailFault.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        }

        /// <summary>操作日志写回主界面日志区</summary>
        private void Log(string msg) => (Owner as MainWindow)?.AddLog(msg);

        private static void Push(List<double> series, double value)
        {
            series.Add(value);
            if (series.Count > MaxPoints)
                series.RemoveAt(0);
        }

        private void UpdatePolyline(Polyline line, Canvas canvas, IList<double> values, double min, double max)
        {
            line.Points.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 1 || h <= 1 || values.Count < 2) return;

            double range = max - min;
            if (range <= 0) range = 1;
            for (int i = 0; i < values.Count; i++)
            {
                double x = i * w / (MaxPoints - 1);
                double y = h - (values[i] - min) / range * h;
                line.Points.Add(new Point(x, y));
            }
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
