using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace AeroStart.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _owns;
    public bool IsPrimary => _owns;

    public SingleInstanceService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var suffix = $"{identity.User?.Value ?? Environment.UserName}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = "AeroStart." + suffix;
        _mutex = new Mutex(true, @"Local\" + _pipeName, out _owns);
    }

    public async Task<bool> ForwardAsync(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe);
            await writer.WriteLineAsync(command);
            await writer.FlushAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { return false; }
    }

    public async Task ListenAsync(Action<string> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (command is "settings" or "menu" or "background" or "diagnose-taskbar") received(command);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { if (!_stop.IsCancellationRequested) await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owns) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
