#if !NETSTANDARD1_3
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EstrellasDeEsperanza.AsyncLock;

public enum MutexScope { Machine, User }

public class AsyncMutexLock: IDisposable
{
    private SemaphoreSlim _reentrancy = new SemaphoreSlim(1, 1);
    private int _reentrances = 0;
    // We are using this SemaphoreSlim like a posix condition variable.
    // We only want to wake waiters, one or more of whom will try to obtain
    // a different lock to do their thing. So long as we can guarantee no
    // wakes are missed, the number of awakees is not important.
    // Ideally, this would be "friend" for access only from InnerLock, but
    // whatever.
    internal SemaphoreSlim _retry = new SemaphoreSlim(0, 1);
    private const long UnlockedId = 0x00; // "owning" task id when unlocked
    internal long _owningId = UnlockedId;
    internal int _owningThreadId = (int)UnlockedId;
    private static long AsyncStackCounter = 0;
    public string FileName;
    // An AsyncLocal<T> is not really the task-based equivalent to a ThreadLocal<T>, in that
    // it does not track the async flow (as the documentation describes) but rather it is
    // associated with a stack snapshot. Mutation of the AsyncLocal in an await call does
    // not change the value observed by the parent when the call returns, so if you want to
    // use it as a persistent async flow identifier, the value needs to be set at the outer-
    // most level and never touched internally.
    private static readonly AsyncLocal<long> _asyncId = new AsyncLocal<long>();
    private static long AsyncId => _asyncId.Value;

#if NETSTANDARD1_3
    private static int ThreadCounter = 0x00;
    private static ThreadLocal<int> LocalThreadId = new ThreadLocal<int>(() => ++ThreadCounter);
    private static int ThreadId => LocalThreadId.Value;
#else
    private static int ThreadId => Thread.CurrentThread.ManagedThreadId;
#endif
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
    public static bool IsMac => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

    public AsyncMutexLock(string name, MutexScope scope = MutexScope.Machine)
    {
        this.FileName = LockFileName(name, scope);
    }

#if !DEBUG
    readonly
#endif
    struct InnerLock : IDisposable
    {
        private readonly AsyncMutexLock _parent;
        private readonly long _oldId;
        private readonly int _oldThreadId;
#if DEBUG
        private bool _disposed;
#endif

        internal InnerLock(AsyncMutexLock parent, long oldId, int oldThreadId)
        {
            _parent = parent;
            _oldId = oldId;
            _oldThreadId = oldThreadId;
#if DEBUG
            _disposed = false;
#endif
        }

        internal async Task<IDisposable> ObtainLockAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (InnerTryEnter(synchronous: false))
                {
                    break;
                }
                // We need to wait for someone to leave the lock before trying again.
                // We need to "atomically" obtain _retry and release _reentrancy, but there
                // is no equivalent to a condition variable. Instead, we call *but don't await*
                // _retry.WaitAsync(), then release the reentrancy lock, *then* await the saved task.
                var waitTask = _parent._retry.WaitAsync(cancellationToken).ConfigureAwait(false);
                _parent._reentrancy.Release();
                await waitTask;
            }

            var oldThreadId = _parent._owningThreadId;
            _parent._owningThreadId = ThreadId;

            if (_parent._reentrances == 1) // Poll for mutex
            {
                _parent._reentrancy.Release();

                while (true)
                {
                    await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (TryMutexAcquireOnce())
                        {
                            break;
                        }
                    }
                    catch
                    {
                        _parent._owningThreadId = oldThreadId;
                        // we need to release retry here, since changing owningThreadId before we actually aquire the lock
                        // might cause other threads to wait on retry. It does not hurt if we release retry too much. 
                        if (_parent._retry.CurrentCount == 0)
                        {
                            _parent._retry.Release();
                        }
                        _parent._reentrancy.Release();
                        throw;
                    }
                    _parent._reentrancy.Release();
                    await Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            _parent._owningThreadId = ThreadId;
            _parent._reentrancy.Release();
            return this;
        }

