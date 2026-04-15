using Microsoft.Extensions.DependencyInjection;
using SpawnDev.RTC.DemoConsole;
using SpawnDev.RTC.DemoConsole.UnitTests;
using SpawnDev.UnitTesting;

// Check for chat mode
if (args.Length > 0 && args[0].Equals("chat", StringComparison.OrdinalIgnoreCase))
{
    var server = args.Length > 1 ? args[1] : "wss://localhost:5570";
    await ChatMode.Run(server);
    return 0;
}

// Default: run unit tests
try
{
    var services = new ServiceCollection();
    services.AddSingleton<DesktopRTCTests>();
    var sp = services.BuildServiceProvider();
    var runner = new UnitTestRunner(sp, true);
    await ConsoleRunner.Run(args, runner);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
return 0;
