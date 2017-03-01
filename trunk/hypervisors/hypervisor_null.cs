using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace hypervisors
{
    /// <summary>
    /// This class exposes a bare windows box. It uses SMB to copy files, spawn processes (by shelling out to psexec), and provides
    /// no snapshotting capability.
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// </summary>
    public class hypervisor_null : hypervisor
    {
        string _guestIP;
        string _username;
        string _password;
        private NetworkCredential _cred;

        public hypervisor_null(string guestIP, string _guestUsername, string _guestPassword)
        {
            _guestIP = guestIP;
            _username = _guestUsername;
            _password = _guestPassword;
            _cred = new NetworkCredential(_username, _password);
        }

        public override void connect()
        {
        }

        public override void powerOn()
        {
        }

        public override void powerOff()
        {
        }

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            throw new NotImplementedException();
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            // Execute via cmd.exe so we can capture stdout.
            string stdoutfilename = string.Format("C:\\users\\{0}\\hyp_stdout.txt", _username);
            string stderrfilename = string.Format("C:\\users\\{0}\\hyp_stderr.txt", _username);
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

        public override void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            // Execute via cmd.exe so we can capture stdout.
            string cmdargs = String.Format("/c  {0} {1} ", toExecute, args);
            if (stdoutfilename != null)
                cmdargs += " 1> " + stdoutfilename;
            if (stderrfilename != null)
                cmdargs += " 2> " + stderrfilename;
            if (retCodeFilename != null)
                cmdargs += " & echo %errorlevel% > " + retCodeFilename;

            executionResult toRet = new executionResult();
            toRet.resultCode = _startExecutable("cmd.exe", cmdargs, workingDir, true);
        }

        private int _startExecutable(string toExecute, string cmdArgs, string workingDir = null, bool detach = false)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            string args = string.Format("\\\\{0} {6} -u {1} -p {2} -w {5} -h {3} {4}"
                , _guestIP, _username, _password, toExecute, cmdArgs, workingDir, detach ? " -d " : "");
            ProcessStartInfo info = new ProcessStartInfo("psexec.exe", args);
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Normal;

            Process proc = Process.Start(info);

            proc.WaitForExit();

            return proc.ExitCode;
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

        public override void copyToGuest(string srcpath, string dstpath, bool ignoreExisting)
        {   
            if (!dstpath.ToLower().StartsWith("c:"))
                throw new Exception("Only C:\\ is shared");

            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, dstpath.Substring(2));
            if (destUNC.EndsWith("\\"))
                destUNC += Path.GetFileName(srcpath);

            int retries = 10;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        if (File.Exists(destUNC))
                        {
                            if (ignoreExisting)
                                break;
                            throw new Exception("File " + destUNC + " already exists on target");
                        }

                        System.IO.File.Copy(srcpath, destUNC);
                    }
                    break;
                }
                catch (Win32Exception)
                {
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
//                catch (FileNotFoundException)
//                {
//                    return null;
//                }
//                catch (IOException)
//                {
//                    if (DateTime.Now > deadline)
//                        throw;
//                }
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Serializable]
    public class hypervisorExecutionException : Exception
    {
    }
}