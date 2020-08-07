namespace RedisDistributedLock.Interfaces
{
    public interface IDistributedLock
    {
        /// <summary>
        /// The Lock identity.  
        /// </summary>
        string LockId { get; }
    }
}