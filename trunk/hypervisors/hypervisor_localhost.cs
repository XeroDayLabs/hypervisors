using System;
using System.Diagnostics;
using System.IO;

namespace hypervisors
{
    public class hypervisor_localhost : hypervisor
    {
        private Process _p = null;

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            throw new NotImplementedException();
        }

        public override void connect()
        {
        }

        protected override void _Dispose()
        {
            try
            {
                if (_p != null)
                {
                    _p.CloseMainWindow();
                    _p.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
            }
            base._Dispose();
        }

        public override void powerOn()
        {

        }

        public override void powerOff()
        {

        }

        public override void copyToGuest(string srcpath, string dstpath, bool ignoreExisting)
        {
            if (dstpath.EndsWith("\\"))
                dstpath += Path.GetFileName(srcpath);
            if (File.Exists(dstpath))
            {
                if (ignoreExisting)
                    return;
                throw  new Exception("File " + dstpath + " already exists");
            }

            File.Copy(srcpath, dstpath);
        }

        public override string getFileFromGuest(string srcpath)
        {
            return File.ReadAllText(srcpath);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            ProcessStartInfo ps = new ProcessStartInfo(toExecute, args);
            ps.UseShellExecute = false;
            ps.RedirectStandardError = true;
            ps.RedirectStandardOutput = true;
            ps.WorkingDirectory = workingDir;
            _p = Process.Start(ps);
            _p.WaitForExit();

            return new executionResult()
            {
                resultCode = _p.ExitCode,
                stderr = _p.StandardError.ReadToEnd(),
                stdout = _p.StandardOutput.ReadToEnd()
            };
        }

        public override void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            if (stdoutfilename != null || stderrfilename != null || retCodeFilename != null)
                throw new NotSupportedException();

            ProcessStartInfo ps = new ProcessStartInfo(toExecute, args);
            ps.WorkingDirectory = workingDir;
            _p = Process.Start(ps);
        }

        public override void mkdir(string newDir)
        {
            Directory.CreateDirectory(newDir);            
        }
    }
}