using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UltraZip.Services;

public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\UltraZip.SingleInstance";
    private const string PipeName = "UltraZip.Commands";
    private static readonly byte[] Acknowledgement = Encoding.UTF8.GetBytes("OK");

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
    }

    public bool IsPrimary => _ownsMutex;
    public event Action<IReadOnlyList<string>>? RequestReceived;

    public void StartServer()
    {
        if (!_ownsMutex) throw new InvalidOperationException("只有主实例可以启动命令管道。");
        _serverTask ??= Task.Run(ServerLoopAsync);
    }

    public async Task<bool> TryForwardAsync(IReadOnlyList<string> args)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(args);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(1000);
                await client.WriteAsync(payload);
                await client.FlushAsync();
                var acknowledgement = new byte[2];
                int read = await client.ReadAsync(acknowledgement);
                return read == acknowledgement.Length && acknowledgement.AsSpan().SequenceEqual(Acknowledgement);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                await Task.Delay(150);
            }
        }
        return false;
    }

    private async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                byte[] payload = await ReadMessageAsync(server, _cts.Token);
                var args = JsonSerializer.Deserialize<string[]>(payload);
                if (args != null) RequestReceived?.Invoke(args);
                await server.WriteAsync(Acknowledgement, _cts.Token);
                await server.FlushAsync(_cts.Token);
                server.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
    }

    private static async Task<byte[]> ReadMessageAsync(NamedPipeServerStream stream,
        CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        do
        {
            int read = await stream.ReadAsync(buffer, ct);
            message.Write(buffer, 0, read);
        } while (!stream.IsMessageComplete);
        return message.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask?.Wait(500); } catch (AggregateException) { }
        _cts.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
