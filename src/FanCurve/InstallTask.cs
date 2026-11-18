using System.Security.AccessControl;
using System.Security.Principal;
using FanCurve.Core;

namespace FanCurve;

public static class InstallTask
{
    public static int Run(string? sourceConfig)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("install-task só funciona no Windows.");
            return 1;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("install-task precisa de um prompt de administrador.");
            return 1;
        }

        Directory.CreateDirectory(FanCurvePaths.ConfigDirectory);
        Directory.CreateDirectory(FanCurvePaths.DataDirectory);
        Directory.CreateDirectory(FanCurvePaths.HistoryDirectory);
        Directory.CreateDirectory(FanCurvePaths.LogDirectory);
        Protect(FanCurvePaths.ConfigDirectory, usersCanRead: true);
        Protect(FanCurvePaths.DataDirectory, usersCanRead: true);
        Protect(FanCurvePaths.LogDirectory, usersCanRead: true);

        if (!File.Exists(FanCurvePaths.ConfigFile))
        {
            if (string.IsNullOrWhiteSpace(sourceConfig))
            {
                Console.Error.WriteLine($"Não há config em {FanCurvePaths.ConfigFile}. Passe --config <arquivo>.");
                return 1;
            }

            var bytes = File.ReadAllBytes(sourceConfig);
            var errors = ConfigValidator.Validate(AppConfig.Parse(bytes));
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }

                return 1;
            }

            WriteAtomic(FanCurvePaths.ConfigFile, bytes);
        }

        var exe = InstallBinaries();
        var arguments = $"run \"{FanCurvePaths.ConfigFile}\" --data \"{FanCurvePaths.DataDirectory}\" --logs \"{FanCurvePaths.LogDirectory}\"";
        var taskRun = $"\\\"{exe}\\\" {arguments}";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = $"/Create /F /TN \\FanCurve /SC ONSTART /RU SYSTEM /RL HIGHEST /TR \"{taskRun}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o schtasks.");
        process.WaitForExit();
        var stderr = process.StandardError.ReadToEnd();
        var stdout = process.StandardOutput.ReadToEnd();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(stderr);
            Console.Error.WriteLine(stdout);
            return process.ExitCode;
        }

        Console.WriteLine($"O FanCurve passa a iniciar com o Windows. Config: {FanCurvePaths.ConfigFile}");
        return 0;
    }

    public static bool IsScheduled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RunSchtasks("/Query /TN \\FanCurve") == 0;
    }

    public static int Remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("startup off só funciona no Windows.");
            return 1;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("Desligar a inicialização pede um prompt de administrador.");
            return 1;
        }

        RunSchtasks("/Delete /F /TN \\FanCurve");
        Console.WriteLine("A inicialização automática foi desligada. Se o FanCurve já estiver em execução, ele continua até o desligamento ou até o comando stop.");
        return 0;
    }

    private static string InstallBinaries()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Não achei o caminho do FanCurve.exe.");
        var sourceDir = Path.GetDirectoryName(current) ?? throw new InvalidOperationException("Executável sem pasta.");
        var destination = FanCurvePaths.ProgramFilesRoot;
        if (!string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        return Path.Combine(destination, "FanCurve.exe");
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o schtasks.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void Protect(string directory, bool usersCanRead)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRule(rule);
        }

        Add(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Add(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        if (usersCanRead)
        {
            Add(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        }

        info.SetAccessControl(security);
    }

    private static void Add(DirectorySecurity security, WellKnownSidType sid, FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    public static void WriteAtomic(string destination, ReadOnlySpan<byte> bytes) =>
        ConfigFiles.WriteAtomic(destination, bytes);
}
