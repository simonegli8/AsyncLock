## AsyncLock: An async/await-friendly lock

[![NuGet](https://img.shields.io/nuget/v/EstrellasDeEsperanza.AsyncLock.svg)](https://www.nuget.org/packages/EstrellasDeEsperanza.AsyncLock)

AsyncLock is an async/await-friendly lock implementation for .NET Standard, making writing code like the snippet below (mostly) possible:

```csharp
lock (_lockObject)
{
    await DoSomething();
}
```
Unlike most other so-called "async locks" for C#, AsyncLock is actually designed to support the programming paradigm `lock` encourages, not just the technical elements. You can read more about the pitfalls with other so-called asynchronous locks and the difficulties of creating a reentrance-safe implementation [here](https://neosmart.net/blog/2017/asynclock-an-asyncawait-friendly-locking-library-for-c-and-net/).

With `AsyncLock`, you don't have to worry about which thread is running what code in order to determine whether or not your locks will have any effect or if they'll be bypassed completely, you just write code the way you normally would and you'll find AsyncLock to correctly marshal access to protected code segments.

### Using AsyncLock

There are only three functions to familiarize yourself with: the `AsyncLock()` constructor and the two locking variants `Lock()`/`LockAsync()` .

`AsyncLock()` creates a new asynchronous lock. A separate AsyncLock should be used for each "critical operation" you will be performing. (Or you can use a global lock just like some people still insist on using global mutexes and semaphores. We won't judge too harshly.)

Everywhere you would normally use `lock (_lockObject)` you will now use one of

* `using (_lock.Lock())` or
* `using (await _lock.LockAsync())`

That's all there is to it!

### Async-friendly locking by design

Much like the`SemaphoreSlim` class, `AsyncLock` offers two different "wait" options, a blocking `Lock()` call and the asynchronous `LockAsync()` call. The utmost scare should be taken to never call `LockAsync()` without an `await` before it, for obvious reasons.

Upon using `LockAsync()`, `AsyncLock` will attempt to obtain exclusive access to the lock. Should that not be possible in the current state, it will cede its execution slot and return to the caller, allowing the system to marshal resources efficiently as needed without blocking until the lock becomes available. Once the lock is available, the `AsyncLock()` call will resume, transferring execution to the protected section of the code.

### AsyncLock usage example

```csharp
private class AsyncLockTest
{
    var _lock = new AsyncLock();

    void Test()
    {
        // The code below will be run immediately (likely in a new thread)
        Task.Run(async () =>
             {
                 // A first call to LockAsync() will obtain the lock without blocking
                 using (await _lock.LockAsync())
                 {
                     // A second call to LockAsync() will be recognized as being
                     // reentrant and permitted to go through without blocking.
                     using (await _lock.LockAsync())
                     {
                         // We now exclusively hold the lock for 1 minute
                         await Task.Delay(TimeSpan.FromMinutes(1));
                     }
                 }
             }).Wait(TimeSpan.FromSeconds(30));

        // This call to obtain the lock is made synchronously from the main thread.
        // It will, however, block until the asynchronous code which obtained the lock
        // above finishes.
        using (_lock.Lock())
        {
            // Now we have obtained exclusive access.
            // <Safely perform non-thread-safe operation safely here>
        }
    }
}
```

## Async Global Mutex
The class `AsyncMutexLock` works exactly like `AsyncLock`, except that it uses a cross process machine wide file lock.
It can be used as an async friendly version of a global Mutex. For this, you create a named lock by calling the constructor
`AsyncMutexLock("MyMutexName")`. You can then synchronize processes using this global lock.
If you want to create an `AsyncMutexLock` that is only usable by the current user, you can use the constructor 
`AsyncMutexLock("MyMutexName", MutexScope.User)`, for a machine wide lock, use `MutexScope.Machine`.
The default is `MutexScope.Machine`.

### Lock File
In order to synchronize `AsyncMutexLock` uses a lock file in the file system. This file should be in a secure folder,
otherwise unauthorized users can open the file and take the lock away from the process that created it, and make a
Denial of Service attack. Therefore, the lock file is made writeable and readable only by the user that created it, and
the folder for the lock files is made writeable and readable by every user, but not deletable or renamable by any user
except the owner of a lock file. The default folder for the lock files is `/var/lock/EstrellasDeEsperanza-AsyncMutexLock`
on Linux, `/var/tmp/EstrellasDeEsperanza-AsyncMutexLock` on macOS
and the BSDs, and `%ProgramData%\EstrellasDeEsperanza-AsyncMutexLock` on Windows.

You can also specify a full path to a folder that only the users that should be able to take the lock can enter,
for example `new AsyncMutexLock("/etc/my-service/my.lock")` with that folder owned by root or by a group;
whoever cannot enter the folder cannot hold the lock.

If you want to create the lock folder in an installer, call `AsyncMutexLock.CreateLockFolder("MyLock")` in the
installer.

A lock is held by whoever can open its file, even read-only, so a lock file that other users can open is a lock
that other users can hold: a denial of service for whoever made it. That is why, on Unix, the file of a named lock is for its creator
alone (mode 0600). The folder for the named locks is open to every user, like `/tmp` (mode 1777, rwxrwxrwt): every user can make locks in
it, and only the owner of a lock file (or root) can remove or rename it, so nobody can take the lock file of another away. It is
`/var/lock/EstrellasDeEsperanza-AsyncMutexLock` on Linux (`/var/lock` is `/run/lock`, which is empty after a boot and is not cleaned up
while the machine runs), `/var/tmp/EstrellasDeEsperanza-AsyncMutexLock` on macOS and the BSDs (macOS has no `/var/lock`, and
`/Library/Application Support` belongs to root), and `%ProgramData%\EstrellasDeEsperanza-AsyncMutexLock` on Windows.
Both together mean that every user can make his own locks, but a named lock is only usable by the user who made it, and a second user who
tries to use it gets an `UnauthorizedAccessException` instead of waiting for a lock that nobody holds. The same is true if a lock file
cannot be created at all.
On Debian and Ubuntu every user can make the folder under `/var/lock`. On openSUSE, Arch and others `/var/lock` belongs to root (mode 755), so
root has to make the folder once after every boot (`mkdir -m 1777 /var/lock/EstrellasDeEsperanza-AsyncMutexLock`, from a systemd
`tmpfiles.d` file, say: `d /run/lock/EstrellasDeEsperanza-AsyncMutexLock 1777 root root -`); until then a user who is not root gets an
`UnauthorizedAccessException` that says so.
Two things follow. The owner of the folder can remove any lock file in it, sticky bit or not, and in a folder that every user can make, the
first user who makes it owns it. So these locks are for processes that cooperate, not a protection against a user who wants to get in the
way. A lock that several users have to take, or that a service running as root relies on, belongs in a folder that exactly these users can
enter, given as a full path: `new AsyncMutexLock("/etc/my-service/my.lock")` with that folder owned by root or by a group; whoever cannot
enter the folder cannot hold the lock. `MutexScope.User` keeps the file in the folder of the user.
