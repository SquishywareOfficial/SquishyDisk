using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SquishyDisk.Core;
using Microsoft.Win32.SafeHandles;

namespace SquishyDisk.Windows;

// Private, local-only pipe. Both ends authenticate the actual peer PID; the random nonce binds this session.
public sealed class DriveAccessBroker : IDriveInfoService
{
    internal const string PipePrefix = "SquishyDisk-";
    internal static bool ValidPipeName(string name) => name.StartsWith(PipePrefix, StringComparison.Ordinal) && Guid.TryParseExact(name[PipePrefix.Length..], "N", out _);
    private readonly NamedPipeServerStream pipe;
    private readonly Process worker;
    private readonly SemaphoreSlim gate = new(1);
    private DriveAccessBroker(NamedPipeServerStream pipe, Process worker) { this.pipe = pipe; this.worker = worker; }
    public bool IsConnected { get { try { return pipe.IsConnected && !worker.HasExited; } catch (ObjectDisposedException) { return false; } } }
    public static async Task<DriveAccessBroker> StartAsync(CancellationToken cancellation = default)
    {
        string name = PipePrefix + Guid.NewGuid().ToString("N"), nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var server = CreateLocalPipe(name);
        Process? child = null;
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.Arguments = $"--drive-access-worker {name} {nonce} {Environment.ProcessId}";
            child = Process.Start(info) ?? throw new IOException("The administrator helper did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) || pid != child.Id) throw new IOException("Unexpected drive helper process.");
            string hello = await ReadPacket<string>(server, timeout.Token).ConfigureAwait(false);
            if (hello != nonce) throw new IOException("Drive helper authentication failed.");
            await WritePacket(server, nonce, timeout.Token).ConfigureAwait(false);
            return new(server, child);
        }
        catch { server.Dispose(); child?.Dispose(); throw; }
    }
    public async Task<DriveReply> SendAsync(DriveRequest request, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            await WritePacket(pipe, request, timeout.Token).ConfigureAwait(false);
            var response = await ReadPacket<Envelope>(pipe, timeout.Token).ConfigureAwait(false);
            if (response.Error != null) throw new IOException(response.Error);
            return response.Reply ?? throw new IOException("Empty drive helper response.");
        }
        catch (OperationCanceledException) { pipe.Dispose(); throw; }
        finally { gate.Release(); }
    }
    public static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 4 || !ValidPipeName(args[1]) || args[2].Length != 64 || !int.TryParse(args[3], out int parentId) || !SmartctlPayload.Elevated) return 2;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
            using var lifetime = new CancellationTokenSource();
            // Observe parent termination even if the pipe is idle or a device is not responding.
            _ = parent.WaitForExitAsync(lifetime.Token).ContinueWith(t => { if (t.IsCompletedSuccessfully) lifetime.Cancel(); }, TaskScheduler.Default);
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != parentId) return 4;
                await WritePacket(pipe, args[2], connect.Token).ConfigureAwait(false);
                if (await ReadPacket<string>(pipe, connect.Token).ConfigureAwait(false) != args[2]) return 5;
            }
            await using var service = new SmartctlService();
            try
            {
                while (!lifetime.IsCancellationRequested && pipe.IsConnected)
                {
                    var request = await ReadPacket<DriveRequest>(pipe, lifetime.Token).ConfigureAwait(false);
                    Envelope reply;
                    try
                    {
                        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        operation.CancelAfter(TimeSpan.FromMinutes(3));
                        reply = new(await service.SendAsync(request, operation.Token).ConfigureAwait(false), null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or TimeoutException or JsonException or FormatException or Win32Exception or OperationCanceledException)
                    { reply = new(null, ex.Message); }
                    await WritePacket(pipe, reply, lifetime.Token).ConfigureAwait(false);
                }
            }
            finally { lifetime.Cancel(); }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException or Win32Exception or UnauthorizedAccessException or JsonException) { return 1; }
    }
    public ValueTask DisposeAsync() { pipe.Dispose(); worker.Dispose(); gate.Dispose(); return ValueTask.CompletedTask; }
    private record Envelope(DriveReply? Reply, string? Error);
    internal static async Task WritePacket<T>(Stream pipe, T value, CancellationToken cancellation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > 16 * 1024 * 1024) throw new IOException("Drive response is too large.");
        await pipe.WriteAsync(BitConverter.GetBytes(data.Length), cancellation).ConfigureAwait(false);
        await pipe.WriteAsync(data, cancellation).ConfigureAwait(false);
        await pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }
    internal static async Task<T> ReadPacket<T>(Stream pipe, CancellationToken cancellation)
    {
        byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int size = BitConverter.ToInt32(header);
        if (size < 1 || size > 16 * 1024 * 1024) throw new IOException("Invalid drive response length.");
        byte[] data = new byte[size]; await pipe.ReadExactlyAsync(data, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new IOException("Invalid drive response.");
    }
    internal static NamedPipeServerStream CreateLocalPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + identity.User!.Value + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw new Win32Exception();
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 8, 1, 65536, 65536, 0, ref security); // overlapped, first instance, reject remote clients
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, int instances, int outputSize, int inputSize, int timeout, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, int revision, out IntPtr descriptor, out int size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
