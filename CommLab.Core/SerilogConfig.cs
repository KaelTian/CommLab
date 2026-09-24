using Serilog;

namespace CommLab.Core
{
    public static class SerilogConfig
    {
        public static void Configure(string componentName)
        {
            var outputTemplate =
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{Component}] {Message:lj}{NewLine}{Exception}";

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("Component", componentName)
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.File(
                    path: $"logs/{componentName.ToLowerInvariant()}-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Component}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("日志初始化完成（Console + File）");
        }
    }
}
