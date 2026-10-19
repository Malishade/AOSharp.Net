using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AOSharp.Injection
{
    /// <summary>
    /// Injects NativeHost.dll into the 32-bit game process from this 64-bit loader.
    ///
    /// Reloaded.Injector used to do this. It assembles its call stub with FASM, which needs a
    /// scratch buffer below 2GB, and WebView2's Chromium reserves the whole low 4GB of the host
    /// process — so the allocation can never succeed and Reloaded's retry loop spins for ~15
    /// minutes (rescanning the entire address space 3x per 64KB step) before it finally throws.
    /// Calling LoadLibraryW on a remote thread needs no low memory at all.
    /// </summary>
    public static class NativeInjector
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint LIST_MODULES_32BIT = 0x01;
        private const uint LIST_MODULES_64BIT = 0x02;
        private const uint WAIT_OBJECT_0 = 0x0;
        private const uint INJECT_TIMEOUT_MS = 15000;

        public static bool Inject(Process targetProcess)
        {
            try
            {
                var dllPath = Path.Combine(AppContext.BaseDirectory, "NativeHost.dll");

                if (!File.Exists(dllPath))
                {
                    Log.Error($"[Inject] Bootstrap DLL not found at: {dllPath}");
                    return false;
                }

                if (targetProcess == null || targetProcess.HasExited)
                {
                    Log.Error("[Inject] Target process is not running.");
                    return false;
                }

                var handle = targetProcess.Handle;

                if (!IsWow64Process(handle, out var targetIsWow64))
                {
                    Log.Error($"[Inject] IsWow64Process failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                var moduleFilter = targetIsWow64 ? LIST_MODULES_32BIT : LIST_MODULES_64BIT;

                if (GetRemoteModuleBase(handle, moduleFilter, Path.GetFileName(dllPath)) != IntPtr.Zero)
                {
                    Log.Information("[Inject] NativeHost.dll is already loaded in the target.");
                    return true;
                }

                var kernel32Base = GetRemoteModuleBase(handle, moduleFilter, "kernel32.dll");
                if (kernel32Base == IntPtr.Zero)
                {
                    Log.Error("[Inject] Could not locate kernel32.dll in the target process.");
                    return false;
                }

                // RVAs are identical on disk and in memory, so the matching kernel32 on disk gives
                // us the offset of LoadLibraryW without reading the target's export table.
                var kernel32Path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    targetIsWow64 ? "SysWOW64" : "System32",
                    "kernel32.dll");

                if (!TryGetExportRva(kernel32Path, "LoadLibraryW", out var loadLibraryRva, out var rvaError))
                {
                    Log.Error($"[Inject] {rvaError}");
                    return false;
                }

                var loadLibraryAddress = (IntPtr)((ulong)kernel32Base + loadLibraryRva);

                return CallLoadLibrary(handle, loadLibraryAddress, dllPath);
            }
            catch (Exception ex)
            {
                Log.Error($"[Inject] Injection failed: {ex.Message}");
                return false;
            }
        }

        private static bool CallLoadLibrary(IntPtr handle, IntPtr loadLibraryAddress, string dllPath)
        {
            var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            var remotePath = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)pathBytes.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

            if (remotePath == IntPtr.Zero)
            {
                Log.Error($"[Inject] VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            var threadOwnsBuffer = false;
            try
            {
                if (!WriteProcessMemory(handle, remotePath, pathBytes, (UIntPtr)pathBytes.Length, out _))
                {
                    Log.Error($"[Inject] WriteProcessMemory failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                var thread = CreateRemoteThread(handle, IntPtr.Zero, 0, loadLibraryAddress, remotePath, 0, out _);
                if (thread == IntPtr.Zero)
                {
                    Log.Error($"[Inject] CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                try
                {
                    if (WaitForSingleObject(thread, INJECT_TIMEOUT_MS) != WAIT_OBJECT_0)
                    {
                        // The thread may still be reading the path, so leave the buffer allocated.
                        threadOwnsBuffer = true;
                        Log.Error($"[Inject] LoadLibraryW did not return within {INJECT_TIMEOUT_MS} ms.");
                        return false;
                    }

                    if (!GetExitCodeThread(thread, out var moduleHandle))
                    {
                        Log.Error($"[Inject] GetExitCodeThread failed: {Marshal.GetLastWin32Error()}");
                        return false;
                    }

                    if (moduleHandle == 0)
                    {
                        Log.Error("[Inject] LoadLibraryW returned NULL in the target process.");
                        return false;
                    }

                    Log.Information($"[Inject] NativeHost.dll loaded at 0x{moduleHandle:X} in the target process.");
                    return true;
                }
                finally
                {
                    CloseHandle(thread);
                }
            }
            finally
            {
                if (!threadOwnsBuffer)
                    VirtualFreeEx(handle, remotePath, UIntPtr.Zero, MEM_RELEASE);
            }
        }

        private static IntPtr GetRemoteModuleBase(IntPtr handle, uint filter, string moduleName)
        {
            var modules = new IntPtr[1024];
            var size = (uint)(modules.Length * IntPtr.Size);

            if (!EnumProcessModulesEx(handle, modules, size, out var needed, filter))
                return IntPtr.Zero;

            var count = Math.Min((int)(needed / IntPtr.Size), modules.Length);
            var name = new StringBuilder(260);

            for (var i = 0; i < count; i++)
            {
                name.Clear();
                if (GetModuleBaseName(handle, modules[i], name, (uint)name.Capacity) == 0)
                    continue;

                if (string.Equals(name.ToString(), moduleName, StringComparison.OrdinalIgnoreCase))
                    return modules[i];
            }

            return IntPtr.Zero;
        }

        /// <summary>Reads the RVA of a named export straight out of the PE export directory.</summary>
        private static bool TryGetExportRva(string peFilePath, string exportName, out uint rva, out string error)
        {
            rva = 0;
            error = null;

            if (!File.Exists(peFilePath))
            {
                error = $"{peFilePath} not found.";
                return false;
            }

            var pe = File.ReadAllBytes(peFilePath);

            var peHeader = BitConverter.ToInt32(pe, 0x3C);
            if (BitConverter.ToUInt32(pe, peHeader) != 0x00004550) // "PE\0\0"
            {
                error = $"{peFilePath} is not a PE image.";
                return false;
            }

            var sectionCount = BitConverter.ToUInt16(pe, peHeader + 6);
            var optionalHeaderSize = BitConverter.ToUInt16(pe, peHeader + 20);
            var optionalHeader = peHeader + 24;

            var magic = BitConverter.ToUInt16(pe, optionalHeader);
            var dataDirectories = optionalHeader + (magic == 0x20B ? 112 : 96); // PE32+ : PE32

            var exportDirRva = BitConverter.ToUInt32(pe, dataDirectories);
            var exportDirSize = BitConverter.ToUInt32(pe, dataDirectories + 4);
            if (exportDirRva == 0)
            {
                error = $"{peFilePath} has no export directory.";
                return false;
            }

            var sections = optionalHeader + optionalHeaderSize;

            uint RvaToOffset(uint value)
            {
                for (var i = 0; i < sectionCount; i++)
                {
                    var section = sections + i * 40;
                    var sectionRva = BitConverter.ToUInt32(pe, section + 12);
                    var sectionSize = BitConverter.ToUInt32(pe, section + 16);
                    if (value >= sectionRva && value < sectionRva + sectionSize)
                        return BitConverter.ToUInt32(pe, section + 20) + (value - sectionRva);
                }
                return value; // still inside the headers
            }

            var exportDir = (int)RvaToOffset(exportDirRva);
            var nameCount = BitConverter.ToUInt32(pe, exportDir + 24);
            var functionsOffset = (int)RvaToOffset(BitConverter.ToUInt32(pe, exportDir + 28));
            var namesOffset = (int)RvaToOffset(BitConverter.ToUInt32(pe, exportDir + 32));
            var ordinalsOffset = (int)RvaToOffset(BitConverter.ToUInt32(pe, exportDir + 36));

            for (var i = 0; i < nameCount; i++)
            {
                var nameOffset = (int)RvaToOffset(BitConverter.ToUInt32(pe, namesOffset + i * 4));

                var end = nameOffset;
                while (pe[end] != 0)
                    end++;

                if (Encoding.ASCII.GetString(pe, nameOffset, end - nameOffset) != exportName)
                    continue;

                var ordinal = BitConverter.ToUInt16(pe, ordinalsOffset + i * 2);
                var functionRva = BitConverter.ToUInt32(pe, functionsOffset + ordinal * 4);

                if (functionRva >= exportDirRva && functionRva < exportDirRva + exportDirSize)
                {
                    error = $"{exportName} is a forwarded export in {peFilePath}.";
                    return false;
                }

                rva = functionRva;
                return true;
            }

            error = $"{exportName} was not found in {peFilePath}.";
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
            uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
            UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
            IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb,
            out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName,
            uint nSize);
    }
}
