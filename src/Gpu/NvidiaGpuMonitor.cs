using System.Runtime.InteropServices;

// ===========================================================================
// MONITORAMENTO DE GPU NVIDIA (NVML P/INVOKE)
// ===========================================================================
public static class NvidiaGpuMonitor
{
    private static bool _nvmlInicializado = false;
    private static bool _nvmlIndisponivel = false;
    private static IntPtr _deviceHandle = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct nvmlUtilization_t
    {
        public uint gpu;
        public uint memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct nvmlMemory_t
    {
        public ulong total;
        public ulong free;
        public ulong used;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libname);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlInit();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetEncoderSessions(IntPtr device, ref uint sessionCount, IntPtr sessionInfos);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out nvmlUtilization_t utilization);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);

    private static void Inicializar()
    {
        if (_nvmlInicializado || _nvmlIndisponivel) return;

        try
        {
            // Tenta pré-carregar a DLL para máxima compatibilidade com diferentes caminhos do driver
            IntPtr libHandle = LoadLibrary("nvml.dll");
            if (libHandle == IntPtr.Zero)
            {
                string nvsmiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
                if (File.Exists(nvsmiPath))
                {
                    LoadLibrary(nvsmiPath);
                }
            }

            int ret = nvmlInit();
            if (ret == 0) // NVML_SUCCESS
            {
                ret = nvmlDeviceGetHandleByIndex_v2(0, out _deviceHandle);
                if (ret == 0)
                {
                    _nvmlInicializado = true;
                    Console.WriteLine("[*] NVML de monitoramento da GPU NVIDIA inicializado com sucesso.");
                    return;
                }
            }
            
            _nvmlIndisponivel = true;
        }
        catch (DllNotFoundException)
        {
            _nvmlIndisponivel = true;
            Console.WriteLine("[!] NVML (nvml.dll) não encontrada. Monitoramento da GPU NVIDIA desabilitado.");
        }
        catch (Exception ex)
        {
            _nvmlIndisponivel = true;
            Console.WriteLine($"[!] Erro ao inicializar NVML: {ex.Message}. Monitoramento NVIDIA desabilitado.");
        }
    }

    public static (uint? encoderLoad, uint? encoderSessions, uint? gpuLoad, ulong? vramUsed, ulong? vramTotal) ObterMetricas()
    {
        Inicializar();

        if (!_nvmlInicializado)
        {
            return (null, null, null, null, null);
        }

        try
        {
            uint load = 0;
            uint samplingPeriod = 0;
            int retLoad = nvmlDeviceGetEncoderUtilization(_deviceHandle, out load, out samplingPeriod);

            uint sessionCount = 0;
            // Passar IntPtr.Zero no sessionInfos apenas para ler a quantidade na variável sessionCount
            int retSessions = nvmlDeviceGetEncoderSessions(_deviceHandle, ref sessionCount, IntPtr.Zero);

            nvmlUtilization_t utilization;
            int retUtil = nvmlDeviceGetUtilizationRates(_deviceHandle, out utilization);

            nvmlMemory_t memory;
            int retMem = nvmlDeviceGetMemoryInfo(_deviceHandle, out memory);

            uint? loadRet = (retLoad == 0) ? load : null;
            uint? sessionsRet = (retSessions == 0 || retSessions == 2) ? sessionCount : null;
            uint? gpuLoadRet = (retUtil == 0) ? utilization.gpu : null;
            ulong? vramUsedRet = (retMem == 0) ? (memory.used / 1024 / 1024) : null;
            ulong? vramTotalRet = (retMem == 0) ? (memory.total / 1024 / 1024) : null;

            return (loadRet, sessionsRet, gpuLoadRet, vramUsedRet, vramTotalRet);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }
    
    public static void Finalizar()
    {
        if (_nvmlInicializado)
        {
            try
            {
                nvmlShutdown();
            }
            catch {}
            _nvmlInicializado = false;
        }
    }
}
