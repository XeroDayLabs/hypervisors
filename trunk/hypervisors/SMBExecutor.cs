using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace hypervisors
{
    /// <summary>
    /// This class exposes a bare windows box. It uses SMB to copy files, and can spawn processes (by shelling out to psexec).
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// It may be neccessary to set the required registry key on the guest before using this, or it may not be, since we moved to
    /// WMI for remote execution instead of shelling out to psexec.
    /// </summary>
    public class SMBExecutor : remoteExecution
    {
        readonly string _guestIP;
        readonly string _username;
        readonly string _password;
        private readonly NetworkCredential _cred;

        readonly ConcurrentBag<Task> tasksToDispose = new ConcurrentBag<Task>();

        public SMBExecutor(string guestIP, string _guestUsername, string _guestPassword)
        {
            _guestIP = guestIP;
            _username = _guestUsername;
            _password = _guestPassword;
            _cred = new NetworkCredential(_username, _password);
        }

        public override void testConnectivity()
        {
            executionResult res = startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();
        }

        public override IAsyncExecutionResult startExecutableAsyncInteractively(string toExecute, string args, string workingDir = null)
        {
            if (workingDir != null)
                throw new NotSupportedException();

            string tempDir = string.Format("C:\\users\\{0}\\", _username);

            execFileSet fileSet = base.prepareForExecution(toExecute, args, tempDir);
            
            // Use a scheduled task to run interactively with the remote users desktop.
            string scheduledTaskName = Guid.NewGuid().ToString();
            using (hypervisor_localhost local = new hypervisor_localhost())
            {
                executionResult taskCreation = local.startExecutable("schtasks.exe", string.Format(
                    "/f /create /s {0} /tr \"'{1}'\" /it /sc onstart /tn {4} /u {2} /p {3} ",
                    _guestIP, fileSet.launcherPath, _username, _password, scheduledTaskName));
                if (taskCreation.resultCode != 0)
                    throw new Exception("Couldn't create scheduled task, stdout " + taskCreation.stdout + " / stderr " + taskCreation.stderr);
                executionResult taskStart = local.startExecutable("schtasks.exe", string.Format(
                    "/run /s {0} /tn {3} /u {1} /p {2}",
                    _guestIP, _username, _password, scheduledTaskName));
                if (taskStart.resultCode != 0)
                    throw new Exception("Couldn't start scheduled task, stdout " + taskStart.stdout + " / stderr " + taskStart.stderr);
            }
            return new asyncExecutionResultViaFile(this, fileSet);
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            var connOpts = new ConnectionOptions();
            connOpts.Username = _username;
            connOpts.Password = _password;

            string tempDir = string.Format("C:\\users\\{0}\\", _username);
            if (workingDir == null)
                workingDir = tempDir;

            // We try to connect to the client before we do anything else. This is what usually times out.
            // There's no way (?) to set a timeout for ManagementScope.connect, so we run it in a task and bail if it takes too
            // long.
            string wmiPath = String.Format("\\\\{0}\\root\\cimv2", _guestIP);
            var scope = new ManagementScope(wmiPath, connOpts);
            ManualResetEvent connectedOK = new ManualResetEvent(false);
            Task foo = Task.Run(() =>
            {
                scope.Connect();
                connectedOK.Set();
            }) ;
            tasksToDispose.Add(foo);
            if (!connectedOK.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException();

            // We do this by creating two batch files on the target.
            // The first contains the command we're executing, and the second simply calls the first with redirection to the files
            // we want our output in.
            execFileSet fileSet = base.prepareForExecution(toExecute, args, tempDir);

            var proc = new ManagementClass(scope, new ManagementPath("Win32_Process"), new ObjectGetOptions());
            object[] methodCreateArgs = new object[] {fileSet.launcherPath, workingDir, null, 0};
            UInt32 s = (UInt32) proc.InvokeMethod("create", methodCreateArgs);
            if (s != 0)
                throw new hypervisorExecutionException_retryable();

            return new asyncExecutionResultViaFile(this, fileSet);
        }

        public override void mkdir(string newDir, cancellableDateTime deadline)
        {
            doNetworkCall(() =>
            {
                string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, newDir.Substring(2));

                while (!Directory.Exists(destUNC))
                    Directory.CreateDirectory(destUNC);
            }, deadline);
        }

        public override void deleteFile(string toDelete, cancellableDateTime deadline)
        {
            doNetworkCall(() =>
            {
                string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, toDelete.Substring(2));

                if (Directory.Exists(destUNC))
                    Directory.Delete(destUNC, true);
                else if (File.Exists(destUNC))
                    File.Delete(destUNC);
            }, deadline );
        }

        public override void Dispose()
        {
            foreach (Task task in tasksToDispose)
            {
                try { task.Wait(); } catch (Exception) { }
                task.Dispose();
            }
        }

        private void doNetworkCall(Action toRun, cancellableDateTime deadline)
        {
            doNetworkCall(() =>
            {
                toRun.Invoke();
                return 0;
            }, deadline);
        }

        private T doNetworkCall<T>(Func<T> toRun, cancellableDateTime deadline)
        {
            Exception e;
            T toRet = tryDoNetworkCall(() =>
            {
                T toRetInner = toRun.Invoke();
                return new triedNetworkCallRes<T>() { res = toRetInner };
            }, deadline, out e);
            if (e != null)
                throw e;

            return toRet;
        }

        public class triedNetworkCallRes<T>
        {
            public T res;
            public bool retryRequested = false;
            public Exception error = null;
        }

        private T tryDoNetworkCall<T>(Func<triedNetworkCallRes<T>> toRun, cancellableDateTime deadline, out Exception excepOrNull)
        {
            while (true)
            {
                Exception e;
                using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred, out e))
                {
                    if (e != null)
                    {
                        Win32Exception eAsWin32 = e as Win32Exception;
                        if (eAsWin32 != null)
                        {
                            if (eAsWin32.NativeErrorCode == 86)
                            {
                                // This is ERROR_INVALID_PASSWORD. It is fatal so pass it upward.
                                excepOrNull = e;
                                return default(T);
                            }
                        }
                        // Retry Win32Exceptions and IOExceptions
                        if (eAsWin32 != null || e is IOException)
                        {
                            if (!deadline.stillOK)
                            {
                                // On no, we've run out of retry time :/
                                excepOrNull = e;
                                return default(T);
                            }

                            Thread.Sleep(TimeSpan.FromSeconds(1));
                            continue;
                        }

                        // Other exceptions should be passed up to the caller.
                        excepOrNull = e;
                        return default(T);
                    }

                    triedNetworkCallRes<T> toRet = toRun.Invoke();
                    if (toRet.retryRequested)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                        continue;
                    }
                    if (toRet.error != null)
                    {
                        excepOrNull = toRet.error;
                        return default(T);
                    }

                    excepOrNull = null;
                    return toRet.res;
                }
            }
        }

        public override void copyToGuest(string dstPath, string srcPath, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline  = new cancellableDateTime(TimeSpan.FromMinutes(3));

            if (!dstPath.ToLower().StartsWith("c:"))
                throw new Exception("Only C:\\ is shared");
            if (!File.Exists(srcPath))
                throw new Exception("src file not found");

            doNetworkCall(() =>
            {
                string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, dstPath.Substring(2));
                if (destUNC.EndsWith("\\"))
                    destUNC += Path.GetFileName(srcPath);

                File.Copy(srcPath, destUNC, true);
            }, deadline);
        }

        public override string tryGetFileFromGuest(string srcpath, out Exception e)
        {
            cancellableDateTime deadline = new cancellableDateTime(TimeSpan.FromMinutes(5));

            if (!srcpath.ToLower().StartsWith("c:"))
            {
                e = new Exception("Only C:\\ is shared");
                return null;
            }

            return tryDoNetworkCall<string>(() =>
            {
                try
                {
                    // Check the file exists before attempting to open it, as this can save a FileNotFoundException sometimes.
                    string srcUNC = string.Format("\\\\{0}\\C{1}", _guestIP, srcpath.Substring(2));
                    if (!File.Exists(srcUNC))
                        return new triedNetworkCallRes<string>() {retryRequested = true};

                    using (FileStream srcFile = File.Open(srcUNC, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        using (StreamReader srcReader = new StreamReader(srcFile))
                        {
                            return new triedNetworkCallRes<string>() {res = srcReader.ReadToEnd()};
                        }
                    }
                }
                catch (Exception)
                {
                    return new triedNetworkCallRes<string>() {error = new hypervisorExecutionException()};
                }
            }, deadline, out e);
        }
    }

    public class execFileSet
    {
        public readonly string stdOutFilename;
        public readonly string stdErrFilename;
        public readonly string returnCodeFilename;
        public readonly string launcherPath;

        public execFileSet(string newstdOutFilename, string newstdErrFilename, string newreturnCodeFilename, string newLauncherPath)
        {
            launcherPath = newLauncherPath;
            stdOutFilename = newstdOutFilename;
            stdErrFilename = newstdErrFilename;
            returnCodeFilename = newreturnCodeFilename;
        }
    }

    [Serializable]
    public class hypervisorExecutionException : Exception
    {
        public hypervisorExecutionException()
            : base()
        {
        }

        public hypervisorExecutionException(string a)
            : base(a)
        {
        }
    }

    [Serializable]
    public class hypervisorExecutionException_retryable : Exception
    {
    }
}