namespace MatchLens;

// FIFO within each priority; background details cannot jump queued overview stats.
public sealed class PriorityGate(int capacity)
{
    sealed record Waiter(int Priority,TaskCompletionSource<bool> Ready);
    readonly object sync=new();
    readonly LinkedList<Waiter> waiting=[];
    readonly int maximum=capacity;
    int available=capacity>0?capacity:throw new ArgumentOutOfRangeException(nameof(capacity));
    public async Task WaitAsync(int priority,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();LinkedListNode<Waiter> node;
        lock(sync)
        {
            if(available>0){available--;return;}
            var waiter=new Waiter(priority,new(TaskCreationOptions.RunContinuationsAsynchronously));
            var next=waiting.First;while(next!=null&&next.Value.Priority<=priority)next=next.Next;
            node=next==null?waiting.AddLast(waiter):waiting.AddBefore(next,waiter);
        }
        using var registration=ct.Register(()=>
        {
            lock(sync)if(node.List!=null){waiting.Remove(node);node.Value.Ready.TrySetCanceled(ct);}
        });
        await node.Value.Ready.Task;
    }
    public void Release()
    {
        lock(sync)
        {
            if(waiting.First is {} node){waiting.RemoveFirst();node.Value.Ready.TrySetResult(true);}
            else if(available<maximum)available++;else throw new InvalidOperationException("Unbalanced priority gate release");
        }
    }
}
