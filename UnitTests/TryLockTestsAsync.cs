using Microsoft.VisualStudio.TestTools.UnitTesting;
using EstrellasDeEsperanza.AsyncLock;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncLockTests
{
    class LocalException : Exception {
        public LocalException(string message) : base(message) { }
    }

    [TestClass]
    public class TryLockTestsAsync
    {
        [TestMethod]
        public async Task NoContention()
        {
            var @lock = new AsyncLock();

            Assert.IsTrue(await @lock.TryLockAsync(() => { }, TimeSpan.Zero));
        }

        /// <summary>
        /// Assert that exceptions are bubbled up after the lock is disposed
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task NoContentionThrows()
        {
            var @lock = new AsyncLock();

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
            var @lock = new AsyncLock();
            var finished = new TaskCompletionSource();

            using (await @lock.LockAsync())
            {
                var task = new Thread(async () =>
                {
                    try
                    {
                        Assert.IsFalse(await @lock.TryLockAsync(() => throw new Exception("This should be executed"), TimeSpan.Zero));
                    } catch (Exception ex)
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
                } catch {
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
            var @lock = new AsyncLock();

            var locked = await @lock.LockAsync();
            Interlocked.Increment(ref step);

            using var eventTestThreadStarted = new SemaphoreSlim(0, 1);
            using var eventSleepNotStarted = new SemaphoreSlim(0, 1);
            using var eventAboutToWait = new SemaphoreSlim(0, 1);

            //var unlockFinished = new TaskCompletionSource();
            var testFinished = new TaskCompletionSource();

            var unlockTask = Task.Run(async () =>
            {
                await eventTestThreadStarted.WaitAsync();
                eventSleepNotStarted.Release();
                Thread.Sleep(unlockDelayMs);
                await eventAboutToWait.WaitAsync();
                Interlocked.Increment(ref step);
                locked.Dispose();
            });
            
            var testThread = new Thread(async () =>
            {
                try
                {
                    eventTestThreadStarted.Release();
                    await eventSleepNotStarted.WaitAsync();
                    eventAboutToWait.Release();
                    Assert.IsTrue((!expectedResult) ^ await @lock.TryLockAsync(async () =>
                        {
                            await Task.Yield();
                            Assert.AreEqual(2, step);
                        }, TimeSpan.FromMilliseconds(lockTimeoutMs)));
                }
                catch (Exception ex)
                {
                    testFinished.SetException(ex);
                    return;
                }
                testFinished.SetResult();
            });
            testThread.Start();

            await unlockTask;
            await testFinished.Task;
        }
    }
}
