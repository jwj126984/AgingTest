using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// 数据监控详情窗口：
    /// 数据区全部来自 BMS UDS 22 读取（附录1-数据解析），由老化流程已建立的 UdsClient 提供；
    /// 电源区显示本工位所在层的大电流电源 N36190 与高精度电源 N36620 回采电压/电流。
    /// 不再提供 MOS 控制、均衡管理、休眠唤醒、通信信息等手工控制/说明区域。
    /// </summary>
    public partial class DataMonitorWindow : Window
    {
        private const int MaxPoints = 60;
        private const int PollIntervalMs = 2000;

        private readonly WorkStation _ws;
        private readonly Func<int, UdsClient?>? _udsProvider;
        private readonly DispatcherTimer _timer;
        private readonly int _canIndex;
        private readonly int _canChannel;
        private readonly ObservableCollection<CanFrameRow> _frames = new();
        private readonly MonitorSnapshot _snap = new();

        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private bool _pollStarted;
        private bool _closed;

        private readonly List<double> _voltSeries = new();
        private readonly List<double> _tempSeries = new();
        private readonly List<double> _currSeries = new();

        private long _refreshCount;

        public DataMonitorWindow(WorkStation ws, Func<int, UdsClient?>? udsProvider = null)
        {
            InitializeComponent();
            _ws = ws;
            _udsProvider = udsProvider;
            (_canIndex, _canChannel) = ResolveCan(ws.StationNo);
            GridCanFrames.ItemsSource = _frames;

            Title = $"数据监控详情 - 工位 {ws.Code}";
            TxtDetailStation.Text = ws.Code;
            TxtDetailProduct.Text = string.IsNullOrEmpty(ws.ProductCode) ? "未绑定" : ws.ProductCode;
            TxtCanLink.Text = $"CAN{_canIndex} / CH{_canChannel}";
            TxtPowerLayer.Text = $"第 {ws.Layer} 层";
            TxtUdsState.Text = "未连接";
            TxtDetailCount.Text = "刷新 0";

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            CanComm.FrameReceived += OnCanFrame;
            Closed += (_, _) =>
            {
                _closed = true;
                _timer.Stop();
                CanComm.FrameReceived -= OnCanFrame;
                try { _pollCts?.Cancel(); } catch { }
            };
            Loaded += (_, _) =>
            {
                UpdateCanLinkState();
                StartPolling();
                Tick();
            };
        }

        // ==================== 周期刷新 ====================

        private void Tick()
        {
            _refreshCount++;
            TxtDetailCount.Text = $"刷新 {_refreshCount}";

            bool aging = _ws.State == WorkStationState.Aging;

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

            if (aging)
            {
                string phase = string.IsNullOrWhiteSpace(_ws.AgingPhaseText)
                    ? $"{_ws.AgingStepText} {_ws.AgingStepRemainSec}s"
                    : _ws.AgingPhaseText;
                TxtDetailAgingStep.Text = phase;
                TxtDetailAgingStep.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0xA8));
            }
            else
            {
                TxtDetailAgingStep.Text = "--";
                TxtDetailAgingStep.Foreground = new SolidColorBrush(Color.FromRgb(0xAF, 0xC4, 0xE8));
            }

            RenderData(pushCharts: true);
            UpdateCanLinkState();
        }

        /// <summary>把后台读取到的真实值刷到界面；pushCharts=true 时同时推进曲线。</summary>
        private void RenderData(bool pushCharts)
        {
            TxtV1.Text = FormatVoltage(_snap.Cell1, 3);
            TxtV2.Text = FormatVoltage(_snap.Cell2, 3);
            TxtV3.Text = FormatVoltage(_snap.Cell3, 3);
            TxtV4.Text = FormatVoltage(_snap.Cell4, 3);
            TxtInnerV.Text = FormatVoltage(_snap.InnerV, 2);
            TxtOuterV.Text = FormatVoltage(_snap.OuterV, 2);
            TxtCurr.Text = FormatCurrent(_snap.Current);
            TxtSoc.Text = FormatPercent(_snap.Soc);

            TxtT1.Text = FormatTemp(_snap.CellTemp1);
            TxtT2.Text = FormatTemp(_snap.CellTemp2);
            TxtChargeMosT.Text = FormatTemp(_snap.ChargeMosTemp);
            TxtDischargeMosT.Text = FormatTemp(_snap.DischargeMosTemp);
            TxtBuckT.Text = FormatTemp(_snap.BuckTemp);
            TxtBoardT.Text = FormatTemp(_snap.BoardTemp);
            TxtCellDelta.Text = FormatCellDelta(_snap);
            TxtPackDelta.Text = FormatPackDelta(_snap);

            TxtBigV.Text = FormatVoltage(_snap.BigV, 2);
            TxtBigA.Text = FormatCurrent(_snap.BigA);
            TxtSmallV.Text = FormatVoltage(_snap.SmallV, 2);
            TxtSmallA.Text = FormatCurrent(_snap.SmallA);
            TxtPowerLayer.ToolTip = _snap.PowerDetail;

            TxtUdsState.Text = _snap.UdsStatus;
            TxtUdsState.ToolTip = _snap.UdsDetail;
            TxtUdsState.Foreground = new SolidColorBrush(_snap.UdsOk
                ? Color.FromRgb(0x7E, 0xE0, 0xA0)
                : _snap.UdsStatus.StartsWith("未连接", StringComparison.Ordinal)
                    ? Color.FromRgb(0xAF, 0xC4, 0xE8)
                    : Color.FromRgb(0xFF, 0xB4, 0x9A));

            TxtDetailFault.Text = string.IsNullOrEmpty(_ws.FaultInfo) ? "正常" : _ws.FaultInfo;
            TxtDetailFault.Foreground = new SolidColorBrush(string.IsNullOrEmpty(_ws.FaultInfo)
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xC0, 0x39, 0x2B));

            TxtDetailDtc.Text = string.IsNullOrEmpty(_ws.DtcInfo) || _ws.DtcInfo == "无" ? "无" : _ws.DtcInfo;
            TxtDetailDtc.Foreground = new SolidColorBrush(string.IsNullOrEmpty(_ws.DtcInfo) || _ws.DtcInfo == "无"
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xC0, 0x39, 0x2B));

            if (!pushCharts) return;
            if (!double.IsNaN(_snap.Cell1)) Push(_voltSeries, _snap.Cell1);
            if (!double.IsNaN(_snap.CellTemp1)) Push(_tempSeries, _snap.CellTemp1);
            if (!double.IsNaN(_snap.Current)) Push(_currSeries, _snap.Current);
            UpdatePolyline(LineVoltage, CanvasVoltage, _voltSeries, 3.0, 4.5);
            UpdatePolyline(LineTemp, CanvasTemp, _tempSeries, 0, 100);
            UpdatePolyline(LineCurrent, CanvasCurrent, _currSeries, 0, 10);
            TxtCurV.Text = FormatVoltage(_snap.Cell1, 2);
            TxtCurT.Text = FormatTemp(_snap.CellTemp1);
            TxtCurI.Text = FormatCurrent(_snap.Current);
        }

        // ==================== UDS / 电源后台读取 ====================

        private void StartPolling()
        {
            if (_pollStarted) return;
            _pollStarted = true;
            _pollCts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_closed)
            {
                var snap = new MonitorSnapshot();
                try
                {
                    var uds = _udsProvider?.Invoke(_ws.StationNo);
                    if (uds == null)
                    {
                        snap.UdsStatus = "未连接";
                        snap.UdsDetail = "启动老化后复用该工位的 UDS 客户端读取";
                    }
                    else
                    {
                        var errors = new List<string>();
                        ReadUdsSnapshot(uds, snap, errors);
                        snap.UdsOk = errors.Count == 0;
                        snap.UdsStatus = snap.UdsOk
                            ? $"正常 {DateTime.Now:HH:mm:ss}"
                            : $"部分失败 {errors.Count} 项";
                        snap.UdsDetail = string.Join(Environment.NewLine, errors.Take(8));
                    }

                    ReadPowerSnapshot(snap);
                }
                catch (Exception ex)
                {
                    snap.UdsOk = false;
                    snap.UdsStatus = "读取异常";
                    snap.UdsDetail = ex.Message;
                }

                if (_closed) return;
                try
                {
                    await Dispatcher.InvokeAsync(() => ApplySnapshot(snap));
                }
                catch (TaskCanceledException) { return; }

                try { await Task.Delay(PollIntervalMs, ct); }
                catch (TaskCanceledException) { return; }
            }
        }

        /// <summary>按附录1 读取并解析模拟量 DID；读取期间关闭逐帧日志，避免刷主界面。</summary>
        private static void ReadUdsSnapshot(UdsClient uds, MonitorSnapshot snap, List<string> errors)
        {
            lock (uds.SyncRoot)
            {
                bool oldLog = uds.Transport.LogEnabled;
                uds.Transport.LogEnabled = false;
                try
                {
                    snap.Cell1 = ReadNumeric(uds, BmsDids.CellVoltage1, errors);
                    snap.Cell2 = ReadNumeric(uds, BmsDids.CellVoltage2, errors);
                    snap.Cell3 = ReadNumeric(uds, BmsDids.CellVoltage3, errors);
                    snap.Cell4 = ReadNumeric(uds, BmsDids.CellVoltage4, errors);
                    snap.InnerV = ReadNumeric(uds, BmsDids.InnerVoltage, errors);
                    snap.OuterV = ReadNumeric(uds, BmsDids.OuterVoltage, errors);
                    snap.Current = ReadNumeric(uds, BmsDids.Current, errors);
                    snap.Soc = ReadNumeric(uds, BmsDids.Soc, errors);
                    snap.CellTemp1 = ReadNumeric(uds, BmsDids.CellTemp0, errors);
                    snap.CellTemp2 = ReadNumeric(uds, BmsDids.CellTemp1, errors);
                    snap.ChargeMosTemp = ReadNumeric(uds, BmsDids.ChargeMosTemp, errors);
                    snap.DischargeMosTemp = ReadNumeric(uds, BmsDids.DischargeMosTemp, errors);
                    snap.BuckTemp = ReadNumeric(uds, BmsDids.BuckTemp, errors);
                    snap.BoardTemp = ReadNumeric(uds, BmsDids.BoardTemp, errors);
                }
                finally
                {
                    uds.Transport.LogEnabled = oldLog;
                }
            }
        }

        private static double ReadNumeric(UdsClient uds, ushort did, List<string> errors)
        {
            try
            {
                var v = BmsEolService.ReadDid(uds, did);
                if (v.Ok && !double.IsNaN(v.Numeric)) return v.Numeric;
                errors.Add($"{v.DidText}：{v.Text}");
            }
            catch (Exception ex)
            {
                errors.Add($"0x{did:X4}：{ex.Message}");
            }
            return double.NaN;
        }

        private void ReadPowerSnapshot(MonitorSnapshot snap)
        {
            var errors = new List<string>();
            int layer = _ws.Layer;

            if (DeviceComm.N36100.Count >= layer && DeviceComm.N36100[layer - 1].Connected)
            {
                var client = DeviceComm.N36100[layer - 1].Client;
                snap.BigV = SafeRead(client.MeasVoltage, errors, "N36190 电压");
                snap.BigA = SafeRead(client.MeasCurrent, errors, "N36190 电流");
            }
            else
            {
                errors.Add("N36190 未连接");
            }

            if (DeviceComm.N36600.Count >= layer && DeviceComm.N36600[layer - 1].Connected)
            {
                var client = DeviceComm.N36600[layer - 1].Client;
                snap.SmallV = SafeRead(client.MeasVoltage, errors, "N36620 电压");
                snap.SmallA = SafeRead(client.MeasCurrent, errors, "N36620 电流");
            }
            else
            {
                errors.Add("N36620 未连接");
            }

            snap.PowerDetail = string.Join(Environment.NewLine, errors);
        }

        private static double SafeRead(Func<double> read, List<string> errors, string label)
        {
            try
            {
                double v = read();
                if (double.IsNaN(v)) errors.Add($"{label} 返回 NaN");
                return v;
            }
            catch (Exception ex)
            {
                errors.Add($"{label}：{ex.Message}");
                return double.NaN;
            }
        }

        private void ApplySnapshot(MonitorSnapshot snap)
        {
            if (_closed) return;

            _snap.Cell1 = snap.Cell1;
            _snap.Cell2 = snap.Cell2;
            _snap.Cell3 = snap.Cell3;
            _snap.Cell4 = snap.Cell4;
            _snap.InnerV = snap.InnerV;
            _snap.OuterV = snap.OuterV;
            _snap.Current = snap.Current;
            _snap.Soc = snap.Soc;
            _snap.CellTemp1 = snap.CellTemp1;
            _snap.CellTemp2 = snap.CellTemp2;
            _snap.ChargeMosTemp = snap.ChargeMosTemp;
            _snap.DischargeMosTemp = snap.DischargeMosTemp;
            _snap.BuckTemp = snap.BuckTemp;
            _snap.BoardTemp = snap.BoardTemp;
            _snap.BigV = snap.BigV;
            _snap.BigA = snap.BigA;
            _snap.SmallV = snap.SmallV;
            _snap.SmallA = snap.SmallA;
            _snap.UdsOk = snap.UdsOk;
            _snap.UdsStatus = snap.UdsStatus;
            _snap.UdsDetail = snap.UdsDetail;
            _snap.PowerDetail = snap.PowerDetail;

            if (!double.IsNaN(snap.Cell1)) _ws.CellVoltages[0] = snap.Cell1;
            if (!double.IsNaN(snap.Cell2)) _ws.CellVoltages[1] = snap.Cell2;
            if (!double.IsNaN(snap.Cell3)) _ws.CellVoltages[2] = snap.Cell3;
            if (!double.IsNaN(snap.Cell4)) _ws.CellVoltages[3] = snap.Cell4;
            if (!double.IsNaN(snap.CellTemp1)) _ws.CellTemps[0] = snap.CellTemp1;
            if (!double.IsNaN(snap.CellTemp2)) _ws.CellTemps[1] = snap.CellTemp2;
            if (!double.IsNaN(snap.InnerV)) _ws.InnerVoltage = snap.InnerV;
            if (!double.IsNaN(snap.OuterV)) _ws.OuterVoltage = snap.OuterV;
            if (!double.IsNaN(snap.Current)) _ws.Current = snap.Current;
            if (!double.IsNaN(snap.Soc)) _ws.Soc = snap.Soc;
            if (!double.IsNaN(snap.ChargeMosTemp)) _ws.MosTemp = snap.ChargeMosTemp;

            RenderData(pushCharts: false);
        }

        // ==================== 本工位 CAN 报文监视 ====================

        private static (int can, int ch) ResolveCan(int stationNo)
        {
            var uds = DeviceComm.Root.Comm.Uds;
            int perBox = Math.Max(1, uds.StationsPerBox);
            int offset = Math.Max(0, stationNo - 1);
            int can = uds.FirstCanIndex + offset / perBox;
            int ch = uds.FirstChannel + offset % perBox;

            if (can < 1) can = 1;
            if (can > CanComm.CardCount) can = CanComm.CardCount;
            if (ch < 0) ch = 0;
            if (ch >= CanComm.ChannelCount) ch = CanComm.ChannelCount - 1;
            return (can, ch);
        }

        private void UpdateCanLinkState()
        {
            if (TxtCanLink == null || CanLinkDot == null || TxtCanLinkState == null) return;
            TxtCanLink.Text = $"CAN{_canIndex} / CH{_canChannel}";
            bool started = CanComm.IsChannelStarted(_canIndex, _canChannel);
            CanLinkDot.Fill = new SolidColorBrush(started
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0xB0, 0xB6, 0xC0));
            TxtCanLinkState.Text = started ? "通道已启动，实时收发" : "通道未启动";
            TxtCanLinkState.Foreground = new SolidColorBrush(started
                ? Color.FromRgb(0x2F, 0xA8, 0x4F) : Color.FromRgb(0x88, 0x88, 0x88));
        }

        /// <summary>CAN 接收/发送线程回调：只接收本工位对应 CAN 盒/通道的报文，切回 UI 线程显示。</summary>
        private void OnCanFrame(CanComm.CanFrameInfo frame)
        {
            if (_closed || frame.CanIndex != _canIndex || frame.Channel != _canChannel) return;

            if (Dispatcher.CheckAccess())
            {
                AddCanFrame(frame);
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AddCanFrame(frame)));
            }
        }

        private void AddCanFrame(CanComm.CanFrameInfo frame)
        {
            if (_closed) return;

            var row = new CanFrameRow
            {
                Time = frame.Time.ToString("HH:mm:ss.fff"),
                Direction = frame.IsTx ? "TX 发送" : "RX 接收",
                Id = $"0x{frame.Id:X}",
                Kind = frame.Kind,
                Length = frame.Len.ToString(),
                Data = frame.DataText
            };

            _frames.Add(row);
            while (_frames.Count > 200) _frames.RemoveAt(0);
            TxtFrameCount.Text = $"报文 {_frames.Count}";
            GridCanFrames.ScrollIntoView(row);
        }

        // ==================== 格式化 ====================

        private static string FormatVoltage(double value, int digits)
            => double.IsNaN(value) ? "--" : $"{value.ToString($"F{digits}")} V";

        private static string FormatCurrent(double value)
            => double.IsNaN(value) ? "--" : $"{value:F2} A";

        private static string FormatTemp(double value)
            => double.IsNaN(value) ? "--" : $"{value:F1} ℃";

        private static string FormatPercent(double value)
            => double.IsNaN(value) ? "--" : $"{value:F0} %";

        private static string FormatCellDelta(MonitorSnapshot s)
        {
            double[] cells = { s.Cell1, s.Cell2, s.Cell3, s.Cell4 };
            if (cells.Any(double.IsNaN)) return "--";
            return $"{(cells.Max() - cells.Min()) * 1000:F0} mV";
        }

        private static string FormatPackDelta(MonitorSnapshot s)
            => double.IsNaN(s.InnerV) || double.IsNaN(s.OuterV) ? "--" : $"{s.OuterV - s.InnerV:F2} V";

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

        /// <summary>本窗口一次轮询的快照；NaN 表示该值本次未读到。</summary>
        private sealed class MonitorSnapshot
        {
            public double Cell1 = double.NaN;
            public double Cell2 = double.NaN;
            public double Cell3 = double.NaN;
            public double Cell4 = double.NaN;
            public double InnerV = double.NaN;
            public double OuterV = double.NaN;
            public double Current = double.NaN;
            public double Soc = double.NaN;
            public double CellTemp1 = double.NaN;
            public double CellTemp2 = double.NaN;
            public double ChargeMosTemp = double.NaN;
            public double DischargeMosTemp = double.NaN;
            public double BuckTemp = double.NaN;
            public double BoardTemp = double.NaN;
            public double BigV = double.NaN;
            public double BigA = double.NaN;
            public double SmallV = double.NaN;
            public double SmallA = double.NaN;
            public bool UdsOk;
            public string UdsStatus = "未连接";
            public string UdsDetail = "";
            public string PowerDetail = "";
        }
    }

    /// <summary>DataMonitorWindow 报文列表的一行。</summary>
    public sealed class CanFrameRow
    {
        public string Time { get; init; } = "";
        public string Direction { get; init; } = "";
        public string Id { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Length { get; init; } = "";
        public string Data { get; init; } = "";
    }
}
