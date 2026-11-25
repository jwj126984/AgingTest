using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// 充电测试页（以一层 8 工位为单位）。
    /// 工位↔继电器板/电阻卡/负载/CAN/电源的默认映射见 ChargeStationMap；
    /// 表格中的“双工位”可现场修改，修改结果写回 Config.json 的 ChargeTest.DualStationNos。
    /// </summary>
    public partial class UC_ChargeTest : UserControl
    {
        private ChargeTestSetting _cfg = new();
        private ChargeTestService? _svc;
        private readonly ObservableCollection<StationRow> _rows = new();
        private int _currentLayer = 1;
        private bool _busy;
        private bool _loading;

        public UC_ChargeTest()
        {
            InitializeComponent();
            GridStations.ItemsSource = _rows;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg = DeviceComm.Root.Comm.ChargeTest ??= new ChargeTestSetting();

            _loading = true;
            CmbLayer.ItemsSource = Enumerable.Range(1, 6).ToList();
            CmbLayer.SelectedIndex = 0;
            LoadConfigToUi();
            _loading = false;

            RefreshStations(1);
            UpdateButtons();
            Log("充电测试页已加载。设备连接请在“通讯设置”完成；本页只执行流程控制。");
        }

        // ================================================================
        // 界面 ↔ 配置
        // ================================================================
        private void LoadConfigToUi()
        {
            TxtLoadVon.Text = _cfg.LoadVonVolt.ToString("0.###");
            TxtLoadCurrent.Text = _cfg.LoadCurrentA.ToString("0.###");
            TxtSmallVolt.Text = _cfg.SmallSupplyVolt.ToString("0.###");
            TxtSmallLimit.Text = _cfg.SmallSupplyCurrentLimitA.ToString("0.###");
            TxtBigVolt.Text = _cfg.BigSupplyVolt.ToString("0.###");
            TxtBigLimit.Text = _cfg.BigSupplyCurrentLimitA.ToString("0.###");
            TxtPerStation.Text = _cfg.PerStationCurrentA.ToString("0.###");
            TxtTolerance.Text = _cfg.BigCurrentToleranceA.ToString("0.###");
            TxtSmallMax.Text = _cfg.SmallCurrentMaxA.ToString("0.###");
            TxtWakeTimeout.Text = _cfg.WakeTimeoutMs.ToString();
            TxtTempRes.Text = _cfg.CellTempResistanceKOhm.ToString("0.###");
            TxtOvenTemp.Text = _cfg.OvenTemperature.ToString("0.0");
            TxtOvenHum.Text = _cfg.OvenHumidity.ToString("0.0");
            TxtIndicatorCtl.Text = _cfg.IndicatorCtl.ToString();
            TxtIndicatorAddr.Text = _cfg.IndicatorRelayAddr.ToString();
            ChkOven.IsChecked = _cfg.OvenEnabled;
            ChkStopOven.IsChecked = _cfg.StopOvenOnFinish;
        }

        private void ReadUiToConfig()
        {
            _cfg.LoadVonVolt = Parse(TxtLoadVon, "负载 VON");
            _cfg.LoadCurrentA = Parse(TxtLoadCurrent, "负载限流");
            _cfg.SmallSupplyVolt = Parse(TxtSmallVolt, "小电源电压");
            _cfg.SmallSupplyCurrentLimitA = Parse(TxtSmallLimit, "小电源限流");
            _cfg.BigSupplyVolt = Parse(TxtBigVolt, "大电源电压");
            _cfg.BigSupplyCurrentLimitA = Parse(TxtBigLimit, "大电源限流");
            _cfg.PerStationCurrentA = Parse(TxtPerStation, "单工位目标电流");
            _cfg.BigCurrentToleranceA = Parse(TxtTolerance, "总电流允差");
            _cfg.SmallCurrentMaxA = Parse(TxtSmallMax, "小电源电流上限");
            _cfg.WakeTimeoutMs = (int)Parse(TxtWakeTimeout, "唤醒超时");
            _cfg.CellTempResistanceKOhm = Parse(TxtTempRes, "电芯温度电阻");
            _cfg.OvenTemperature = Parse(TxtOvenTemp, "烘箱温度");
            _cfg.OvenHumidity = Parse(TxtOvenHum, "烘箱湿度");
            _cfg.IndicatorCtl = (int)Parse(TxtIndicatorCtl, "指示灯 CTL");
            _cfg.IndicatorRelayAddr = (int)Parse(TxtIndicatorAddr, "指示灯继电器地址");

            if (_cfg.LoadCurrentA <= 0 || _cfg.SmallSupplyCurrentLimitA <= 0 || _cfg.PerStationCurrentA <= 0)
                throw new InvalidOperationException("电流参数必须大于 0");
            if (_cfg.CellTempResistanceKOhm <= 0 || _cfg.CellTempResistanceKOhm > 409.5)
                throw new InvalidOperationException("电芯温度电阻范围 0~409.5kΩ");
            if (_cfg.IndicatorCtl is < 0 or > 24)
                throw new InvalidOperationException("指示灯 CTL 范围 0~24");
            if (_cfg.IndicatorRelayAddr is < 1 or > 247)
                throw new InvalidOperationException("继电器地址范围 1~247");
            if (_cfg.WakeTimeoutMs < 500)
                throw new InvalidOperationException("唤醒超时至少 500ms");
            if (_cfg.OvenHumidity is < 0 or > 100)
                throw new InvalidOperationException("烘箱湿度范围 0~100%");

            _cfg.OvenEnabled = ChkOven.IsChecked == true;
            _cfg.StopOvenOnFinish = ChkStopOven.IsChecked == true;

            PersistDualSelections();
            DeviceComm.SaveCommSettings();
        }

        private static double Parse(TextBox box, string name)
        {
            if (!double.TryParse(box.Text.Trim(), out double v))
                throw new InvalidOperationException($"{name} 不是有效数字：{box.Text}");
            return v;
        }

        // ================================================================
        // 工位映射
        // ================================================================
        private void CmbLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbLayer.SelectedIndex < 0) return;
            PersistDualSelections();
            RefreshStations(CmbLayer.SelectedIndex + 1);
        }

        private void RefreshStations(int layer)
        {
            _currentLayer = layer;
            _rows.Clear();
            for (int col = 1; col <= 8; col++)
            {
                var ws = new WorkStation(layer, col);
                _rows.Add(new StationRow(ws, _cfg.IsDualStation(ws.StationNo)));
            }
            GrpStations.Header = $"工位与设备映射（第 {layer} 层）";
            UpdateSelectedCount();
        }

        private void PersistDualSelections()
        {
            if (_rows.Count == 0) return;
            _cfg.DualStationNos ??= new List<int>();
            _cfg.DualStationNos.RemoveAll(n => (n - 1) / 8 + 1 == _currentLayer);
            _cfg.DualStationNos.AddRange(_rows.Where(r => r.IsDual).Select(r => r.Station.StationNo));
        }

        private void UpdateSelectedCount()
        {
            int count = _rows.Count(r => r.IsSelected);
            TxtSelectedCount.Text = $"已选 {count} 工位";
        }

        private List<ChargeStationMap> BuildSelectedMaps()
        {
            var maps = _rows.Where(r => r.IsSelected)
                .Select(r => new ChargeStationMap(r.Station, r.IsDual))
                .ToList();
            if (maps.Count == 0) throw new InvalidOperationException("请至少勾选一个工位");
            return maps;
        }

        private void GridStations_CurrentCellChanged(object sender, EventArgs e) => UpdateSelectedCount();

        private void GridStations_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PersistDualSelections();
                UpdateSelectedCount();
            }));
        }

        // ================================================================
        // 启动 / 停止
        // ================================================================
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                ReadUiToConfig();
                var maps = BuildSelectedMaps();
                int layer = CmbLayer.SelectedIndex + 1;

                _busy = true;
                UpdateButtons();
                _svc = new ChargeTestService(_cfg);
                Log($"加载工艺参数并启动第 {layer} 层：{maps.Count} 工位");
                await Task.Run(() => _svc.StartAsync(layer, maps, Log));
            }
            catch (Exception ex)
            {
                Log("启动失败：" + ex.Message);
                MessageBox.Show(ex.Message, "充电测试启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _svc == null) return;
            try
            {
                _busy = true;
                UpdateButtons();
                await Task.Run(() => _svc.StopAsync(Log));
            }
            catch (Exception ex)
            {
                Log("停止失败：" + ex.Message);
                MessageBox.Show(ex.Message, "充电测试停止异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            bool active = _svc != null && (_svc.State == ChargeTestState.Aging || _svc.State == ChargeTestState.Starting || _svc.State == ChargeTestState.Stopping);
            BtnStart.IsEnabled = !_busy && !active;
            BtnStop.IsEnabled = !_busy && _svc != null && _svc.State == ChargeTestState.Aging;

            TxtSvcState.Text = _svc?.State switch
            {
                ChargeTestState.Starting => "启动中",
                ChargeTestState.Aging => "老化中",
                ChargeTestState.Stopping => "停止中",
                ChargeTestState.Failed => "异常（已回退）",
                _ => "空闲"
            };
            TxtSvcState.Foreground = _svc?.State switch
            {
                ChargeTestState.Aging => new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45)),
                ChargeTestState.Failed => new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)),
                ChargeTestState.Starting or ChargeTestState.Stopping => new SolidColorBrush(Color.FromRgb(0xF2, 0x70, 0x22)),
                _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
            };
        }

        // ================================================================
        // 日志与卸载保护
        // ================================================================
        private void Log(string msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(msg)));
                return;
            }
            LstLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            if (LstLog.Items.Count > 600) LstLog.Items.RemoveAt(LstLog.Items.Count - 1);
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // 关闭窗口/卸载页面时若仍在老化，强制按逆序关闭，避免输出无人监管。
            var svc = _svc;
            if (svc == null) return;
            if (svc.State is not (ChargeTestState.Aging or ChargeTestState.Starting)) return;

            try
            {
                Task.Run(() => svc.StopAsync(_ => { })).Wait(6000);
            }
            catch { }
        }

        public class StationRow : INotifyPropertyChanged
        {
            private bool _isDual;
            private bool _isSelected = true;

            public WorkStation Station { get; }

            public StationRow(WorkStation station, bool isDual)
            {
                Station = station;
                _isDual = isDual;
            }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }

            public bool IsDual
            {
                get => _isDual;
                set
                {
                    if (_isDual == value) return;
                    _isDual = value;
                    OnPropertyChanged(nameof(IsDual));
                    OnPropertyChanged(nameof(MapText));
                }
            }

            /// <summary>单/双通道组变化时刷新映射说明。</summary>
            public string MapText => new ChargeStationMap(Station, _isDual).MapText;

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged(string name)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
