using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.SerialportHelper;

namespace AgingTest.Models
{
    public enum ChargeTestState
    {
        Idle,
        Starting,
        Aging,
        Stopping,
        Failed
    }

    /// <summary>
    /// 老化测试充电流程（以一层 8 工位为单位），严格按现场工艺顺序：
    ///   负载 VON=2V/5A → 小电源 12.8V → 继电器唤醒 + CAN 网络管理唤醒 → 电阻 10kΩ →
    ///   大电源 14.4V（先不输出）→ 继电器上电通道 → UDS 强控 MOS 闭合 → 大电源输出 →
    ///   校验大电源总电流（5A/工位）且小电源电流≈0 → 烘箱设定并启动 → 指示灯。
    /// 停止按逆序：断 MOS 并回读确认 → 关继电器上电通道 → 关大电源 → 关小电源。
    /// 所有设备调用均复用 DeviceComm 的现有连接句柄；流程不自动连接设备，连接仍在通讯设置页完成。
    /// </summary>
    public class ChargeTestService
    {
        private readonly ChargeTestSetting _cfg;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<ChargeStationMap> _activeStations = new();
        private readonly Dictionary<int, UdsStation> _uds = new();

        private UdsStationManager? _udsMgr;
        private int _activeLayer;
        private CancellationTokenSource? _wakeCts;
        private Task? _wakeTask;
        private bool _ovenAcquired;

        public ChargeTestState State { get; private set; } = ChargeTestState.Idle;
        public int ActiveLayer => _activeLayer;
        public IReadOnlyList<ChargeStationMap> ActiveStations => _activeStations;

        public ChargeTestService(ChargeTestSetting cfg)
        {
            _cfg = cfg;
        }

