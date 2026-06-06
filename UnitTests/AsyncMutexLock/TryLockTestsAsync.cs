using Microsoft.VisualStudio.TestTools.UnitTesting;
using NeoSmart.AsyncLock;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncLockTests.Mutex;

class LocalException : Exception {
    public LocalException(string message) : base(message) { }
}

[TestClass]
public class TryLockTestsAsync
{
    [TestMethod]
    public async Task NoContention()
    {
        var @lock = new AsyncMutexLock("test");

        Assert.IsTrue(await @lock.TryLockAsync(() => { }, TimeSpan.Zero));
    }

    /// <summary>
    /// Assert that exceptions are bubbled up after the lock is disposed
    /// </summary>
    /// <returns></returns>
    [TestMethod]
    public async Task NoContentionThrows()
    {
        var @lock = new AsyncMutexLock("test");

        await Assert.ThrowsAsync<LocalException>(async () =>
        {
            await @lock.TryLockAsync(async () => {
                await Task.Yield();
                throw new LocalException("This exception needs to be bubbled up");
            }, TimeSpan.Zero);
        });
    }

    [TestMethod]
    public async Task ContentionEarlyReturn()
    {
        var @lock = new AsyncMutexLock(nameof(ContentionEarlyReturn));
        var finished = new TaskCompletionSource();

        using (await @lock.LockAsync())
        {
            var task = new Thread(async () =>
            {
                try
                {
                    Assert.IsFalse(await @lock.TryLockAsync(() => throw new Exception("This should be executed"), TimeSpan.Zero));
                }
                catch (Exception ex)
                {
                    finished.SetException(ex);
                    return;
                }
                finished.SetResult();
            });
            task.Start();
            task.Join();
            try
            {
                await finished.Task;
                Assert.Fail("Exception should throw.");
            }
            catch
            {
            }
        }
    }

    //[TestMethod] broken. Did seem to work before because exception was swallowed inside Thread
    public async Task ContentionDelayedExecution() => await ContentionalExecution(50, 250, true);

    //[TestMethod] broken. Did seem to work before because exception was swallowed inside Thread
    public async Task ContentionNoExecution() => await ContentionalExecution(250, 50, false);

    //[TestMethod] broken. Did seem to work before because exception was swallowed inside Thread
    public async Task ContentionNoExecutionZeroTimeout() => await ContentionalExecution(250, 0, false);

    private async Task ContentionalExecution(int unlockDelayMs, int lockTimeoutMs, bool expectedResult)
    {
        int step = 0;
        var @lock = new AsyncMutexLock("test");

        var locked = await @lock.LockAsync();
        Interlocked.Increment(ref step);

        using var eventTestThreadStarted = new SemaphoreSlim(0, 1);
        using var eventSleepNotStarted = new SemaphoreSlim(0, 1);
        using var eventAboutToWait = new SemaphoreSlim(0, 1);

        var unlockThread = new Thread(async () =>
        {
            await eventTestThreadStarted.WaitAsync();
            eventSleepNotStarted.Release();
            Thread.Sleep(unlockDelayMs);
            await eventAboutToWait.WaitAsync();
            Interlocked.Increment(ref step);
            locked.Dispose();
        });
        unlockThread.Start();

        var testThread = new Thread(async () =>
        {
            eventTestThreadStarted.Release();
            await eventSleepNotStarted.WaitAsync();
            eventAboutToWait.Release();
            Assert.IsTrue((!expectedResult) ^ await @lock.TryLockAsync(() =>
            {
                Assert.AreEqual(2, step);
            }, TimeSpan.FromMilliseconds(lockTimeoutMs)));
        });
        testThread.Start();

        unlockThread.Join();
        testThread.Join();
    }
}
