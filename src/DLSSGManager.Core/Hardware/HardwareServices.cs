using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using Microsoft.Win32;

namespace DLSSGManager.Core.Hardware;

public static class GpuArchitectureCatalog
{
    private static readonly (Regex Pattern, string Architecture, string Capability, string Shader, bool Supported, bool Newer)[] Patterns =
    [
        (new(@"\bRTX\s*(?:PRO\s*)?50\d{2}\b", RegexOptions.IgnoreCase), "Blackwell", "12.0", "SM120", true, true),
        (new(@"\bRTX\s*PRO\s*6000\b", RegexOptions.IgnoreCase), "Blackwell", "12.0", "SM120", true, true),
        (new(@"\bRTX\s*(?:PRO\s*)?40\d{2}\b", RegexOptions.IgnoreCase), "Ada Lovelace", "8.9", "SM89", true, true),
        (new(@"\bRTX\s*6000\s*Ada\b", RegexOptions.IgnoreCase), "Ada Lovelace", "8.9", "SM89", true, true),
        (new(@"\bRTX\s*A[456]\d{3}\b", RegexOptions.IgnoreCase), "Ampere", "8.6", "SM86", true, false),
        (new(@"\bRTX\s*30\d{2}\b", RegexOptions.IgnoreCase), "Ampere", "8.6", "SM86", true, false),
        (new(@"\bRTX\s*20\d{2}\b", RegexOptions.IgnoreCase), "Turing", "7.5", "SM75", true, false),
        (new(@"\bTITAN\s*RTX\b", RegexOptions.IgnoreCase), "Turing", "7.5", "SM75", true, false),
        (new(@"\bGTX\s*16\d{2}\b", RegexOptions.IgnoreCase), "Turing", "7.5", "SM75", false, false)
    ];

    private static readonly Dictionary<uint, (string Architecture, string Capability, string Shader, bool Supported, bool Newer)> DeviceIds = new()
    {
        [0x1E02] = ("Turing", "7.5", "SM75", true, false),
        [0x1E04] = ("Turing", "7.5", "SM75", true, false),
        [0x1E07] = ("Turing", "7.5", "SM75", true, false),
        [0x1E30] = ("Turing", "7.5", "SM75", true, false),
        [0x1E84] = ("Turing", "7.5", "SM75", true, false),
        [0x1F02] = ("Turing", "7.5", "SM75", true, false),
        [0x1F07] = ("Turing", "7.5", "SM75", true, false),
        [0x1F47] = ("Turing", "7.5", "SM75", true, false),
        [0x1F82] = ("Turing", "7.5", "SM75", true, false),
        [0x2182] = ("Turing", "7.5", "SM75", true, false),
        [0x2204] = ("Ampere", "8.6", "SM86", true, false),
        [0x2206] = ("Ampere", "8.6", "SM86", true, false),
        [0x2208] = ("Ampere", "8.6", "SM86", true, false),
        [0x2216] = ("Ampere", "8.6", "SM86", true, false),
        [0x2482] = ("Ampere", "8.6", "SM86", true, false),
        [0x2484] = ("Ampere", "8.6", "SM86", true, false),
        [0x2486] = ("Ampere", "8.6", "SM86", true, false),
        [0x2488] = ("Ampere", "8.6", "SM86", true, false),
        [0x2489] = ("Ampere", "8.6", "SM86", true, false),
        [0x249C] = ("Ampere", "8.6", "SM86", true, false),
        [0x24C7] = ("Ampere", "8.6", "SM86", true, false),
        [0x24DC] = ("Ampere", "8.6", "SM86", true, false),
        [0x2504] = ("Ampere", "8.6", "SM86", true, false),
        [0x2520] = ("Ampere", "8.6", "SM86", true, false),
        [0x2544] = ("Ampere", "8.6", "SM86", true, false),
        [0x2560] = ("Ampere", "8.6", "SM86", true, false),
        [0x2584] = ("Ampere", "8.6", "SM86", true, false),
        [0x2684] = ("Ada Lovelace", "8.9", "SM89", true, true),
        [0x2704] = ("Ada Lovelace", "8.9", "SM89", true, true),
        [0x2782] = ("Ada Lovelace", "8.9", "SM89", true, true),
        [0x2786] = ("Ada Lovelace", "8.9", "SM89", true, true),
        [0x27A1] = ("Blackwell", "12.0", "SM120", true, true),
        [0x2B85] = ("Blackwell", "12.0", "SM120", true, true),
        [0x2C02] = ("Blackwell", "12.0", "SM120", true, true),
        [0x2C18] = ("Blackwell", "12.0", "SM120", true, true),
        [0x2F04] = ("Blackwell", "12.0", "SM120", true, true)
    };

