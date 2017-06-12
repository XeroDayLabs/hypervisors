using System;
using System.Diagnostics;
using System.IO;

namespace hypervisors
{
    public class hypervisor_localhost : hypervisor
    {
        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            throw new NotImplementedException();
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

        public override void copyToGuest(string srcpath, string dstpath)
        {
            if (dstpath.EndsWith("\\"))
                dstpath += Path.GetFileName(srcpath);
            if (File.Exists(dstpath))
                return;

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
            using (Process process = Process.Start(ps))
            {
                process.WaitForExit();

                return new executionResult(process);
            }
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            ProcessStartInfo ps = new ProcessStartInfo(toExecute, args);
            ps.UseShellExecute = false;
            ps.RedirectStandardError = true;
            ps.RedirectStandardOutput = true;
            ps.WorkingDirectory = workingDir;
            Process proc = Process.Start(ps);
            return new asycExeuctionResult_localhost(proc);
        }

        public override IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null)
        {
            // startExecutableAsync in this class can never fail anyway.
            return startExecutableAsync(toExecute, args, workingDir);
        }

        public override void mkdir(string newDir)
        {
            Directory.CreateDirectory(newDir);            
        }
    }

    public class asycExeuctionResult_localhost : IAsyncExecutionResult
    {
        private readonly Process _proc;

        public asycExeuctionResult_localhost(Process proc)
        {
            _proc = proc;
        }

        public executionResult getResultIfComplete()
        {
            if (!_proc.HasExited)
                return null;

            return new executionResult(_proc.StandardOutput.ReadToEnd(), _proc.StandardError.ReadToEnd(), _proc.ExitCode);
        }

        public void Dispose()
        {
           _proc.Dispose();
        }
    }
}