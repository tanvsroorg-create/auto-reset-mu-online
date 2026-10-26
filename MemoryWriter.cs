using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuAutoReset;

/// <summary>Process-memory helper compatible with the methods used by MainForm.</summary>
public sealed class MemoryWriter : IDisposable
{
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private readonly Process _process;
    private readonly IntPtr _handle;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public MemoryWriter(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, process.Id);
        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Không thể mở process. Hãy chạy ứng dụng với quyền phù hợp.");
    }

    public IntPtr GetModuleBaseAddress(Process process, string moduleName)
    {
        foreach (ProcessModule module in process.Modules)
            if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return module.BaseAddress;
        throw new InvalidOperationException($"Không tìm thấy module {moduleName}.");
    }

    public ushort ReadUInt16(IntPtr address)
    {
        byte[] buffer = Read(address, sizeof(ushort));
        return BitConverter.ToUInt16(buffer, 0);
    }

    public uint ReadUInt32(IntPtr address)
    {
        byte[] buffer = Read(address, sizeof(uint));
        return BitConverter.ToUInt32(buffer, 0);
    }

    public void WriteUInt16(IntPtr address, ushort value) => Write(address, BitConverter.GetBytes(value));

    public IntPtr ResolvePointerChainAddress(IntPtr moduleBase, int baseOffset, params int[] offsets)
    {
        IntPtr address = new(unchecked((long)ReadUInt32(IntPtr.Add(moduleBase, baseOffset))));
        foreach (int offset in offsets)
        {
            if (address == IntPtr.Zero) throw new InvalidOperationException("Pointer chain trả về địa chỉ null.");
            address = new IntPtr(unchecked((long)ReadUInt32(IntPtr.Add(address, offset))));
        }
        return address;
    }

private byte[] Read(IntPtr address, int size)
{
    ThrowIfDisposed();

    byte[] buffer = new byte[size];

    bool success = ReadProcessMemory(
        _handle,
        address,
        buffer,
        size,
        out IntPtr read);

    long bytesRead = read.ToInt64();

    if (!success || bytesRead != size)
    {
        int error = Marshal.GetLastWin32Error();

        throw new System.ComponentModel.Win32Exception(
            error,
            $"ReadProcessMemory thất bại: " +
            $"Address=0x{address.ToInt64():X}, " +
            $"Size={size}, Read={bytesRead}, " +
            $"Win32Error={error}");
    }

    return buffer;
}

    private void Write(IntPtr address, byte[] buffer)
    {
        ThrowIfDisposed();
        if (!WriteProcessMemory(_handle, address, buffer, buffer.Length, out IntPtr written) || written.ToInt64() != buffer.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Ghi bộ nhớ thất bại tại 0x{address.ToInt64():X}.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MemoryWriter));
        if (_process.HasExited) throw new InvalidOperationException("Process game đã thoát.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }
}
