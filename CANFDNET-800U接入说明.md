# CANFDNET-800U 网口 8 通道接入说明

> 依据：ZLG《接口函数使用手册 V1.24（800H常用说明.pdf）》3.3.7 以太网系列 2、附录1 表4.1、附录3 表4.3；
> CANFDNET-800U 产品使用指南 5.5~5.6；以及 ZCANPRO「设备管理 / 启动」界面截图。

## 1. 设备类型号（决定 `ZCAN_OpenDevice` 的第 1 个参数）

| 设备类型 | 类型号 | 说明 |
| --- | --- | --- |
| CANFDNET-800U-TCP | **57** | PC 作为客户端连接设备，或设备作为客户端、PC 作为服务器 |
| CANFDNET-800U-UDP | **58** | UDP 模式（需同时设本地端口） |

类型号必须与设备端「CAN(FD)转以太网」里配置的工作模式一致，否则打开设备失败。

## 2. 初始化流程（已按手册 2.1 流程图实现）

```
ZCAN_OpenDevice(类型号, 设备索引, 0)        // 一台设备只打开一次
   ↓
for 通道 ch = 0..7:
    ZCAN_SetValue("ch/work_mode", 0 客户端 / 1 服务器)   // 必须在 StartCAN 之前
    ZCAN_SetValue("ch/ip",        设备IP)                // 客户端模式
    ZCAN_SetValue("ch/work_port", 通道端口)              // 客户端模式（设备该通道的监听端口）
    ZCAN_SetValue("ch/local_port",本地端口)              // 服务器 / UDP 模式
    ZCAN_InitCAN(设备句柄, ch, 配置)                     // CAN / CANFD 两种结构体
    ZCAN_StartCAN(通道句柄)
   ↓
ZCAN_Transmit / ZCAN_TransmitFD → ZCAN_Receive / ZCAN_ReceiveFD
   ↓
ZCAN_CloseDevice
```

要点：

1. **通道号前缀**：网络参数路径为 `n/属性`，`n` 是通道号 0~7，不是设备级一次下发。旧代码只对 `0/...` 下发一次，导致 7 个通道参数错误。
2. **必须早于 StartCAN**：手册每个属性的「注意点」都要求 `需在 ZCAN_StartCAN 之前设置`。
3. **CAN 与 CANFD 的 InitCAN 结构体不同**：CAN 结构 20 字节，CANFD 结构 32 字节（多 `abi_timing / dbit_timing / brp / pad`）。用同一个 20 字节结构传 CANFD 会越界写坏内存，现已拆成两个 P/Invoke 重载。
4. **停止通道用 `ZCAN_ResetCAN`**，恢复用 `ZCAN_StartCAN`；关闭整台设备用 `ZCAN_CloseDevice`（会一并关闭 8 通道）。
5. **波特率、控制器类型、终端电阻属于设备端配置**（ZCANPRO「网络设备配置工具」，密码默认 88888，提交后下载到设备）。接口库对以太网系列只负责「通道链接 + 数据收发」，因此启动流程**不再**下发波特率/动态配置。

## 3. 通道端口约定

- 界面上每台 CAN 盒只需填 **IP 地址** 和 **端口号**；端口号填「该盒通道0的端口」，通道 `ch` 自动取 `端口号 + ch`。例：填 4001 → 通道0~7 依次 4001~4008（与 ZCANPRO 启动界面的工作端口一致）。
- 设备 IP 默认按设备号取 `192.168.0.10` ~ `192.168.0.15`（1~6 号设备），可逐台修改。
- 设备搜索 / 批量改 IP 功能已移除；IP、掩码、网关请用 ZCANPRO 的「设备管理」或设备配置工具直接设定。
- 旧配置里的 `RemotePort`（如 8000）不再作为默认值使用，升级后请按设备实际端口核对一次。

## 4. 界面操作

