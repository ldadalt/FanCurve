using FanCurve.Core;

namespace FanCurve;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? Help() : args[0] switch
            {
                "list" => List(),
                "check" => Check(Option(args, 1)),
                "run" => await Run(args).ConfigureAwait(false),
                "status" => Status(args),
                "stop" => Stop(args),
                "apply-config" => Apply(args),
                "install-task" => InstallTask.Run(OptionValue(args, "--config")),
                "startup" => Startup(args),
                "help" or "--help" or "-h" => Help(),
                _ => Help(),
            };
        }
        catch (ConfigRejectedException ex)
        {
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int List()
    {
        using var hub = new HardwareHub();
        hub.Open(storage: true);
        foreach (var row in hub.List())
        {
            var value = row.Value is null ? "—" : row.Value.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"{row.Id}  {row.Type}  {value}  {row.Name}");
        }

        return 0;
    }

    private static int Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("Uso: FanCurve check <fancurve.json>");
            return 1;
        }

        var bytes = File.ReadAllBytes(path);
        var config = AppConfig.Parse(bytes);
        var errors = ConfigValidator.Validate(config);
        var fanSensors = 0;
        var hardwareNote = "Hardware não foi aberto; as fans não entraram na conta.";
        try
        {
            using var hub = new HardwareHub();
            var storage = config.Metrics.Any(static id => id.Contains("/nvme/", StringComparison.Ordinal) || id.Contains("/hdd/", StringComparison.Ordinal));
            hub.Open(storage || config.RecordAllFans);
            if (!hub.TryBind(config, out var binding, out var bindErrors) || binding is null)
            {
                errors.AddRange(bindErrors);
            }
            else
            {
                fanSensors = binding.FanSpeeds.Length;
                hardwareNote = "Sensores da configuração encontrados.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            hardwareNote = "Hardware não abriu: " + ex.Message;
        }

        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        var channels = errors.Count == 0 && fanSensors > 0
            ? ConfigValidator.EstimatedChannels(config, fanSensors)
            : ConfigValidator.EstimatedChannels(config);
        var bytesPerMonth = StorageLayout.HistoryBytesPerMonth(Math.Max(channels, 1));
        Console.WriteLine(hardwareNote);
        Console.WriteLine($"Canais estimados: {channels}. Histórico: {bytesPerMonth / 1024d / 1024d:0.0} MB/mês, {bytesPerMonth * 12 / 1024d / 1024d:0.0} MB/ano.");
        return errors.Count == 0 ? 0 : 1;
    }

    private static async Task<int> Run(string[] args)
    {
        var path = Option(args, 1);
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("Uso: FanCurve run <fancurve.json> [--data pasta] [--logs pasta]");
            return 1;
        }

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;
        var data = OptionValue(args, "--data") ?? Path.Combine(folder, "data");
        var logs = OptionValue(args, "--logs") ?? Path.Combine(folder, "logs");
        using var service = FanService.Start(full, Path.GetFullPath(data), Path.GetFullPath(logs));
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancel.Cancel();
        };
        if (int.TryParse(OptionValue(args, "--seconds"), out var seconds) && seconds > 0)
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(seconds));
            Console.WriteLine($"FanCurve rodando por {seconds} s. Depois devolve as fans.");
        }
        else
        {
            Console.WriteLine("FanCurve rodando. Ctrl+C devolve as fans para a BIOS.");
        }
        await service.RunAsync(cancel.Token).ConfigureAwait(false);
        return 0;
    }

    private static int Status(string[] args)
    {
        var data = OptionValue(args, "--data");
        if (string.IsNullOrWhiteSpace(data))
        {
            Console.Error.WriteLine("Uso: FanCurve status --data <pasta>");
            return 1;
        }

        using var reader = RecentRingReader.Open(Path.GetFullPath(data));
        var status = reader.ReadStatus();
        var ageSeconds = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - status.LastTickUnixMs) / 1000d;
        Console.WriteLine($"Amostras {status.WriteCount}. Batimento há {ageSeconds:0} s. Fail-safe 0x{status.FailsafeMask:X}. Retomadas {status.ResumeCount}.");
        var outputs = new Half[FanCurveLimits.MaxRules];
        var count = reader.ReadRuleOutputs(outputs);
        for (var i = 0; i < count; i++)
        {
            Console.WriteLine($"Regra {i}: {(float)outputs[i]:0.0}%");
        }

        if (status.WriteCount == 0 || reader.ChannelCount < 2)
        {
            return 0;
        }

        var times = new long[1];
        var values = new Half[reader.ChannelCount];
        if (reader.CopyNew(status.WriteCount - 1, times, values, out _) == 1)
        {
            Console.WriteLine($"Última amostra: {(float)values[0]:0.0} °C, saída {(float)values[1]:0.0}%.");
        }

        return 0;
    }

    private static int Stop(string[] args)
    {
        var data = OptionValue(args, "--data") ?? FanCurvePaths.DataDirectory;
        var path = Path.Combine(Path.GetFullPath(data), "stop.request");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, "stop"u8.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Não consegui pedir a parada. Rode o FanCurve stop como administrador.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine("Parada pedida. As fans voltam ao controle anterior em até 2 s.");
        return 0;
    }

    private static int Apply(string[] args)
    {
        var source = Option(args, 1);
        var destination = OptionValue(args, "--to");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            Console.Error.WriteLine("Uso: FanCurve apply-config <origem> --to <destino>");
            return 1;
        }

        var bytes = File.ReadAllBytes(source);
        var errors = ConfigValidator.Validate(AppConfig.Parse(bytes));
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        InstallTask.WriteAtomic(Path.GetFullPath(destination), bytes);
        Console.WriteLine("Configuração gravada. O FanCurve em execução relê em até 5 s.");
        return 0;
    }

    private static int Startup(string[] args) => Option(args, 1) switch
    {
        "status" => StartupStatus(),
        "on" => InstallTask.Run(OptionValue(args, "--config")),
        "off" => InstallTask.Remove(),
        _ => Help(),
    };

    private static int StartupStatus()
    {
        Console.WriteLine(InstallTask.IsScheduled() ? "ligada" : "desligada");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            FanCurve list
            FanCurve check <fancurve.json>
            FanCurve run <fancurve.json> [--data pasta] [--logs pasta] [--seconds N]
            FanCurve status --data <pasta>
            FanCurve stop [--data pasta]
            FanCurve apply-config <origem> --to <destino>
            FanCurve install-task --config <fancurve.json>
            FanCurve startup status
            FanCurve startup on --config <fancurve.json>
            FanCurve startup off
            """);
        return 1;
    }

    private static string? Option(string[] args, int index) => index < args.Length && !args[index].StartsWith('-') ? args[index] : null;

    private static string? OptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
