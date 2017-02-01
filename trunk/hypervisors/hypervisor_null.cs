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
            string stdoutfilename = string.Format("C:\\users\\{0}\\hyp_stdout.txt", _username);
            string stderrfilename = string.Format("C:\\users\\{0}\\hyp_stderr.txt", _username);
            // Execute via cmd.exe so we can capture stdout.
            string cmdargs = String.Format("/c {0} {1} 1> {2} 2> {3}", toExecute, args, stdoutfilename, stderrfilename);
            executionResult toRet = new executionResult();
            toRet.resultCode = _startExecutable("cmd.exe", cmdargs, workingDir); ;
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

        public int _startExecutable(string toExecute, string cmdArgs, string workingDir = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            string args = string.Format("\\\\{0} -u {1} -p {2} -w {5} -h {3} {4}", _guestIP, _username, _password, toExecute, cmdArgs, workingDir);
            ProcessStartInfo info = new ProcessStartInfo("psexec.exe", args);
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Normal;

            Process proc = Process.Start(info);

            proc.WaitForExit();

//            if (proc.ExitCode != 0)
//                throw new psExecException(stderr, proc.ExitCode);
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

        public override void copyToGuest(string srcpath, string dstpath)
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

            int retries = 10;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        return System.IO.File.ReadAllText(srcUNC);
                    }
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
    }

    [Serializable]
    public class hypervisorExecutionException : Exception
    {
    }
}