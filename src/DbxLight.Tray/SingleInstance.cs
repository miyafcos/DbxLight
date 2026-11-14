using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DbxLight.Tray;

/// <summary>The owning thread must dispose this object, because the mutex is thread-affine.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource cancellation = new();
    private Task? server;
    public bool IsFirst { get; }
    public string PipeName { get; }

    public SingleInstance(string statePath)
    {
        string identity = WindowsIdentity.GetCurrent().User!.Value + "|" + Path.GetFullPath(statePath).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        PipeName = "DbxLight.Tray." + hash;
        mutex = new Mutex(false, @"Local\" + PipeName);
        try { IsFirst = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsFirst = true; }
    }

    public void Listen(Action<string[]> receive)
    {
        if (!IsFirst || server is not null) throw new InvalidOperationException("Pipe ownership is required.");
        server = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    byte[] header = new byte[4];
                    await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                    int length = BitConverter.ToInt32(header);
                    if (length is < 2 or > 65536) throw new InvalidDataException("Invalid pipe payload.");
                    byte[] body = new byte[length];
                    await pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                    string[] args = JsonSerializer.Deserialize<string[]>(body) ?? throw new InvalidDataException("Empty request.");
                    receive(args);
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or ArgumentException)
                {
                    // A malformed/disconnected client must not take down the resident application.
                }
            }
        });
    }

    public async Task ForwardAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(args);
        if (body.Length > 65536) throw new ArgumentException("Request is too large.");
        await pipe.WriteAsync(BitConverter.GetBytes(body.Length), timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        byte[] ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
        if (ack[0] != 1) throw new IOException("The resident application did not accept the request.");
    }

    public void Dispose()
    {
        cancellation.Cancel();
        server?.GetAwaiter().GetResult();
        cancellation.Dispose();
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
