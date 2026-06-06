using Microsoft.VisualStudio.TestTools.UnitTesting;
using EstrellasDeEsperanza.AsyncLock;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncLockTests;
using System.Diagnostics;

namespace AsyncLockTests.Mutex;

/// <summary>
/// Creates multiple indepndent tasks, each with its own lock, and runs them
/// all in parallel. There should be no contention for the lock between the
/// parallelly executed tasks, but each task then recursively obtains what
/// should be the same lock - which should again be contention-free - after
/// an await point that may or may not resume on the same actual thread the
/// previous lock was obtained with.
/// </summary>
[TestClass]
public class MixedSyncAsync
{
    [TestMethod]
    public async Task MixedSyncAsyncExecution()
    {
        int count = 0, nsync = 0, nasync = 0; 
        var threads = new List<Thread>(10);
        var tasks = new List<Task>(10);
        var asyncLock = new AsyncMutexLock(nameof(MixedSyncAsync));
        var rng = new Random();
        var start = DateTime.UtcNow;

        {
            //using var l = asyncLock.Lock();
            for (int i = 0; i < 10; ++i)
            {
                var thread = new Thread(() =>
                {
                    var id = Interlocked.Increment(ref nsync);
                    Debug.WriteLine($"Sync Thread: {id}; Sync Thread ID: {Thread.CurrentThread.ManagedThreadId}");
                    var t0 = DateTime.UtcNow;
                    using (asyncLock.Lock())
                    {
                        var time = DateTime.UtcNow - t0;
                        Debug.WriteLine($"SyncLock{id}.1: {time}");

                        Assert.AreEqual(1, Interlocked.Increment(ref count));
                        Thread.Sleep(rng.Next(1, 10) * 10);
                        t0 = DateTime.UtcNow;
                        using (asyncLock.Lock())
                        {
                            time = DateTime.UtcNow - t0;
                            Debug.WriteLine($"SyncLock{id}.2: {time}");

                            Thread.Sleep(10);
                            Assert.AreEqual(0, Interlocked.Decrement(ref count));
                        }

                        Assert.AreEqual(0, count);
                    }

                });
                thread.Start();
                threads.Add(thread);
            }

            for (int i = 0; i < 10; ++i)
            {
                var task = Task.Run(async () =>
                {
                    var id = Interlocked.Increment(ref nasync);
                    Debug.WriteLine($"Async Thread: {id}; Async Thread ID: {Thread.CurrentThread.ManagedThreadId}");

                    var t0 = DateTime.UtcNow;
                    using (await asyncLock.LockAsync())
                    {
                        var time = DateTime.UtcNow - t0;
                        Debug.WriteLine($"AsyncLock{id}.1: {time}");

                        Assert.AreEqual(1, Interlocked.Increment(ref count));
                        Assert.AreEqual(1, count);
                        await Task.Delay(rng.Next(1, 10) * 10);
                        t0 = DateTime.UtcNow;
                        using (await asyncLock.LockAsync())
                        {
                            time = DateTime.UtcNow - t0;
                            Debug.WriteLine($"AsyncLock{id}.2: {time}");
                            await Task.Delay(10);
                            Assert.AreEqual(0, Interlocked.Decrement(ref count));
                        }

                        Assert.AreEqual(0, count);
                    }

                });
                tasks.Add(task);
            }
        }

        await Task.WhenAll(tasks);
        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.AreEqual(0, count);
    }
}
