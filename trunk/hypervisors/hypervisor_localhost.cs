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
            throw new NotImplementedException();
        }

        public override void powerOff()
        {
            throw new NotImplementedException();
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {
            if (dstpath.EndsWith("\\"))
                dstpath += Path.GetFileName(srcpath);
            if (File.Exists(dstpath))
                File.Delete(dstpath);
            File.Copy(srcpath, dstpath);
        }

        public override void startExecutable(string toExecute, string args)
        {
            _p = Process.Start(toExecute, args);
        }

        public override void mkdir(string newDir)
        {
            Directory.CreateDirectory(newDir);            
        }
    }
}