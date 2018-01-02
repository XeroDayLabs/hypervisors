using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using System.Threading;
using System.Threading.Tasks;

namespace hypervisors
{
    /// <summary>
    /// This is a legacy psexec.exe-powered executor, intended as a temporary solution for systems that cannot yet work properly
    /// with WMI-based execution.
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// It is neccessary to set the required registry key on the guest before using this - see the wiki. 
    /// </summary>
    public class SMBExecutorWithPSExec : SMBExecutor
    {
        public SMBExecutorWithPSExec(string guestIP, string _guestUsername, string _guestPassword)
            : base(guestIP, _guestUsername, _guestPassword)
        {
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            return _startExecutableAsync(toExecute, args, workingDir, false);
        }

        public override IAsyncExecutionResult startExecutableAsyncInteractively(string toExecute, string args, string workingDir = null)
        {
            return _startExecutableAsync(toExecute, args, workingDir, true);
        }

        public IAsyncExecutionResult _startExecutableAsync(string toExecute, string args, string workingDir, bool runInteractively)
        {
            string tempDir = string.Format("C:\\users\\{0}\\", _username);

            if (workingDir == null)
                workingDir = tempDir;

            // We do this by creating two batch files on the target.
            // The first contains the command we're executing, and the second simply calls the first with redirection to the files
            // we want our output in. This simplifies escaping on the commandline via psexec.
            execFileSet fileSet = base.prepareForExecution(toExecute, args, tempDir);

            // Now execute the launcher.bat via psexec.
            string psExecArgs = string.Format("\\\\{0} {5} {6} -accepteula -u {1} -p {2} -w {4} -n 5 -h \"{3}\"",
                _guestIP, _username, _password, fileSet.launcherPath, workingDir, "-d", runInteractively ? " -i " : "");
            ProcessStartInfo info = new ProcessStartInfo(findPsexecPath(), psExecArgs);
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Hidden;

            //Debug.WriteLine(string.Format("starting on {2}: {0} {1}", toExecute, cmdArgs, _guestIP));
            using (Process proc = Process.Start(info))
            {
                // We allow psexec a relatively long window to start the process async on the host.
                // This is because psexec can frequently take a long time to operate. Note that we 
                // supply "-n" to psexec so we don't wait for a long time for non-responsive machines
                // (eg, in the poweron path).
                if (!proc.WaitForExit((int)TimeSpan.FromSeconds(65).TotalMilliseconds))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    return null;
                }

                // Now we can scrape stdout and make sure the process was started correctly. 
                string psexecStdErr = proc.StandardError.ReadToEnd();
                if (psexecStdErr.Contains("The handle is invalid."))
                    return null;
                if (!psexecStdErr.Contains(" started on " + _guestIP + " with process ID "))
                    return null;
                if (psexecStdErr.Contains("The specified service has been marked for deletion."))
                {
                    // Oh no!! This means that psexec, on the target machine, is left in a non-functional state. Attempts to use
                    // it to start any processes will fail D:
                    // I can't fid a way to recover from this, so we have to force a machine reboot here DDD: Hopefully it only
                    // happens during deployment of the fuzzer (?), in which case we can recover just by deploying again. 
                    // I think we need a better way to execute remotely, PSExec may not be the best :(
                    throw new targetNeedsRebootingException(psexecStdErr, proc.ExitCode);
                }

                // Note that we can't check the return status here, since psexec returns a PID :/
                return new asyncExecutionResultViaFile(this, fileSet);
            }
        }

