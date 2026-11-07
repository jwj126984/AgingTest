using AgingTest.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgingTest.Views
{
    /// <summary>
    /// CAN-800H 调试页：发送 CAN/CANFD 帧、实时接收监视、通道工具。
    /// 复用主界面已建立的连接（句柄经 CanComm 共享），不重复打开设备。
    /// </summary>
    public partial class UC_CanDebug : UserControl
    {
        private readonly ObservableCollection<CanComm.CanFrameInfo> _frames = new();
        private readonly object _lock = new();
        private readonly List<CanComm.CanFrameInfo> _pending = new();
        private readonly DispatcherTimer _drainTimer;
        private bool _paused;

        private const int MaxShown = 1000;

        private int CardIndex => (int)CmbCard.SelectedItem;
        private int Channel => (int)CmbCh.SelectedItem;

        public UC_CanDebug()
        {
            InitializeComponent();
            CmbCard.ItemsSource = Enumerable.Range(1, CanComm.CardCount).ToList();
            CmbCh.ItemsSource = Enumerable.Range(0, CanComm.ChannelCount).ToList();
            CmbCard.SelectedIndex = 0;
            CmbCh.SelectedIndex = 0;
            CmbCard.SelectionChanged += (_, _) => RefreshStatus();
            CmbCh.SelectionChanged += (_, _) => RefreshStatus();

            DgFrames.ItemsSource = _frames;
            CanComm.FrameReceived += CanComm_FrameReceived;
            CanComm.StateChanged += CanComm_StateChanged;

            _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _drainTimer.Tick += (_, _) => DrainPending();
            _drainTimer.Start();
            RefreshStatus();
        }

        // 后台接收线程回调：入队，由 UI 定时器批量上屏
        private void CanComm_FrameReceived(CanComm.CanFrameInfo info)
        {
            lock (_lock) _pending.Add(info);
        }

        private void CanComm_StateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshStatus();
            else Dispatcher.Invoke(RefreshStatus);
        }

        private void DrainPending()
        {
            List<CanComm.CanFrameInfo> batch;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                batch = new List<CanComm.CanFrameInfo>(_pending);
                _pending.Clear();
            }
            if (_paused) return;

            foreach (var f in batch)
            {
                _frames.Add(f);
                if (_frames.Count > MaxShown) _frames.RemoveAt(0);
            }
            TxtRecvCount.Text = _frames.Count.ToString();
        }

        // 鼠标位于页面任意子控件上方时，滚轮统一滚动外层 ScrollViewer（否则被 DataGrid 等子控件拦截）
        private void RootScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            double target = sv.VerticalOffset - e.Delta;
            target = Math.Max(0, Math.Min(target, sv.ScrollableHeight));
            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        }

        private void RefreshStatus()
        {
            bool online = CanComm.IsOnline(CardIndex);
            bool started = CanComm.IsChannelStarted(CardIndex, Channel);
            TxtOnline.Text = "在线状态：" + (online ? "在线" : "离线");
            TxtOnline.Foreground = online
                ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
            TxtChState.Text = $"通道{Channel} {(started ? "已启动" : "未启动")}";
        }

        // ==================== 发送 ====================
        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (!CanComm.IsOnline(CardIndex))
            {
                TxtSendResult.Text = "设备未连接";
                TxtSendResult.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                return;
            }
            try
            {
                if (!uint.TryParse(TxtId.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
                {
                    SetSendResult("ID 无效", false); return;
                }
                bool isExt = ChkExt.IsChecked == true;
                int type = CmbFrameType.SelectedIndex;
                bool isFd = type == 1;
                bool isRemote = type == 2;

                var data = new List<byte>();
                if (!isRemote)
                {
                    var tokens = TxtData.Text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var t in tokens)
                    {
                        if (!byte.TryParse(t.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                        { SetSendResult("数据字节无效：" + t, false); return; }
                        data.Add(b);
                    }
                    if (isFd && data.Count > 64) { SetSendResult("CANFD 数据最多 64 字节", false); return; }
                    if (!isFd && data.Count > 8) { SetSendResult("CAN2.0 数据最多 8 字节", false); return; }
                }

                bool ok = CanComm.Send(CardIndex, Channel, id, data.ToArray(), isExt, isRemote, isFd);
                SetSendResult(ok ? $"已发送 CAN{(isFd ? "FD" : "")} ID=0x{id:X} 长度={data.Count}" : "发送失败（通道未就绪）", ok);
            }
            catch (Exception ex)
            {
                SetSendResult("发送异常：" + ex.Message, false);
            }
        }

        private void SetSendResult(string msg, bool ok)
        {
            TxtSendResult.Text = msg;
            TxtSendResult.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
        }

        // ==================== 接收监视 ====================
        private void BtnToggleRecv_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            BtnToggleRecv.Content = _paused ? "恢复显示" : "暂停显示";
        }

        private void BtnClearView_Click(object sender, RoutedEventArgs e)
        {
            _frames.Clear();
            TxtRecvCount.Text = "0";
        }

        // ==================== 通道工具 ====================
        private void BtnClearBuf_Click(object sender, RoutedEventArgs e)
        {
            bool ok = CanComm.ClearBuffer(CardIndex, Channel);
            ShowToolResult("清空收发缓冲", ok);
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            bool ok = CanComm.ResetCan(CardIndex, Channel);
            ShowToolResult("重置CAN通道", ok);
        }

        private void BtnReadNum_Click(object sender, RoutedEventArgs e)
        {
            TxtRecvNum.Text = CanComm.GetReceiveNum(CardIndex, Channel, 0).ToString();
            TxtRecvNumFd.Text = CanComm.GetReceiveNum(CardIndex, Channel, 1).ToString();
        }

        private void ShowToolResult(string op, bool ok)
        {
            TxtToolResult.Text = op + (ok ? " 成功" : " 失败/通道未就绪");
            TxtToolResult.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _drainTimer?.Stop();
            CanComm.FrameReceived -= CanComm_FrameReceived;
            CanComm.StateChanged -= CanComm_StateChanged;
        }
    }
}