        internal async Task<IDisposable?> TryObtainLockAsync(TimeSpan timeout)
        {
            // In case of zero-timeout, don't even wait for protective lock contention
            if (timeout == TimeSpan.Zero)
            {
                if (!_parent._reentrancy.Wait(timeout)) return null;
                if (InnerTryEnter(synchronous: false))
                {
                    // Reset the owning thread id after all await calls have finished, otherwise we
                    // could be resumed on a different thread and set an incorrect value.
                    try
                    {
                        if (_parent._reentrances != 1 || TryMutexAcquireOnce())
                        {
                            _parent._owningThreadId = ThreadId;
                            _parent._reentrancy.Release();
                            return this;
                        }
                    }
                    catch (Exception)
                    {
                        _parent._reentrancy.Release();
                        throw;
                    }
                }
                _parent._reentrancy.Release();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var last = now;
            var remainder = timeout;

            // We need to wait for someone to leave the lock before trying again.
            while (remainder > TimeSpan.Zero)
            {
                if (!await _parent._reentrancy.WaitAsync(remainder).ConfigureAwait(false)) return null;
                if (InnerTryEnter(synchronous: false))
                {
                    var oldThreadId = _parent._owningThreadId;
                    _parent._owningThreadId = ThreadId;

                    if (_parent._reentrances == 1) // Poll for mutex
                    {
                        _parent._reentrancy.Release();

                        Task<bool>? reentrancyLock = null;
                        while (remainder > TimeSpan.Zero)
                        {
                            if (!await (reentrancyLock = _parent._reentrancy.WaitAsync(remainder)).ConfigureAwait(false))
                            {
                                await _parent._reentrancy.WaitAsync();
                                _parent._owningThreadId = oldThreadId;
                                // we need to release retry here, since changing owningThreadId before we actually aquire the lock
                                // might cause other threads to wait on retry. It does not hurt if we release retry too much. 
                                if (_parent._retry.CurrentCount == 0)
                                {
                                    _parent._retry.Release();
                                }
                                _parent._reentrancy.Release();
                                return null;
                            }
                            try
                            {
                                if (TryMutexAcquireOnce())
                                {
                                    break;
                                }
                            }
                            catch
                            {
                                _parent._owningThreadId = oldThreadId;
                                // we need to release retry here, since changing owningThreadId before we actually aquire the lock
                                // might cause other threads to wait on retry. It does not hurt if we release retry too much. 
                                if (_parent._retry.CurrentCount == 0)
                                {
                                    _parent._retry.Release();
                                }
                                _parent._reentrancy.Release();
                                throw;
                            }

                            _parent._reentrancy.Release();

                            now = DateTimeOffset.UtcNow;
                            remainder -= now - last;
                            last = now;
                            var poll = TimeSpan.FromTicks(Math.Min(pollTimeSpan.Ticks, remainder.Ticks));
                            if (poll > TimeSpan.Zero) Thread.Sleep(poll);

                            now = DateTimeOffset.UtcNow;
                            remainder -= now - last;
                            last = now;
                        }
                    }

                    if (remainder > TimeSpan.Zero)
                    {
                        // Reset the owning thread id after all await calls have finished, otherwise we
                        // could be resumed on a different thread and set an incorrect value.
                        _parent._owningThreadId = ThreadId;
                        _parent._reentrancy.Release();
                        return this;
                    }
                    else
                    {
                        _parent._owningThreadId = oldThreadId;
                        // we need to release retry here, since changing owningThreadId before we actually aquire the lock
                        // might cause other threads to wait on retry. It does not hurt if we release retry too much.
                        if (_parent._retry.CurrentCount == 0)
                        {
                            _parent._retry.Release();
                        }
                        _parent._reentrancy.Release();
                        return null;
                    }
                }

                now = DateTimeOffset.UtcNow;
                remainder -= now - last;
                last = now;
                if (remainder <= TimeSpan.Zero)
                {
                    _parent._reentrancy.Release();
                    return null;
                }

                var waitTask = _parent._retry.WaitAsync(remainder).ConfigureAwait(false);
                _parent._reentrancy.Release();
                if (!await waitTask)
                {
                    return null;
                }

                now = DateTimeOffset.UtcNow;
                remainder -= now - last;
                last = now;
            }

            return null;
        }

        internal async Task<IDisposable?> TryObtainLockAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (true)
                {
                    await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (InnerTryEnter(synchronous: false))
                    {
                        break;
                    }
                    // We need to wait for someone to leave the lock before trying again.
                    var waitTask = _parent._retry.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _parent._reentrancy.Release();
                    await waitTask;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            var oldThreadId = _parent._owningThreadId;
            _parent._owningThreadId = ThreadId;

            if (_parent._reentrances == 1) // Poll for mutex
            {
                _parent._reentrancy.Release();

                try
                {
                    while (true)
                    {
                        await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (TryMutexAcquireOnce()) break;
                        }
                        catch
                        {
                            _parent._owningThreadId = oldThreadId;
                            // we need to release retry here, since changing owningThreadId before we actually aquire the lock
                            // might cause other threads to wait on retry. It does not hurt if we release retry too much. 
                            if (_parent._retry.CurrentCount == 0)
                            {
                                _parent._retry.Release();
                            }
                            _parent._reentrancy.Release();
                            throw;
                        }
                        _parent._reentrancy.Release();
                        await Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false); ;
                    }
                }
                catch
                {
                    return null;
                }
            }
            _parent._owningThreadId = ThreadId;
            _parent._reentrancy.Release();

