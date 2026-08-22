using System.Runtime.InteropServices;
using MobiFlight.Core.Devices;
using MobiFlight.Core.Xplane;

namespace MobiFlight.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CommandLine.Parse(args);

        if (options.WantsHelp || options.Positional.Count == 0)
        {
            PrintUsage();
            return options.Positional.Count == 0 && !options.WantsHelp ? 1 : 0;
        }

        try
        {
            return options.Positional[0].ToLowerInvariant() switch
            {
                "xplane" => await RunXplaneAsync(options),
                "serial" => RunSerial(options),
                "doctor" => await RunDoctorAsync(options),
                var unknown => Fail($"Unknown command '{unknown}'. Run 'mobiflight --help'.")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ---------------------------------------------------------------- X-Plane

    private static async Task<int> RunXplaneAsync(CommandLine options)
    {
        var endpoint = options.Endpoint();
        var subcommand = options.Positional.Count > 1 ? options.Positional[1].ToLowerInvariant() : "probe";

        switch (subcommand)
        {
            case "probe":
                return await ProbeXplaneAsync(endpoint, options.Timeout());

            case "watch":
            {
                var dataRefs = options.Positional.Skip(2).ToList();
                if (dataRefs.Count == 0) return Fail("Usage: mobiflight xplane watch <dataref> [<dataref>...]");

                return await WatchAsync(endpoint, dataRefs, options.Frequency());
            }

            case "read":
            {
                if (options.Positional.Count < 3) return Fail("Usage: mobiflight xplane read <dataref>");

                return await ReadOnceAsync(endpoint, options.Positional[2], options.Timeout());
            }

            case "write":
            {
                if (options.Positional.Count < 4) return Fail("Usage: mobiflight xplane write <dataref> <value>");
                if (!float.TryParse(options.Positional[3], System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return Fail($"'{options.Positional[3]}' is not a number.");
                }

                await using var client = new XplaneUdpClient(endpoint);
                client.Start();
                await client.WriteDataRefAsync(options.Positional[2], value);
                Console.WriteLine($"Wrote {value} to {options.Positional[2]} at {endpoint}.");
                return 0;
            }

            case "command":
            {
                if (options.Positional.Count < 3) return Fail("Usage: mobiflight xplane command <command>");

                await using var client = new XplaneUdpClient(endpoint);
                client.Start();
                await client.SendCommandAsync(options.Positional[2]);
                Console.WriteLine($"Sent command {options.Positional[2]} to {endpoint}.");
                return 0;
            }

            default:
                return Fail($"Unknown xplane subcommand '{subcommand}'.");
        }
    }

    private static async Task<int> ProbeXplaneAsync(XplaneEndpoint endpoint, TimeSpan timeout)
    {
        if (!endpoint.TryResolve(out var resolved, out var error))
        {
            Console.Error.WriteLine($"  FAIL  {error}");
            return 1;
        }

        Console.WriteLine($"Probing X-Plane at {resolved} (timeout {timeout.TotalSeconds:F0}s)...");

        await using var client = new XplaneUdpClient(endpoint);
        var connected = new TaskCompletionSource();
        client.Connected += (_, _) => connected.TrySetResult();

        client.Start();

        var finished = await Task.WhenAny(connected.Task, Task.Delay(timeout));

        if (finished == connected.Task)
        {
            Console.WriteLine($"  OK    X-Plane answered on {resolved}.");
            Console.WriteLine($"        Local UDP port in use: {client.LocalPort}");
            return 0;
        }

        Console.Error.WriteLine($"  FAIL  No answer from {resolved}.");
        Console.Error.WriteLine();
        PrintXplaneTroubleshooting(endpoint);
        return 1;
    }

    private static async Task<int> WatchAsync(XplaneEndpoint endpoint, IReadOnlyList<string> dataRefs, int frequency)
    {
        await using var client = new XplaneUdpClient(endpoint);

        client.Connected += (_, _) => Console.WriteLine($"# connected to {endpoint}");
        client.Disconnected += (_, _) => Console.WriteLine($"# lost connection to {endpoint}");
        client.DataRefChanged += (_, e) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {e.DataRef} = {e.Value}");

        client.Start();

        foreach (var dataRef in dataRefs)
        {
            await client.SubscribeAsync(dataRef, frequency);
            Console.WriteLine($"# subscribed to {dataRef} @ {frequency} Hz");
        }

        Console.WriteLine("# press Ctrl+C to stop");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };

        await stop.Task;
        return 0;
    }

    private static async Task<int> ReadOnceAsync(XplaneEndpoint endpoint, string dataRef, TimeSpan timeout)
    {
        await using var client = new XplaneUdpClient(endpoint);
        var received = new TaskCompletionSource<float>();

        client.DataRefChanged += (_, e) =>
        {
            if (e.DataRef == dataRef) received.TrySetResult(e.Value);
        };

        client.Start();
        await client.SubscribeAsync(dataRef, frequency: 5);

        var finished = await Task.WhenAny(received.Task, Task.Delay(timeout));

        if (finished == received.Task)
        {
            Console.WriteLine(received.Task.Result.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }

        Console.Error.WriteLine($"No value for '{dataRef}' within {timeout.TotalSeconds:F0}s.");
        Console.Error.WriteLine("The sim may not be reachable, or the dataref name may be wrong.");
        return 1;
    }

    // ------------------------------------------------------------------ Serial

    private static int RunSerial(CommandLine options)
    {
        var subcommand = options.Positional.Count > 1 ? options.Positional[1].ToLowerInvariant() : "list";

        switch (subcommand)
        {
            case "list":
            {
                var all = options.Has("all");
                var ports = SerialPortScanner.GetPortNames(includeUnlikely: all);

                if (ports.Count == 0)
                {
                    Console.WriteLine("No serial ports found.");
                    Console.WriteLine(SerialPortScanner.GetTroubleshootingHint());
                    if (!all) Console.WriteLine("Use --all to include ports that are unlikely to be a board.");
                    return 1;
                }

                foreach (var port in ports)
                {
                    var marker = SerialPortScanner.IsLikelyBoard(port) ? "*" : " ";
                    Console.WriteLine($" {marker} {port}");
                }

                Console.WriteLine();
                Console.WriteLine("* = looks like a USB board");
                return 0;
            }

            case "probe":
            {
                var baud = options.Int("baud", MobiFlightBoardProbe.DefaultBaudRate);
                var explicitPort = options.Value("port");

                var ports = explicitPort is null
                    ? SerialPortScanner.GetPortNames()
                    : new[] { explicitPort };

                if (ports.Count == 0)
                {
                    Console.WriteLine("No candidate serial ports found.");
                    Console.WriteLine(SerialPortScanner.GetTroubleshootingHint());
                    return 1;
                }

                var found = 0;
                foreach (var port in ports)
                {
                    Console.Write($"Probing {port} at {baud} baud... ");
                    var info = MobiFlightBoardProbe.Probe(port, baud);

                    if (info is null)
                    {
                        Console.WriteLine("no MobiFlight board.");
                        continue;
                    }

                    found++;
                    Console.WriteLine("found.");
                    Console.WriteLine($"    Type:    {info.Type}");
                    Console.WriteLine($"    Name:    {info.Name}");
                    Console.WriteLine($"    Serial:  {info.Serial}");
                    Console.WriteLine($"    Version: {info.Version}");
                    if (info.CoreVersion is not null) Console.WriteLine($"    Core:    {info.CoreVersion}");
                }

                if (found == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine(SerialPortScanner.GetTroubleshootingHint());
                }

                return found > 0 ? 0 : 1;
            }

            default:
                return Fail($"Unknown serial subcommand '{subcommand}'.");
        }
    }

    // ------------------------------------------------------------------ Doctor

    private static async Task<int> RunDoctorAsync(CommandLine options)
    {
        Console.WriteLine("MobiFlight cross platform environment check");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        Console.WriteLine($"  OS       {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  Arch     {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"  Runtime  {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        var problems = 0;

        Console.WriteLine("Serial ports");
        var ports = SerialPortScanner.GetPortNames();
        if (ports.Count == 0)
        {
            problems++;
            Console.WriteLine("  WARN  No candidate serial ports found.");
            Console.WriteLine($"        {SerialPortScanner.GetTroubleshootingHint()}");
        }
        else
        {
            foreach (var port in ports) Console.WriteLine($"  OK    {port}");
        }

        Console.WriteLine();
        Console.WriteLine("X-Plane");
        var endpoint = options.Endpoint();
        if (await ProbeXplaneAsync(endpoint, options.Timeout()) != 0) problems++;

        Console.WriteLine();
        Console.WriteLine(problems == 0
            ? "All checks passed."
            : $"{problems} check(s) need attention.");

        return problems == 0 ? 0 : 1;
    }

    private static void PrintXplaneTroubleshooting(XplaneEndpoint endpoint)
    {
        Console.Error.WriteLine("Things to check on the machine running X-Plane:");
        Console.Error.WriteLine("  1. X-Plane is running and past the loading screen.");
        Console.Error.WriteLine("  2. Settings > Network > \"Accept incoming connections\" is enabled.");
        Console.Error.WriteLine("     X-Plane 12 ships with this turned off.");
        Console.Error.WriteLine($"  3. The firewall allows inbound UDP on port {endpoint.Port}.");
        Console.Error.WriteLine("     On macOS: System Settings > Network > Firewall > Options,");
        Console.Error.WriteLine("     then allow incoming connections for X-Plane.");
        Console.Error.WriteLine("  4. Both machines are on the same subnet and no client isolation");
        Console.Error.WriteLine("     is active on the Wi-Fi access point.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            mobiflight - cross platform MobiFlight tools

            USAGE
              mobiflight <command> [subcommand] [arguments] [options]

            COMMANDS
              doctor                        Check the environment: serial ports and X-Plane reachability.

              xplane probe                  Verify that X-Plane answers over UDP.
              xplane read <dataref>         Print one value and exit.
              xplane watch <dataref>...     Stream values until Ctrl+C.
              xplane write <dataref> <val>  Set a dataref.
              xplane command <command>      Trigger an X-Plane command.

              serial list                   List serial ports that could be a board.
              serial probe                  Ask each port whether a MobiFlight board answers.

            OPTIONS
              --host <host>     Machine running X-Plane. Name or IPv4. Default 127.0.0.1
              --port <port>     X-Plane UDP port. Default 49000
              --timeout <sec>   How long to wait for the sim. Default 5
              --freq <hz>       Updates per second for watch. Default 5
              --baud <rate>     Serial baud rate. Default 115200
              --port <name>     For 'serial probe': probe only this port
              --all             For 'serial list': include unlikely ports
              -h, --help        Show this help

            EXAMPLES
              # X-Plane on a Mac at 192.168.1.10, MobiFlight tools anywhere on the LAN
              mobiflight doctor --host 192.168.1.10
              mobiflight xplane watch sim/cockpit2/radios/actuators/com1_frequency_hz_833 --host 192.168.1.10
              mobiflight xplane command sim/systems/avionics_on --host 192.168.1.10
              mobiflight serial probe
            """);
    }
}
