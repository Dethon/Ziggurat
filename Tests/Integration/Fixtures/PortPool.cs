namespace Tests.Integration.Fixtures;

// The bookkeeping behind TestPort, with the band and the probe injected so its exhaustion and
// reuse rules are testable without a kernel bind per assertion.
public sealed class PortPool(int bandStart, int bandEnd, Func<int, bool> isFree)
{
    // Every number currently out or ever burnt. The probe is a real bind, so it can only say the
    // port was free at the moment it asked — remembering the answer is what makes a repeat
    // impossible until the holder gives the number back.
    private readonly HashSet<int> _issued = [];
    private readonly Queue<int> _released = new();
    private readonly Lock _gate = new();

    private int _next = bandStart;

    public int Get()
    {
        // Released ports first, then the walk: a port this pool holds is never offered to two
        // callers at once, so the walk only pays for ports something else on the machine has.
        while (true)
        {
            int candidate;
            lock (_gate)
            {
                if (_released.TryDequeue(out candidate))
                {
                    _issued.Add(candidate);
                }
                else
                {
                    candidate = _next++;
                    if (candidate >= bandEnd)
                    {
                        throw new InvalidOperationException(
                            $"No unused loopback port left below {bandEnd}; "
                            + "the reserved band looks exhausted.");
                    }

                    if (!_issued.Add(candidate))
                    {
                        continue;
                    }
                }
            }

            // A released port that fails the probe stays in _issued and out of the queue: burnt,
            // never handed out, rather than spun on while whoever still binds it keeps it busy.
            if (isFree(candidate))
            {
                return candidate;
            }
        }
    }

    // A port comes back only from the caller it was issued to, after whatever bound it has
    // stopped. Removing before enqueuing is what makes a double release a no-op instead of the
    // same number in the queue twice — which would be two callers holding one port.
    public void Release(int port)
    {
        lock (_gate)
        {
            if (_issued.Remove(port))
            {
                _released.Enqueue(port);
            }
        }
    }
}