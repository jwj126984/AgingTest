using AgingTest.Models;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgingTest.Views
{
    /// <summary>
    /// 烘箱控制器控制页（X4MCGSLDETYA120MV104.05WA，Modbus TCP Server 网口）。
    /// 状态：7990 运行状态 / 7991 当前温度 / 7992 湿度 / 8024 设定温度 / 8042~8043 累计运行时间 / 8034 段次 / 8012~8016 预计完成。
    /// 控制：保持寄存器 8010=1 启动 / 8010=2 停止，8100 定值温度、8101 定值湿度（×10，负温补码）；线圈 8000 启动、8001 停止、8029 暂停、8103 继续、8002 故障复位、8010 照明。
    /// </summary>
    public partial class UC_OvenControl : UserControl
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private bool _busy;

        public UC_OvenControl()
        {
            InitializeComponent();
            _timer.Tick += (_, _) => RefreshStatus();
            DeviceComm.StateChanged += OnCommStateChanged;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            _timer.Start();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            DeviceComm.StateChanged -= OnCommStateChanged;
        }

        private void OnCommStateChanged()
        {
            if (!IsLoaded) return;
            Dispatcher.BeginInvoke(new Action(RefreshStatus));
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

        // ==================== 状态刷新 ====================
        private async void RefreshStatus()
        {
            if (_busy) return;

            if (!DeviceComm.Oven.Connected)
            {
                DotOven.Fill = new SolidColorBrush(Color.FromRgb(0xC0, 0xC4, 0xCC));
                TxtConn.Text = "未连接（通讯设置里连接烘箱）";
                TxtConn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                TxtCurrentTemp.Text = "—";
                TxtSetTemp.Text = "—";
                TxtHumidity.Text = "—";
                TxtRunning.Text = "未知";
                TxtRunTime.Text = "—";
                TxtSection.Text = "—";
                TxtFinish.Text = "—";
                TxtLight.Text = "—";
                return;
            }

            _busy = true;
            var client = DeviceComm.Oven.Client;
            OvenController.OvenStatus st = null!;
            bool lightOk = false;
            bool lightOn = false;
            string error = null!;

            await Task.Run(() =>
            {
                try
                {
                    st = client.ReadStatus();
                    try { lightOn = client.ReadLight(); lightOk = true; } catch { }
                }
                catch (Exception ex) { error = ex.Message; }
            });

            _busy = false;

            if (st == null || error != null)
            {
                DotOven.Fill = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                TxtConn.Text = "通讯异常：" + (error ?? "无响应");
                TxtConn.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));
                return;
            }

            DotOven.Fill = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            TxtConn.Text = $"已连接 {DeviceComm.Oven.Cfg.Ip}:{DeviceComm.Oven.Cfg.Port} · 站号 {DeviceComm.Oven.Cfg.UnitId}";
            TxtConn.Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
            TxtTimestamp.Text = "更新于 " + st.Timestamp.ToString("HH:mm:ss");

            TxtCurrentTemp.Text = st.TempText;
            TxtSetTemp.Text = st.SetTempText;
            TxtHumidity.Text = st.HumidityText;
            TxtRunning.Text = st.RunningText;
            TxtRunning.Foreground = new SolidColorBrush(st.Running == true ? Color.FromRgb(0x28, 0xA7, 0x45) : Color.FromRgb(0x88, 0x88, 0x88));
            TxtRunTime.Text = $"{st.TotalRunHours} 时 {st.TotalRunMinutes} 分";
            TxtSection.Text = st.ProgramSection.ToString();
            TxtFinish.Text = st.EstimatedFinish?.ToString("MM-dd HH:mm") ?? "—";
            TxtLight.Text = lightOk ? (lightOn ? "开" : "关") : "—";
        }

        // ==================== 设定下发 ====================
        private void BtnSetTemp_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TxtInputTemp.Text.Trim(), out double t))
            {
                MessageBox.Show("温度值格式不正确", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Run("下发设定温度", () =>
            {
                DeviceComm.Oven.Client.SetTemperature(t);
                Log($"下发设定温度 {t:0.0} ℃（寄存器 8100 = {OvenController.ToRegister(t)}）");
            });
        }

        private void BtnSetHumidity_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TxtInputHumidity.Text.Trim(), out double h))
            {
                MessageBox.Show("湿度值格式不正确", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Run("下发设定湿度", () =>
            {
                DeviceComm.Oven.Client.SetHumidity(h);
                Log($"下发设定湿度 {h:0.0} %（寄存器 8101 = {OvenController.ToRegister(h)}）");
            });
        }

        // ==================== 运行控制 ====================
        private void BtnStart_Click(object sender, RoutedEventArgs e)
            => Run("启动运行", () => { DeviceComm.Oven.Client.Start(); Log("下发启动（线圈 8000）"); });

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (!DeviceComm.Oven.Connected)
            {
                MessageBox.Show("烘箱控制器未连接，请先在“通讯设置”中连接。", "停止运行", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = await Task.Run(() => DeviceComm.Oven.Client.StopAndVerify());
                Log("停止结果：" + result.message);
                if (!result.ok)
                    throw new InvalidOperationException(result.message);
            }
            catch (Exception ex)
            {
                Log("停止运行失败：" + ex.Message);
                MessageBox.Show("停止运行失败：" + ex.Message, "通讯错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshStatus();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
            => Run("暂停", () => { DeviceComm.Oven.Client.Pause(); Log("下发暂停（线圈 8029）"); });

        private void BtnResume_Click(object sender, RoutedEventArgs e)
            => Run("继续运行", () => { DeviceComm.Oven.Client.Resume(); Log("下发继续运行（线圈 8103）"); });

        private void BtnFaultReset_Click(object sender, RoutedEventArgs e)
            => Run("故障复位", () => { DeviceComm.Oven.Client.FaultReset(); Log("下发故障复位（线圈 8002）"); });

        private void BtnLightOn_Click(object sender, RoutedEventArgs e)
            => Run("照明开", () => { DeviceComm.Oven.Client.SetLight(true); Log("照明开（线圈 8010 = 1）"); });

        private void BtnLightOff_Click(object sender, RoutedEventArgs e)
            => Run("照明关", () => { DeviceComm.Oven.Client.SetLight(false); Log("照明关（线圈 8010 = 0）"); });

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
            => Run("程式跳段", () => { DeviceComm.Oven.Client.ProgramSkip(); Log("程式跳段（线圈 8006）"); });

        private void BtnHold_Click(object sender, RoutedEventArgs e)
            => Run("保持", () => { DeviceComm.Oven.Client.Hold(); Log("保持（线圈 8007）"); });

        private void BtnAutoTuneTemp_Click(object sender, RoutedEventArgs e)
            => Run("温度自整定", () => { DeviceComm.Oven.Client.AutoTuneTemperature(); Log("温度自整定（线圈 8014）"); });

        private void BtnAutoTuneHum_Click(object sender, RoutedEventArgs e)
            => Run("湿度自整定", () => { DeviceComm.Oven.Client.AutoTuneHumidity(); Log("湿度自整定（线圈 8015）"); });

        // ==================== 公共 ====================
        /// <summary>统一的连接检查 + 异常提示 + 刷新。</summary>
        private void Run(string action, Action body)
        {
            if (!DeviceComm.Oven.Connected)
            {
                MessageBox.Show("烘箱控制器未连接，请先在“通讯设置”中连接。", action, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Log($"{action}失败：{ex.Message}");
                MessageBox.Show($"{action}失败：{ex.Message}", "通讯错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshStatus();
        }

        private void Log(string text)
        {
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
            while (LogList.Items.Count > 200) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        }
    }
}