    public static GpuDevice Describe(RawGpuAdapter adapter, string? driverVersion)
    {
        var vendor = adapter.VendorId switch
        {
            0x10DE => GpuVendor.Nvidia,
            0x1002 => GpuVendor.Amd,
            0x8086 => GpuVendor.Intel,
            0x1414 => GpuVendor.Microsoft,
            _ => GpuVendor.Other
        };
        var architecture = "Unknown";
        var capability = "Unknown";
        var shader = "Unknown";
        var supported = false;
        var newer = false;
        var identified = false;
        if (vendor == GpuVendor.Nvidia && DeviceIds.TryGetValue(adapter.DeviceId, out var known))
        {
            (architecture, capability, shader, supported, newer) = known;
            identified = true;
        }
        foreach (var pattern in Patterns)
        {
            if (!pattern.Pattern.IsMatch(adapter.Name))
                continue;
            architecture = pattern.Architecture;
            capability = pattern.Capability;
            shader = pattern.Shader;
            supported = pattern.Supported;
            newer = pattern.Newer;
            identified = true;
            break;
        }

        var discrete = vendor is GpuVendor.Nvidia or GpuVendor.Amd && adapter.DedicatedVideoMemory >= 512L * 1024 * 1024;
        return new GpuDevice
        {
            Name = string.IsNullOrWhiteSpace(adapter.Name) ? "Unknown adapter" : adapter.Name.Trim(),
            Vendor = vendor,
            VendorName = vendor switch
            {
                GpuVendor.Nvidia => "NVIDIA",
                GpuVendor.Amd => "AMD",
                GpuVendor.Intel => "Intel",
                GpuVendor.Microsoft => "Microsoft",
                _ => "Other"
            },
            Architecture = architecture,
            ComputeCapability = capability,
            ShaderModel = shader,
            DedicatedVideoMemory = adapter.DedicatedVideoMemory,
            DeviceId = adapter.DeviceId,
            IsDiscrete = discrete,
            IsSupportedRtx = supported,
            IsNewerArchitecture = newer,
            ArchitectureIdentified = identified,
            DriverVersion = driverVersion,
            Luid = adapter.Luid
        };
    }
}

