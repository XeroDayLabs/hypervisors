using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

            execFileSet fileSet = base.prepareForExecution(toExecute, args, tempDir);

            // We do this by creating two batch files on the target.
            // The first contains the command we're executing, and the second simply calls the first with redirection to the files
            // we want our output in. This simplifies escaping on the commandline via psexec.
            string payloadBatchfile = Path.GetTempFileName() + ".bat";
            string launcherTempFile = Path.GetTempFileName() + ".bat";
            try
            {
                // Now execute the launcher.bat via psexec.
                string psExecArgs = string.Format("\\\\{0} {5} {6} -accepteula -u {1} -p {2} -w {4} -h \"{3}\"",
                    _guestIP, _username, _password, fileSet.launcherPath, workingDir, "-d", runInteractively ? " -i " : "");
                ProcessStartInfo info = new ProcessStartInfo(@"C:\ProgramData\chocolatey\bin\PsExec.exe", psExecArgs);
                info.RedirectStandardError = true;
                info.RedirectStandardOutput = true;
                info.UseShellExecute = false;
                info.WindowStyle = ProcessWindowStyle.Hidden;

                //Debug.WriteLine(string.Format("starting on {2}: {0} {1}", toExecute, cmdArgs, _guestIP));
                Process proc = Process.Start(info);

                // We allow psexec 65 seconds to start the process async on the host.
                if (!proc.WaitForExit((int) TimeSpan.FromSeconds(65).TotalMilliseconds))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    throw new TimeoutException();
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
            finally
            {
                // And delete our temp files.
                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(3);
                while (true)
                {
                    try
                    {
                        File.Delete(payloadBatchfile);
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        if (deadline < DateTime.Now)
                            throw;
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
                while (true)
                {
                    try
                    {
                        File.Delete(launcherTempFile);
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        if (deadline < DateTime.Now)
                            throw;
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
            }
        }

        public override void mkdir(string newDir)
        {
            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, newDir.Substring(2));
            int retries = 60;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        if (!Directory.Exists(destUNC))
                            Directory.CreateDirectory(destUNC);
                    }
                    break;
                }
                catch (Win32Exception e)
                {
                    if (e.NativeErrorCode == 1219)  // "multiple connections to a server are not allowed"
                        throw;
                    if (retries-- == 0)
                        throw;
                }
                catch (IOException)
                {
                    if (retries-- == 0)
                        throw;
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {   
            if (!dstpath.ToLower().StartsWith("c:"))
                throw new Exception("Only C:\\ is shared");

            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, dstpath.Substring(2));
            if (destUNC.EndsWith("\\"))
                destUNC += Path.GetFileName(srcpath);

            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(3);
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        if (File.Exists(destUNC))
                            break;
                        // race condition here?
                        System.IO.File.Copy(srcpath, destUNC);
                    }
                    break;
                }
                catch (Win32Exception)
                {
                    if (DateTime.Now > deadline)
                        throw;
                }
                catch (IOException)
                {
                    if (DateTime.Now > deadline)
                        throw;
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        public override string getFileFromGuest(string srcpath)
        {
            if (!srcpath.ToLower().StartsWith("c:"))
                throw new Exception("Only C:\\ is shared");

            string srcUNC = string.Format("\\\\{0}\\C{1}", _guestIP, srcpath.Substring(2));

            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(5);
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        using (FileStream srcFile = File.Open(srcUNC, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            using (StreamReader srcReader = new StreamReader(srcFile))
                            {
                                return srcReader.ReadToEnd();
                            }
                        }
                    }
                }
                catch (Win32Exception e)
                {
                    if (e.NativeErrorCode == 1219)
                    {
                        // This is ERROR_SESSION_CREDENTIAL_CONFLICT.
                        // It indicates we need manual intervention.
                        throw;
                    }

                    if (DateTime.Now > deadline)
                        throw;

                    // retry on other win32 exceptions
                }
                catch (Exception)
                {
                    throw new hypervisorExecutionException();
                }

                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
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
    public class hypervisorExecutionException : Exception { }

    [Serializable]
    public class hypervisorExecutionException_retryable : Exception { }
}