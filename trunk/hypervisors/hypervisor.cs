using System;
using System.IO;

namespace hypervisors
{
    public abstract class hypervisor : IDisposable
    {
        public abstract void restoreSnapshotByName(string snapshotNameOrID);
        public abstract void connect();
        public abstract void powerOn();
        public abstract void powerOff();
        public abstract void copyToGuest(string srcpath, string dstpath);
        public abstract string getFileFromGuest(string srcpath);
        public abstract executionResult startExecutable(string toExecute, string args, string workingdir = null);
        public abstract void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string returnCodeFilename = null);
        public abstract void mkdir(string newDir);

        public void Dispose()
        {
            _Dispose();
        }

        protected virtual void _Dispose()
        {
        }

        public void copyDirToGuest(string src, string dest)
        {
            if (!File.Exists(dest))
                mkdir(dest);
            foreach (string srcName in Directory.GetFiles(src))
            {
                copyToGuest(srcName, dest + "\\");
            }
            foreach (string srcName in Directory.GetDirectories(src))
            {
                copyDirToGuest(srcName, Path.Combine(dest, Path.GetFileName(srcName)));
            }
        }
    }

    public class executionResult
    {
        public int resultCode;

        public string stdout;

        public string stderr;
    }
}