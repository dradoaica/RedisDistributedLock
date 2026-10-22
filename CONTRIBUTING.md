# Contributing to RedisDistributedLock

Thank you for your interest in contributing to RedisDistributedLock! This library is a .NET implementation of the
[Redlock](https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/) distributed locking algorithm,
originally extracted from [redlock-cs](https://github.com/KidFashion/redlock-cs/) and improved with async APIs,
dependency injection support, retries and automatic lease renewal.

## Getting Started

If this is your first visit, a great way to begin is by tackling issues tagged as `"help wanted"` or
`"good first issue"`.

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (see `global.json`)
- A Redis server for manual testing, for example:

  ```bash
  docker run --rm -p 6379:6379 redis:latest
  ```

### Building

```bash
dotnet restore
dotnet build -c Release
```

The library targets `netstandard2.1`, so please avoid APIs that are not available on that target.

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

To exercise the Redlock quorum logic, test against multiple independent Redis masters, not just a single instance.

## Spot a Typo?

Typos and other small fixes are important to us — no contribution is too small! Go ahead and open a Pull Request;
there's
no need to create a separate issue.

## Found a Bug or Have a Suggestion?

Please open an [issue](https://github.com/dradoaica/RedisDistributedLock/issues/new) with:

- A clear title and description
- The RedisDistributedLock version, .NET runtime and Redis version/topology
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
- Don't bump `<Version>` in `RedisDistributedLock.csproj` in your PR; the maintainer does that when releasing. Every
  push
  to `main` packs the project and pushes it to GitHub Packages, skipping versions that already exist.
- Update `README.md` when you change public APIs or behavior.

## Contribution Principles

- **Correctness first**: Changes to locking, unlocking, extending or lease renewal must preserve the Redlock guarantees:
  majority quorum, validity time accounting for elapsed time and clock drift, and lock ownership checked atomically in
  Lua scripts. Please explain in the PR why the change is safe, including failure scenarios (Redis instance down,
  network partition, slow responses).
- **Backward compatibility**: The public API (`IDistributedLockManager`, `IDistributedLock`,
  `IRenewableDistributedLockHandle`, `RedisDistributedLockOptions`, `AddRedisDistributedLock`) is used by downstream
  applications. Avoid breaking changes; if one is necessary, call it out clearly.
- **Minimal dependencies**: The library intentionally depends only on StackExchange.Redis, Polly, AsyncEnumerator and
  Microsoft.Extensions abstractions. New dependencies need a strong justification.
- **Reliability**: We welcome improvements to error handling, retry/backoff behavior and lease renewal. Describe the
  scenario that motivated the change and how you verified it.
- **Performance**: Include before/after measurements (e.g. BenchmarkDotNet) for performance changes. Optimizations
  that make the locking logic harder to reason about will receive extra scrutiny.
- **Tests**: The repository doesn't have an automated test suite yet. Contributions that add one (ideally
  running against real Redis instances, e.g. via Testcontainers) are very welcome, and tests for new behavior are
  appreciated.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE.md).

## Code of Conduct

To ensure a welcoming and positive environment for everyone, please adhere to our
[Code of Conduct](CODE_OF_CONDUCT.md).
