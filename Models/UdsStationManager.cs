using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AgingTest.Models
{
    /// <summary>
    /// 一个工位的诊断客户端（工位号 → CAN 盒 / 通道）。
    /// 每个工位的 UdsClient 独立过滤自己通道的 0x775/0x77D 报文，互不干扰。
    /// </summary>
    public class UdsStation : IDisposable
    {
        private readonly UdsSetting _cfg;

        public int StationNo { get; }
        public int CanIndex { get; }
        public int Channel { get; }
        public string ChannelText => $"CAN{CanIndex} CH{Channel}";
        public string Status { get; set; } = "未连接";

        public UdsClient? Client { get; private set; }
        public bool IsSessionOpen => Client?.IsSessionActive == true;
        public bool IsUnlocked => Client?.IsSecurityUnlocked == true;

        internal UdsStation(int stationNo, int canIndex, int channel, UdsSetting cfg)
        {
            StationNo = stationNo;
            CanIndex = canIndex;
            Channel = channel;
            _cfg = cfg;
        }

        /// <summary>创建（或复用）该工位的 UDS 客户端。CAN 通道需已在通讯设置页启动。</summary>
        public UdsClient Open(Action<string>? log = null)
        {
            if (Client != null) return Client;

            var client = new UdsClient(CanIndex, Channel, _cfg.RequestId, _cfg.ResponseId, _cfg.IsFd, _cfg.IsExt)
            {
                TimeoutMs = _cfg.TimeoutMs,
                AutoKeepAlive = false,   // 多工位由 UdsStationManager 单线程统一保活
            };
            if (log != null) client.Log += log;

            Client = client;
            Status = "已创建，待 10 03";
            return client;
        }

        /// <summary>10 03 + 算法库解锁。</summary>
        public void Prepare(string? algorithmPath = null, byte? level = null)
        {
            var client = Client ?? Open();
            BmsEolService.PrepareDiagnosisWithAlgorithm(client, algorithmPath, level ?? _cfg.SecurityLevel);
            Status = "已解锁";
        }

        public void Close()
        {
            Client?.Dispose();
            Client = null;
            Status = "未连接";
        }

        public void Dispose() => Close();

        public override string ToString() => $"工位{StationNo}（{ChannelText}）：{Status}";
    }

    /// <summary>
    /// 48 工位诊断管理器。默认映射：工位 n → CAN 盒 ((n-1)/StationsPerBox)+FirstCanIndex，
    /// 通道 ((n-1)%StationsPerBox)+FirstChannel；默认 StationsPerBox=8 时，工位 1~8 → CAN1 ch0~ch7，
    /// 9~16 → CAN2，依此类推（48 工位占 CAN1~CAN6，现场为 6 台 CAN 盒）。
    /// 所有已开会话的工位由一个后台线程统一发 3E 80 保活（S3=5000ms）。
    /// </summary>
    public class UdsStationManager : IDisposable
    {
        private readonly UdsSetting _cfg;
        private Thread? _keepAliveThread;
        private volatile bool _keepAliveRunning;

        public List<UdsStation> Stations { get; } = new();

        public UdsStationManager(UdsSetting? cfg = null)
        {
            _cfg = cfg ?? new UdsSetting();
            int stationsPerBox = Math.Max(1, _cfg.StationsPerBox);
            int count = Math.Max(1, _cfg.StationCount);

            for (int no = 1; no <= count; no++)
            {
                int boxOffset = (no - 1) / stationsPerBox;
                int channel = _cfg.FirstChannel + (no - 1) % stationsPerBox;
                int canIndex = _cfg.FirstCanIndex + boxOffset;

                if (channel < 0 || channel >= CanComm.ChannelCount)
                    throw new ArgumentOutOfRangeException(nameof(cfg),
                        $"工位 {no} 映射到通道 {channel}，超出 0~{CanComm.ChannelCount - 1}；请调整 StationsPerBox/FirstChannel");

                Stations.Add(new UdsStation(no, canIndex, channel, _cfg));
            }
        }

        public UdsStation? this[int stationNo] => Stations.FirstOrDefault(s => s.StationNo == stationNo);

        public UdsStation? FindByChannel(int canIndex, int channel)
            => Stations.FirstOrDefault(s => s.CanIndex == canIndex && s.Channel == channel);

        /// <summary>按老化工位“层-列”取诊断通道（WorkStation：6 层 × 8 列，工位号 = (层-1)*8 + 列）。</summary>
        public UdsStation? FindByWorkStation(int layer, int column, int columnsPerLayer = 8)
            => this[(layer - 1) * columnsPerLayer + column];

        /// <summary>打开全部工位客户端（不发报文，仅建立对象与报文过滤）。</summary>
        public void OpenAll(Action<string>? log = null)
        {
            foreach (var station in Stations) station.Open(log);
        }

        /// <summary>对指定工位执行 10 03 + 算法库解锁。</summary>
        public void Prepare(int stationNo, string? algorithmPath = null, byte? level = null)
        {
            var station = this[stationNo] ?? throw new ArgumentOutOfRangeException(nameof(stationNo), $"工位 {stationNo} 不存在");
            station.Prepare(algorithmPath, level);
        }

        /// <summary>单线程统一保活：每 intervalMs 对处于扩展会话的工位发 3E 80。</summary>
        public void StartKeepAlive(int intervalMs = 2000)
        {
            if (_keepAliveRunning) return;
            _keepAliveRunning = true;
            _keepAliveThread = new Thread(() =>
            {
                while (_keepAliveRunning)
                {
                    Thread.Sleep(intervalMs);
                    if (!_keepAliveRunning) break;

                    foreach (var station in Stations)
                    {
                        if (!station.IsSessionOpen || station.Client == null) continue;
                        try { station.Client.TesterPresent(); }
                        catch { /* 掉线/通道未启动，忽略；下次读取时会重新报错 */ }
                    }
                }
            })
            { IsBackground = true, Name = "UdsStationKeepAlive" };
            _keepAliveThread.Start();
        }

        public void StopKeepAlive()
        {
            _keepAliveRunning = false;
            _keepAliveThread?.Join(1000);
            _keepAliveThread = null;
        }

        public void CloseAll()
        {
            foreach (var station in Stations) station.Close();
        }

        public void Dispose()
        {
            StopKeepAlive();
            CloseAll();
        }
    }
}
