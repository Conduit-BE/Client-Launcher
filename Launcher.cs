using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class Launcher
{
    private const string DllName = "ConduitClient.dll";
    private const string GameExe = "Minecraft.Windows.exe";
    private const string PackageFamily = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const ushort Amd64 = 0x8664;

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConduitClient");
        string log = Path.Combine(root, "logs", "launcher.log");

        try
        {
            using (var mutex = new Mutex(false, @"Local\ConduitClientLauncher"))
            {
                try
                {
                    if (!mutex.WaitOne(0))
                        return 0;
                }
                catch (AbandonedMutexException)
                {
                    // The previous launcher exited while holding the mutex.
                }

                try
                {
                    bool useLevi = args.Length > 0 && args[0] == "--levi";
                    if (useLevi && args.Length > 2)
                        throw new ArgumentException("Usage: ConduitClientLauncher.exe --levi [instance name]");
                    string executable = useLevi ? null : GetExplicitExecutable(args);
                    string dll = Path.Combine(root, "dll", DllName);
                    if (!File.Exists(dll))
                        InstallDll(dll);
                    ValidateDll(dll);

                    using (Process game = useLevi
                        ? LaunchLeviMinecraft(FindLeviLauncher(), args.Length == 2 ? args[1] : null)
                        : LaunchMinecraft(executable))
                    {
                        Log(log, "Minecraft process selected (PID " + game.Id + ").");
                        Inject(game, dll);
                    }

                    Log(log, "Conduit Client is loaded.");
                    return 0;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception error)
        {
            Log(log, error.ToString());
            return 1;
        }
    }

    private static void Log(string path, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Release
    {
        public bool draft { get; set; }
        public DateTimeOffset? published_at { get; set; }
        public Asset[] assets { get; set; }
    }

    private sealed class Asset
    {
        public string name { get; set; }
        public string state { get; set; }
        public string browser_download_url { get; set; }
        public string digest { get; set; }
        public long size { get; set; }
    }

    private static void InstallDll(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(2);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ConduitClientLauncher/1.0");
                var json = new JavaScriptSerializer();
                Release[] releases = json.Deserialize<Release[]>(http.GetStringAsync(
                    "https://api.github.com/repos/Conduit-BE/Client/releases?per_page=30").GetAwaiter().GetResult());
                Release latest = releases.Where(release => !release.draft && release.published_at.HasValue)
                    .OrderByDescending(release => release.published_at).FirstOrDefault();
                Asset asset = latest == null || latest.assets == null ? null : latest.assets.FirstOrDefault(
                    file => file.name == DllName && file.state == "uploaded");
                if (asset == null)
                    throw new InvalidOperationException("The latest published Conduit Client release has no " + DllName + " asset.");

                var download = new Uri(asset.browser_download_url);
                if (download.Scheme != Uri.UriSchemeHttps || download.Host != "github.com" ||
                    !download.AbsolutePath.StartsWith("/Conduit-BE/Client/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected GitHub release asset URL.");

                using (HttpResponseMessage response = http.GetAsync(download).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (FileStream output = File.Create(temporary))
                        source.CopyTo(output);
                }

                using (FileStream input = File.OpenRead(temporary))
                {
                    if (input.Length != asset.size)
                        throw new InvalidDataException("The downloaded DLL size differs from the GitHub release asset.");

                    if (!String.IsNullOrEmpty(asset.digest))
                    {
                        using (SHA256 sha = SHA256.Create())
                        {
                            string digest = "sha256:" + BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                            if (!String.Equals(digest, asset.digest, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("The downloaded DLL failed its GitHub SHA-256 check.");
                        }
                    }
                }

                ValidateDll(temporary);
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateDll(string path)
    {
        using (var input = new BinaryReader(File.OpenRead(path)))
        {
            if (input.BaseStream.Length >= 64 && input.ReadUInt16() == 0x5A4D)
            {
                input.BaseStream.Position = 0x3C;
                int peOffset = input.ReadInt32();
                if (peOffset >= 64 && peOffset <= input.BaseStream.Length - 24)
                {
                    input.BaseStream.Position = peOffset;
                    if (input.ReadUInt32() == 0x4550 && input.ReadUInt16() == Amd64)
                    {
                        input.BaseStream.Position = peOffset + 22;
                        if ((input.ReadUInt16() & 0x2000) != 0)
                            return;
                    }
                }
            }
        }

        throw new InvalidDataException(path + " is not a Windows x64 DLL.");
    }

    private static string GetExplicitExecutable(string[] args)
    {
        if (args.Length > 1)
            throw new ArgumentException("Usage: ConduitClientLauncher.exe [path to Minecraft.Windows.exe]");
        if (args.Length == 0)
        {
            string sibling = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GameExe);
            return File.Exists(sibling) ? sibling : null;
        }

        string path = Path.GetFullPath(args[0]);
        if (!String.Equals(Path.GetFileName(path), GameExe, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Expected a path to " + GameExe + ".", path);
        return path;
    }

    private static Process LaunchMinecraft(string executable)
    {
        if (executable != null)
            return StartExecutable(executable, executable);

        Process registered = ActivateMinecraft();
        if (registered != null)
            return registered;

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            string xboxGames = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(xboxGames))
                continue;

            string[] directories;
            try { directories = Directory.GetDirectories(xboxGames, "Minecraft*"); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string directory in directories)
            {
                if (Path.GetFileName(directory).IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                string content = Path.Combine(directory, "Content");
                string game = Path.Combine(content, GameExe);
                string helper = Path.Combine(content, "gamelaunchhelper.exe");
                if (File.Exists(game))
                    return StartExecutable(File.Exists(helper) ? helper : game, game);
            }
        }

        string levi = FindLeviLauncher();
        if (levi != null)
            return LaunchLeviMinecraft(levi, null);

        throw new FileNotFoundException("Minecraft for Windows and LeviLauncher were not found. Pass the path to " + GameExe + ".");
    }

    private static string FindLeviLauncher()
    {
        string[] directories =
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LeviLauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LeviLauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LeviLauncher")
        };
        return directories.Select(directory => Path.Combine(directory, "LeviLauncher.exe")).FirstOrDefault(File.Exists);
    }

    private sealed class LeviLaunchResult
    {
        public int? pid { get; set; }
    }

    private static Process LaunchLeviMinecraft(string launcher, string instance)
    {
        if (launcher == null)
            throw new FileNotFoundException("LeviLauncher.exe was not found. For a portable copy, place it beside ConduitClientLauncher.exe.");

        string arguments = "launch --detach --timeout 30s --json";
        if (instance != null)
        {
            if (String.IsNullOrWhiteSpace(instance) || instance.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Expected a valid LeviLauncher instance name.");
            arguments += " -- \"" + instance + "\"";
        }

        using (Process command = Process.Start(new ProcessStartInfo(launcher, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(launcher)
        }))
        {
            command.StandardInput.Close();
            var output = command.StandardOutput.ReadToEndAsync();
            var errors = command.StandardError.ReadToEndAsync();
            if (!command.WaitForExit(45000))
            {
                if (!command.HasExited)
                    command.Kill();
                throw new TimeoutException("LeviLauncher did not return a Minecraft process within 45 seconds.");
            }

            // Minecraft can inherit the CLI's output pipes, so reading must stay bounded
            // even after the CLI has exited.
            if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, errors }, 3000))
                throw new TimeoutException("LeviLauncher exited but did not close its output pipes.");
            if (command.ExitCode != 0)
                throw new InvalidOperationException("LeviLauncher failed (exit " + command.ExitCode + "): " +
                    (String.IsNullOrWhiteSpace(errors.Result) ? output.Result.Trim() : errors.Result.Trim()));

            var result = new JavaScriptSerializer().Deserialize<LeviLaunchResult>(output.Result);
            if (result == null || !result.pid.HasValue || result.pid.Value <= 0)
                throw new InvalidOperationException("LeviLauncher did not report a Minecraft process ID.");

            Process game = Process.GetProcessById(result.pid.Value);
            if (String.Equals(game.ProcessName + ".exe", GameExe, StringComparison.OrdinalIgnoreCase))
                return game;

            game.Dispose();
            throw new InvalidOperationException("LeviLauncher reported a process other than " + GameExe + ".");
        }
    }

    private static Process StartExecutable(string launcher, string executable)
    {
        Process started = Process.Start(new ProcessStartInfo(launcher)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)
        });
        if (String.Equals(launcher, executable, StringComparison.OrdinalIgnoreCase))
            return started;

        started.Dispose();
        var startup = Stopwatch.StartNew();
        while (startup.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)))
            {
                using (candidate)
                {
                    try
                    {
                        if (String.Equals(candidate.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                            return Process.GetProcessById(candidate.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("The Xbox game launch helper did not start " + executable + ".");
    }

    private static Process ActivateMinecraft()
    {
        var activation = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            uint processId;
            int result = activation.ActivateApplication(PackageFamily + "!Game", null, 0, out processId);
            if (result < 0)
                result = activation.ActivateApplication(PackageFamily + "!App", null, 0, out processId);
            if (result < 0)
                return null;

            var game = Process.GetProcessById(checked((int)processId));
            if (String.Equals(game.ProcessName + ".exe", GameExe, StringComparison.OrdinalIgnoreCase))
                return game;

            game.Dispose();
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(activation);
        }
    }

    private static void Inject(Process game, string dll)
    {
        const uint processAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
        IntPtr process = OpenProcess(processAccess, false, game.Id);
        if (process == IntPtr.Zero)
            throw Win32Error("OpenProcess");

        try
        {
            ushort processMachine, nativeMachine;
            if (!IsWow64Process2(process, out processMachine, out nativeMachine))
                throw Win32Error("IsWow64Process2");
            if (processMachine != 0 ? processMachine != Amd64 : nativeMachine != Amd64)
                throw new InvalidOperationException("Minecraft must be an x64 process.");

            IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            IntPtr owner;
            if (loadLibrary == IntPtr.Zero || !GetModuleHandleEx(0x06, loadLibrary, out owner))
                throw Win32Error("Resolve LoadLibraryW");

            // LoadLibraryW may be forwarded to another module, such as KernelBase.
            string ownerName = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .First(module => module.BaseAddress == owner).ModuleName;
            long offset = loadLibrary.ToInt64() - owner.ToInt64();

            ProcessModule remoteOwner = null;
            var startup = Stopwatch.StartNew();
            while (startup.Elapsed < TimeSpan.FromSeconds(30) && !game.HasExited)
            {
                game.Refresh();
                ProcessModule[] modules;
                try
                {
                    modules = game.Modules.Cast<ProcessModule>().ToArray();
                }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode != 299 && error.NativeErrorCode != 24)
                        throw;
                    Thread.Sleep(100);
                    continue;
                }

                if (modules.Any(module => SamePath(module, dll)))
                    return;

                remoteOwner = modules.FirstOrDefault(module =>
                    String.Equals(module.ModuleName, ownerName, StringComparison.OrdinalIgnoreCase));
                if (remoteOwner != null)
                    break;
                Thread.Sleep(100);
            }

            if (remoteOwner == null)
                throw new InvalidOperationException("Minecraft exited or its Windows loader did not become ready.");

            IntPtr remoteLoader = new IntPtr(remoteOwner.BaseAddress.ToInt64() + offset);
            LoadDll(process, remoteLoader, dll);
            game.Refresh();
            if (!game.Modules.Cast<ProcessModule>().Any(module => SamePath(module, dll)))
                throw new InvalidOperationException("Minecraft did not load " + DllName + ".");
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool SamePath(ProcessModule module, string dll)
    {
        return String.Equals(module.FileName, dll, StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadDll(IntPtr process, IntPtr loader, string dll)
    {
        byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
        UIntPtr length = new UIntPtr((uint)path.Length);
        IntPtr buffer = VirtualAllocEx(process, IntPtr.Zero, length, 0x3000, 0x04);
        if (buffer == IntPtr.Zero)
            throw Win32Error("VirtualAllocEx");

        bool safeToFree = true;
        try
        {
            UIntPtr written;
            if (!WriteProcessMemory(process, buffer, path, length, out written) || written != length)
                throw Win32Error("WriteProcessMemory");

            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loader, buffer, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
                throw Win32Error("CreateRemoteThread");

            safeToFree = false;
            try
            {
                uint wait = WaitForSingleObject(thread, 30000);
                if (wait == 258)
                    throw new TimeoutException("Minecraft's DLL loader did not finish within 30 seconds.");
                if (wait != 0)
                    throw Win32Error("WaitForSingleObject");
                safeToFree = true;
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            // A thread that outlives the wait may still be reading the path.
            if (safeToFree)
                VirtualFreeEx(process, buffer, UIntPtr.Zero, 0x8000);
        }
    }

    private static Win32Exception Win32Error(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, operation + " failed (" + code + "): " + new Win32Exception(code).Message);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr length, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize,
        IntPtr start, IntPtr argument, uint flags, IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
