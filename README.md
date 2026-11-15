# RedisDistributedLock (extracted from [redlock-cs](https://github.com/KidFashion/redlock-cs/) and improved)

Distributed locks on Redis or [Valkey](https://valkey.io/) for .NET, using the
[Redlock](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/) algorithm, with background lease
renewal and a notification when a lock is lost.

The library needs nothing beyond `SET NX PX` and Lua scripting, and is tested against both Redis and Valkey. Wherever
this document says Redis, Valkey works the same way, with the same connection strings.

## Installation

The package is published to [GitHub Packages](https://github.com/dradoaica/RedisDistributedLock/packages):

```bash
dotnet add package RedisDistributedLock
```

## Registration

```csharp
// A single Redis instance
services.AddRedisDistributedLock("localhost:6379");

// Redlock across independent Redis masters: a lock needs a majority of them (here 2 of 3)
services.AddRedisDistributedLock(["redis1:6379", "redis2:6379", "redis3:6379"]);

// Connections you already have; the lock manager doesn't dispose of them
services.AddRedisDistributedLock(options => options.ConnectionMultiplexers.Add(multiplexer));
```

The masters must be independent: no replication between them and not nodes of the same cluster. Use an odd number, so
that a majority survives the loss of a minority. A master that is down, at startup or later, doesn't prevent locking as
long as a majority is reachable.

## Usage

Resolve `IDistributedLockManager` (a singleton) and pick one of the two kinds of lock.

### A lock that renews itself

For work of unknown duration. The lease is renewed in the background until the handle is disposed, which also releases
the lock:

```csharp
await using var handle = await lockManager.TryCreateRenewableLockHandleAsync(
    "my-resource",
    lockPeriod: TimeSpan.FromSeconds(30),
    leasePeriod: TimeSpan.FromSeconds(30), // renewed every leasePeriod / 2
    RenewalRetryStrategy.ExponentialBackoff,
    preExecuteCheck: null,
    cancellationToken
);
if (handle == null)
{
    return; // someone else holds the lock
}

// handle.LockLost is canceled if the lock can't be kept: stop working on the resource
using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLost);
await DoWorkAsync(cts.Token);
```

- `leasePeriod` must not be longer than `lockPeriod`. The lease is renewed every `leasePeriod / 2`, which leaves at
  least half of the lock period to retry a renewal that fails.
- The retry strategy chooses how failed renewals are retried: `ExponentialBackoff` retries as soon as possible and then
  backs off exponentially, `Speedup` halves the delay after each failure.
- `preExecuteCheck` is called before each renewal. Returning `false` stops the renewal and cancels `LockLost`. The lock
  is then left to expire rather than released, which gives you the rest of the lock period to stop.
- `LockLost` is canceled when the lock is held by someone else, or at the moment it expires without having been renewed
  (e.g. because a majority of the masters stayed unreachable). It is not canceled when you release the lock.

### A lock with a fixed lifetime

For short work that is sure to finish within `lockPeriod`. Nothing renews this lock; it expires by itself if it isn't
released:

```csharp
var distributedLock = await lockManager.TryLockAsync("my-resource", TimeSpan.FromSeconds(30), cancellationToken);
if (distributedLock != null)
{
    try
    {
        // critical section
    }
    finally
    {
        await lockManager.ReleaseLockAsync(distributedLock, cancellationToken);
    }
}
```

`RenewAsync` extends such a lock by another `lockPeriod`. It returns `false` when the renewal failed but the lock is
still valid (try again soon), and throws `DistributedLockLostException` once the lock is gone.

Both `Try...` methods return `null` when the lock is held by someone else, after a few quick retries. Locks aren't
reentrant: acquiring a lock you already hold fails too.

A lock (`handle.InnerLock` for a renewable one) tells how long it can still be assumed to be held: `IsValid` and
`RemainingValidity`, which is somewhat less than the lock period to allow for clock drift. `LockId` is the id you
passed, `Key` the Redis key that holds the lock.

## Options

| Option                   | Default               | Description                                                                                                 |
|--------------------------|-----------------------|-------------------------------------------------------------------------------------------------------------|
| `RedisConnectionString`  |                       | Connection string of a Redis master.                                                                        |
| `RedisConnectionStrings` |                       | Connection strings of further independent Redis masters.                                                    |
| `ConnectionMultiplexers` |                       | Existing connections to independent Redis masters, used in addition to the connection strings.              |
| `LockPrefix`             | `lock_`               | Prepended to every lock id to build the Redis key.                                                          |
| `InstanceTimeout`        | `lockPeriod / 4`      | How long to wait for one master before counting it as unreachable. Never more than half of the lock period. |
| `RetryCount`             | `3`                   | How many times acquiring, renewing or releasing is retried when a majority isn't reached.                   |
| `RetryDelay`             | `200ms`               | Upper bound of the random delay before each retry.                                                          |
| `TimeProvider`           | `TimeProvider.System` | The clock that lock periods, timeouts and delays are measured with. Replaceable for tests.                  |

Invalid options (no master, a timeout that isn't positive, a negative retry count or delay) throw an
`OptionsValidationException` when the lock manager is created.

Connections created from connection strings are opened in the background, so creating the lock manager doesn't wait for
them. They always use `abortConnect=false` and fail commands right away while disconnected, so that an unreachable
master can't hold up the others. Connections you pass in keep your settings; the `InstanceTimeout` still applies to
them.

Each master must be configured only once. A master that is configured twice, e.g. once by name and once by IP address,
would get two votes, so the lock manager compares the run ids of the masters and throws an `InvalidOperationException`
from `Try...` when two of them are the same server.

Register logging to see why a lock wasn't acquired or was lost: the manager logs through
`ILogger<RedisDistributedLockManager>`, unreachable instances at `Debug` and lost locks at `Warning`.

## What a lock does and doesn't guarantee

A lock is a lease: it is yours only until it expires. A process that is paused for longer than the lock period (a long
garbage collection, a suspended VM) can resume believing that it still holds a lock that someone else has acquired in
the meantime, and `LockLost` can only tell it after the fact. Redlock also assumes that the clocks of the masters run at
roughly the same rate.

So use these locks to avoid doing the same work twice, not as the only protection of data that must never be written
concurrently. For that, also make the protected resource reject stale writers, e.g. with a version check or a
[fencing token](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html), which this library doesn't
provide.

## Upgrading from 2.x

3.0 changes the public API and some behavior:

- **Renewal retry strategy**: `TryCreateRenewableLockHandleAsync` takes a `RenewalRetryStrategy` instead of
  `bool linear`. Replace `false` with `RenewalRetryStrategy.ExponentialBackoff` and `true` with
  `RenewalRetryStrategy.Speedup`.
- **Lease period**: `leasePeriod` must not be longer than `lockPeriod` (an `ArgumentException` otherwise).
- **Renewable handle**: dispose of the handle (`await using`) to stop the renewal and release the lock, instead of
  stopping `LeaseRenewalTimer` and calling `ReleaseLockAsync`. `LeaseRenewalTimer` is gone.
- **Internal types**: `RenewableDistributedLockHandle`, `RenewableDistributedLockHandleFactory`, `RenewLeaseCommand`,
  `TaskSeriesTimer`, `LinearSpeedupStrategy` and the `ITaskSeriesTimer`, `ITaskSeriesCommand` and `IDelayStrategy`
  interfaces are no longer public.
- **Lock id**: `IDistributedLock.LockId` and `DistributedLockLostException.LockId` are the id you passed, without the
  prefix; the Redis key is `IDistributedLock.Key`. `IDistributedLock` also has `IsValid` and `RemainingValidity`, which
  your own implementations of the interface, if any, need to add.
- **Redis keys**: the prefix is always prepended. An id that already starts with it, such as `lock_orders`, used to be
  taken as the key itself and now gets the key `lock_lock_orders`: instances running 2.x and 3.x don't exclude each
  other for such ids, so don't mix the versions while such a lock is in use.
- **`LockLost`**: also canceled when `preExecuteCheck` returns `false`, and now at the moment the lock expires instead
  of at the next renewal attempt. No longer canceled when the lock is released through `ReleaseLockAsync` while the
  handle is still renewing.
- **Exceptions**: invalid options throw `OptionsValidationException`, a master configured twice throws
  `InvalidOperationException`, a disposed lock manager throws `ObjectDisposedException` (except from
  `ReleaseLockAsync`), `ReleaseLockAsync` throws `OperationCanceledException` when its token is canceled and
  `ArgumentException` for a lock of another manager.
- **Local bookkeeping**: the lock manager no longer remembers the locks it holds, so a lock that expired without being
  released can be acquired again.
- **Dependencies**: Polly is no longer used; Microsoft.Extensions.Logging.Abstractions and Microsoft.Bcl.TimeProvider
  are.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build and test.
