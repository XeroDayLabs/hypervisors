using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

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

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null, DateTime deadline = default(DateTime))
        {
            if (workingDir == null)
                workingDir = "C:\\";

            if (deadline == default(DateTime))
                deadline = DateTime.Now + TimeSpan.FromDays(7);

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
            return new asyncExecutionResult_localhost(proc);
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

    public class asyncExecutionResult_localhost : IAsyncExecutionResult
    {
        private readonly Process _proc;

        public asyncExecutionResult_localhost(Process proc)
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
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill();
                    DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(2);
                    while (!_proc.HasExited)
                    {
                        if (DateTime.Now > deadline)
                            throw new TimeoutException();
                        _proc.Kill();
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // ...
            }
            _proc.Dispose();
        }
    }
}