        private string findPsexecPath()
        {
            // Search the system PATH, and also these two hardcoded locations.
            List<string> candidates = new List<string>();
            candidates.AddRange(Environment.GetEnvironmentVariable("PATH").Split(';')); // Check it out, an old-school injection here! Can you spot it?
            // Chocolatey installs to this path by default
            candidates.Add(@"C:\ProgramData\chocolatey\bin");
            foreach (string candidatePath in candidates)
            {
                string psExecPath32 = Path.Combine(candidatePath, "psexec.exe");
                string psExecPath64 = Path.Combine(candidatePath, "psexec64.exe");

                if (File.Exists(psExecPath32))
                    return psExecPath32;
                if (File.Exists(psExecPath64))
                    return psExecPath64;
            }

            throw new Exception("PSExec not found. Put it in your system PATH or install via chocolatey ('choco install sysinternals').");
        }
    }

    /// <summary>
    /// This class exposes a bare windows box. It uses SMB to copy files, and WMI to execute things.
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// </summary>
    public class SMBExecutor : remoteExecution
    {
        protected readonly string _guestIP;
        protected readonly string _username;
        protected readonly string _password;
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
            // Shell out to create it.
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

            //
            // We do things a bit differently to what we'd expect here.
            // We don't use WMI by calling the WMI API. Instead, we shell out to wmic.exe.
            // This is for a few reasons:
            //  1) Poor timeout support on the ManagementScope API
            //     There's no real way to change the timeout used by ManagementScope.Connect. The best we can do is to make a 
            //     task which runs it async, and then to give up if it doesn't complete in time. Messy, and we can't stop it
            //     from throwing when it fails.
            //  2) Unsubstantiated reports online of people encountering memory leaks, particularly when using multithreading
            //  3) I just saw an AccessViolationException in the method 'Connectnsecureiwbemservices', as called by this WMI
            //     stuff
            //  4) I'm seeing weird failiures to run things at unpredictable times and suspect this will fix things.

            // We execute by creating two batch files on the target.
            // The first contains the command we're executing, and the second simply calls the first with redirection to the files
            // we want our output in.
            execFileSet fileSet = base.prepareForExecution(toExecute, args, tempDir);

            string wmicArgs = string.Format("/node:\"{0}\" /user:\"{1}\" /password:\"{2}\" process call create \"cmd /c {3}\",\"{4}\"",
                _guestIP, _username, _password, fileSet.launcherPath, workingDir);
            ProcessStartInfo info = new ProcessStartInfo("wmic.exe", wmicArgs);
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Hidden;

            using (Process proc = Process.Start(info))
            {
                if (!proc.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    return null;
                }

                // WMIC will return zero on success, or an error code
                if (proc.ExitCode != 0)
                {
                    switch ((uint)proc.ExitCode)
                    {
                        case 0x800706ba:
                            // "The RPC server was unavailable"
                            return null;
                        case 0x800706b5:
                            // "The interface is unknown"
                            return null;
                        case 0x80070005:
                            // "Access is denied"
                            return null;
                        case 0x80131500:
                        case 0x80010108:
                            // "Object invoked has disconnected from its clients"
                            return null;
                        default:
                            throw new Win32Exception(proc.ExitCode);
                    }
                }

                // stdout will return something similar to the following:
                //
                //  Executing (Win32_Process)->Create()
                //  Method execution successful.
                //  Out Parameters:
                //  instance of __PARAMETERS
                //  {
                //          ProcessId = 2176;
                //          ReturnValue = 0;
                //  };
                string stdout = proc.StandardOutput.ReadToEnd();

                if (!stdout.Contains("Method execution successful") ||
                    !stdout.Contains("ReturnValue = 0;"))
                    return null;

                return new asyncExecutionResultViaFile(this, fileSet);
            }
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
            }, deadline);
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
                return new triedNetworkCallRes<T>() {res = toRetInner};
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

                            deadline.doCancellableSleep(TimeSpan.FromSeconds(1));
                            continue;
                        }

                        // Other exceptions should be passed up to the caller.
                        excepOrNull = e;
                        return default(T);
                    }

                    triedNetworkCallRes<T> toRet = toRun.Invoke();
                    if (toRet.retryRequested)
                    {
                        deadline.doCancellableSleep(TimeSpan.FromSeconds(3));
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
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(3));

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
        public hypervisorExecutionException() : base()
        {
        }

        public hypervisorExecutionException(string a) : base(a)
        {
        }
    }

    [Serializable]
    public class hypervisorExecutionException_retryable : Exception
    {
    }
}