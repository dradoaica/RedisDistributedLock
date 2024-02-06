﻿using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

/// <summary>Defines a recurring command that may fail gracefully.</summary>
public interface IRecurrentCommand
{
    /// <summary>Attempts to execute the command.</summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A <see cref="Task" /> that will execute the command. The task's result is <see langword="false" /> if the
    ///     command fails gracefully; otherwise <see langword="true" />.
    /// </returns>
    /// <remarks>
    ///     The task completes successfully with a <see langword="false" /> result rather than faulting to indicate a
    ///     graceful failure.
    /// </remarks>
    Task<bool> TryExecuteAsync(CancellationToken cancellationToken);
}