1. **协议**（页面顶部，全局共用）：CAN / CANFD，CANFD 加速（是/否）。必须与设备端「控制器类型」一致。
2. **每台 CAN 盒一行**：填 IP 地址、端口号，点「连接」；程序内部依次完成 打开设备（类型 57，索引 = 设备号-1）→ 8 个通道下发 `work_mode=客户端`、`ip`、`work_port` → `ZCAN_InitCAN` → `ZCAN_StartCAN`，状态显示「已连接 x/8 通道」（满 8 绿、不满 8 红）。
3. 点「断开」调用 `ZCAN_CloseDevice`，8 个通道一并释放。
4. 连接成功后 IP / 端口 / 协议自动写回 `Config.json`（保留 `GjdaDevices`、`Comm` 分区）；程序启动**不再自动连接**，需在主界面点击「连接设备」或在本页逐台点「连接」。

## 5. 收发监视（硬件控制 → CAN-800H 调试）

- 取帧线程归 `CanComm` 所有：任一通道 `ZCAN_StartCAN` 成功后自动启动（`EnsureReceiveLoop`），
  因此从「通讯设置」逐台连接或主界面「连接设备」批量连接，监视界面都能收到帧；程序启动时不连 CAN。
- 轮询逻辑：每通道先 `ZCAN_GetReceiveNum(h,0/1)`，再 `ZCAN_Receive / ZCAN_ReceiveFD`（每次最多 256 帧，每轮最多 8 批），
  通道间 `Sleep 5ms`，无通道时 `50ms`。
- **发送回显**：`CanComm.Send` 成功后调用 `RaiseFrameSent`，同一帧带 `IsTx=true` 广播，
  监视列表「方向」列显示「发送 / 接收」，因此本机发出的帧与总线收到的帧都在同一张表里。
- 列表最多保留 1000 帧并自动滚到最新；计数器分「接收 / 发送」累计；「仅显示当前卡/通道」勾选后按 `卡+通道` 过滤。
- 页面顶部同时显示「通道已启动/未启动 · 接收线程运行中/未运行」，便于判断是没连上还是没取帧。

## 6. 常见失败原因

| 现象 | 排查 |
| --- | --- |
| 打开设备失败，句柄为 0 | 类型号与实际工作模式不一致（TCP 57 / UDP 58）；索引错；IP 不在同网段；`zlgcan.dll` 未放在 exe 同目录 |
| 网络参数设置失败 | 该通道在设备端未使能转发配置，或端口被占用；服务器模式需确认 PC 端口未被防火墙拦截 |
| 通道启动失败返回 0x0200 | 打开设备错误；先用「设备信息」确认设备在线 |
| 收发无数据 | 先看调试页「接收线程」是否运行中；协议（CAN/CANFD）必须与设备端「控制器类型」一致；波特率在设备端设置，需与总线上其它节点一致 |
| 能发不能收 | 设备端波特率/终端电阻与总线不匹配；通道处于只听模式；或另一端未上电 |
| CANFD 通道异常/崩溃 | 确认使用 CANFD 专用初始化结构体（本工程已修正） |

## 7. 改动文件

| 文件 | 说明 |
| --- | --- |
| `Models/ZlgCan.cs` | SDK 封装：类型号常量、CAN/CANFD 两套 InitCAN 结构体、设备信息、错误码翻译、DLL 缺失提示 |
| `Models/CanComm.cs` | 初始化逻辑：`ConnectCard(ip, 端口)` 一次完成 打开设备→逐通道下发参数→InitCAN→StartCAN；另含单通道启停、整卡批量启动；内置帧接收线程与发送回显 |
| `Views/UC_CanDebug.xaml(.cs)` | 调试页：收发帧列表（方向/卡/通道/ID/类型/长度/数据）、接收与发送计数、按卡+通道过滤 |
| `Models/Config.cs` | `CanChannelItem`（每通道 IP/端口/协议/模式）、`CanCardItem.BasePort`（起始端口）、旧配置兼容补齐 |
| `Views/admin/UC_CanZlg800H.xaml(.cs)` | 连接页：每盒只填 IP + 端口号，连接/断开 + 状态；协议全局选择（已移除设备搜索、设备管理、逐通道启动） |
| `MainWindow.xaml.cs` | `ConnectCanCardsFromConfig` 仅在点击「连接设备」时按 `Config.json` 批量连接；启动不连 CAN，断开按钮同时释放 CAN |
