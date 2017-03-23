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
    public class SMBExecutor : IRemoteExecution
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

        public executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            // Execute via cmd.exe so we can capture stdout.
            string stdoutfilename = string.Format("C:\\users\\{0}\\hyp_stdout_{1}.txt", _username, Guid.NewGuid().ToString());
            string stderrfilename = string.Format("C:\\users\\{0}\\hyp_stderr_{1}.txt", _username, Guid.NewGuid().ToString());
            string cmdargs = String.Format("/c {0} {1} 1> {2} 2> {3}", toExecute, args, stdoutfilename, stderrfilename);
            executionResult toRet = new executionResult();
            toRet.resultCode = _startExecutable("cmd.exe", cmdargs, workingDir, false); ;

            try
            {
                toRet.stdout = getFileFromGuest(stdoutfilename);
                toRet.stderr = getFileFromGuest(stderrfilename);
            }
            catch (FileNotFoundException)
            {
                throw new hypervisorExecutionException();
            }

            return toRet;
        }
        
        public void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            // Execute via cmd.exe so we can capture stdout.
            string cmdargs = String.Format("/c {0} {1} ", toExecute, args);
            if (stdoutfilename != null)
                cmdargs += " 1> " + stdoutfilename;
            if (stderrfilename != null)
                cmdargs += " 2> " + stderrfilename;
            if (retCodeFilename != null)
                cmdargs += " & echo %errorlevel% > " + retCodeFilename;     // FIXME: does this errorlevel need escaping?!

            executionResult toRet = new executionResult();
            toRet.resultCode = _startExecutable("cmd.exe", cmdargs, workingDir, true);
        }

        private int _startExecutable(string toExecute, string cmdArgs, string workingDir = null, bool detach = false)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            string tempFile = Path.GetTempFileName() + ".bat";
            try
            {
                File.WriteAllText(tempFile, toExecute + " " + cmdArgs);

                string psExecArgs = string.Format("\\\\{0} {6} {7} -c -u {1} -p {2} -w {5} -h \"{4}\"",
                    _guestIP, _username, _password, toExecute, tempFile, workingDir, detach ? " -d " : "", runInteractively ? " -i " : "");
                ProcessStartInfo info = new ProcessStartInfo("psexec.exe", psExecArgs);
                info.RedirectStandardError = true;
                info.RedirectStandardOutput = true;
                info.UseShellExecute = false;
                info.WindowStyle = ProcessWindowStyle.Hidden;

                Debug.WriteLine(string.Format("starting on {2}: {0} {1}", toExecute, cmdArgs, _guestIP));
                Process proc = Process.Start(info);

                if (detach)
                {
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
                }
                else
                {
                    proc.WaitForExit();
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                Debug.WriteLine(stdout);
                Debug.WriteLine(stderr);

                if (detach)
                {
                    Debug.WriteLine("Child PID is " + proc.ExitCode);
                    return 0;
                }

                Debug.WriteLine("psexec returned " + proc.ExitCode);
                if (proc.ExitCode == 6 || proc.ExitCode == 2250)
                    throw new hypervisorExecutionException_retryable();
                return proc.ExitCode;
            }
            finally
            {
                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(3);
                while (true)
                {
                    try
                    {
                        File.Delete(tempFile);
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

        public void mkdir(string newDir)
        {
            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, newDir.Substring(2));
            int retries = 60;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
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

        public void copyToGuest(string srcpath, string dstpath)
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

        public string getFileFromGuest(string srcpath)
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

    public interface IRemoteExecution
    {
        void mkdir(string newDir);
        void copyToGuest(string srcpath, string dstpath);
        string getFileFromGuest(string srcpath);
        executionResult startExecutable(string toExecute, string args, string workingDir = null);
        void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null);
    }

    [Serializable]
    public class hypervisorExecutionException : Exception { }

    [Serializable]
    public class hypervisorExecutionException_retryable : Exception { }
}