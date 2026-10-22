# Security Policy

## Supported Versions

RedisDistributedLock is published as a NuGet package to
[GitHub Packages](https://github.com/dradoaica/RedisDistributedLock/packages). Only the latest released version receives
security fixes.

| Version | Supported          |
|---------|--------------------|
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

If you find a security issue with an older version, please try updating to the latest version first. If for some reason
you can't update, please let us know why so that we can better understand your situation.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Report them privately through
[GitHub Security Advisories](https://github.com/dradoaica/RedisDistributedLock/security/advisories/new). Please
include:

- The affected version of RedisDistributedLock (and of StackExchange.Redis, if relevant)
- Your Redis topology (single instance, multiple independent masters, cluster, Sentinel) and version
- A description of the issue and its impact
- Steps to reproduce, ideally a minimal code sample
- Any suggested fix or mitigation

You can expect an initial response within 7 days. Once the issue is confirmed, a fix will be prepared and released, and
the advisory will be published with credit to you (unless you prefer to remain anonymous).

## Scope

Because this is a distributed locking library, "security" includes correctness of the mutual exclusion guarantee.
Examples of issues we consider in scope:

- **Mutual exclusion violations**: two clients holding the same lock at the same time under conditions the Redlock
  algorithm is meant to handle (e.g. quorum miscalculation, incorrect validity/clock-drift computation)
- **Ownership violations**: a client releasing or extending a lock it does not own (e.g. a flaw in the Lua unlock/extend
  scripts or in lock value generation)
- **Lease renewal failures** that silently leave a caller believing it still holds an expired lock
- **Secret leakage**: the Redis connection string, password or other credentials being logged, exposed in exceptions or
  otherwise disclosed by the library
- **Key injection**: lock IDs or prefixes that allow a caller to read, overwrite or delete unrelated Redis keys
- Vulnerabilities in dependencies (StackExchange.Redis, Polly, AsyncEnumerator, Microsoft.Extensions.*) that are
  exploitable through this library

## Out of Scope

- Known limitations of the Redlock algorithm itself, such as the lack of fencing tokens and sensitivity to process
  pauses (GC, VM suspension) or significant clock jumps. If your use case requires strict correctness under these
  conditions, use a consensus-based system instead.
- Insecure Redis deployments (no authentication, no TLS, Redis exposed to untrusted networks). Configure these via the
  connection string, e.g. `password=...,ssl=true`.
- Vulnerabilities in Redis server itself; please report those to the
  [Redis project](https://github.com/redis/redis/security/policy).
