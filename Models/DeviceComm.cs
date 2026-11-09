using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using Common.SerialportHelper;

namespace AgingTest.Models
{
    /// <summary>
    /// 【集中通讯管理】统一持有并管理所有被控设备的连接句柄与连接/断开操作。
    /// 硬件拓扑：
    ///   电子负载 GJDA-100-32 ×2（共用 1 路 RS485 串口，靠 2 个总线地址区分）
    ///   485总线 ×6（电阻模拟卡 + 继电器板共用；每总线 = 1 串口，挂 电阻卡地址1~4、继电器板地址17~20）
    ///   大电流程控电源 N36100 ×6（各自独立 IP/端口）
    ///   高精度程控电源 N36600 ×6（各自独立 IP/端口）
    ///   CAN卡 ZLG-800H ×6（由通讯设置窗口单独管理，主界面启动时经 ReadFullConfig 自动连接）
    /// 通讯设置窗口编辑并保存参数，主界面“连接设备/断开设备”调用 ConnectAll/DisconnectAll，
    /// 设备控制页通过本类获取连接状态与设备句柄。
    /// </summary>
    public static class DeviceComm
    {
        public const int LoadCount = 2;          // 电子负载台数
        public const int RrPortCount = 6;        // 电阻/继电器共用 485 总线数
        public const int ResistorAddrBase = 1;   // 每总线电阻卡地址 1~4
        public const int ResistorAddrCount = 4;
        public const int RelayAddrBase = 17;     // 每总线继电器板地址 17~20
        public const int RelayAddrCount = 4;
        public const int N36100Count = 6;        // 大电流电源台数
        public const int N36600Count = 6;        // 高精度电源台数

        private static readonly string ConfigPath = Path.Combine(Environment.CurrentDirectory, "Config.json");
        private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

        /// <summary>全局配置根对象（含 CanCardList / GjdaDevices / Comm），保存时整体写回以保留其它分区。</summary>
        public static Config Root { get; private set; } = new();

        // ==================== 运行时状态 ====================
        public class LoadRuntime
        {
            public GjdaDeviceItem Cfg { get; set; } = new();
            public GJDA10032? Handle { get; set; }
            public bool Connected => Handle != null;
        }
        /// <summary>电子负载运行时（DevId 1~2）。</summary>
        public static readonly List<LoadRuntime> Loads = new();

        /// <summary>电子负载共享 485 串口运行时（2 台负载共用）。</summary>
        public class LoadPortRuntime
        {
            public RrPortSetting Cfg { get; set; } = new();
            public SerialPort? Sp { get; set; }
            public bool Connected => Sp != null && Sp.IsOpen;
        }
        /// <summary>电子负载共享 485 串口。</summary>
        public static readonly LoadPortRuntime LoadPort = new();

        public class RrPort
        {
            public RrPortSetting Cfg { get; set; } = new();
            public ModbusRtuClient Mb { get; } = new();
            public bool Connected => Mb.IsOpen;
        }
        /// <summary>485 总线运行时（电阻/继电器共用，下标0~5）。</summary>
        public static readonly List<RrPort> RrPorts = new();

        public class N36100Device
        {
            public NetDeviceSetting Cfg { get; set; } = new();
            public N36100PowerClient Client { get; } = new();
            public bool Connected => Client.IsConnected;
        }
        public class N36600Device
        {
            public NetDeviceSetting Cfg { get; set; } = new();
            public N36600PowerClient Client { get; } = new();
            public bool Connected => Client.IsConnected;
        }
        /// <summary>大电流程控电源（6台）。</summary>
        public static readonly List<N36100Device> N36100 = new();
        /// <summary>高精度程控电源（6台）。</summary>
        public static readonly List<N36600Device> N36600 = new();

        /// <summary>任一设备连接状态变化时触发。</summary>
        public static event Action? StateChanged;
        public static void RaiseStateChanged() => StateChanged?.Invoke();

        /// <summary>任一被控设备已连接（主界面通讯状态指示灯用）。</summary>
        public static bool AnyConnected =>
            Loads.Any(l => l.Connected)
            || RrPorts.Any(p => p.Connected)
            || N36100.Any(d => d.Connected)
            || N36600.Any(d => d.Connected);

