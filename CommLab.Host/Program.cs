using CommLab.Core;
using CommLab.Core.Protocol;
using Serilog;
using Serilog.Core;

SerilogConfig.Configure("host");
var Logger = Log.ForContext("Host", true);
using var cts=new CancellationTokenSource();

var client = new DeviceClient("127.0.0.1", 5000);
client.OnStateChanged += s => Logger.Information("UI 收到状态通知: {State}", s);

await client.StartAsync(cts.Token);
Logger.Information("上位机已启动，输入命令：r=读寄存器  s=并发5连发  q=退出");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine()?.Trim().ToLowerInvariant();
    switch (line)
    {
        case "r":
            try
            {
                var resp = await client.SendCommandAsync(
                    DeviceFrame.CmdReadRegister, new byte[] { 0x00, 0x01 },
                    TimeSpan.FromSeconds(2), cts.Token);
                Logger.Information("✔ 读到寄存器值: {Val}", BitConverter.ToInt32(resp.Payload));
            }
            catch (Exception ex) { Logger.Error("✘ 命令最终失败: {Msg}", ex.Message); }
            break;

        case "s":   // 验证并发保护：5 个命令并发，内部由 _sendLock 串行化
            var tasks = Enumerable.Range(0, 5).Select(async i =>
            {
                try
                {
                    var resp = await client.SendCommandAsync(
                        DeviceFrame.CmdReadRegister, new byte[] { (byte)i, 0x00 },
                        TimeSpan.FromSeconds(2), cts.Token);
                    Logger.Information("✔ 并发#{I} 成功，值={Val}", i, BitConverter.ToInt32(resp.Payload));
                }
                catch (Exception ex) { Logger.Error("✘ 并发#{I} 失败: {Msg}", i, ex.Message); }
            });
            await Task.WhenAll(tasks);
            break;

        case "q":
            cts.Cancel();
            await client.StopAsync();
            Log.CloseAndFlush();
            return;
    }
}

