using CommLab.Core;
using CommLab.Core.Protocol;
using Serilog;
using System.Net;
using System.Net.Sockets;

SerilogConfig.Configure("device");
var Logger = Log.ForContext("DeviceServer", true);


FaultMode mode = FaultMode.Normal;

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Logger.Information("模拟设备启动，监听 0.0.0.0:5000");
Logger.Information("故障注入: [N]正常 [D]慢应答5s [C]收到请求即断连 [S]静默 [H]只回心跳 [B]坏CRC [Q]退出");

_ = Task.Run(AcceptLoopAsync);

while (true)
{
    var k = Console.ReadKey(intercept: true).Key;
    switch (k)
    {
        case ConsoleKey.N: mode = FaultMode.Normal; Logger.Information(">>> 切换故障模式: 正常"); break;
        case ConsoleKey.D: mode = FaultMode.SlowResponse; Logger.Information(">>> 切换故障模式: 慢应答5s"); break;
        case ConsoleKey.C: mode = FaultMode.DropConnection; Logger.Information(">>> 切换故障模式: 收到请求即断连"); break;
        case ConsoleKey.S: mode = FaultMode.Silent; Logger.Information(">>> 切换故障模式: 静默(全不回)"); break;
        case ConsoleKey.H: mode = FaultMode.HeartbeatOnly; Logger.Information(">>> 切换故障模式: 只回心跳"); break;
        case ConsoleKey.B: mode = FaultMode.BadCrc; Logger.Information(">>> 切换故障模式: 坏CRC"); break;
        case ConsoleKey.Q: listener.Stop(); Log.CloseAndFlush(); return;
    }
}


async Task AcceptLoopAsync()
{
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => HandleClientAsync(client));
    }
}

async Task HandleClientAsync(TcpClient client)
{
    var ep = client.Client.RemoteEndPoint;
    Logger.Information("上位机已连接：{EP}", ep);
    var stream = client.GetStream();
    var buffer = new List<byte>();
    var tmp = new byte[4096];
    try
    {
        while (true)
        {
            int n = await stream.ReadAsync(tmp);
            if (n == 0)
            {
                Logger.Information("上位机断开：{EP}", ep);
                return;
            }
            buffer.AddRange(new ReadOnlySpan<byte>(tmp, 0, n));

            while (DeviceFrame.TryParse(buffer, out var f))
            {
                byte[]? resp = await BuildResponseAsync(f!);
                if (resp is null)
                {
                    Logger.Warning("[故障] 主动关闭连接");
                    client.Close();
                    return;
                }
                if (resp.Length == 0) continue; // 静默：收到但不回
                await stream.WriteAsync(resp);
                Logger.Debug("已应答 CMD=0x{Cmd:X2}", f!.Cmd);
            }
        }
    }
    catch (Exception ex) { Logger.Warning(ex, "客户端处理结束"); }
}

async Task<byte[]?> BuildResponseAsync(DeviceFrame req)
{
    switch (mode)
    {
        case FaultMode.Silent: return Array.Empty<byte>();
        case FaultMode.DropConnection: return null; // null = 关连接
        case FaultMode.SlowResponse: await Task.Delay(5000); break; // 模拟设备卡死
        case FaultMode.BadCrc:
            var bad = new DeviceFrame
            {
                Cmd = DeviceFrame.CmdReadRegisterAck,
                Payload = BitConverter.GetBytes(DateTime.Now.Second)
            }.Serialize();
            bad[^1] ^= 0xFF; // 捣乱CRC
            return bad;
    }

    if (mode == FaultMode.HeartbeatOnly && req.Cmd != DeviceFrame.CmdHeartbeat)
    {
        Logger.Debug("[故障] 只回心跳，CMD=0x{Cmd:X2}不应答", req.Cmd);
        return Array.Empty<byte>();
    }

    var ack = req.Cmd switch
    {
        DeviceFrame.CmdHeartbeat => new DeviceFrame
        {
            Cmd = DeviceFrame.CmdHeartbeatAck
        },
        DeviceFrame.CmdReadRegister => new DeviceFrame
        {
            Cmd = DeviceFrame.CmdReadRegisterAck,
            Payload = BitConverter.GetBytes(DateTime.Now.Second) // 应答一个"寄存器值"
        },
        _ => new DeviceFrame
        {
            Cmd = DeviceFrame.CmdError,
            Payload = new byte[] { 0x01 } // 错误码 0x01 = 未知命令
        }
    };
    return ack.Serialize();


}

enum FaultMode
{
    Normal,
    SlowResponse,
    DropConnection,
    Silent,
    HeartbeatOnly,
    BadCrc
}