            return this;
        }

        internal IDisposable ObtainLock(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                _parent._reentrancy.Wait(cancellationToken);
                if (InnerTryEnter(synchronous: true))
                {
                    break;
                }
                // We need to wait for someone to leave the lock before trying again.

                _parent._reentrancy.Release();
                _parent._retry.Wait(cancellationToken);
            }

            if (_parent._reentrances == 1) // Poll for mutex
            {
                _parent._reentrancy.Release();

                while (true)
                {
                    _parent._reentrancy.Wait(cancellationToken);
                    try
                    {
                        if (TryMutexAcquireOnce())
                        {
                            break;
                        }
                    }
                    catch
                    {
                        _parent._reentrancy.Release();
                        throw;
                    }
                    _parent._reentrancy.Release();

                    Thread.Sleep(pollMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            _parent._reentrancy.Release();
            return this;
        }

        internal IDisposable? TryObtainLock(TimeSpan timeout)
        {
            // In case of zero-timeout, don't even wait for protective lock contention
            if (timeout == TimeSpan.Zero)
            {
                if (!_parent._reentrancy.Wait(timeout)) return null;
                if (InnerTryEnter(synchronous: true))
                {
                    if (_parent._reentrances != 1 || TryMutexAcquireOnce())
                    {
                        _parent._reentrancy.Release();
                        return this;
                    }
                }
                _parent._reentrancy.Release();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var last = now;
            var remainder = timeout;

            // We need to wait for someone to leave the lock before trying again.
            while (remainder > TimeSpan.Zero)
            {
                if (!_parent._reentrancy.Wait(remainder)) return null;

                now = DateTimeOffset.UtcNow;
                remainder -= now - last;
                last = now;

                if (InnerTryEnter(synchronous: true))
                {
                    if (_parent._reentrances == 1) // Poll for mutex
                    {
                        _parent._reentrancy.Release();

                        while (remainder > TimeSpan.Zero)
                        {
                            if (!_parent._reentrancy.Wait(remainder)) return null;
                            try
                            {
                                if (TryMutexAcquireOnce())
                                {
                                    break;
                                }
                            }
                            catch
                            {
                                _parent._reentrancy.Release();
                                throw;
                            }

                            _parent._reentrancy.Release();

                            now = DateTimeOffset.UtcNow;
                            remainder -= now - last;
                            last = now;
                            var poll = TimeSpan.FromTicks(Math.Min(pollTimeSpan.Ticks, remainder.Ticks));
                            if (poll > TimeSpan.Zero) Thread.Sleep(poll);

                            now = DateTimeOffset.UtcNow;
                            remainder -= now - last;
                            last = now;
                        }

                        if (remainder <= TimeSpan.Zero) return null;
                    }

                    _parent._reentrancy.Release();
                    return this;
                }

                now = DateTimeOffset.UtcNow;
                remainder -= now - last;
                last = now;

                _parent._reentrancy.Release();
                if (!_parent._retry.Wait(remainder))
                {
                    return null;
                }

                now = DateTimeOffset.UtcNow;
                remainder -= now - last;
                last = now;
            }

            return null;
        }

        private bool InnerTryEnter(bool synchronous)
        {
            if (synchronous)
            {
                if (_parent._owningThreadId == UnlockedId)
                {
                    _parent._owningThreadId = ThreadId;
                }
                else if (_parent._owningThreadId != ThreadId)
                {
                    // Another thread currently owns the lock
                    return false;
                }
                _parent._owningId = AsyncMutexLock.AsyncId;
            }
            else
            {
                if (_parent._owningId == UnlockedId)
                {
                    _parent._owningId = AsyncMutexLock.AsyncId;
                }
                else if (_parent._owningId != _oldId)
                {
                    // Another thread currently owns the lock
                    return false;
                }
                else
                {
                    // Nested re-entrance
                    _parent._owningId = AsyncId;
                }
            }

            // We can go in
            _parent._reentrances += 1;
            return true;
        }

        private bool TryMutexAcquireOnce(CancellationToken cancel = default) => _parent.TryMutexAcquireOnce(cancel);

        public void Dispose()
        {
#if DEBUG
            Debug.Assert(!_disposed);
            _disposed = true;
#endif
            var @this = this;
            var oldId = this._oldId;
            var oldThreadId = this._oldThreadId;
            @this._parent._reentrancy.Wait();
            try
            {
                @this._parent._reentrances -= 1;
                @this._parent._owningId = oldId;
                @this._parent._owningThreadId = oldThreadId;
                if (@this._parent._reentrances == 0)
                {
                    // The owning thread is always the same so long as we
                    // are in a nested stack call. We reset the owning id
                    // only when the lock is fully unlocked.
                    @this._parent._owningId = UnlockedId;
                    @this._parent._owningThreadId = (int)UnlockedId;

                    _parent.MutexRelease();
                }
                // We can't place this within the _reentrances == 0 block above because we might
                // still need to notify a parallel reentrant task to wake. I think.
                // This should not be a race condition since we only wait on _retry with _reentrancy locked,
                // then release _reentrancy so the Dispose() call can obtain it to signal _retry in a big hack.
                if (@this._parent._retry.CurrentCount == 0)
                {
                    @this._parent._retry.Release();
                }
            }
            finally
            {
                @this._parent._reentrancy.Release();
            }
        }
    }

    // Mutex code
    FileStream? LockFileStream;
    int FlockFile = -1;

    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    private const int O_CREAT = 0x40;
    private const int O_RDWR = 0x2;

    const int pollMilliseconds = 100;
    static readonly TimeSpan pollTimeSpan = TimeSpan.FromMilliseconds(pollMilliseconds);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    const int MaxUnauthorizedAccessExceptionRetries = 1600;
    private static bool CanRetryTransientFileSystemError(ref int retryCount)
    {
        if (retryCount >= MaxUnauthorizedAccessExceptionRetries) { return false; }

        ++retryCount;

        return true;
    }
    private void EnsureDirectoryExists()
    {
        var retryCount = 0;

        var directory = Path.GetDirectoryName(FileName);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return;
            }
            catch (Exception ex)
            {
                // This can indicate either a transient failure during concurrent creation/deletion or a permissions issue.
                // If we encounter it, assume it is transient unless it persists.
                // For a long time, I just checked for UnauthorizedAccessException here. However, recent tests on Linux have
                // shown that in race conditions we can see IOException as well, presumably because there is some period during
                // directory creation where it presents as a file.
                if (ex is UnauthorizedAccessException or IOException
                    && CanRetryTransientFileSystemError(ref retryCount))
                {
                    continue;
                }

                throw new InvalidOperationException($"Failed to ensure that lock file directory {directory} exists", ex);
            }
        }
    }

    internal bool TryMutexAcquireOnce(CancellationToken cancel = default)
    {
        if (IsWindows)
        {
            int retryCount = 0;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                FileStream lockFileStream;
                try
                {
                    // key arguments: 
                    // OpenOrCreate to be robust to the file existing or not
                    lockFileStream = new FileStream(FileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                    try
                    {
                        lockFileStream.WriteByte(0);
                        lockFileStream.Flush();
                        lockFileStream.Lock(0, 1);
                        //AppDomain.CurrentDomain.ProcessExit += MutexRelease;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return false;
                    }
                    catch (IOException ex)
                    {
                        return false;
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // this should almost never happen because we just created the directory but in a race condition it could. Just retry
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    // This can happen in few cases:

                    // The path is already directory, so we'll never be able to open a handle of it as a file
                    if (Directory.Exists(FileName))
                    {
                        throw new InvalidOperationException($"Failed to create lock file '{FileName}' because it is already the name of a directory");
                    }

                    // The file exists and is read-only
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(FileName); }
                    catch { attributes = FileAttributes.Normal; } // e. g. could fail with FileNotFoundException
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        // We could support this by eschewing DeleteOnClose once we detect that a file is read-only,
                        // but absent interest or a use-case we'll just throw for now
                        throw new NotSupportedException($"Locking on read-only file '{FileName}' is not supported");
                    }

                    // Frustratingly, this error can be thrown transiently due to concurrent creation/deletion. Initially assume
                    // that it is transient and just retry
                    if (CanRetryTransientFileSystemError(ref retryCount))
                    {
                        continue;
                    }

                    // If we get here, we've exhausted our retries: assume that it is a legitimate permissions issue
                    throw;
                }
                // this should never happen because we validate. However if it does (e. g. due to some system configuration change?), throw so that
                // this doesn't end up in the IOException block (PathTooLongException is IOException)
                catch (PathTooLongException) { throw; }
                catch (IOException)
                {
                    // the hope is that if we get here the only failure reason would be that the file is locked
                    return false;
                }

                LockFileStream = lockFileStream;
#if !NETSTANDARD1_3
                AppDomain.CurrentDomain.ProcessExit += MutexRelease;
#endif
                return true;
            }
        }
        else // Unix, use flock
        {
            int file = -1;
            try
            {
                EnsureDirectoryExists();

                file = open(FileName, O_CREAT | O_RDWR, 0x1A4); // 0644

                if (file == -1) return false;

                if (flock(file, LOCK_EX | LOCK_NB) == 0)
                {
                    this.FlockFile = file;
                    return true;
                }
                else
                {
                    var errno = Marshal.GetLastWin32Error();
                }

                return false;
            }
            finally
            {
                if (file != -1 && this.FlockFile != file) close(file);
            }
        }
    }

    internal void MutexRelease(object? sender = null, EventArgs? args = default)
    {
        if (IsWindows)
        {
            var file = Interlocked.Exchange(ref this.LockFileStream!, null);
            if (file != null)
            {
                try
                {
                    file.Unlock(0, 1);
                    file.Close();
                    file.Dispose();
                    AppDomain.CurrentDomain.ProcessExit -= MutexRelease;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        else
        {
            if (FlockFile != -1)
            {
                flock(FlockFile, LOCK_UN);
                close(FlockFile);

                FlockFile = -1;
            }
        }
    }

    public void Dispose() => MutexRelease();
    ~AsyncMutexLock() => Dispose();

    // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
    // the AsyncLocal value.
    public Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
        return @lock.ObtainLockAsync(cancellationToken);
    }

    // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
    // the AsyncLocal value.
    public Task<bool> TryLockAsync(Action callback, TimeSpan timeout)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

        return @lock.TryObtainLockAsync(timeout)
            .ContinueWith(state =>
            {
                if (state.Exception is AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                var disposableLock = state.Result;
                if (disposableLock is null)
                {
                    return false;
                }

                try
                {
                    callback();
                }
                finally
                {
                    disposableLock.Dispose();
                }
                return true;
            });
    }

    // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
    // the AsyncLocal value.
    public Task<bool> TryLockAsync(Func<Task> callback, TimeSpan timeout)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

        return @lock.TryObtainLockAsync(timeout)
            .ContinueWith(state =>
            {
                if (state.Exception is AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                var disposableLock = state.Result;
                if (disposableLock is null)
                {
                    return Task.FromResult(false);
                }

                return callback()
                    .ContinueWith(result =>
                    {
                        disposableLock.Dispose();

                        if (result.Exception is AggregateException ex)
                        {
                            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                        }

                        return true;
                    }, TaskScheduler.Default);
            }, TaskScheduler.Default).Unwrap();
    }

    // Make sure InnerLock.TryLockAsync() does not use await, because an async function triggers a snapshot of
    // the AsyncLocal value.
    public Task<bool> TryLockAsync(Action callback, CancellationToken cancellationToken)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

        return @lock.TryObtainLockAsync(cancellationToken)
            .ContinueWith(state =>
            {
                if (state.Exception is AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                var disposableLock = state.Result;
                if (disposableLock is null)
                {
                    return false;
                }

                try
                {
                    callback();
                }
                finally
                {
                    disposableLock.Dispose();
                }
                return true;
            }, TaskScheduler.Default);
    }

    // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
    // the AsyncLocal value.
    public Task<bool> TryLockAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

        return @lock.TryObtainLockAsync(cancellationToken)
            .ContinueWith(state =>
            {
                if (state.Exception is AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                var disposableLock = state.Result;
                if (disposableLock is null)
                {
                    return Task.FromResult(false);
                }

                return callback()
                    .ContinueWith(result =>
                    {
                        disposableLock.Dispose();

                        if (result.Exception is AggregateException ex)
                        {
                            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                        }

                        return true;
                    }, TaskScheduler.Default);
            }, TaskScheduler.Default).Unwrap();
    }

    public IDisposable Lock(CancellationToken cancellationToken = default)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        // Increment the async stack counter to prevent a child task from getting
        // the lock at the same time as a child thread.
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
        return @lock.ObtainLock(cancellationToken);
    }

    public bool TryLock(Action callback, TimeSpan timeout)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        // Increment the async stack counter to prevent a child task from getting
        // the lock at the same time as a child thread.
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
        var lockDisposable = @lock.TryObtainLock(timeout);
        if (lockDisposable is null)
        {
            return false;
        }

        // Execute the callback then release the lock
        try
        {
            callback();
        }
        finally
        {
            lockDisposable.Dispose();
        }
        return true;
    }

    public Task<IDisposable> LockAsync(TimeSpan timeout)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

        return @lock.TryObtainLockAsync(timeout)
            .ContinueWith(state =>
            {
                if (state.Exception is AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                }
                var disposableLock = state.Result;
                if (disposableLock is null) throw new TimeoutException("LockAsync timed out.");
                return disposableLock;
            });
    }

    public IDisposable Lock(TimeSpan timeout)
    {
        var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
        // Increment the async stack counter to prevent a child task from getting
        // the lock at the same time as a child thread.
        _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
        var lockDisposable = @lock.TryObtainLock(timeout);
        if (lockDisposable is null) throw new TimeoutException("TryLock timed out.");
        return lockDisposable;
    }

    [DllImport("libc")]
    public static extern uint getuid();

    public static bool UnixIsRoot => getuid() == 0;

    public static string LockFileName(string name, MutexScope scope = MutexScope.Machine)
    {
        if (Path.IsPathRooted(name)) return name;
#if NETSTANDARD1_3
        throw new NotSupportedException("Only full filenames are supported as name on netstandard1.3");
#else
        if (name.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("AsyncMutexLock does not support local mutexes");
        //if (IsWindows) return $"Global\\{name.Replace('/', '_')}";

        var pattern = @"[ $%&""'=?!^_:\t\r\n\\/]";
        pattern = Path.DirectorySeparatorChar == '\\' ? 
            @"[ $%&""'=?!^_:\t\r\n/]" :
            pattern.Replace(Path.DirectorySeparatorChar.ToString(), "");

        name = Regex.Replace(name, pattern, "-");

        if (scope == MutexScope.User)
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var lockpath = Path.Combine(root, "asyncmutexlock");
            var lockfile = Path.Combine(lockpath, $"{name}.lock");
            Directory.CreateDirectory(lockpath);
            return lockfile;
        }
        else
        {
            string root;
            if (IsMac) root = "/Library/Application Support";
            else root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var lockpath = Path.Combine(root, "asyncmutexlock");
            var lockfile = Path.Combine(lockpath, $"{name}.lock");
            Directory.CreateDirectory(lockpath);
            if (!IsWindows) Unix.chmod(lockpath, 0x1FF);
            return lockfile;
        }
#endif
    }
}
#endif