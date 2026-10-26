using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MuAutoReset;

/// <summary>
/// C# port of the AutoIt packet-buffer / remote-thread lifecycle.
/// The caller supplies the machine-code stub (opcode) because its bytes and
/// the game's packet-send function address vary by client/version.
/// </summary>
public sealed class PacketSender : IDisposable
{
    private const uint ProcessAccess = 0x0008 | 0x0010 | 0x0020 | 0x0400 | 0x0002;
    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemRelease = 0x8000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    private readonly IntPtr _processHandle;
    private readonly bool _ownsHandle;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public PacketSender(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processHandle = OpenProcess(ProcessAccess, false, process.Id);
        if (_processHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không mở được process game.");
        _ownsHandle = true;
    }

    /// <summary>
    /// Writes packet bytes to a remote buffer, asks buildStub to create the
    /// version-specific machine-code stub, runs it in a remote thread, then
    /// clears the packet buffer. buildStub receives (packetBufferAddress,
    /// sendFunctionAddress, isSendPacket2); it must return raw machine-code bytes.
    /// </summary>
    public void SendPacket(string packetHex, IntPtr sendFunctionAddress,
        Func<IntPtr, IntPtr, bool, byte[]> buildStub, uint timeoutMs = 5000)
        => SendPacketCore(packetHex, sendFunctionAddress, buildStub, false, timeoutMs);

    /// <summary>Equivalent lifecycle for AutoIt's sendpacket2 variant.</summary>
    public void SendPacket2(string packetHex, IntPtr sendFunctionAddress,
        Func<IntPtr, IntPtr, bool, byte[]> buildStub, uint timeoutMs = 5000)
        => SendPacketCore(packetHex, sendFunctionAddress, buildStub, true, timeoutMs);

    private void SendPacketCore(string packetHex, IntPtr sendFunctionAddress,
        Func<IntPtr, IntPtr, bool, byte[]> buildStub, bool secondVariant, uint timeoutMs)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buildStub);
        byte[] packet = ParseHex(packetHex);
        if (packet.Length == 0) throw new ArgumentException("Packet không được rỗng.", nameof(packetHex));
        if (sendFunctionAddress == IntPtr.Zero) throw new ArgumentException("Địa chỉ hàm gửi packet chưa được đặt.", nameof(sendFunctionAddress));

        IntPtr packetBuffer = IntPtr.Zero;
        IntPtr codeBuffer = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            packetBuffer = Allocate(Math.Max(packet.Length, 1));
            codeBuffer = Allocate(1024);
            WriteExact(packetBuffer, packet);

            byte[] stub = buildStub(packetBuffer, sendFunctionAddress, secondVariant);
            if (stub == null || stub.Length == 0)
                throw new InvalidOperationException("Opcode/stub chưa được cung cấp.");
            if (stub.Length > 1024)
                throw new ArgumentException("Opcode vượt quá buffer 1024 byte.", nameof(buildStub));
            WriteExact(codeBuffer, stub);

            thread = CreateRemoteThread(_processHandle, IntPtr.Zero, UIntPtr.Zero,
                codeBuffer, IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread thất bại.");

            uint wait = WaitForSingleObject(thread, timeoutMs);
            if (wait == WaitTimeout)
                throw new TimeoutException($"Thread gửi packet chưa kết thúc sau {timeoutMs} ms.");
            if (wait != WaitObject0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForSingleObject trả về 0x{wait:X8}.");

            Thread.Sleep(1); // tương đương Sleep(1) trong AutoIt
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            // Giữ đúng ý tưởng AutoIt: xóa dữ liệu packet trước khi giải phóng buffer.
            if (packetBuffer != IntPtr.Zero)
            {
                try { WriteExact(packetBuffer, new byte[packet.Length]); } catch { /* giữ lỗi gốc */ }
                VirtualFreeEx(_processHandle, packetBuffer, UIntPtr.Zero, MemRelease);
            }
            if (codeBuffer != IntPtr.Zero)
                VirtualFreeEx(_processHandle, codeBuffer, UIntPtr.Zero, MemRelease);
        }
    }

    private IntPtr Allocate(int size)
    {
        IntPtr address = VirtualAllocEx(_processHandle, IntPtr.Zero, (UIntPtr)(uint)size,
            MemCommitReserve, PageExecuteReadWrite);
        if (address == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx thất bại.");
        return address;
    }

    private void WriteExact(IntPtr address, byte[] bytes)
    {
        if (!WriteProcessMemory(_processHandle, address, bytes, bytes.Length, out IntPtr written) || written.ToInt64() != bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory thất bại tại 0x{address.ToInt64():X}.");
    }

    public static byte[] ParseHex(string hex)
    {
        if (hex == null) throw new ArgumentNullException(nameof(hex));
        string clean = hex.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
        if ((clean.Length & 1) != 0) throw new FormatException("Chuỗi hex phải có số ký tự chẵn.");
        byte[] bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"Byte hex không hợp lệ tại vị trí {i * 2}.");
        }
        return bytes;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PacketSender));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHandle && _processHandle != IntPtr.Zero) CloseHandle(_processHandle);
    }
}