        /// <summary>取某 485 总线上某电阻卡的 Modbus 客户端（已设从站地址）。portIndex=1~6, addr=1~4。</summary>
        public static ModbusRtuClient ResistorMb(int portIndex, int addr)
        {
            var mb = RrPorts[portIndex - 1].Mb;
            mb.Slave = (byte)addr;
            return mb;
        }
        /// <summary>取某 485 总线上某继电器板的 Modbus 客户端（已设从站地址）。portIndex=1~6, addr=17~20。</summary>
        public static ModbusRtuClient RelayMb(int portIndex, int addr)
        {
            var mb = RrPorts[portIndex - 1].Mb;
            mb.Slave = (byte)addr;
            return mb;
        }

        // ================================================================
        // 配置读写
        // ================================================================
        private static Config LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return new Config();
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOpt) ?? new Config(); }
            catch { return new Config(); }
        }

        private static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Root, JsonOpt);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        /// <summary>从 Config.json 载入配置并初始化全部运行时。</summary>
        public static void LoadSettings()
        {
            Root = LoadConfig();

            // 电子负载（至少 2 台）
            Loads.Clear();
            foreach (var g in Root.GjdaDevices)
                Loads.Add(new LoadRuntime { Cfg = g });
            EnsureLoads();

            // 电子负载共享 485 串口
            Root.Comm.LoadPort ??= new RrPortSetting();
            LoadPort.Cfg = Root.Comm.LoadPort;

            // 485 总线（6 路）
            RrPorts.Clear();
            for (int i = 0; i < RrPortCount; i++)
            {
                var cfg = Root.Comm.RrPorts.Count > i ? Root.Comm.RrPorts[i] : null;
                if (cfg == null)
                {
                    cfg = new RrPortSetting();
                    Root.Comm.RrPorts.Add(cfg);
                }
                RrPorts.Add(new RrPort { Cfg = cfg });
            }

            // 大电流电源 N36100（6 台）
            N36100.Clear();
            for (int i = 0; i < N36100Count; i++)
            {
                var cfg = Root.Comm.N36100.Count > i ? Root.Comm.N36100[i] : null;
                if (cfg == null)
                {
                    cfg = new NetDeviceSetting();
                    Root.Comm.N36100.Add(cfg);
                }
                N36100.Add(new N36100Device { Cfg = cfg });
            }

            // 高精度电源 N36600（6 台）
            N36600.Clear();
            for (int i = 0; i < N36600Count; i++)
            {
                var cfg = Root.Comm.N36600.Count > i ? Root.Comm.N36600[i] : null;
                if (cfg == null)
                {
                    cfg = new NetDeviceSetting();
                    Root.Comm.N36600.Add(cfg);
                }
                N36600.Add(new N36600Device { Cfg = cfg });
            }
        }

        /// <summary>确保至少 2 台电子负载配置记录存在。</summary>
        public static void EnsureLoads()
        {
            for (int i = 1; i <= LoadCount; i++)
            {
                if (!Loads.Any(l => l.Cfg.DevId == i))
                {
                    var g = new GjdaDeviceItem { DevId = i, ComPort = "COM1", BaudRate = 9600, SlaveAddr = 1 };
                    Root.GjdaDevices.Add(g);
                    Loads.Add(new LoadRuntime { Cfg = g });
                }
            }
        }

        /// <summary>持久化通讯设置（485端口/电源），保留 CAN 卡与电子负载分区。</summary>
        public static void SaveCommSettings() => Save();

        /// <summary>保存单台电子负载通道参数。</summary>
        public static void SaveLoadChannels(int devId, List<GjdaChannelItem> channels)
        {
            var rt = Loads.FirstOrDefault(l => l.Cfg.DevId == devId);
            if (rt != null)
            {
                rt.Cfg.Channels = channels;
                Save();
            }
        }

        // ================================================================
        // 电子负载（GJDA-100-32 ×2，共用 1 路 485，双总线地址）
        // ================================================================
        public static string? ConnectLoad(int devId)
        {
            var rt = Loads.FirstOrDefault(l => l.Cfg.DevId == devId);
            if (rt == null) return "设备编号错误";
            if (rt.Connected) return null;
            try
            {
                // 共享 485 串口：首台连接时打开
                if (!LoadPort.Connected)
                {
                    try
                    {
                        LoadPort.Sp = new SerialPort(LoadPort.Cfg.ComPort, LoadPort.Cfg.Baud, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = 2000,
                            WriteTimeout = 1000
                        };
                        LoadPort.Sp.Open();
                    }
                    catch (Exception ex)
                    {
                        LoadPort.Sp = null;
                        return $"负载 485 串口 {LoadPort.Cfg.ComPort} 打开失败：{ex.Message}";
                    }
                }
                var h = new GJDA10032();
                h.AttachSharedPort(LoadPort.Sp!, rt.Cfg.SlaveAddr);
                if (!h.CheckConnect())
                {
                    h.Close();
                    return $"负载{devId}（地址 {rt.Cfg.SlaveAddr}）通讯检测失败！检查 485 接线、波特率、总线地址";
                }
                rt.Handle = h;
                RaiseStateChanged();
                return null;
            }
            catch (Exception ex)
            {
                return $"负载{devId}连接异常：{ex.Message}";
            }
        }

        public static void DisconnectLoad(int devId)
        {
            var rt = Loads.FirstOrDefault(l => l.Cfg.DevId == devId);
            if (rt == null) return;
            try { rt.Handle?.Close(); } catch { }
            rt.Handle = null;
            // 两台负载都断开后再关闭共享 485 串口
            if (!Loads.Any(l => l.Connected))
            {
                try { LoadPort.Sp?.Close(); } catch { }
                LoadPort.Sp?.Dispose();
                LoadPort.Sp = null;
            }
            RaiseStateChanged();
        }

        // ================================================================
        // 485 总线（电阻模拟卡 + 继电器板共用）
        // ================================================================
        public static string? ConnectRrPort(int portIndex)
        {
            var port = RrPorts[portIndex - 1];
            if (port.Connected) return null;
            try { port.Mb.Open(port.Cfg.ComPort, port.Cfg.Baud, (byte)1); RaiseStateChanged(); return null; }
            catch (Exception ex) { return $"485-{portIndex} 连接失败：{ex.Message}"; }
        }

        public static void DisconnectRrPort(int portIndex)
        {
            try { RrPorts[portIndex - 1].Mb.Close(); } catch { }
            RaiseStateChanged();
        }

        // ================================================================
        // 大电流电源 N36100 ×6
        // ================================================================
        public static string? ConnectN36100(int index)
        {
            var d = N36100[index - 1];
            if (d.Connected) return null;
            try { d.Client.Connect(d.Cfg.Ip, d.Cfg.Port); RaiseStateChanged(); return null; }
            catch (Exception ex) { return $"N36100-{index} 连接失败：{ex.Message}"; }
        }

        public static void DisconnectN36100(int index)
        {
            try { N36100[index - 1].Client.Disconnect(); } catch { }
            RaiseStateChanged();
        }

        // ================================================================
        // 高精度电源 N36600 ×6
        // ================================================================
        public static string? ConnectN36600(int index)
        {
            var d = N36600[index - 1];
            if (d.Connected) return null;
            try { d.Client.Connect(d.Cfg.Ip, d.Cfg.Port); RaiseStateChanged(); return null; }
            catch (Exception ex) { return $"N36600-{index} 连接失败：{ex.Message}"; }
        }

        public static void DisconnectN36600(int index)
        {
            try { N36600[index - 1].Client.Disconnect(); } catch { }
            RaiseStateChanged();
        }

        // ================================================================
        // 批量连接/断开
        // ================================================================
        /// <summary>连接全部被控设备（2负载 + 6个485 + 6+6电源）。返回错误信息列表。</summary>
        public static List<string> ConnectAll()
        {
            var errors = new List<string>();
            for (int i = 1; i <= LoadCount; i++)
            {
                var e = ConnectLoad(i);
                if (e != null) errors.Add(e);
            }
            for (int i = 1; i <= RrPortCount; i++)
            {
                var e = ConnectRrPort(i);
                if (e != null) errors.Add(e);
            }
            for (int i = 1; i <= N36100Count; i++)
            {
                var e = ConnectN36100(i);
                if (e != null) errors.Add(e);
            }
            for (int i = 1; i <= N36600Count; i++)
            {
                var e = ConnectN36600(i);
                if (e != null) errors.Add(e);
            }
            return errors;
        }

        /// <summary>断开全部被控设备。</summary>
        public static void DisconnectAll()
        {
            for (int i = 1; i <= LoadCount; i++) DisconnectLoad(i);
            for (int i = 1; i <= RrPortCount; i++) DisconnectRrPort(i);
            for (int i = 1; i <= N36100Count; i++) DisconnectN36100(i);
            for (int i = 1; i <= N36600Count; i++) DisconnectN36600(i);
        }
    }
}
