using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace hypervisors
{
    /// <summary>
    /// This class exposes a bare windows box. It uses SMB to copy files, and can spawn processes (by shelling out to psexec).
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// Also, ensure that you set the required registry key on the guest before using this, otherwise you will see "the handle is invalid"
    /// errors - see the wiki.
    /// </summary>
    public class SMBExecutor : remoteExecution
    {
        string _guestIP;
        string _username;
        string _password;
        private NetworkCredential _cred;

        /// <summary>
        /// This will pass '-i' to psexec, causing the target process to be executed on the currently-logged-in user's desktop.
        /// This won't work if there is no user logged in.
        /// </summary>
        public bool runInteractively = true;

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

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
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
                if (!proc.WaitForExit((int) TimeSpan.FromSeconds(65).TotalMilliseconds))
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

                // Note that we can't check the return status here, since psexec returns a PID :/
                return new asyncExecutionResultViaFile(this, fileSet);
            }
        }

        private string findPsexecPath()
        {
            // Search the system PATH, and also these two hardcoded locations.
            List<string> candidates = new List<string>();
            candidates.AddRange(Environment.GetEnvironmentVariable("PATH").Split(';')); // Check it out, an old-school injection here! Can you spot it?
            candidates.AddRange(new string[]
            {
                // Chocolatey installs to this path by default, but also installs the 64-bit version by default.
                @"C:\ProgramData\chocolatey\bin",
                @"C:\ProgramData\chocolatey\bin"
            }
                );
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

        public override void mkdir(string newDir)
        {
            doNetworkCall(() =>
            {
                string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, newDir.Substring(2));

                while (!Directory.Exists(destUNC))
                    Directory.CreateDirectory(destUNC);
            }, TimeSpan.FromMinutes(2));
        }

        public override void deleteFile(string toDelete)
        {
            doNetworkCall(() =>
            {
                string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, toDelete.Substring(2));

                if (Directory.Exists(destUNC))
                    Directory.Delete(destUNC, true);
                else if (File.Exists(destUNC))
                    File.Delete(destUNC);
            }, TimeSpan.FromMinutes(2));
        }

        private void doNetworkCall(Action toRun, TimeSpan timeout)
        {
            doNetworkCall(() =>
            {
                toRun.Invoke();
                return 0;
            }, timeout);
        }

        private T doNetworkCall<T>(Func<T> toRun, TimeSpan timeout)
        {
            Exception e;
            T toRet = tryDoNetworkCall(() =>
            {
                T toRetInner = toRun.Invoke();
                return new triedNetworkCallRes<T>() { res = toRetInner };
            }, timeout, out e);
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

        private T tryDoNetworkCall<T>(Func<triedNetworkCallRes<T>> toRun, TimeSpan timeout, out Exception excepOrNull)
        {
            DateTime deadline = DateTime.Now + timeout;
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
                            if (DateTime.Now > deadline)
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

        public override void copyToGuest(string dstPath, string srcPath)
        {
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
            }, TimeSpan.FromMinutes(3));
        }

        public override string tryGetFileFromGuest(string srcpath, out Exception e)
        {
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

            }, TimeSpan.FromMinutes(5), out e);
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