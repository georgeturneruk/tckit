using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace TcKit.Adapters.Automation;

/// <summary>
/// Marshals work onto a single long-lived STA thread. The TwinCAT Automation Interface (DTE / COM)
/// is apartment-threaded; MCP tool calls arrive on arbitrary thread-pool (MTA) threads, so every
/// COM operation must hop onto a dedicated STA thread. Work items run one at a time in submission
/// order, which also serialises access to the shared DTE session.
/// </summary>
internal sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaExecutor()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TcKit-COM-STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public T Run<T>(Func<T> func)
    {
        Exception? failure = null;
        T result = default!;
        using var done = new ManualResetEventSlim(false);

        _queue.Add(() =>
        {
            try
            {
                result = func();
            }
#pragma warning disable CA1031 // Captured and rethrown on the caller's thread below, preserving the stack.
            catch (Exception ex)
            {
                failure = ex;
            }
#pragma warning restore CA1031
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
