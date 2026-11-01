using Microsoft.VisualStudio.TestTools.UnitTesting;
using EstrellasDeEsperanza.AsyncLock;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AsyncLockTests.Mutex;

/// <summary>
/// The lock files of a machine wide <see cref="AsyncMutexLock"/> on Unix are for their owner alone. A lock is held by whoever can open its
/// file, even read-only (flock needs no write access), so a lock file that others can open is a lock that others can hold, and keep
/// everybody else out. The tests use files in a temporary folder. They are inconclusive on Windows, and those that need the permissions
/// of an ordinary user are inconclusive for root, which ignores permissions.
/// </summary>
[TestClass]
public class UnixLockFilePermissions
{
    [DllImport("libc")]
    static extern uint umask(uint mask);

    [DllImport("libc")]
    static extern uint getuid();

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static void RequireUnix()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Unix lock files.");
    }

    static void RequireOrdinaryUser()
    {
        RequireUnix();
        if (getuid() == 0) Assert.Inconclusive("Root ignores file permissions.");
    }

    static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "asynclock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    static void Delete(string folder)
    {
        foreach (var file in Directory.GetFiles(folder))
        {
            try { File.SetUnixFileMode(file, OwnerOnly); } catch (Exception) { }
        }
        Directory.Delete(folder, true);
    }

    [TestMethod]
    [DataRow(0u)]    // nothing is taken away: a lock file must still not become more open than its owner
    [DataRow(0x3Fu)] // 077
    [DataRow(0x80u)] // 0200: the owner could not even write
    public void A_new_lock_file_is_for_its_owner_alone_whatever_the_umask(uint mask)
    {
        RequireUnix();
        var folder = NewFolder();
        var path = Path.Combine(folder, "new.lock");
        var previous = umask(mask);
        try
        {
            using (new AsyncMutexLock(path).Lock(TimeSpan.Zero))
            {
                Assert.AreEqual(OwnerOnly, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            umask(previous);
            Delete(folder);
        }
    }

    [TestMethod]
    public void A_lock_file_that_is_already_there_keeps_its_mode()
    {
        RequireUnix();
        var folder = NewFolder();
        var path = Path.Combine(folder, "old.lock");
        File.WriteAllText(path, "");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead; // 0640
        File.SetUnixFileMode(path, mode);
        try
        {
            using (new AsyncMutexLock(path).Lock(TimeSpan.Zero)) { }

            Assert.AreEqual(mode, File.GetUnixFileMode(path));
        }
        finally { Delete(folder); }
    }

    [TestMethod]
    public void Two_locks_on_the_same_file_exclude_each_other()
    {
        RequireUnix();
        var folder = NewFolder();
        var path = Path.Combine(folder, "pair.lock");
        try
        {
            var first = new AsyncMutexLock(path);
            var second = new AsyncMutexLock(path);
            using (first.Lock(TimeSpan.Zero))
            {
                Assert.ThrowsExactly<TimeoutException>(() => second.Lock(TimeSpan.Zero));
            }
            using (second.Lock(TimeSpan.Zero)) { } // free again once the first has let go
        }
        finally { Delete(folder); }
    }

    [TestMethod]
    public void A_lock_file_that_the_user_may_not_open_for_writing_is_an_error_and_not_a_lock_that_somebody_else_holds()
    {
        RequireOrdinaryUser();
        var folder = NewFolder();
        var path = Path.Combine(folder, "readonly.lock");
        File.WriteAllText(path, "");
        // what another user sees of a lock file of root: it can be read, but not written. Opening it read-only would be enough to hold the
        // lock, which is exactly what must not be possible for somebody who does not own it.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => new AsyncMutexLock(path).Lock(TimeSpan.Zero));
        }
        finally { Delete(folder); }
    }

    [TestMethod]
    public void A_lock_file_that_nobody_may_open_is_an_error_too()
    {
        RequireOrdinaryUser();
        var folder = NewFolder();
        var path = Path.Combine(folder, "closed.lock");
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => new AsyncMutexLock(path).Lock(TimeSpan.Zero));
        }
        finally { Delete(folder); }
    }

    [TestMethod]
    public void A_symbolic_link_in_place_of_the_lock_file_does_not_get_its_target_changed()
    {
        RequireUnix();
        var folder = NewFolder();
        var target = Path.Combine(folder, "target");
        File.WriteAllText(target, "content");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); // 0604
        var path = Path.Combine(folder, "link.lock");
        File.CreateSymbolicLink(path, target);
        try
        {
            using (new AsyncMutexLock(path).Lock(TimeSpan.Zero)) { }

            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, File.GetUnixFileMode(target));
            Assert.AreEqual("content", File.ReadAllText(target));
        }
        finally { Delete(folder); }
    }

    [TestMethod]
    public void A_lock_file_that_cannot_be_created_is_an_error_and_not_a_lock_that_somebody_else_holds()
    {
        RequireOrdinaryUser();
        var folder = NewFolder();
        var path = Path.Combine(folder, "new.lock");
        // a folder in which this user may not create files: what a second user finds when the folder of the locks belongs to somebody else
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => new AsyncMutexLock(path).Lock(TimeSpan.Zero));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Delete(folder);
        }
    }

    [TestMethod]
    public void The_machine_wide_lock_folder_is_where_every_user_can_make_it()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Unix lock folders.");
        string path;
        try { path = AsyncMutexLock.LockFileName("UnixLockFilePermissions.Place"); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("On this system root has to make the folder first."); return; }

        // /var/lock on Linux (the place for lock files); on macOS there is none that every user can write to, so /var/tmp, like on the BSDs
        var expected = OperatingSystem.IsLinux() ? "/var/lock/EstrellasDeEsperanza-AsyncMutexLock/" : "/var/tmp/EstrellasDeEsperanza-AsyncMutexLock/";
        Assert.IsTrue(path.StartsWith(expected, StringComparison.Ordinal), $"The lock file is {path}, expected it in {expected}");
    }

    [TestMethod]
    public void The_directory_of_the_machine_wide_lock_files_is_open_to_every_user_and_sticky()
    {
        RequireUnix();
        if (getuid() != 0) Assert.Inconclusive("Only root creates and changes the shared lock directory.");

        var directory = Path.GetDirectoryName(AsyncMutexLock.LockFileName("UnixLockFilePermissions.Directory"))!;

        var mode = File.GetUnixFileMode(directory);
        Assert.IsTrue(mode.HasFlag(UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.GroupWrite),
            "Every user has to be able to create locks in it.");
        Assert.IsTrue(mode.HasFlag(UnixFileMode.StickyBit),
            "Without it any user could remove the lock file of another, and a second process would then hold 'the same' lock.");
    }
}
