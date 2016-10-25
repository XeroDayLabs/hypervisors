using System;

namespace hypervisors
{
    public abstract class hypervisor : IDisposable
    {
        public abstract void restoreSnapshotByName(string snapshotNameOrID);
        public abstract void connect();
        public abstract void powerOn();
        public abstract void powerOff();
        public abstract void copyToGuest(string srcpath, string dstpath);
        public abstract void startExecutable(string toExecute, string args);
        public abstract void mkdir(string newDir);

        public void Dispose()
        {
            _Dispose();
        }

        protected virtual void _Dispose()
        {
        }
    }
}