        // ================================================================
        // 启动流程
        // ================================================================
        public async Task StartAsync(int layer, IReadOnlyList<ChargeStationMap> stations, Action<string> log)
        {
            await _gate.WaitAsync();
            try
            {
                if (State == ChargeTestState.Aging)
                    throw new InvalidOperationException("当前已有老化流程在运行，请先执行停止流程");
                if (stations.Count == 0)
                    throw new InvalidOperationException("未选择任何工位");

                Validate(layer, stations);
                _activeLayer = layer;
                _activeStations.Clear();
                _activeStations.AddRange(stations);
                _ovenAcquired = false;
                State = ChargeTestState.Starting;
                log($"===== 老化测试充电流程开始：第 {layer} 层，{stations.Count} 个工位 =====");

                try
                {
                    await RunStartSequenceAsync(stations, log);
                    State = ChargeTestState.Aging;
                    log("===== 上电校验通过，进入老化计时；周期唤醒从启动阶段持续发送，计时结束后由主界面按逆序关闭 =====");
                }
                catch
                {
                    log("流程异常，执行安全回退……");
                    try { await SafeRollbackAsync(log); }
                    catch (Exception rb) { log("安全回退异常：" + rb.Message); }
                    StopPeriodicWake(null);
                    CloseUds();
                    _activeStations.Clear();
                    State = ChargeTestState.Failed;
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // ================================================================
        // 停止流程（逆序）
        // ================================================================
        public async Task StopAsync(Action<string> log)
        {
            await _gate.WaitAsync();
            try
            {
                // 进入停止流程先停周期唤醒，避免停止 MOS/电源时还在持续唤醒 BMS。
                StopPeriodicWake(log);

                if (_activeStations.Count == 0)
                {
                    State = ChargeTestState.Idle;
                    log("当前没有正在运行的老化测试");
                    return;
                }

                State = ChargeTestState.Stopping;
                log($"===== 停止老化测试：第 {_activeLayer} 层，按逆序关闭 =====");

                bool mosOpen = await OpenMosAndVerifyAsync(_activeStations, log);
                if (!mosOpen)
                {
                    log("MOS 断开状态无法确认，立即紧急关闭大/小电源并断开继电器，请人工确认 MOS 状态！");
                    await EmergencyOffAsync(_activeStations, log);
                    throw new InvalidOperationException("MOS 断开状态无法确认，已紧急关闭电源，请人工检查");
                }

                // MOS 已确认断开，不再需要诊断会话，立即停止 3E 80 会话维持报文。
                try
                {
                    _udsMgr?.StopKeepAlive(log);
                    log("UDS 会话维持报文（3E 80）已停止");
                }
                catch (Exception ex) { log("UDS 会话维持停止失败：" + ex.Message); }

                foreach (var st in _activeStations)
                {
                    foreach (int ctl in st.PowerCtls)
                        TryRelay(st, ctl, false, log);
                }

                var big = BigSupply();
                if (big != null)
                {
                    big.SetOutputOnOff(false);
                    log($"大电源 N36190（{_activeLayer} 号）输出已关闭");
                }

                var small = SmallSupply();
                if (small != null)
                {
                    small.SetOutputOnOff(false);
                    log($"小电源 N36620（{_activeLayer} 号）输出已关闭");
                }

                SetIndicator(_activeStations, false, log);

                if (_ovenAcquired)
                {
                    _ovenAcquired = false;
                    try
                    {
                        // 同一台烘箱由多层共用：引用计数归零才真正停止；断链时 Modbus 层会自动重连。
                        DeviceComm.StopOvenForAging(_cfg.StopOvenOnFinish, log);
                    }
                    catch (Exception ex)
                    {
                        log("烘箱停止失败：" + ex.Message);
                        throw;
                    }
                }

                log("===== 老化测试停止完成 =====");
            }
            catch
            {
                State = ChargeTestState.Failed;
                throw;
            }
            finally
            {
                CloseUds();
                _activeStations.Clear();
                if (State != ChargeTestState.Failed) State = ChargeTestState.Idle;
                _gate.Release();
            }
        }

        // ================================================================
        // 启动顺序
        // ================================================================
        private async Task RunStartSequenceAsync(IReadOnlyList<ChargeStationMap> stations, Action<string> log)
        {
            var ct = CancellationToken.None;
            var big = BigSupply() ?? throw new InvalidOperationException($"大电源 N36190 {_activeLayer} 号未连接");
            var small = SmallSupply() ?? throw new InvalidOperationException($"小电源 N36620 {_activeLayer} 号未连接");

            // 0. 先确保大/小电源输出关闭，避免带载上电
            big.SetOutputOnOff(false);
            small.SetOutputOnOff(false);
            await Task.Delay(200, ct);

            // 1. 电子负载前提：VON=2V，恒流 5A（每个负载一次性设置全部 32 通道，不再按工位逐通道设置）
            log($"① 电子负载：全部通道 VON={_cfg.LoadVonVolt:0.###}V，限流={_cfg.LoadCurrentA:0.###}A");
            var loadParam = new GJDA10032.ChSetParam
            {
                Mode = 4,                       // CCFast
                VonVolt = _cfg.LoadVonVolt,
                SetValue = _cfg.LoadCurrentA,
                ExtraParam = 0
            };
            var configuredLoads = new HashSet<int>();
            foreach (var st in stations)
            {
                var load = DeviceComm.Loads.FirstOrDefault(l => l.Cfg.DevId == st.LoadDevId)?.Handle
                           ?? throw new InvalidOperationException($"负载{st.LoadDevId}未连接");
                if (!configuredLoads.Add(st.LoadDevId)) continue;

                load.SetAll32Channels(loadParam, false);
                log($"   负载{st.LoadDevId} → 全部 32 通道一次性设置完成（VON={_cfg.LoadVonVolt:0.###}V，限流={_cfg.LoadCurrentA:0.###}A）");
            }

            // 2. 小电源 12.8V 输出，给 BMS 供电
            log($"② 小电源 N36620：{_cfg.SmallSupplyVolt:0.###}V / 限流 {_cfg.SmallSupplyCurrentLimitA:0.###}A，输出 ON");
            small.SetVoltage(_cfg.SmallSupplyVolt);
            small.SetCurrent(_cfg.SmallSupplyCurrentLimitA);
            small.SetOutputOnOff(true);
            await Task.Delay(300, ct);

            // 3. 继电器唤醒 + CAN 网络管理唤醒
            log("③ 唤醒 BMS：继电器 CTL18/CTL20 + CAN 0x400 网络管理报文");
            foreach (var st in stations)
                await WakeOneAsync(st, stations, log);

            // 4. 电阻模拟电芯温度 10kΩ
            uint ohms = (uint)Math.Round(_cfg.CellTempResistanceKOhm * 1000);
            log($"④ 电阻模拟卡：{_cfg.CellTempResistanceKOhm:0.###}kΩ（写入 {ohms}Ω）");
            foreach (var st in stations)
            {
                var mb = DeviceComm.ResistorMb(st.ResistorPort, st.ResistorAddr);
                foreach (int reg in st.TempResistorRegisters)
                {
                    mb.WriteRegisters((ushort)reg, new[]
                    {
                        (ushort)((ohms >> 16) & 0xFFFF),
                        (ushort)(ohms & 0xFFFF)
                    });
                }
                await Task.Delay(200, ct);
                log($"   工位{st.Station.Code} → 电阻 485-{st.ResistorPort}/地址{st.ResistorAddr} {st.TempChannelText} = {ohms}Ω");
            }

            // 5. 大电源设定 14.4V，暂不输出
            log($"⑤ 大电源 N36190：{_cfg.BigSupplyVolt:0.###}V / 限流 {_cfg.BigSupplyCurrentLimitA:0.###}A，输出保持 OFF");
            big.SetVoltage(_cfg.BigSupplyVolt);
            big.SetCurrent(_cfg.BigSupplyCurrentLimitA);

            // 6. 依次打开继电器上电通道
            log("⑥ 打开继电器上电通道");
            foreach (var st in stations)
            {
                foreach (int ctl in st.PowerCtls)
                {
                    SetRelay(st, ctl, true);
                    log($"   工位{st.Station.Code} → CTL{ctl} 闭合");
                    await Task.Delay(50, ct);
                }
            }

            // 7. UDS 强控 MOS 闭合
            log("⑦ UDS 强控 MOS 闭合（2F 73 BA 03 00 07）");
            foreach (var st in stations)
            {
                var uds = EnsureUds(st, log);
                BmsEolService.StrongControlMosBuck(uds, true, true, false);
                log($"   工位{st.Station.Code} → MOS 强控闭合命令已发送");
                await Task.Delay(100, ct);
            }
            await Task.Delay(300, ct);

            // 8. 大电源输出
            log("⑧ 大电源 N36190 输出 ON");
            big.SetOutputOnOff(true);

            // 9. 指示灯（如配置了 CTL）
            SetIndicator(stations, true, log);

            // 10. 电流校验
            log($"⑨ 等待 {_cfg.CurrentCheckDelayMs}ms 后校验电流");
            await Task.Delay(_cfg.CurrentCheckDelayMs, ct);

            double expect = _cfg.PerStationCurrentA * stations.Count;
            var bigSamples = new List<double>();
            var smallSamples = new List<double>();

            for (int i = 0; i < Math.Max(1, _cfg.CurrentCheckSamples); i++)
            {
                double b = big.MeasCurrent();
                double s = small.MeasCurrent();
                if (double.IsNaN(b) || double.IsNaN(s))
                    throw new InvalidOperationException("电源电流回读失败（返回 NaN），请检查网口通讯");
                bigSamples.Add(b);
                smallSamples.Add(Math.Abs(s));
                log($"   采样{i + 1}：大电源={b:0.###}A，小电源={s:0.###}A");
                await Task.Delay(_cfg.CurrentCheckIntervalMs, ct);
            }

            double bigAvg = bigSamples.Average();
            double smallMax = smallSamples.Max();
            log($"   结果：大电源平均 {bigAvg:0.###}A（目标 {expect:0.###}A ±{_cfg.BigCurrentToleranceA:0.###}A），小电源最大 {smallMax:0.###}A（上限 {_cfg.SmallCurrentMaxA:0.###}A）");

            if (Math.Abs(bigAvg - expect) > _cfg.BigCurrentToleranceA)
                throw new InvalidOperationException(
                    $"大电源电流校验失败：实测 {bigAvg:0.###}A，目标 {expect:0.###}A（{stations.Count} 工位 × {_cfg.PerStationCurrentA:0.###}A）");
            if (smallMax > _cfg.SmallCurrentMaxA)
                throw new InvalidOperationException(
                    $"小电源电流校验失败：实测最大 {smallMax:0.###}A，要求 ≤ {_cfg.SmallCurrentMaxA:0.###}A（必须近似为 0）");

            log("   电流校验通过");

            // 11. 烘箱设定温度/湿度并启动（同一台烘箱由多层共用，只在第一层真正启动）
            if (_cfg.OvenEnabled)
            {
                DeviceComm.StartOvenForAging(_cfg.OvenTemperature, _cfg.OvenHumidity, log);
                _ovenAcquired = true;
                log($"⑩ 烘箱：温度 {_cfg.OvenTemperature:0.0}℃，湿度 {_cfg.OvenHumidity:0.0}%");
            }
            else
            {
                log("⑩ 烘箱未启用，跳过");
            }
        }

        // ================================================================
        // 唤醒单个工位
        // ================================================================
        /// <summary>
        /// 唤醒单个工位：闭合唤醒继电器后，等待周期 0x400 的回复。
        /// 0x400 周期任务在第一次唤醒时启动，之后从启动阶段一直每 2 秒发到停止/故障回退。
        /// </summary>
        private async Task WakeOneAsync(ChargeStationMap st, IReadOnlyList<ChargeStationMap> allStations, Action<string> log)
        {
            var ct = CancellationToken.None;
            bool relayOn = false;
            try
            {
                SetRelay(st, st.WakeCtl, true);
                relayOn = true;
                log($"   工位{st.Station.Code} → 继电器 CTL{st.WakeCtl} 闭合，等待 300ms");
                await Task.Delay(300, ct);

                var tcs = new TaskCompletionSource<CanComm.CanFrameInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFrame(CanComm.CanFrameInfo f)
                {
                    if (!f.IsTx && f.CanIndex == st.CanIndex && f.Channel == st.CanChannel)
                        tcs.TrySetResult(f);
                }

                CanComm.FrameReceived += OnFrame;
                try
                {
                    // 第一次唤醒时启动全局周期发送；后续工位直接复用，避免每个工位各自再发一遍。
                    if (_wakeTask == null || _wakeTask.IsCompleted)
                        StartPeriodicWake(allStations, log);

                    log($"   工位{st.Station.Code} → 等待 0x400 周期唤醒回复（每 {Math.Max(2000, _cfg.WakeRepeatIntervalMs)}ms）");
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(_cfg.WakeTimeoutMs, ct));
                    if (completed == tcs.Task)
                    {
                        var f = await tcs.Task;
                        log($"   工位{st.Station.Code} → 收到 BMS 回复 0x{f.Id:X}（{f.Kind}，{f.DataText}），唤醒成功");
                        return;
                    }

                    throw new TimeoutException(
                        $"工位{st.Station.Code} 唤醒超时：{_cfg.WakeTimeoutMs}ms 内未收到 CAN{st.CanIndex}/CH{st.CanChannel} 回复（0x400 周期任务仍在运行）");
                }
                finally
                {
                    CanComm.FrameReceived -= OnFrame;
                }
            }
            finally
            {
                if (relayOn)
                {
                    try { SetRelay(st, st.WakeCtl, false); log($"   工位{st.Station.Code} → CTL{st.WakeCtl} 断开"); }
                    catch (Exception ex) { log($"   工位{st.Station.Code} → CTL{st.WakeCtl} 断开失败：{ex.Message}"); }
                }
            }
        }

        // ================================================================
        // MOS 断开与确认
        // ================================================================
        private async Task<bool> OpenMosAndVerifyAsync(IReadOnlyList<ChargeStationMap> stations, Action<string> log)
        {
            log("① 断开 MOS 并回读 738F 确认");
            foreach (var st in stations)
            {
                try
                {
                    var uds = EnsureUds(st, log);
                    BmsEolService.StrongControlMosBuck(uds, false, false, false);
                    log($"   工位{st.Station.Code} → MOS 断开命令已发送");
                }
                catch (Exception ex)
                {
                    log($"   工位{st.Station.Code} → MOS 断开命令失败：{ex.Message}");
                }
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(500);
                bool allOpen = true;
                foreach (var st in stations)
                {
                    var uds = _uds.TryGetValue(st.StationNo, out var u) ? u.Client : null;
                    if (uds == null) { allOpen = false; log($"   工位{st.Station.Code} → 无诊断会话，无法确认"); continue; }
                    try
                    {
                        var data = uds.ReadDidData(BmsDids.MosBuckStatus);
                        if (data.Length < 1) { allOpen = false; log($"   工位{st.Station.Code} → 738F 无数据"); continue; }
                        int charge = data[0] & 0x03;
                        int discharge = (data[0] >> 2) & 0x03;
                        if (charge == 2 && discharge == 2)
                            log($"   工位{st.Station.Code} → 738F=0x{data[0]:X2}，充电/放电 MOS 均已断开");
                        else
                        {
                            allOpen = false;
                            log($"   工位{st.Station.Code} → 738F=0x{data[0]:X2}，MOS 尚未确认断开（第 {attempt} 次）");
                        }
                    }
                    catch (Exception ex)
                    {
                        allOpen = false;
                        log($"   工位{st.Station.Code} → 回读 738F 失败：{ex.Message}");
                    }
                }
                if (allOpen) return true;
            }
            return false;
        }

        // ================================================================
        // 回退 / 紧急关闭
        // ================================================================
        private async Task SafeRollbackAsync(Action<string> log)
        {
            // 故障回退第一件事：停掉周期唤醒，避免一边回退一边继续唤醒 BMS。
            StopPeriodicWake(log);
            if (_uds.Count > 0)
            {
                foreach (var st in _activeStations)
                {
                    try
                    {
                        var uds = _uds.TryGetValue(st.StationNo, out var u) ? u.Client : null;
                        if (uds != null) BmsEolService.StrongControlMosBuck(uds, false, false, false);
                    }
                    catch { }
                }
            }
            await EmergencyOffAsync(_activeStations, log);

            // 启动阶段失败回退：释放本层对共用烘箱的引用，引用归零时停止烘箱。
            if (_ovenAcquired)
            {
                _ovenAcquired = false;
                try { DeviceComm.StopOvenForAging(true, log); }
                catch (Exception ex) { log("回退时停止烘箱失败：" + ex.Message); }
            }
        }

        private async Task EmergencyOffAsync(IReadOnlyList<ChargeStationMap> stations, Action<string> log)
        {
            try
            {
                var big = BigSupply();
                if (big != null) { big.SetOutputOnOff(false); log("   大电源已关闭"); }
            }
            catch (Exception ex) { log("   大电源关闭失败：" + ex.Message); }

            try
            {
                var small = SmallSupply();
                if (small != null) { small.SetOutputOnOff(false); log("   小电源已关闭"); }
            }
            catch (Exception ex) { log("   小电源关闭失败：" + ex.Message); }

            foreach (var st in stations)
            {
                foreach (int ctl in st.PowerCtls)
                    TryRelay(st, ctl, false, log);
                TryRelay(st, st.WakeCtl, false, log);
            }

            SetIndicator(stations, false, log);
            await Task.Delay(100);
        }

        // ================================================================
        // 设备访问与校验
        // ================================================================
        private void Validate(int layer, IReadOnlyList<ChargeStationMap> stations)
        {
            if (layer is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(layer), "层号必须在 1~6");

            foreach (var st in stations)
            {
                if (st.Layer != layer)
                    throw new InvalidOperationException($"工位{st.Station.Code}不属于第 {layer} 层");
                if (DeviceComm.Loads.FirstOrDefault(l => l.Cfg.DevId == st.LoadDevId)?.Handle == null)
                    throw new InvalidOperationException($"负载{st.LoadDevId}未连接（工位{st.Station.Code}）");
                if (!DeviceComm.RrPorts[st.RelayPort - 1].Connected)
                    throw new InvalidOperationException($"485-{st.RelayPort} 未连接（工位{st.Station.Code}）");
                if (!CanComm.IsChannelStarted(st.CanIndex, st.CanChannel))
                    throw new InvalidOperationException($"CAN{st.CanIndex}/CH{st.CanChannel} 未启动（工位{st.Station.Code}）");
            }

            if (!DeviceComm.N36100[layer - 1].Connected)
                throw new InvalidOperationException($"大电源 N36190（{layer} 号）未连接");
            if (!DeviceComm.N36600[layer - 1].Connected)
                throw new InvalidOperationException($"小电源 N36620（{layer} 号）未连接");
            if (_cfg.OvenEnabled && !DeviceComm.Oven.Connected)
                throw new InvalidOperationException("烘箱控制器未连接（如不使用烘箱，请取消“启用烘箱”）");
            if (!DeviceComm.Root.Comm.Uds.Enabled)
                throw new InvalidOperationException("UDS 诊断未启用，无法控制 MOS");
        }

        private N36100PowerClient? BigSupply()
            => DeviceComm.N36100.Count >= _activeLayer && DeviceComm.N36100[_activeLayer - 1].Connected
                ? DeviceComm.N36100[_activeLayer - 1].Client : null;

        private N36600PowerClient? SmallSupply()
            => DeviceComm.N36600.Count >= _activeLayer && DeviceComm.N36600[_activeLayer - 1].Connected
                ? DeviceComm.N36600[_activeLayer - 1].Client : null;

        private UdsClient EnsureUds(ChargeStationMap st, Action<string> log)
        {
            _udsMgr ??= new UdsStationManager(DeviceComm.Root.Comm.Uds);
            var station = _uds.TryGetValue(st.StationNo, out var exist)
                ? exist
                : (_udsMgr[st.StationNo] ?? throw new InvalidOperationException($"工位{st.StationNo}无诊断映射"));
            _uds[st.StationNo] = station;

            var client = station.Client ?? station.Open(log);
            if (!client.IsSessionActive)
            {
                station.Prepare(DeviceComm.Root.Comm.Uds.SecurityAlgorithmPath, DeviceComm.Root.Comm.Uds.SecurityLevel);
                log($"   工位{st.Station.Code} → 10 03 + 27 解锁完成");
            }
            else if (!client.IsSecurityUnlocked)
            {
                BmsEolService.UnlockByAlgorithm(client, DeviceComm.Root.Comm.Uds.SecurityLevel,
                    DeviceComm.Root.Comm.Uds.SecurityAlgorithmPath);
                log($"   工位{st.Station.Code} → 27 解锁完成");
            }

            if (_udsMgr != null)
                _udsMgr.StartKeepAlive(2000, log);
            return client;
        }

        /// <summary>从启动阶段开始按固定周期持续发送 0x400 唤醒报文，直到停止/故障回退。</summary>
        private void StartPeriodicWake(IReadOnlyList<ChargeStationMap> stations, Action<string> log)
        {
            StopPeriodicWake(null);
            if (stations.Count == 0) return;

            int interval = Math.Max(2000, _cfg.WakeRepeatIntervalMs);
            var cts = new CancellationTokenSource();
            _wakeCts = cts;
            log($"唤醒报文周期发送已启动：{stations.Count} 个工位，每 {interval}ms 一帧 0x400");

            _wakeTask = Task.Run(async () =>
            {
                bool failLogged = false;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        foreach (var st in stations)
                        {
                            if (cts.IsCancellationRequested) break;
                            try
                            {
                                if (!BmsEolService.SendWakeupFrame(st.CanIndex, st.CanChannel, false) && !failLogged)
                                {
                                    log($"   唤醒报文发送失败：CAN{st.CanIndex}/CH{st.CanChannel} 未启动");
                                    failLogged = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!failLogged)
                                {
                                    log($"   唤醒报文发送异常：{ex.Message}");
                                    failLogged = true;
                                }
                            }
                        }
                        await Task.Delay(interval, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log("唤醒报文周期发送异常结束：" + ex.Message); }
                finally { log("唤醒报文周期发送已停止"); }
            });
        }

        /// <summary>停止周期唤醒；结束测试、故障回退、窗口退出都必须调用。</summary>
        private void StopPeriodicWake(Action<string>? log)
        {
            var cts = _wakeCts;
            var task = _wakeTask;
            _wakeCts = null;
            _wakeTask = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try
            {
                if (task != null && !task.Wait(1000))
                    log?.Invoke("唤醒报文停止等待超时（可能还有最后一帧在发送）");
            }
            catch { }
            try { cts.Dispose(); } catch { }
        }

        private void CloseUds()
        {
            StopPeriodicWake(null);
            try { _udsMgr?.StopKeepAlive(); } catch { }
            try { _udsMgr?.CloseAll(); } catch { }
            _udsMgr = null;
            _uds.Clear();
        }

        private static void SetRelay(ChargeStationMap st, int ctl, bool on)
        {
            if (ctl is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(ctl), $"CTL{ctl} 超出 1~24");
            DeviceComm.RelayMb(st.RelayPort, st.RelayAddr)
                .WriteSingleCoil((ushort)(0x0700 + ctl - 1), on);
        }

        private void TryRelay(ChargeStationMap st, int ctl, bool on, Action<string> log)
        {
            try { SetRelay(st, ctl, on); }
            catch (Exception ex) { log($"   工位{st.Station.Code} → CTL{ctl} {(on ? "闭合" : "断开")}失败：{ex.Message}"); }
        }

        private void SetIndicator(IReadOnlyList<ChargeStationMap> stations, bool on, Action<string> log)
        {
            if (_cfg.IndicatorCtl is < 1 or > 24) return;
            foreach (var st in stations)
            {
                try
                {
                    DeviceComm.RelayMb(st.RelayPort, _cfg.IndicatorRelayAddr)
                        .WriteSingleCoil((ushort)(0x0700 + _cfg.IndicatorCtl - 1), on);
                    log($"   工位{st.Station.Code} → 指示灯 485-{st.RelayPort}/地址{_cfg.IndicatorRelayAddr} CTL{_cfg.IndicatorCtl} {(on ? "亮" : "灭")}");
                }
                catch (Exception ex)
                {
                    log($"   工位{st.Station.Code} → 指示灯控制失败：{ex.Message}");
                }
            }
        }
    }
}
