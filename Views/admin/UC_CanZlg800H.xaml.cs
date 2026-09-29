using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using AgingTest.NetCan;
using AgingTest.Models;

namespace AgingTest.Views
{
    public partial class UC_CanZlg800H : UserControl
    {
        // 保存12个CAN卡实例
        private readonly List<CanCardItem> _canCardList = new();
        private readonly string _jsonConfigPath = Path.Combine(Environment.CurrentDirectory, "Config.json");
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public UC_CanZlg800H()
        {
            InitializeComponent();
            Create12CanCards();
            LoadConfigFromJson(); //页面打开加载JSON回显
        }

        /// <summary>动态创建1~12号CAN卡UI卡片（只保留截图6行配置）</summary>
        private void Create12CanCards()
        {
            // ========== 先循环生成全部12个CAN卡片 ==========
            for (int i = 1; i <= 6; i++)
            {
                // 默认IP初始化：CAN1=192.168.0.10
                var defaultIp = NetCan800HNetConfig.GetPresetIpByDevNo(i);
                var card = new CanCardItem
                {
                    CanIndex = i,
                    IpAddress = defaultIp,
                    WorkMode = 0, // 默认客户端 0客户端,1服务器
                    LocalPort = 4001,
                    RemotePort = 8000
                };
                _canCardList.Add(card);
                // 卡片容器
                var groupBox = new GroupBox
                {
                    Header = $"CAN{i} - ZLG800H",
                    Width = 420,
                    Margin = new Thickness(8),
                    Tag = i
                };
                var stackMain = new StackPanel
                {
                    Margin = new Thickness(8)
                };
                // ========== 行1：协议 ==========
                var rowProto = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowProto.Children.Add(new TextBlock { Text = "协议：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var cbbProto = new ComboBox { Width = 120, Tag = i };
                cbbProto.Items.Add("CAN");
                cbbProto.Items.Add("CAN FD");
                cbbProto.SelectedIndex = 0;
                rowProto.Children.Add(cbbProto);
                stackMain.Children.Add(rowProto);
                // ========== 行2：CANFD加速（仅CAN FD时可用） ==========
                var rowFdAcc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowFdAcc.Children.Add(new TextBlock { Text = "CANFD加速：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var cbbFdAcc = new ComboBox { Width = 120, Tag = i, IsEnabled = false };
                cbbFdAcc.Items.Add("是");
                cbbFdAcc.Items.Add("否");
                cbbFdAcc.SelectedIndex = 0;
                rowFdAcc.Children.Add(cbbFdAcc);
                stackMain.Children.Add(rowFdAcc);
                // ========== 行3：工作模式（客户端/服务器） ==========
                var rowWorkMode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowWorkMode.Children.Add(new TextBlock { Text = "工作模式：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var cbbWorkMode = new ComboBox { Width = 120, Tag = i };
                cbbWorkMode.Items.Add("客户端"); // index0 →0
                cbbWorkMode.Items.Add("服务器"); // index1 →1
                cbbWorkMode.SelectedIndex = 0;
                rowWorkMode.Children.Add(cbbWorkMode);
                stackMain.Children.Add(rowWorkMode);
                // ========== 行4：本地端口 ==========
                var rowLocalPort = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowLocalPort.Children.Add(new TextBlock { Text = "本地端口：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var txtLocalPort = new TextBox { Width = 120, Text = "4001", Tag = i };
                rowLocalPort.Children.Add(txtLocalPort);
                stackMain.Children.Add(rowLocalPort);
                // ========== 行5：IP地址 ==========
                var rowIp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowIp.Children.Add(new TextBlock { Text = "ip地址：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var txtIp = new TextBox { Width = 140, Text = defaultIp, Tag = i };
                rowIp.Children.Add(txtIp);
                stackMain.Children.Add(rowIp);
                // ========== 行6：工作端口 ==========
                var rowWorkPort = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                rowWorkPort.Children.Add(new TextBlock { Text = "工作端口：", Width = 70, VerticalAlignment = VerticalAlignment.Center });
                var txtWorkPort = new TextBox { Width = 120, Text = "8000", Tag = i };
                rowWorkPort.Children.Add(txtWorkPort);
                stackMain.Children.Add(rowWorkPort);
                // ========== 按钮行：【保存】【连接】【断开】 ==========
                var row5 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                var btnSave = new Button
                {
                    Content = "保存",
                    Width = 90,
                    Background = new SolidColorBrush(Color.FromRgb(23, 162, 184)),
                    Foreground = Brushes.White,
                    Tag = i
                };
                btnSave.Click += BtnSaveSingleCard_Click;
                var btnConnect = new Button
                {
                    Content = "连接",
                    Width = 90,
                    Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                    Foreground = Brushes.White,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = i
                };
                btnConnect.Click += BtnCanConnect_Click;
                var btnDisconnect = new Button
                {
                    Content = "断开",
                    Width = 90,
                    Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                    Foreground = Brushes.White,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = i
                };
                btnDisconnect.Click += BtnCanDisconnect_Click;
                row5.Children.Add(btnSave);
                row5.Children.Add(btnConnect);
                row5.Children.Add(btnDisconnect);
                stackMain.Children.Add(row5);

                groupBox.Content = stackMain;
                WrapCanCards.Children.Add(groupBox);

                //===================== 事件绑定：协议切换，控制FD加速可见性 =====================
                cbbProto.SelectionChanged += (s, e) =>
                {
                    bool isCanFd = cbbProto.SelectedItem.ToString() == "CAN FD";
                    rowFdAcc.Visibility = isCanFd ? Visibility.Visible : Visibility.Collapsed;
                };
                //===================== 事件绑定：工作模式切换，控制IP、工作端口显示隐藏 =====================
                cbbWorkMode.SelectionChanged += (s, e) =>
                {
                    bool isClient = cbbWorkMode.SelectedItem.ToString() == "客户端";
                    rowIp.Visibility = isClient ? Visibility.Visible : Visibility.Collapsed;
                    rowWorkPort.Visibility = isClient ? Visibility.Visible : Visibility.Collapsed;
                };
            }
        }

        /// <summary>单卡片保存按钮，单独保存当前卡片配置到统一Config.json</summary>
        private void BtnSaveSingleCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int canId)
            {
                var groupBox = FindGroupBoxByCardIndex(canId);
                if (groupBox == null) return;
                var cardConfig = ReadCardUiValue(groupBox);
                SaveSingleCardConfig(cardConfig);
                MessageBox.Show($"CAN{canId} 配置已保存到JSON！", "保存成功");
            }
        }

        /// <summary>【新增】批量搜索设备并分配IP 按钮点击事件</summary>
        private async void BtnBatchResetIp_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            btn.IsEnabled = false;
            try
            {
                MessageBox.Show("开始广播搜索局域网NetCAN800H设备，请等待...\n程序需要管理员权限！", "提示");
                //1.搜索所有在线设备
                var devList = NetCan800HNetConfig.SearchDevices();
                if (devList.Count == 0)
                {
                    MessageBox.Show("未搜索到任何NetCAN800H设备！\n检查网线、同网段、供电，管理员权限运行程序", "错误");
                    return;
                }
                if (devList.Count > 12)
                {
                    MessageBox.Show($"搜索到{devList.Count}台设备，最多只处理前12台！");
                }
                //2.循环依次设置IP，设备1→192.168.0.10，设备2→11...
                int successCount = 0;
                for (int idx = 0; idx < devList.Count && idx < 12; idx++)
                {
                    int canNo = idx + 1;
                    var devInfo = devList[idx];
                    string targetIp = NetCan800HNetConfig.GetPresetIpByDevNo(canNo);
                    bool setOk = NetCan800HNetConfig.SetPresetIpByDevNo(devInfo.Mac, canNo, "88888");
                    if (setOk)
                    {
                        successCount++;
                        // 更新内存卡片
                        var card = _canCardList[canNo - 1];
                        card.IpAddress = targetIp;
                        // 回填UI
                        var gb = FindGroupBoxByCardIndex(canNo);
                        if (gb != null)
                        {
                            FillCardUiValue(gb, card);
                        }
                        //保存到JSON
                        SaveSingleCardConfig(card);
                    }
                    else
                    {
                        MessageBox.Show($"❌MAC:{devInfo.Mac} IP设置失败，请确认密码88888");
                    }
                    await Task.Delay(1200); //每台设备修改IP后等待网口重启
                }
                MessageBox.Show($"批量IP分配完成！成功{successCount}/{devList.Count}台\n设备网口重启，请等待2~3秒后再连接CAN", "完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"批量设置IP异常：{ex.Message}", "异常");
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private void BtnCanConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int canId)
            {
                var groupBox = FindGroupBoxByCardIndex(canId);
                if (groupBox == null) return;
                var cardConfig = ReadCardUiValue(groupBox);
                var card = _canCardList[canId - 1];
                // ========== 这里是CAN连接逻辑，连接成功后执行保存 ==========
                MessageBox.Show($"准备连接 CAN{canId} ZLG800H（网口）", "CAN操作");
                //bool connectOk = card.CanHandle.OpenNet(cardConfig.IpAddress, cardConfig.DevIndex, cardConfig.CanChannel, ...);
                bool connectOk = true; // 临时占位，后续替换成真实驱动返回值
                if (connectOk)
                {
                    card.IsConnected = true;
                    // ✅ 连接成功，自动保存当前卡片配置到JSON
                    SaveSingleCardConfig(cardConfig);
                    MessageBox.Show($"CAN{canId} ✅ 连接成功，配置已自动保存");
                }
                else
                {
                    MessageBox.Show($"CAN{canId} ❌ 连接失败");
                }
            }
        }

        private void BtnCanDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int canId)
            {
                var card = _canCardList[canId - 1];
                MessageBox.Show($"准备断开 CAN{canId} ZLG800H", "CAN操作");
                //card.CanHandle?.Close();
                card.IsConnected = false;
                MessageBox.Show($"CAN{canId} 已断开");
            }
        }

        #region JSON 读写【使用统一Config.json，保护GjdaDevices电子负载数据】
        /// <summary>保存单张CAN卡片配置，不破坏电子负载GjdaDevices</summary>
        private void SaveSingleCardConfig(CanCardItem updatedCard)
        {
            try
            {
                Config fullRootCfg;
                // 1.读取完整旧根配置，保留CanCardList不丢失
                if (File.Exists(_jsonConfigPath))
                {
                    string oldJson = File.ReadAllText(_jsonConfigPath);
                    fullRootCfg = JsonSerializer.Deserialize<Config>(oldJson, _jsonOptions) ?? new Config();
                }
                else
                {
                    fullRootCfg = new Config();
                }

                // 更新CanCardList：查找同编号覆盖，没有就新增
                var existItem = fullRootCfg.CanCardList.Find(x => x.CanIndex == updatedCard.CanIndex);
                if (existItem != null)
                {
                    existItem.IpAddress = updatedCard.IpAddress;
                    existItem.WorkMode = updatedCard.WorkMode;
                    existItem.LocalPort = updatedCard.LocalPort;
                    existItem.RemotePort = updatedCard.RemotePort;
                    existItem.CanFdAcc = updatedCard.CanFdAcc;
                    existItem.IsCanFd = updatedCard.IsCanFd;
                }
                else
                {
                    fullRootCfg.CanCardList.Add(updatedCard);
                }

                // 全部置false：运行状态不持久化
                foreach (var item in fullRootCfg.CanCardList)
                    item.IsConnected = false;

                // 写回完整Config.json
                string json = JsonSerializer.Serialize(fullRootCfg, _jsonOptions);
                File.WriteAllText(_jsonConfigPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败：{ex.Message}", "警告");
            }
        }

        /// <summary>页面加载读取Config.json，回填UI，GjdaDevices不会被触碰</summary>
        private void LoadConfigFromJson()
        {
            if (!File.Exists(_jsonConfigPath)) return;
            try
            {
                string json = File.ReadAllText(_jsonConfigPath);
                var fullRootCfg = JsonSerializer.Deserialize<Config>(json, _jsonOptions);
                if (fullRootCfg == null || fullRootCfg.CanCardList == null) return;

                foreach (var savedItem in fullRootCfg.CanCardList)
                {
                    var gb = FindGroupBoxByCardIndex(savedItem.CanIndex);
                    if (gb == null) continue;
                    FillCardUiValue(gb, savedItem);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载CAN配置失败：{ex.Message}", "警告");
            }
        }
        #endregion

        #region UI控件读取/回填工具方法
        /// <summary>从GroupBox读取卡片所有UI值，封装CanCardItem
        /// WorkMode：0客户端，1服务器
        /// IsCanFd：false普通CAN true CAN‑FD
        /// CanFdAcc：0=否，1=是
        /// UI下拉：CanFdAcc 索引0="是"(1)，索引1="否"(0)
        /// </summary>
        private CanCardItem ReadCardUiValue(GroupBox gb)
        {
            var item = new CanCardItem();
            if (gb.Tag is int canIdx)
                item.CanIndex = canIdx;
            if (gb.Content is StackPanel panel)
            {
                foreach (var row in panel.Children)
                {
                    if (row is StackPanel spRow)
                    {
                        foreach (var c in spRow.Children)
                        {
                            if (c is TextBox tb)
                            {
                                var tbHeader = spRow.Children[0] as TextBlock;
                                if (tbHeader == null) continue;
                                if (tbHeader.Text == "ip地址：")
                                {
                                    item.IpAddress = tb.Text;
                                }
                                if (tbHeader.Text == "本地端口：")
                                {
                                    int.TryParse(tb.Text, out int lp);
                                    item.LocalPort = lp;
                                }
                                if (tbHeader.Text == "工作端口：")
                                {
                                    int.TryParse(tb.Text, out int wp);
                                    item.RemotePort = wp;
                                }
                            }
                            else if (c is ComboBox cbb)
                            {
                                var tbHeader = spRow.Children[0] as TextBlock;
                                if (tbHeader == null) continue;

                                if (tbHeader.Text == "工作模式：")
                                {
                                    //0客户端，1服务器，SelectedIndex直接对应
                                    item.WorkMode = cbb.SelectedIndex;
                                }
                                if (tbHeader.Text == "协议：")
                                {
                                    //IsCanFd bool：false=CAN true=CAN‑FD
                                    item.IsCanFd = cbb.SelectedItem.ToString() == "CAN FD";
                                }
                                if (tbHeader.Text == "CANFD加速：")
                                {
                                    //UI索引0=是 →1；索引1=否→0
                                    item.CanFdAcc = cbb.SelectedIndex == 0 ? 1 : 0;
                                }
                            }
                        }
                    }
                }
            }
            return item;
        }

        /// <summary>把CanCardItem回填到UI控件
        /// WorkMode：0客户端，1服务器 → SelectedIndex
        /// IsCanFd：bool → 选择"CAN"/"CAN FD"
        /// CanFdAcc：1=是，0=否 → UI下拉索引0是"是"，1是"否"
        /// </summary>
        private void FillCardUiValue(GroupBox gb, CanCardItem item)
        {
            if (gb.Content is not StackPanel panel) return;
            foreach (var row in panel.Children)
            {
                if (row is StackPanel spRow)
                {
                    foreach (var c in spRow.Children)
                    {
                        if (c is TextBox tb)
                        {
                            var tbHeader = spRow.Children[0] as TextBlock;
                            if (tbHeader?.Text == "ip地址：")
                                tb.Text = item.IpAddress;
                            if (tbHeader?.Text == "本地端口：")
                                tb.Text = item.LocalPort.ToString();
                            if (tbHeader?.Text == "工作端口：")
                                tb.Text = item.RemotePort.ToString();
                        }
                        else if (c is ComboBox cbb)
                        {
                            var tbHeader = spRow.Children[0] as TextBlock;
                            if (tbHeader == null) continue;

                            if (tbHeader?.Text == "工作模式：")
                            {
                                cbb.SelectedIndex = item.WorkMode;
                            }
                            if (tbHeader?.Text == "协议：")
                            {
                                cbb.SelectedItem = item.IsCanFd ? "CAN FD" : "CAN";
                            }
                            if (tbHeader?.Text == "CANFD加速：")
                            {
                                //CanFdAcc=1(是)→索引0；0(否)→索引1
                                cbb.SelectedIndex = item.CanFdAcc == 1 ? 0 : 1;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>根据卡片编号找到GroupBox</summary>
        private GroupBox? FindGroupBoxByCardIndex(int canIndex)
        {
            foreach (var child in WrapCanCards.Children)
            {
                if (child is GroupBox gb && gb.Tag is int tag && tag == canIndex)
                {
                    return gb;
                }
            }
            return null;
        }
        #endregion
    }
}
