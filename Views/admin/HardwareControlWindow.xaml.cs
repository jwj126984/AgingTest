using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgingTest.Models;

namespace AgingTest.Views
{
    public partial class HardwareControlWindow : Window
    {
        private static readonly Brush BrushOnline = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
        private static readonly Brush BrushOffline = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));

        /// <summary>
        /// 按菜单名缓存已创建的设备控制页。切页仅切换 Visibility，
        /// 控件不会从视觉树移除，故不触发 Unloaded，各设备的连接与当前状态得以保持；
        /// 窗口关闭时整树卸载，各 UC 的 Unloaded 断开各自设备连接。
        /// </summary>
        private readonly Dictionary<string, UIElement> _pages = new();

        public HardwareControlWindow()
        {
            InitializeComponent();
            DeviceComm.StateChanged += OnStateChanged;
            CanComm.StateChanged += OnStateChanged;
            Closed += (_, _) =>
            {
                DeviceComm.StateChanged -= OnStateChanged;
                CanComm.StateChanged -= OnStateChanged;
            };
            RefreshAllStates();
            ShowPage("多通道电子负载");
        }

        /// <summary>任一设备连接状态变化（DeviceComm 或 CanComm）时刷新全部指示。</summary>
        private void OnStateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshAllStates();
            else Dispatcher.Invoke(RefreshAllStates);
        }

        /// <summary>刷新顶栏总指示灯 + 左侧各设备菜单圆点。</summary>
        private void RefreshAllStates()
        {
            UpdateCommIndicator();
            DotLoad.Fill = DeviceComm.Loads.Any(l => l.Connected) ? BrushOnline : BrushOffline;
            DotN36100.Fill = DeviceComm.N36100.Any(d => d.Connected) ? BrushOnline : BrushOffline;
            DotN36600.Fill = DeviceComm.N36600.Any(d => d.Connected) ? BrushOnline : BrushOffline;
            bool rr = DeviceComm.RrPorts.Any(p => p.Connected);
            DotResistor.Fill = rr ? BrushOnline : BrushOffline;
            DotRelay.Fill = rr ? BrushOnline : BrushOffline;
            DotCan.Fill = CanComm.Cards.Any(c => c.IsOnline) ? BrushOnline : BrushOffline;
        }

        /// <summary>根据是否有设备连接，更新顶栏“通讯状态”指示灯。</summary>
        private void UpdateCommIndicator()
        {
            bool any = DeviceComm.AnyConnected || CanComm.Cards.Any(c => c.IsOnline);
            TxtComm.Text = any ? "设备已连接" : "通讯未连接";
            TxtComm.Foreground = any ? new SolidColorBrush(Color.FromRgb(0xD9, 0xF3, 0xE3)) : new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0xA8));
            CommDot.Fill = any ? new SolidColorBrush(Color.FromRgb(0x2F, 0xA8, 0x4F)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x20));
        }

        /// <summary>取（或首次创建）指定菜单对应的设备页并加入 PageHost。</summary>
        private UIElement? EnsurePage(string menuName)
        {
            if (_pages.TryGetValue(menuName, out var cached)) return cached;
            UIElement? page = menuName switch
            {
                "多通道电子负载" => new UC_LoadDevice(),
                "电阻模拟卡" => new UC_ResistorBoard(),
                "继电器板" => new UC_RelayBoard(),
                "大电流程控电源" => new UC_N36100Power(),
                "高精度程控电源" => new UC_N36600Power(),
                "CAN-800H 调试" => new UC_CanDebug(),
                _ => null
            };
            if (page == null) return null;
            _pages[menuName] = page;
            PageHost.Children.Add(page);
            return page;
        }

        /// <summary>显示指定页，其余页 Collapsed（保持实例与连接）。</summary>
        private void ShowPage(string menuName)
        {
            var page = EnsurePage(menuName);
            if (page == null)
            {
                MessageBox.Show($"{menuName}页面暂未实现", "提示", MessageBoxButton.OK);
                return;
            }
            foreach (UIElement child in PageHost.Children)
                child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                // Content 为 StackPanel（文字 + 状态圆点），取第一个 TextBlock 的文本作为菜单名
                string menuName = (btn.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
                HeaderText.Text = menuName;
                ShowPage(menuName);

                // 左侧菜单按钮高亮
                var parentPanel = btn.Parent as StackPanel;
                if (parentPanel != null)
                {
                    foreach (var child in parentPanel.Children)
                    {
                        if (child is Button menuBtn)
                        {
                            menuBtn.Background = Brushes.Transparent;
                        }
                    }
                }
                btn.Background = new SolidColorBrush(Color.FromRgb(216, 228, 240));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}
