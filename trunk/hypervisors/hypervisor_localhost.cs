using System;
using System.Diagnostics;
using System.IO;

namespace hypervisors
{
    public class hypervisor_localhost : hypervisor
    {
        public override void restoreSnapshot()
        {
            throw new NotImplementedException();
        }

        public override void connect()
        {
        }
        
        public override void powerOn(DateTime deadline = default(DateTime))
        {

        }

        public override void powerOff(DateTime deadline = default(DateTime))
        {

        }

        public override void copyToGuest(string dstpath, string srcpath)
        {
            if (dstpath.EndsWith("\\"))
                dstpath += Path.GetFileName(dstpath);
            if (File.Exists(dstpath))
                return;

            File.Copy(dstpath, srcpath);
        }

        public override string getFileFromGuest(string srcpath, TimeSpan timeout = default(TimeSpan))
        {
            return File.ReadAllText(srcpath);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null, DateTime deadline = new DateTime())
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
                TimeSpan timeout = deadline - DateTime.Now;
                if (!process.WaitForExit((int) timeout.TotalMilliseconds))
                    throw new TimeoutException();

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