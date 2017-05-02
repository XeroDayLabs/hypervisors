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

        public override void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            if (stdoutfilename != null || stderrfilename != null || retCodeFilename != null)
                throw new NotSupportedException();

            ProcessStartInfo ps = new ProcessStartInfo(toExecute, args);
            ps.WorkingDirectory = workingDir;
            Process.Start(ps).Dispose();
        }

        public override void mkdir(string newDir)
        {
            Directory.CreateDirectory(newDir);            
        }
    }
}