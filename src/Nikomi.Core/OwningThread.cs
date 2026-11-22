namespace Nikomi.Core;

/// <summary>
/// The thread an object that is not thread-safe has been bound to, and the check that it is used there.
/// Until it is bound, any one thread at a time may use the object; keeping it to one at a time is then
/// the caller's job (the console program does it with a lock).
/// </summary>
internal sealed class OwningThread(string what)
{
    private int _id;   // Environment.CurrentManagedThreadId; 0 while not bound

    public void Bind() => _id = Environment.CurrentManagedThreadId;

    /// <summary>Throws when the object is bound and this is not its thread.</summary>
    public void Check()
    {
        int id = _id;
        if (id != 0 && id != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException(
                $"{what} is not thread-safe and belongs to thread {id}; it was used on thread {Environment.CurrentManagedThreadId}.");
    }
}
