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
        public abstract void copyToGuest(string srcpath, string dstpath, bool ignoreExisting = false);
        public abstract string getFileFromGuest(string srcpath);
        public abstract executionResult startExecutable(string toExecute, string args, string workingDir = null);
        public abstract void mkdir(string newDir);

        public void Dispose()
        {
            _Dispose();
        }

        protected virtual void _Dispose()
        {
        }

        public void copyDirToGuest(string src, string dest, bool ignoreErrors = false)
        {
            if (!File.Exists(dest))
                mkdir(dest);
            foreach (string srcName in Directory.GetFiles(src))
            {
                copyToGuest(srcName, dest + "\\", ignoreErrors);
            }
            foreach (string srcName in Directory.GetDirectories(src))
            {
                copyDirToGuest(srcName, Path.Combine(dest, Path.GetFileName(srcName)), ignoreErrors);
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