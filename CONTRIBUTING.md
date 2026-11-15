# Contributing to RedisDistributedLock

Thank you for your interest in contributing to RedisDistributedLock! This library is a .NET implementation of the
[Redlock](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/) distributed locking algorithm,
originally extracted from [redlock-cs](https://github.com/KidFashion/redlock-cs/) and improved with async APIs,
dependency injection support, retries, and automatic lease renewal.

## Getting Started

If this is your first visit, a great way to begin is by tackling issues tagged as `"help wanted"` or
`"good first issue"`.

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (see `global.json`)
- Docker, to run the Redis and Valkey masters that the integration tests use

### Building

```bash
dotnet restore
dotnet build -c Release
```

The library targets `netstandard2.1`, so please avoid APIs that are not available on that target.

### Testing

```bash
docker compose up -d --wait
dotnet test
```

The unit tests (`tests/RedisDistributedLock.UnitTests`) replace the Redis masters with in-memory fakes that can be taken
down, hung or wiped, so they need no Redis. They also replace the clock (`RedisDistributedLockOptions.TimeProvider`)
with one that only the test moves, so a lock period can't run out because the machine was slow: measure time in the
library with the `TimeProvider`, never with `Stopwatch` or `Task.Delay`, and move the clock in a test with the helpers
of `FakeTime`. The integration tests (`tests/RedisDistributedLock.IntegrationTests`) run
the same tests against real Redis masters and against real Valkey masters: the three of each started by
`docker-compose.yaml` (Redis on ports 6379 to 6381, Valkey on 6382 to 6384), or the semicolon-separated connection
strings in `REDIS_TEST_CONNECTION_STRINGS` and `VALKEY_TEST_CONNECTION_STRINGS`. They are skipped when the masters
can't be reached. Set `REDIS_TESTS_REQUIRED=true`, as CI does, to make them fail instead.

Either kind can be run alone:

```bash
dotnet test --project tests/RedisDistributedLock.UnitTests
dotnet test --project tests/RedisDistributedLock.IntegrationTests
```

`scripts/check-vulnerable-packages.sh` fails when a dependency, direct or transitive, has a known vulnerability of
severity High or Critical. The Tests workflow runs all of the above on every pull request and push to `main`.

### Trying it out

```csharp
services.AddRedisDistributedLock("localhost:6379", options => options.LockPrefix = "lock_");

var lockManager = serviceProvider.GetRequiredService<IDistributedLockManager>();
var handle = await lockManager.TryLockAsync("my-resource", TimeSpan.FromSeconds(30), cancellationToken);
if (handle != null)
{
    try
    {
        // critical section
    }
    finally
    {
        await lockManager.ReleaseLockAsync(handle, cancellationToken);
    }
}
```

### Testing against multiple Redis masters

A single Redis instance means a quorum of 1, so the Redlock quorum logic is never really exercised. To exercise it,
test against multiple independent Redis masters, not just a single instance. `docker-compose.yaml` starts three of them
on ports 6379, 6380, and 6381:

```bash
docker compose up -d
```

Register them with the multi-master overload (a lock then needs 2 of 3 instances to agree):

```csharp
services.AddRedisDistributedLock(["localhost:6379", "localhost:6380", "localhost:6381"]);
```

Then simulate failures while holding a renewable lock:

- `docker compose stop redis3`: the lock should still be held and renewed (2 of 3 instances remain).
- `docker compose stop redis2`: the quorum is gone, so `IRenewableDistributedLockHandle.LockLost` should fire once
  the lock's validity runs out.
- `docker compose start redis2 redis3` to bring them back, and `docker compose down` when you're done.

A renewal waits for the slowest instance, so a master that doesn't answer delays it by up to
`RedisDistributedLockOptions.InstanceTimeout` (a quarter of `lockPeriod` by default). `leasePeriod` can't be longer than
`lockPeriod`, which leaves room for that.

The instances must be independent masters. Pointing at replicas or nodes of the same cluster doesn't give Redlock's
guarantees, because StackExchange.Redis treats them as a single deployment.

## Spot a Typo?

Typos and other small fixes are important to us — no contribution is too small! Go ahead and open a Pull Request;
there's no need to create a separate issue.

## Found a Bug or Have a Suggestion?

Please open an [issue](https://github.com/dradoaica/RedisDistributedLock/issues/new) with:

- A clear title and description
- The RedisDistributedLock version, .NET runtime and Redis or Valkey version/topology
- Steps to reproduce or a minimal code sample (for bugs)
- Whether you're interested in implementing the change yourself

**Security issues** (including anything that could let two clients hold the same lock) must be reported privately; see
[SECURITY.md](SECURITY.md).

## Pull Requests

- Branch from `main` and keep each PR focused on a single change.
- Follow the code style defined in `.editorconfig` (file-scoped namespaces, primary constructors where they fit,
  nullable
  reference types enabled). Fix any new compiler warnings.
- Use `ConfigureAwait(false)` on awaited calls inside the library.
- Don't bump `<Version>` in `RedisDistributedLock.csproj` in your PR. A release is a published GitHub release: the
  Release workflow runs the tests, packs the project with the version of the release's tag (e.g. `3.0.0`) and pushes it
  to GitHub Packages (and to nuget.org, when a `NUGET_API_KEY` secret is set).
- Update `README.md` when you change public APIs or behavior.

## Contribution Principles

- **Correctness first**: Changes to locking, unlocking, extending, or lease renewal must preserve the Redlock
  guarantees:
  majority quorum, validity time accounting for elapsed time and clock drift, and lock ownership checked atomically in
  Lua scripts. Please explain in the PR why the change is safe, including failure scenarios (Redis instance down,
  network partition, slow responses).
- **Backward compatibility**: The public API (`IDistributedLockManager`, `IDistributedLock`,
  `IRenewableDistributedLockHandle`, `RenewalRetryStrategy`, `RedisDistributedLockManager`,
  `RedisDistributedLockOptions`, `AddRedisDistributedLock`, `DistributedLockLostException`) is used by downstream
  applications. Avoid breaking changes; if one is necessary, call it out clearly.
- **Minimal dependencies**: The library intentionally depends only on StackExchange.Redis, Microsoft.Extensions
  abstractions and Microsoft.Bcl.TimeProvider (the clock, so that tests can replace it). New dependencies need a strong
  justification.
- **Reliability**: We welcome improvements to error handling, retry/backoff behavior, and lease renewal. Describe the
  scenario that motivated the change and how you verified it.
- **Performance**: Include before/after measurements (e.g. BenchmarkDotNet) for performance changes. Optimizations
  that make the locking logic harder to reason about will receive extra scrutiny.
- **Tests**: Add tests for new behavior and for every bug fix. Failure scenarios (instance down, slow or restarted)
  belong in the unit tests, where they are deterministic; anything that depends on what Redis itself does belongs in
  the integration tests.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE.md).

## Code of Conduct

To ensure a welcoming and positive environment for everyone, please adhere to our
[Code of Conduct](CODE_OF_CONDUCT.md).
