using AgingTest.Models;
using Common.SerialportHelper;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgingTest.Views
{
    /// <summary>
    /// 多通道电子负载（GJDA-100-32 ×4）控制页。
    /// 展示 32 通道实时采集数据并支持点击通道块设置负载参数。
    /// 通讯连接与串口/波特率/从站参数由 DeviceComm 统一管理（通讯设置窗口/主界面操作），
    /// 本页仅读取 DeviceComm 的连接状态与设备句柄。
    /// </summary>
    public partial class UC_LoadDevice : UserControl
    {
        private readonly DispatcherTimer[] _devTimers = new DispatcherTimer[5];

        public UC_LoadDevice()
        {
            InitializeComponent();
            // 配置与运行时由 MainWindow 启动时的 DeviceComm.LoadSettings() 统一加载，
            // 此处不要再调 LoadSettings()，否则会清空 Loads 重建，丢失已连接的句柄。
            DeviceComm.StateChanged += DeviceComm_StateChanged;
            for (int i = 1; i <= DeviceComm.LoadCount; i++)
            {
                _devTimers[i] = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                int devId = i;
                _devTimers[i].Tick += (_, _) => AutoReadData(devId);
            }
            RefreshStates();
            RenderAllChannels();
        }

        private DeviceComm.LoadRuntime? GetRuntime(int devId)
            => DeviceComm.Loads.FirstOrDefault(x => x.Cfg.DevId == devId);

        private void DeviceComm_StateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshStates();
            else Dispatcher.Invoke(RefreshStates);
        }

        /// <summary>刷新设备连接状态文字，并启停对应自动采集定时器。</summary>
        private void RefreshStates()
        {
            for (int i = 1; i <= DeviceComm.LoadCount; i++)
            {
                var rt = GetRuntime(i);
                bool conn = rt?.Connected == true;
                var tb = GetDevStatusTb(i);
                if (tb != null)
                {
                    tb.Text = conn ? $"设备状态：已连接，地址:{rt!.Cfg.SlaveAddr}" : "设备状态：未连接";
                    tb.Foreground = conn
                        ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                        : new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                }
                if (conn)
                {
                    if (!_devTimers[i].IsEnabled) _devTimers[i].Start();
                }
                else
                {
                    _devTimers[i].Stop();
                }
            }
        }

        private void RenderAllChannels()
        {
            for (int i = 1; i <= DeviceComm.LoadCount; i++) RenderDeviceChannels(i);
        }

        /// <summary>按保存的通道配置渲染某设备 32 个通道块（显示模式/电压/设定/附加）。</summary>
        private void RenderDeviceChannels(int devId)
        {
            var wrapPanel = GetDevWrapPanel(devId);
            if (wrapPanel == null) return;
            wrapPanel.Children.Clear();
            var rt = GetRuntime(devId);
            if (rt == null) return;
            for (int ch = 1; ch <= 32; ch++)
            {
                var savedCh = rt.Cfg.Channels.FirstOrDefault(c => c.ChNo == ch);
                int mode = savedCh?.Mode ?? 0;
                double vonVolt = savedCh?.VonVolt ?? 0;
                double setVal = savedCh?.SetValue ?? 0;
                int extra = savedCh?.ExtraParam ?? 0;
                var border = new Border
                {
                    Style = FindResource("DataTile") as Style,
                    Tag = new Tuple<int, int>(devId, ch)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = $"CH{ch}" });
                sp.Children.Add(new TextBlock { Text = $"模式:{mode}" });
                sp.Children.Add(new TextBlock { Text = $"电压:{vonVolt:F2}" });
                sp.Children.Add(new TextBlock { Text = $"设定:{setVal:F2}" });
                sp.Children.Add(new TextBlock { Text = $"附加:{extra}" });
                border.Child = sp;
                border.MouseDown += Tile_MouseDown;
                wrapPanel.Children.Add(border);
            }
        }

        /// <summary>定时自动采集：读取32通道实时数据并渲染。</summary>
        private void AutoReadData(int devId)
        {
            var rt = GetRuntime(devId);
            var wrapPanel = GetDevWrapPanel(devId);
            if (wrapPanel == null || rt == null || !rt.Connected || rt.Handle == null)
            {
                _devTimers[devId].Stop();
                return;
            }
            try
            {
                var res = rt.Handle.ReadAll32Channel();
                wrapPanel.Children.Clear();
                foreach (var ch in res.Channels)
                {
                    var tile = new Border { Style = FindResource("DataTile") as Style };
                    tile.Tag = new Tuple<int, int>(devId, ch.ChNo);
                    tile.MouseDown += Tile_MouseDown;
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = $"CH{ch.ChNo}", FontSize = 11 });
                    sp.Children.Add(new TextBlock { Text = $"U:{ch.Voltage:F2} V", FontSize = 12 });
                    sp.Children.Add(new TextBlock { Text = $"I:{ch.Current:F3} A", FontSize = 12 });
                    tile.Child = sp;
                    wrapPanel.Children.Add(tile);
                }
            }
            catch (Exception ex)
            {
                _devTimers[devId].Stop();
                var tb = GetDevStatusTb(devId);
                if (tb != null)
                {
                    tb.Text = "设备状态：通信异常";
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                }
                MessageBox.Show($"设备{devId}自动采集失败：{ex.Message}\n已停止自动刷新", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>点击通道块：打开单通道/批量负载参数设置。</summary>
        private void Tile_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border bd || bd.Tag is not Tuple<int, int> tagData) return;
            int devId = tagData.Item1;
            int chNo = tagData.Item2;
            var rt = GetRuntime(devId);
            if (rt == null) return;

            var savedCh = rt.Cfg.Channels.FirstOrDefault(c => c.ChNo == chNo);
            var win = new LoadSettingWindow();
            win.ClickChNo = chNo;
            if (savedCh != null)
            {
                win.OldConfig = new LoadParamResult
                {
                    ChNo = chNo,
                    ModeText = savedCh.Mode switch
                    {
                        0 => "CC 恒流_慢速",
                        1 => "CV 恒压",
                        2 => "CP 恒功率",
                        3 => "CR 恒电阻",
                        _ => "CC 恒流_慢速"
                    },
                    VonVolt = savedCh.VonVolt,
                    LoadValue = savedCh.SetValue,
                    ExtraParam = savedCh.ExtraParam
                };
            }

            var dialogRes = win.ShowDialog();
            if (dialogRes != true || win.Result == null) return;
            var para = win.Result;
            byte mode = para.ModeText switch
            {
                "CC 恒流_慢速" => 0,
                "CV 恒压" => 1,
                "CP 恒功率" => 2,
                "CR 恒电阻" => 3,
                _ => 0
            };
            var chParam = new GJDA10032.ChSetParam
            {
                Mode = mode,
                VonVolt = para.VonVolt,
                SetValue = para.LoadValue,
                ExtraParam = para.ExtraParam
            };

            // 未连接时仅保存配置，不向设备下发
            if (!rt.Connected || rt.Handle == null)
            {
                MessageBox.Show($"设备{devId}未连接，参数仅保存未下发，请先在通讯设置中连接", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            var channels = new System.Collections.Generic.List<GjdaChannelItem>(rt.Cfg.Channels);
            if (para.IsSingleChannel)
            {
                if (rt.Connected && rt.Handle != null) rt.Handle.SetSingleChannel(chNo, chParam, false);
                var exist = channels.FirstOrDefault(c => c.ChNo == chNo);
                if (exist != null)
                {
                    exist.Mode = mode; exist.VonVolt = para.VonVolt; exist.SetValue = para.LoadValue; exist.ExtraParam = para.ExtraParam;
                }
                else
                {
                    channels.Add(new GjdaChannelItem { ChNo = chNo, Mode = mode, VonVolt = para.VonVolt, SetValue = para.LoadValue, ExtraParam = para.ExtraParam });
                }
            }
            else
            {
                if (rt.Connected && rt.Handle != null)
                {
                    for (int ch = 1; ch <= 32; ch++) rt.Handle.SetSingleChannel(ch, chParam, false);
                }
                channels.Clear();
                for (int ch = 1; ch <= 32; ch++)
                {
                    channels.Add(new GjdaChannelItem { ChNo = ch, Mode = mode, VonVolt = para.VonVolt, SetValue = para.LoadValue, ExtraParam = para.ExtraParam });
                }
            }
            rt.Cfg.Channels = channels;
            DeviceComm.SaveLoadChannels(devId, channels);
            RenderDeviceChannels(devId);
        }

        private TextBlock? GetDevStatusTb(int devId)
            => devId switch
            {
                1 => TbDev1Status,
                2 => TbDev2Status,
                _ => null
            };

        private WrapPanel? GetDevWrapPanel(int devId)
            => devId switch
            {
                1 => WrapDev1Data,
                2 => WrapDev2Data,
                _ => null
            };

        // 连接由 DeviceComm 统一管理，本页卸载时仅停止采集定时器、取消订阅，不断开设备连接
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            for (int i = 1; i <= DeviceComm.LoadCount; i++) _devTimers[i]?.Stop();
            DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }
    }
}