public sealed class SystemCompatibilityService : ISystemCompatibilityService
{
    public SystemCompatibilityReport Evaluate(HardwareSnapshot snapshot)
    {
        var reasons = new List<string>();
        var features = new List<string>();
        var primary = SelectPrimary(snapshot.Gpus);
        var namedClient = snapshot.OperatingSystem.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                          snapshot.OperatingSystem.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
        var buildImpliesClient = snapshot.BuildNumber >= 10240;
        var windowsOk = snapshot.Is64Bit && (namedClient || buildImpliesClient) && (snapshot.BuildNumber == 0 || snapshot.BuildNumber >= 10240);
        if (!snapshot.Is64Bit)
            reasons.Add("DLSSG for SM86 requires 64-bit Windows.");
        if (snapshot.BuildNumber > 0 && snapshot.BuildNumber < 10240)
            reasons.Add("Windows 10 or Windows 11 is required. This system is older.");
        if (!namedClient && !buildImpliesClient)
            reasons.Add("Windows 10 or Windows 11 was not identified.");
        else if (!namedClient)
            reasons.Add("The operating system build looks like Windows 10 or 11, but the product name did not confirm it.");

        var unknownArchitecture = primary is { Vendor: GpuVendor.Nvidia, ArchitectureIdentified: false };
        var unsupportedGpu = primary == null || primary.Vendor != GpuVendor.Nvidia || (!primary.IsSupportedRtx && !unknownArchitecture);
        if (primary == null)
            reasons.Add("No graphics adapter was detected.");
        else if (primary.Vendor != GpuVendor.Nvidia)
            reasons.Add($"{primary.Name} is not an NVIDIA GPU. DLSSG for SM86 only runs on NVIDIA RTX GPUs.");
        else if (unknownArchitecture)
            reasons.Add($"{primary.Name} is an NVIDIA adapter, but its architecture was not identified from the device id or a known model name. It is not treated as supported.");
        else if (!primary.IsSupportedRtx)
            reasons.Add($"{primary.Name} is not an RTX 20, 30, 40, or 50-series GPU. Upstream support is RTX 20 (SM75) and RTX 30 (SM86), and 0.4.0 also enables an unvalidated route on RTX 40 (SM89) and RTX 50 (SM120).");
        else
        {
            features.Add($"{primary.Architecture} / compute {primary.ComputeCapability} ({primary.ShaderModel})");
            features.Add(primary.IsNewerArchitecture
                ? "RTX 40/50 route is enabled by upstream 0.4.0 and still has limited measured coverage."
                : "This architecture is in the upstream supported set (RTX 20 / RTX 30).");
        }

        if (snapshot.Gpus.Count > 1)
            features.Add($"{snapshot.Gpus.Count} GPUs detected. Games are associated with the highest-VRAM supported NVIDIA GPU when Windows does not record a per-game preference.");

        var driver = snapshot.NvidiaDriverVersion ?? primary?.DriverVersion;
        var driverKnown = NvidiaDriverVersion.TryParse(driver, out var parsed);
        var driverTooOld = false;
        var driverPtxOnly = false;
        if (primary is { Vendor: GpuVendor.Nvidia } && (primary.IsSupportedRtx || unknownArchitecture))
        {
            if (!driverKnown)
                reasons.Add("An NVIDIA driver version could not be read. NGX, NVAPI, and CUDA support could not be verified.");
            else if (parsed < new Version(555, 0))
            {
                driverTooOld = true;
                reasons.Add($"NVIDIA driver {NvidiaDriverVersion.ToFriendly(driver!)} is older than the R555 PTX requirement documented for the bundled kernels. Update the driver. DLSSG Manager will not change it.");
            }
            else if (parsed < new Version(580, 0))
            {
                driverPtxOnly = true;
                reasons.Add($"NVIDIA driver {NvidiaDriverVersion.ToFriendly(driver!)} can JIT the PTX kernels. Cubin kernels want about R580 or newer and fall back to PTX on older drivers. That fallback is documented and changes the first-frame compile, not the installation.");
            }
            else
                features.Add($"NVIDIA driver {NvidiaDriverVersion.ToFriendly(driver!)} meets the documented cubin range (about R580+).");
        }

        if (snapshot.HagsEnabled == false && !unsupportedGpu && windowsOk)
            reasons.Add("Hardware-accelerated GPU scheduling is off. DLSS Frame Generation needs it. Enable it in Windows Graphics settings and restart Windows. This application will not change that setting.");
        else if (snapshot.HagsEnabled == null && !unsupportedGpu)
            reasons.Add(string.IsNullOrWhiteSpace(snapshot.HagsDetail)
                ? "Hardware-accelerated GPU scheduling could not be read from the graphics driver registry value."
                : snapshot.HagsDetail);
        else if (snapshot.HagsEnabled == true)
            features.Add("Hardware-accelerated GPU scheduling is enabled.");

        features.Add("Graphics API required by the mod: D3D12.");
        features.Add("Frame generation multipliers depend on the installed runtime and the game's own Streamline plugin.");

        SystemCompatibilityStatus status;
        if (!windowsOk || unsupportedGpu)
            status = SystemCompatibilityStatus.Unsupported;
        else if (snapshot.HagsEnabled == false || driverTooOld)
            status = SystemCompatibilityStatus.ActionRequired;
        else if (unknownArchitecture || primary!.IsNewerArchitecture || driverPtxOnly || snapshot.HagsEnabled == null || !driverKnown)
            status = SystemCompatibilityStatus.PotentiallyCompatible;
        else
            status = SystemCompatibilityStatus.Compatible;

        if (status == SystemCompatibilityStatus.Compatible)
            reasons.Insert(0, "The GPU, operating system, driver, and HAGS state match the requirements documented for the current DLSSG for SM86 release.");
        else if (status == SystemCompatibilityStatus.PotentiallyCompatible)
            reasons.Insert(0, "The hardware looks usable, but at least one requirement could not be fully verified against the upstream documentation.");

        var summary = status switch
        {
            SystemCompatibilityStatus.Compatible => "Compatible",
            SystemCompatibilityStatus.PotentiallyCompatible => "Potentially compatible",
            SystemCompatibilityStatus.ActionRequired => "Action required",
            _ => "Unsupported"
        };

        return new SystemCompatibilityReport
        {
            Status = status,
            Summary = summary,
            Reasons = reasons,
            Gpus = snapshot.Gpus,
            PrimaryGpu = primary,
            OperatingSystem = snapshot.OperatingSystem,
            NvidiaDriverVersion = driverKnown ? NvidiaDriverVersion.ToFriendly(driver!) : driver,
            HagsEnabled = snapshot.HagsEnabled,
            HagsDetail = snapshot.HagsDetail,
            Features = features,
            InstallAllowed = status != SystemCompatibilityStatus.Unsupported,
            RequiresAcknowledgement = status is SystemCompatibilityStatus.ActionRequired or SystemCompatibilityStatus.PotentiallyCompatible
        };
    }

