using Microsoft.VisualStudio.TestTools.UnitTesting;
using NeoSmart.AsyncLock;
using System;
using System.Threading;
using AsyncLockTests;

namespace AsyncLockTests.Mutex;

[TestClass]
public class TryLockTests
{
    [TestMethod]
    public void NoContention()
    {
        using var @lock = new AsyncMutexLock(nameof(TryLockTests));

        Assert.IsTrue(@lock.TryLock(() => { }, default));
    }

    [TestMethod]
    public void ContentionEarlyReturn()
    {
        using var @lock = new AsyncMutexLock(nameof(TryLockTests));

        using (@lock.Lock())
        {
            var thread = new Thread(() =>
            {
                Assert.IsFalse(@lock.TryLock(() => throw new Exception("This should never be executed"), default));
            });
            thread.Start();
            thread.Join();
        }
    }

    [TestMethod]
    public void ContentionDelayedExecution() => ContentionalExecution(50, 250, true);

    [TestMethod]
    public void ContentionNoExecution() => ContentionalExecution(250, 50, false);

    [TestMethod]
    public void ContentionNoExecutionZeroTimeout() => ContentionalExecution(250, 0, false);

    private void ContentionalExecution(int unlockDelayMs, int lockTimeoutMs, bool expectedResult)
    {
        int step = 0;
        using var @lock = new AsyncMutexLock(nameof(TryLockTests));

        var locked = @lock.Lock();
        Interlocked.Increment(ref step);

        using var eventTestThreadStarted = new AutoResetEvent(false);
        using var eventSleepNotStarted = new AutoResetEvent(false);
        using var eventAboutToWait = new AutoResetEvent(false);

        var unlockThread = new Thread(() =>
        {
            eventTestThreadStarted.WaitOne();
            eventSleepNotStarted.Set();
            Thread.Sleep(unlockDelayMs);
            eventAboutToWait.WaitOne();
            Interlocked.Increment(ref step);
            locked.Dispose();
        });
        unlockThread.Start();

        var testThread = new Thread(() =>
        {
            eventTestThreadStarted.Set();
            eventSleepNotStarted.WaitOne();
            eventAboutToWait.Set();
            Assert.IsTrue((!expectedResult) ^ @lock.TryLock(() =>
            {
                Assert.AreEqual(2, step);
            }, TimeSpan.FromMilliseconds(lockTimeoutMs)));
        });
        testThread.Start();

        unlockThread.Join();
        testThread.Join();
    }
}
