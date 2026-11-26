using AgingTest.Models;
using demo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgingTest
{
    /// <summary>
    /// 奇瑞C1068 老化柜测试上位机 - 主界面
    /// 当前版本：界面功能 + 扫码绑定 + 测试流程模拟（设备通讯预留，后续接入）
    /// </summary>
    public partial class MainWindow : Window
    {
        // 工位布局：6层 × 8列 = 48工位
        private const int LAYERS = 6;
        private const int COLUMNS = 8;

        // TODO(条码规则): 工位码与产品码的区分规则待客户确认后调整（当前默认 工位码="POS"+编号）
        private const string StationBarcodePrefix = "POS";

        private readonly List<WorkStation> _stations = new();
        private readonly Dictionary<WorkStation, StationCard> _cards = new();
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _testTimer;
        private readonly Random _rnd = new();
        private WorkStation? _pendingStation;   // 已扫描工位码、等待产品码的工位
        private readonly UserInfo _currentUser;

        /// <summary>按层运行的充电/老化服务；点击“老化测试”后按已绑定工位启动。</summary>
        private readonly Dictionary<int, ChargeTestService> _agingServices = new();
        private bool _agingBusy;
        private bool _agingStopPending;

        private static string HexString(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2")));



        private static readonly string ConfigFilePath = Path.Combine(Environment.CurrentDirectory, "Config.json");
        private static readonly JsonSerializerOptions _jsonOpt = new JsonSerializerOptions { WriteIndented = true };
        public MainWindow(UserInfo user)
        {
            InitializeComponent();
            _currentUser = user;
            BuildStationGrid();
            RefreshStats();
            ApplyPermission(user.Role);

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => TxtClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _clockTimer.Start();

            _testTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _testTimer.Tick += TestTimer_Tick;

            ScanInput.Focus();
            AddLog($"系统就绪（当前用户：{user.RoleName}），请先扫描【工位码】绑定工位（先扫工位码，再扫产品码）");
            DeviceComm.LoadSettings();
            DeviceComm.StateChanged += DeviceComm_StateChanged;
            UpdateCommIndicator();
            Closed += (_, _) =>
            {
                DeviceComm.StateChanged -= DeviceComm_StateChanged;
                // 退出程序前尽量按逆序关闭仍在老化的硬件，避免大/小电源保持输出。
                var svcs = _agingServices.Values.ToList();
                if (svcs.Count > 0)
                {
                    Task.Run(() =>
                    {
                        foreach (var svc in svcs)
                        {
                            try { svc.StopAsync(_ => { }).Wait(3000); } catch { }
                        }
                    }).Wait(8000);
                }
                CanComm.Clear();
            };
            ReadFullConfig();
            UpdateTestButtons();
        }

        /// <summary>设备通讯状态变化时刷新顶栏指示灯。</summary>
        private void DeviceComm_StateChanged()
        {
            Dispatcher.Invoke(UpdateCommIndicator);
        }

        /// <summary>根据是否有设备连接，更新顶栏“通讯状态”指示灯。</summary>
        private void UpdateCommIndicator()
        {
            bool any = DeviceComm.AnyConnected;
            TxtComm.Text = any ? "设备已连接" : "通讯未连接";
            TxtComm.Foreground = any ? new SolidColorBrush(Color.FromRgb(0xD9, 0xF3, 0xE3)) : new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0xA8));
            CommDot.Fill = any ? new SolidColorBrush(Color.FromRgb(0x2F, 0xA8, 0x4F)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x20));
        }

        /// <summary>打开通讯设置窗口（设备通讯参数集中编辑/连接管理）。</summary>
        private void BtnCommSettings_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.CommunicationSettingsWindow { Owner = this };
            win.ShowDialog();
            UpdateCommIndicator();
            AddLog("[通讯设置] 通讯设置窗口已关闭");
        }

        /// <summary>连接全部被控设备。</summary>
        private void BtnConnectDevice_Click(object sender, RoutedEventArgs e)
        {
            var errors = Models.DeviceComm.ConnectAll();
            UpdateCommIndicator();
            if (errors.Count == 0)
            {
                AddLog("[连接设备] 全部设备连接成功");
                SetStatus("已连接全部设备");
                Hint("全部设备连接成功");
            }
            else
            {
                foreach (var err in errors) AddLog("[连接设备] " + err);
                AddLog($"[连接设备] 有 {errors.Count} 项设备连接失败");
                SetStatus($"设备连接：{errors.Count} 项失败");
                Hint("部分设备连接失败，详见日志");
            }
        }

        /// <summary>断开全部被控设备。</summary>
        private void BtnDisconnectDevice_Click(object sender, RoutedEventArgs e)
        {
            Models.DeviceComm.DisconnectAll();
            UpdateCommIndicator();
            AddLog("[断开设备] 已断开全部设备");
            SetStatus("已断开全部设备");
            Hint("已断开全部设备");
        }


        public void ReadFullConfig()
        {
            ///can卡连接
            {
                if (!File.Exists(ConfigFilePath))
                {
                    AddLog("Config.json配置文件不存在");
                    return;
                }

                List<CanCardItem>? canCardItemList = null;
                try
                {
                    string jsonText = File.ReadAllText(ConfigFilePath);
                    var cfg = JsonSerializer.Deserialize<Config>(jsonText, _jsonOpt);
                    canCardItemList = cfg?.CanCardList;
                }
                catch (Exception ex)
                {
                    AddLog($"读取JSON配置异常：{ex.Message}");
                    return;
                }

                if (canCardItemList == null || canCardItemList.Count == 0)
                {
                    AddLog("JSON中没有CAN卡配置");
                    return;
                }

                try
                {
                    // 遍历每一张CAN设备配置（CanIndex 1~6），单卡失败不影响其它卡
                    foreach (var card in canCardItemList)
                    {
                        try
                        {
                            var channels = card.EnsureChannels();
                            var enabled = channels.Where(c => c.Enabled).ToList();
                            if (enabled.Count == 0)
                            {
                                AddLog($"【CAN{card.CanIndex}】没有任何启用通道，跳过该设备");
                                continue;
                            }
                            if (enabled.All(c => string.IsNullOrWhiteSpace(c.IpAddress)) && card.WorkMode == 0)
                            {
                                AddLog($"【CAN{card.CanIndex}】通道 IP 配置为空，跳过该设备");
                                continue;
                            }
                            var (ok, err) = CanComm.OpenCard(card.CanIndex, card, AddLog);
                            AddLog($"【CAN{card.CanIndex}】{err}");
                            if (!ok) AddLog($"【CAN{card.CanIndex}】连接失败，继续下一台设备");
                            // 泵一次 UI 消息，保持窗口在批量连接期间可响应
                            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                        }
                        catch (Exception cardEx)
                        {
                            AddLog($"【CAN{card.CanIndex}】连接异常：{cardEx.Message}，继续下一张卡");
                        }
                    }

                    // 接收线程由 CanComm 在通道启动成功后自动拉起，这里只做一次兜底
                    CanComm.EnsureReceiveLoop();
                    AddLog("接收线程已启动");
                    AddLog("====全部CAN卡批量打开流程执行完毕====");
                }
                catch (Exception ex)
                {
                    AddLog($"批量打开CAN整体流程异常：{ex.Message}");
                    return;
                }
            }
            //电子负载连接
            { 
            }
        }






        // ==================== 权限管理（三级权限） ====================

        /// <summary>按角色应用界面权限：作业员 &lt; 技术员 &lt; 工程师</summary>
        private void ApplyPermission(UserRole role)
        {
            // 作业员不可修改测试/机种参数，技术员及以上可修改
            bool canEditParams = role != UserRole.Operator;
            foreach (var tb in new[] { TxtAgingTemp, TxtAgingHumidity, TxtAgingMinutes, TxtPollingMs, TxtRTSeconds })
            {
                tb.IsReadOnly = !canEditParams;
                tb.Background = canEditParams ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5));
                tb.ToolTip = canEditParams ? "可修改" : "仅技术员及以上可修改";
            }
            ChkAgingOven.IsEnabled = canEditParams;

            // 用户管理入口：仅工程师可见
            BtnUserManage.Visibility = role == UserRole.Engineer ? Visibility.Visible : Visibility.Collapsed;

            TxtUser.Text = $"当前用户：{_currentUser.RoleName}（{_currentUser.DisplayName}）";
            TxtUser.ToolTip = $"账号：{_currentUser.UserName}";
        }

        /// <summary>打开用户管理窗口（仅工程师入口可见）</summary>
        private void BtnUserManage_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.UserManageWindow(_currentUser) { Owner = this };
            win.ShowDialog();
            AddLog("[用户管理] 用户管理窗口已关闭");
        }
        private void BtnHardwareControl_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.HardwareControlWindow { Owner = this };
            win.ShowDialog();
            AddLog("[硬件控制] 硬件控制窗口已关闭");
        }
        /// <summary>退出登录：重新弹出登录窗口，成功后切换到新用户</summary>
        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            var login = new Views.LoginWindow();
            if (login.ShowDialog() == true && login.LoggedUser != null)
            {
                var newMain = new MainWindow(login.LoggedUser);
                Application.Current.MainWindow = newMain;
                newMain.Show();
                Close();   // 关闭旧主窗口（MainWindow 已指向新窗口，不会触发退出）
            }
        }

        // ==================== 工位网格构建 ====================

        /// <summary>点击工位卡片，打开该工位的数据监控详情窗口（非模态，可同时打开多个）</summary>
        private void OpenDetail(WorkStation ws)
        {
            var win = new Views.DataMonitorWindow(ws) { Owner = this };
            win.Show();
        }

        private void BuildStationGrid()
        {
            var grid = new Grid();
            for (int c = 0; c < COLUMNS; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < LAYERS; r++)
                grid.RowDefinitions.Add(new RowDefinition());

            for (int layer = 1; layer <= LAYERS; layer++)
            {
                for (int col = 1; col <= COLUMNS; col++)
                {
                    var ws = new WorkStation(layer, col);
                    _stations.Add(ws);

                    var card = CreateStationCard(ws);
                    Grid.SetRow(card.Root, layer - 1);
                    Grid.SetColumn(card.Root, col - 1);
                    grid.Children.Add(card.Root);
                    _cards[ws] = card;
                    // 点击工位卡片 → 打开数据监控详情窗口
                    card.Root.Cursor = Cursors.Hand;
                    card.Root.MouseLeftButtonUp += (_, _) => OpenDetail(ws);
                    UpdateCard(ws);
                }
            }
            StationGridHost.Content = grid;
        }

        private StationCard CreateStationCard(WorkStation ws)
        {
            var root = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = $"工位 {ws.Code}（{ws.Barcode}）"
            };

            var panel = new StackPanel();

            var txtCode = new TextBlock
            {
                Text = ws.Code,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x38, 0x64)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var txtState = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var txtProduct = new TextBlock
            {
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = "产品二维码"
            };
            var txtData = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(txtCode);
            panel.Children.Add(txtState);
            panel.Children.Add(txtProduct);
            panel.Children.Add(txtData);
            root.Child = panel;

            return new StationCard(root, txtState, txtProduct, txtData);
        }

        private void UpdateCard(WorkStation ws)
        {
            var card = _cards[ws];
            var root = card.Root;

            (Brush bg, Brush border, string stateText, Brush stateColor) = ws.State switch
            {
                WorkStationState.Idle => ((Brush)FindResource("BrushIdle"), (Brush)FindResource("BorderIdle"), "空闲", Brushes.Gray),
                WorkStationState.Bound => ((Brush)FindResource("BrushBound"), (Brush)FindResource("BorderBound"), "已绑定", (Brush)FindResource("BorderBound")),
                WorkStationState.RTAging => ((Brush)FindResource("BrushRT"), (Brush)FindResource("BorderRT"), "常温测试", (Brush)FindResource("BorderRT")),
                WorkStationState.Aging => ((Brush)FindResource("BrushAging"), (Brush)FindResource("BorderAging"), "老化测试", (Brush)FindResource("BorderAging")),
                WorkStationState.Passed => ((Brush)FindResource("BrushPass"), (Brush)FindResource("BorderPass"), "通过", (Brush)FindResource("BorderPass")),
                WorkStationState.Failed => ((Brush)FindResource("BrushFail"), (Brush)FindResource("BorderFail"), "NG", (Brush)FindResource("BorderFail")),
                _ => (Brushes.White, Brushes.Gray, "未知", Brushes.Gray)
            };

            root.Background = bg;
            root.BorderBrush = border;
            // 已绑定/测试中/异常 加粗边框高亮区分
            root.BorderThickness = ws.State switch
            {
                WorkStationState.Idle or WorkStationState.Passed => new Thickness(1),
                _ => new Thickness(2.5)
            };

            card.StateText.Text = stateText;
            card.StateText.Foreground = stateColor;
            card.ProductText.Text = string.IsNullOrEmpty(ws.ProductCode) ? "未绑定" : ws.ProductCode;
            card.ProductText.Foreground = string.IsNullOrEmpty(ws.ProductCode) ? Brushes.Gray : Brushes.DarkSlateGray;

            card.DataText.Text = ws.State switch
            {
                WorkStationState.Aging =>
                    (ws.CellVoltage > 0
                        ? $"V:{ws.CellVoltage:F2}V  T:{ws.CellTemp:F1}℃  I:{ws.Current:F1}A\n"
                        : "") +
                    $"{(string.IsNullOrEmpty(ws.AgingPhaseText) ? $"{ws.AgingStepText} {ws.AgingStepRemainSec}s" : ws.AgingPhaseText)}  剩 {ws.RemainTime:hh\\:mm\\:ss}",
                WorkStationState.RTAging =>
                    $"V:{ws.CellVoltage:F2}V  T:{ws.CellTemp:F1}℃  I:{ws.Current:F1}A\n" +
                    $"内:{ws.InnerVoltage:F2}V 外:{ws.OuterVoltage:F2}V  剩 {ws.RemainTime:hh\\:mm\\:ss}",
                WorkStationState.Passed => $"PASS\n{(ws.TestStartTime.HasValue ? ws.TestStartTime.Value.ToString("HH:mm:ss") : "")}",
                WorkStationState.Failed => $"NG  {ws.FaultInfo}",
                WorkStationState.Bound => ws.BindTime.HasValue ? $"绑定 {ws.BindTime.Value:HH:mm:ss}" : "",
                _ => ""
            };
        }

        // ==================== 扫码绑定 ====================

        private void ScanInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var text = ScanInput.Text;
                ScanInput.Clear();
                if (!string.IsNullOrWhiteSpace(text))
                    HandleScan(text.Trim());
                e.Handled = true;
            }
        }

        private void HandleScan(string raw)
        {
            AddLog($"[扫码] {raw}");

            // 1) 尝试识别为工位码
            var pos = TryParseStationBarcode(raw);
            if (pos.HasValue)
            {
                var ws = _stations.FirstOrDefault(s => s.Layer == pos.Value.Layer && s.Column == pos.Value.Column);
                if (ws == null)
                {
                    Hint($"工位码 {raw} 无效（应在 POS1-1 ~ POS6-8 范围）");
                    _pendingStation = null;
                    return;
                }

                if (ws.State != WorkStationState.Idle)
                {
                    Hint($"工位 {ws.Code} 当前为【{StateName(ws.State)}】状态，空闲工位才可绑定");
                    _pendingStation = null;
                    return;
                }

                _pendingStation = ws;
                TxtPendingStation.Text = $"待绑定工位：{ws.Code}（{ws.Barcode}）→ 请扫描产品码";
                Hint($"已识别工位码 {ws.Barcode}，请扫描产品码");
                return;
            }

            // 2) 否则按产品码处理
            if (_pendingStation == null)
            {
                Hint("请先扫描【工位码】");
                return;
            }

            // TODO(条码规则): 产品码格式校验规则待客户确认（当前仅做非空/最小长度校验）
            if (raw.Length < 4)
            {
                Hint($"产品码 {raw} 长度异常，请检查扫码枪");
                return;
            }

            var target = _pendingStation;
            _pendingStation = null;

            target.ProductCode = raw;
            target.BindTime = DateTime.Now;
            target.State = WorkStationState.Bound;
            target.FaultInfo = "";

            TxtPendingStation.Text = "";
            Hint($"工位 {target.Code} 绑定产品 {raw} 完成");
            AddLog($"[绑定] 工位 {target.Code}（{target.Barcode}）← 产品 {raw}，时间 {target.BindTime:HH:mm:ss}");

            UpdateCard(target);
            RefreshStats();
        }

        /// <summary>
        /// 工位码解析。默认规则：POS + 编号，支持 "POS1-1"（层-列）或 "POS0108"（LLCC 两位层两位列）。
        /// </summary>
        private (int Layer, int Column)? TryParseStationBarcode(string raw)
        {
            var t = raw.Trim().ToUpperInvariant().Replace(" ", "");
            if (!t.StartsWith(StationBarcodePrefix, StringComparison.Ordinal))
                return null;

            var body = t.Substring(StationBarcodePrefix.Length);
            var m1 = Regex.Match(body, @"^(\d+)[-](\d+)$");
            if (m1.Success)
            {
                int l = int.Parse(m1.Groups[1].Value);
                int c = int.Parse(m1.Groups[2].Value);
                return (l >= 1 && l <= LAYERS && c >= 1 && c <= COLUMNS) ? (l, c) : null;
            }
            var m2 = Regex.Match(body, @"^(\d{2})(\d{2})$");
            if (m2.Success)
            {
                int l = int.Parse(m2.Groups[1].Value);
                int c = int.Parse(m2.Groups[2].Value);
                return (l >= 1 && l <= LAYERS && c >= 1 && c <= COLUMNS) ? (l, c) : null;
            }
            return null;
        }

        // ==================== 测试控制 ====================

        private void BtnRT_Click(object sender, RoutedEventArgs e)
        {
            var targets = _stations.Where(s => s.State == WorkStationState.Bound).ToList();
            if (targets.Count == 0)
            {
                Hint("没有已绑定的空闲工位，请先扫码绑定");
                return;
            }
            if (!int.TryParse(TxtRTSeconds.Text, out var sec) || sec < 5)
                sec = 60;

            foreach (var ws in targets)
            {
                ws.State = WorkStationState.RTAging;
                ws.TestType = "常温测试";
                ws.TestStartTime = DateTime.Now;
                ws.TestDuration = TimeSpan.FromSeconds(sec);
                ws.RemainTime = ws.TestDuration;
                ws.FaultInfo = "";
                ws.MosClosed = true;   // 带载条件：MOS 闭合
                UpdateCard(ws);
            }
            if (!_testTimer.IsEnabled) _testTimer.Start();
            AddLog($"[测试] 常温测试启动：{targets.Count} 个工位，时长 {sec} 秒");
            SetStatus($"常温测试进行中：{targets.Count} 个工位");
            RefreshStats();
            ScanInput.Focus();
        }

        private async void BtnAging_Click(object sender, RoutedEventArgs e)
        {
            if (_agingBusy || _agingStopPending || _agingServices.Count > 0)
            {
                Hint("老化测试正在启动/运行，请先停止当前老化");
                return;
            }

            var targets = _stations.Where(s => s.State == WorkStationState.Bound).ToList();
            if (targets.Count == 0)
            {
                Hint("没有已绑定的空闲工位，请先扫码绑定");
                return;
            }
            if (!double.TryParse(TxtAgingTemp.Text, out var temp) || temp < 0)
                temp = 80;
            if (!double.TryParse(TxtAgingHumidity.Text, out var humidity) || humidity is < 0 or > 100)
                humidity = 50;
            if (!int.TryParse(TxtAgingMinutes.Text, out var minutes) || minutes < 1)
                minutes = 120;

            // 主界面输入的温度/湿度/时长直接写入老化工艺参数，充电流程服务按层读取。
            var cfg = DeviceComm.Root.Comm.ChargeTest ??= new ChargeTestSetting();
            cfg.OvenTemperature = temp;
            cfg.OvenHumidity = humidity;
            cfg.OvenEnabled = ChkAgingOven.IsChecked == true;
            DeviceComm.SaveCommSettings();

            var duration = TimeSpan.FromMinutes(minutes);
            foreach (var ws in targets)
            {
                ws.State = WorkStationState.Aging;
                ws.TestType = "老化测试";
                ws.TestStartTime = DateTime.Now;
                ws.TestDuration = duration;
                ws.RemainTime = duration;
                ws.FaultInfo = "";
                ws.DtcInfo = "无";
                ws.MosClosed = false;                 // 硬件启动成功后再置为闭合
                ws.AgingStep = AgingStep.PowerOn;
                ws.AgingStepRemainSec = (int)duration.TotalSeconds;
                ws.AgingPhaseText = "启动中";
                UpdateCard(ws);
            }

            _agingBusy = true;
            UpdateTestButtons();
            SetStatus($"老化测试启动中：{targets.Count} 个工位，正在按层执行上电流程……");
            AddLog($"[老化] 启动：{targets.Count} 个已绑定工位，温度 {temp:F1}℃ / 湿度 {humidity:F0}% / 时长 {minutes} 分钟");
            RefreshStats();

            try
            {
                // 按层分组，逐层执行：负载/电源/继电器/电阻/CAN唤醒/UDS/MOS/电流校验/烘箱
                foreach (var group in targets.GroupBy(s => s.Layer).OrderBy(g => g.Key))
                {
                    int layer = group.Key;
                    var maps = group.Select(ws => new ChargeStationMap(ws, cfg.IsDualStation(ws.StationNo))).ToList();
                    var svc = new ChargeTestService(cfg);

                    AddLog($"[老化] 第 {layer} 层开始上电：{maps.Count} 个工位");
                    await Task.Run(() => svc.StartAsync(layer, maps, AddLog));

                    _agingServices[layer] = svc;
                    foreach (var ws in group)
                    {
                        ws.MosClosed = true;
                        ws.AgingPhaseText = "充电老化中";
                        UpdateCard(ws);
                    }
                }

                _testTimer.Start();
                SetStatus($"老化测试进行中：{targets.Count} 个工位，目标温度 {temp:F1}℃ / 湿度 {humidity:F0}%");
                AddLog($"[老化] 全部层上电校验通过，进入老化计时：{minutes} 分钟");
            }
            catch (Exception ex)
            {
                AddLog($"[老化] 启动失败：{ex.Message}");
                var errors = await StopAgingServicesAsync();
                foreach (var ws in targets)
                {
                    ws.State = WorkStationState.Bound;
                    ws.TestType = "";
                    ws.TestStartTime = null;
                    ws.TestDuration = null;
                    ws.RemainTime = null;
                    ws.MosClosed = false;
                    ws.AgingPhaseText = "";
                    ws.AgingStepRemainSec = 0;
                    ws.FaultInfo = errors.Count > 0 ? "启动失败且回退异常" : "";
                    UpdateCard(ws);
                }
                MessageBox.Show(ex.Message, "老化测试启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _agingBusy = false;
                UpdateTestButtons();
                RefreshStats();
                ScanInput.Focus();
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_agingBusy || _agingStopPending)
            {
                Hint("正在启动/停止硬件流程，请稍候……");
                return;
            }

            var running = _stations.Where(s => s.State is WorkStationState.RTAging or WorkStationState.Aging).ToList();
            if (running.Count == 0)
            {
                Hint("当前没有正在进行的测试");
                return;
            }

            _agingBusy = true;
            UpdateTestButtons();
            SetStatus($"正在停止 {running.Count} 个工位……");
            List<string> errors = new();
            try
            {
                errors = await StopAgingServicesAsync();
            }
            finally
            {
                foreach (var ws in running)
                {
                    AddLog($"[停止] 工位 {ws.Code} {ws.TestType} 已手动停止");
                    if (ws.State == WorkStationState.Aging && errors.Count > 0)
                    {
                        // 硬件停止流程异常：保持 NG，提示人工检查，不能当作正常完成
                        ws.State = WorkStationState.Failed;
                        ws.FaultInfo = "停止流程异常，请检查设备";
                    }
                    else
                    {
                        ws.State = WorkStationState.Bound;
                        ws.TestType = "";
                        ws.TestStartTime = null;
                        ws.TestDuration = null;
                        ws.RemainTime = null;
                        ws.MosClosed = false;
                    }
                    ws.AgingPhaseText = "";
                    ws.AgingStepRemainSec = 0;
                    UpdateCard(ws);
                }
                if (!_stations.Any(s => s.State is WorkStationState.RTAging or WorkStationState.Aging))
                    _testTimer.Stop();
                _agingBusy = false;
                UpdateTestButtons();
                SetStatus($"已停止 {running.Count} 个工位的测试");
                Hint($"已停止 {running.Count} 个工位的测试");
                RefreshStats();
                ScanInput.Focus();
            }
        }

        private async Task<List<string>> StopAgingServicesAsync()
        {
            var errors = new List<string>();
            foreach (var kv in _agingServices.OrderByDescending(k => k.Key).ToList())
            {
                try
                {
                    var svc = kv.Value;
                    await Task.Run(() => svc.StopAsync(AddLog));
                }
                catch (Exception ex)
                {
                    errors.Add($"第 {kv.Key} 层停止失败：{ex.Message}");
                    AddLog($"[停止] 第 {kv.Key} 层停止失败：{ex.Message}");
                }
            }
            _agingServices.Clear();
            return errors;
        }

        private void UpdateTestButtons()
        {
            BtnAging.IsEnabled = !_agingBusy && _agingServices.Count == 0;
            BtnStop.IsEnabled = !_agingBusy;
            BtnRT.IsEnabled = !_agingBusy && _agingServices.Count == 0;
            ScanInput.IsEnabled = !_agingBusy;
        }

        private void TestTimer_Tick(object? sender, EventArgs e)
        {
            bool anyRunning = false;
            var completedAging = new List<WorkStation>();

            foreach (var ws in _stations)
            {
                if (ws.State is not (WorkStationState.RTAging or WorkStationState.Aging))
                    continue;

                anyRunning = true;

                // 常温测试仍走模拟；老化测试由真实充电流程驱动，这里只做总时长倒计时。
                if (ws.State == WorkStationState.RTAging)
                    RTAgingTick(ws);

                ws.RemainTime = (ws.RemainTime ?? TimeSpan.Zero) - TimeSpan.FromSeconds(1);
                if (ws.RemainTime <= TimeSpan.Zero)
                {
                    ws.RemainTime = TimeSpan.Zero;
                    if (ws.State == WorkStationState.Aging)
                    {
                        if (ws.AgingPhaseText != "停止中")
                        {
                            ws.AgingPhaseText = "停止中";
                            completedAging.Add(ws);
                        }
                    }
                    else
                    {
                        // ---- 常温测试完成判定（模拟：95% PASS，5% NG 演示报警）----
                        bool pass = _rnd.NextDouble() < 0.95;
                        ws.State = pass ? WorkStationState.Passed : WorkStationState.Failed;
                        if (!pass)
                        {
                            ws.FaultInfo = "模拟异常（电压超限）";
                            ws.DtcInfo = "P0560";
                        }
                        AddLog($"[完成] 工位 {ws.Code} {ws.TestType} 结束：{(pass ? "PASS" : "NG")}");
                        if (!pass) Hint($"工位 {ws.Code} 测试 NG，请检查！");
                    }
                }
                UpdateCard(ws);
            }

            if (completedAging.Count > 0 && !_agingStopPending)
                _ = CompleteAgingAsync(completedAging);

            if (!anyRunning)
            {
                _testTimer.Stop();
                SetStatus("全部测试已完成，等待下一轮");
            }
            RefreshStats();
        }

        /// <summary>老化计时结束：先按“断MOS→关继电器→关大电源→关小电源”的顺序停机，再判定结果。</summary>
        private async Task CompleteAgingAsync(List<WorkStation> completed)
        {
            _agingStopPending = true;
            try
            {
                AddLog($"[老化] 计时结束，开始逆序停机：{completed.Count} 个工位");
                SetStatus("老化计时结束，正在逆序停机……");
                var errors = await StopAgingServicesAsync();

                foreach (var ws in completed)
                {
                    ws.RemainTime = TimeSpan.Zero;
                    ws.AgingPhaseText = "";
                    ws.MosClosed = false;
                    if (errors.Count == 0)
                    {
                        ws.State = WorkStationState.Passed;
                        ws.FaultInfo = "";
                        AddLog($"[完成] 工位 {ws.Code} 老化结束：PASS");
                    }
                    else
                    {
                        ws.State = WorkStationState.Failed;
                        ws.FaultInfo = "停机异常，请检查设备";
                        AddLog($"[完成] 工位 {ws.Code} 老化结束：停机异常 NG");
                    }
                    UpdateCard(ws);
                }

                if (!_stations.Any(s => s.State is WorkStationState.RTAging or WorkStationState.Aging))
                {
                    _testTimer.Stop();
                    SetStatus(errors.Count == 0 ? "老化测试完成，等待下一轮" : "老化测试结束，但停机存在异常");
                }
                RefreshStats();
            }
            finally
            {
                _agingStopPending = false;
                UpdateTestButtons();
            }
        }

        // ==================== 老化流程 ====================
        // 老化已改为真实设备流程：由 ChargeTestService 按层执行上电/唤醒/MOS/电流校验/烘箱，
        // 主界面只负责总时长倒计时和到点逆序停机；不再使用原先的 12 步模拟循环。

        /// <summary>常温测试每秒模拟（简单恒温25℃带载）</summary>
        private void RTAgingTick(WorkStation ws)
        {
            ws.CellVoltage = 3.2 + _rnd.NextDouble() * 1.0;
            ws.CellTemp = 25 + (_rnd.NextDouble() - 0.5) * 2;
            ws.InnerVoltage = 12.0 + (_rnd.NextDouble() - 0.5) * 1.0;
            ws.OuterVoltage = ws.InnerVoltage + (_rnd.NextDouble() - 0.5) * 0.2;
            ws.Current = _rnd.NextDouble() * 8.0;
            ws.DtcInfo = "无";
            ws.FaultInfo = "";
        }

        // ==================== UI 辅助 ====================

        private void Hint(string msg) => TxtScanHint.Text = msg;

        private void SetStatus(string msg) => TxtStatus.Text = msg;

        internal void AddLog(string msg)
        {
            this.Dispatcher.Invoke(() => {
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
                while (LogList.Items.Count > 200)
                    LogList.Items.RemoveAt(LogList.Items.Count - 1);
            });
        }

        private void RefreshStats()
        {
            TxtStatBound.Text = $"已绑定 {_stations.Count(s => s.State == WorkStationState.Bound)}";
            TxtStatRT.Text = $"常温 {_stations.Count(s => s.State == WorkStationState.RTAging)}";
            TxtStatAging.Text = $"老化 {_stations.Count(s => s.State == WorkStationState.Aging)}";
            TxtStatPass.Text = $"通过 {_stations.Count(s => s.State == WorkStationState.Passed)}";
            TxtStatFail.Text = $"NG {_stations.Count(s => s.State == WorkStationState.Failed)}";
        }

        private static string StateName(WorkStationState s) => s switch
        {
            WorkStationState.Idle => "空闲",
            WorkStationState.Bound => "已绑定",
            WorkStationState.RTAging => "常温测试中",
            WorkStationState.Aging => "老化测试中",
            WorkStationState.Passed => "通过",
            WorkStationState.Failed => "NG",
            _ => "未知"
        };

        /// <summary>点击非输入框区域时，将焦点还给扫码输入框（模拟扫码枪连续扫码场景）</summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is TextBox)
                return;
            if (!ScanInput.IsKeyboardFocusWithin)
                ScanInput.Focus();
        }

        /// <summary>工位卡片 UI 元素集合</summary>
        private class StationCard
        {
            public Border Root { get; }
            public TextBlock StateText { get; }
            public TextBlock ProductText { get; }
            public TextBlock DataText { get; }

            public StationCard(Border root, TextBlock stateText, TextBlock productText, TextBlock dataText)
            {
                Root = root;
                StateText = stateText;
                ProductText = productText;
                DataText = dataText;
            }
        }
    }
}