    public static GpuDevice? SelectPrimary(IReadOnlyList<GpuDevice> gpus)
    {
        return gpus
            .Where(gpu => !gpu.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(gpu => gpu.Vendor == GpuVendor.Nvidia && gpu.IsSupportedRtx)
            .ThenByDescending(gpu => gpu.Vendor == GpuVendor.Nvidia)
            .ThenByDescending(gpu => gpu.DedicatedVideoMemory)
            .FirstOrDefault();
    }
}

public sealed class GpuDetectionService : IGpuDetectionService
{
    private readonly IHardwareProbe _probe;
    private readonly ISystemCompatibilityService _compatibility;

    public GpuDetectionService(IHardwareProbe probe, ISystemCompatibilityService compatibility)
    {
        _probe = probe;
        _compatibility = compatibility;
    }

    public SystemCompatibilityReport Detect()
    {
        var adapters = _probe.QueryAdapters().Where(adapter => !adapter.IsSoftware).ToList();
        var os = _probe.QueryOperatingSystem();
        var driver = _probe.QueryNvidiaDriverVersion();
        var hags = _probe.QueryHags();
        var friendly = string.IsNullOrWhiteSpace(driver) ? null : NvidiaDriverVersion.ToFriendly(driver);
        var gpus = adapters.Select(adapter => GpuArchitectureCatalog.Describe(adapter, friendly ?? adapter.DriverVersion)).ToList();
        var product = os.Build >= 22000 && os.ProductName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)
            ? os.ProductName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase)
            : os.ProductName;
        var osText = string.Join(" ", new[] { product, os.DisplayVersion, os.Build > 0 ? $"build {os.Build}" : "", os.Is64Bit ? "x64" : "x86" }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return _compatibility.Evaluate(new HardwareSnapshot
        {
            Gpus = gpus,
            OperatingSystem = string.IsNullOrWhiteSpace(osText) ? "Unknown Windows" : osText,
            Is64Bit = os.Is64Bit,
            BuildNumber = os.Build,
            NvidiaDriverVersion = friendly,
            HagsEnabled = hags.Enabled,
            HagsDetail = hags.Detail
        });
    }
}

public sealed class WindowsHardwareProbe : IHardwareProbe
{
    public IReadOnlyList<RawGpuAdapter> QueryAdapters()
    {
        try
        {
            var dxgi = DxgiEnumerator.Enumerate();
            if (dxgi.Count > 0)
                return dxgi;
        }
        catch (Exception ex)
        {
            AppLog.Error("DXGI adapter enumeration failed.", ex);
        }
        return QueryWmi();
    }

    public RawOperatingSystem QueryOperatingSystem()
    {
        var os = new RawOperatingSystem { Is64Bit = Environment.Is64BitOperatingSystem };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            os.ProductName = key?.GetValue("ProductName") as string ?? "";
            os.DisplayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            var build = key?.GetValue("CurrentBuild") as string ?? key?.GetValue("CurrentBuildNumber") as string;
            if (int.TryParse(build, out var number))
                os.Build = number;
        }
        catch (Exception ex)
        {
            AppLog.Error("Operating system registry read failed.", ex);
        }
        return os;
    }

    public HagsProbeResult QueryHags()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var value = key?.GetValue("HwSchMode");
            if (value == null)
                return new HagsProbeResult { Enabled = null, Detail = "HwSchMode is not set. Windows is using its default, which this application cannot confirm." };
            var mode = Convert.ToInt32(value);
            return mode switch
            {
                2 => new HagsProbeResult { Enabled = true, Detail = "HwSchMode is 2 (on)." },
                1 => new HagsProbeResult { Enabled = false, Detail = "HwSchMode is 1 (off)." },
                _ => new HagsProbeResult { Enabled = null, Detail = $"HwSchMode is {mode}, which is not a documented on/off value." }
            };
        }
        catch (Exception ex)
        {
            return new HagsProbeResult { Enabled = null, Detail = "HAGS could not be read: " + ex.Message };
        }
    }

    public string? QueryNvidiaDriverVersion()
    {
        var fromSmi = TryNvidiaSmi();
        if (!string.IsNullOrWhiteSpace(fromSmi))
            return fromSmi;
        var fromRegistry = TryUninstallDisplayVersion();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
            return fromRegistry;
        foreach (var adapter in QueryWmi())
        {
            if (adapter.VendorId == 0x10DE && !string.IsNullOrWhiteSpace(adapter.DriverVersion))
                return NvidiaDriverVersion.ToFriendly(adapter.DriverVersion);
        }
        return null;
    }

    private static string? TryNvidiaSmi()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        if (!File.Exists(path))
            return null;
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = "--query-gpu=driver_version --format=csv,noheader",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryUninstallDisplayVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null)
                    continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string ?? "";
                    if (!display.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || !display.Contains("Graphics Driver", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var version = sub?.GetValue("DisplayVersion") as string;
                    if (!string.IsNullOrWhiteSpace(version))
                        return version;
                }
            }
            catch
            {
                // Try the next registry view.
            }
        }
        return null;
    }

    private static List<RawGpuAdapter> QueryWmi()
    {
        var list = new List<RawGpuAdapter>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID, DriverVersion FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                var name = item["Name"]?.ToString() ?? "Unknown";
                var pnp = item["PNPDeviceID"]?.ToString() ?? "";
                uint vendor = 0;
                uint device = 0;
                var ven = Regex.Match(pnp, @"VEN_([0-9A-Fa-f]{4})");
                var dev = Regex.Match(pnp, @"DEV_([0-9A-Fa-f]{4})");
                if (ven.Success)
                    vendor = Convert.ToUInt32(ven.Groups[1].Value, 16);
                if (dev.Success)
                    device = Convert.ToUInt32(dev.Groups[1].Value, 16);
                ulong memory = 0;
                if (item["AdapterRAM"] != null)
                    memory = Convert.ToUInt64(item["AdapterRAM"]);
                list.Add(new RawGpuAdapter
                {
                    Name = name,
                    VendorId = vendor,
                    DeviceId = device,
                    DedicatedVideoMemory = memory,
                    DriverVersion = item["DriverVersion"]?.ToString(),
                    IsSoftware = name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("WMI video controller query failed.", ex);
        }
        return list;
    }
}

internal static class DxgiEnumerator
{
    public static List<RawGpuAdapter> Enumerate()
    {
        var list = new List<RawGpuAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        var hr = CreateDXGIFactory1(ref iid, out var factory);
        if (hr != 0 || factory == IntPtr.Zero)
            return list;
        try
        {
            var factoryTable = Marshal.ReadIntPtr(factory);
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(Marshal.ReadIntPtr(factoryTable, 12 * IntPtr.Size));
            for (uint index = 0; index < 16; index++)
            {
                var adapterHr = enumAdapters(factory, index, out var adapter);
                if (adapterHr == unchecked((int)0x887A0002) || adapter == IntPtr.Zero)
                    break;
                try
                {
                    var adapterTable = Marshal.ReadIntPtr(adapter);
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(Marshal.ReadIntPtr(adapterTable, 10 * IntPtr.Size));
                    var desc = new Desc1();
                    if (getDesc(adapter, ref desc) != 0)
                        continue;
                    list.Add(new RawGpuAdapter
                    {
                        Name = desc.Description,
                        VendorId = desc.VendorId,
                        DeviceId = desc.DeviceId,
                        DedicatedVideoMemory = desc.DedicatedVideoMemory,
                        Luid = unchecked((long)desc.AdapterLuid),
                        IsSoftware = (desc.Flags & 2) != 0
                    });
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return list;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr self, ref Desc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Desc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public ulong AdapterLuid;
        public uint Flags;
    }
}
