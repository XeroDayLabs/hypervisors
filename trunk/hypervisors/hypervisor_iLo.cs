using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Lifetime;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace hypervisors
{
    public class snapshotObjects
    {
        public iscsiTargetToExtentMapping tgtToExtent;
        public snapshot shotToRestore;
        public iscsiExtent extent;
    }

    public class hypervisor_iLo : hypervisorWithSpec<hypSpec_iLo>
    {
        private static readonly Dictionary<string, refCount<hypervisor_iLo_HTTP>> _ilos = new Dictionary<string, refCount<hypervisor_iLo_HTTP>>();

        private readonly remoteExecution _executor;

        private readonly hypSpec_iLo _spec;

        private readonly FreeNASWithCaching theNas;

        public hypervisor_iLo(hypSpec_iLo spec, clientExecutionMethod newExecMethod = clientExecutionMethod.smbWithPSExec)
        {
            _spec = spec;
            lock (_ilos)
            {
                if (!_ilos.ContainsKey(spec.iLoHostname))
                {
                    _ilos.Add(spec.iLoHostname, new refCount<hypervisor_iLo_HTTP>(new hypervisor_iLo_HTTP(spec.iLoHostname, spec.iLoUsername, spec.iLoPassword)));
                }
                else
                {
                    _ilos[spec.iLoHostname].addRef();
                }
            }

            if (_spec.iscsiserverIP != null && _spec.iscsiServerUsername != null && _spec.iscsiServerPassword != null)
                theNas = FreeNasGroup.getOrMake(_spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);

            if (newExecMethod == clientExecutionMethod.smbWithPSExec)
                _executor = new SMBExecutorWithPSExec(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
            else if (newExecMethod == clientExecutionMethod.smbWithWMI)
                _executor = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
            else if (newExecMethod == clientExecutionMethod.vmwaretools)
                throw new NotSupportedException();
            else if (newExecMethod == clientExecutionMethod.SSHToBASH)
                _executor = new SSHExecutor(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
        }

        public override void restoreSnapshot()
        {
            freeNASSnapshot.restoreSnapshot(this, theNas, new cancellableDateTime(TimeSpan.FromMinutes(5)));
        }

        public override void connect()
        {
            refCount<hypervisor_iLo_HTTP> ilo;
            lock (_ilos)
            {
                ilo = _ilos[_spec.iLoHostname];
            }

            lock (ilo)
            {
                ilo.tgt.connect();
            }
        }

        public override void powerOn(cancellableDateTime connectDeadline)
        {
            while (true)
            {
                if (getPowerStatus() == true)
                    break;

                refCount<hypervisor_iLo_HTTP> ilo;
                lock (_ilos)
                {
                    ilo = _ilos[_spec.iLoHostname];
                }

                lock (ilo)
                {
                    ilo.tgt.powerOn();
                }

                connectDeadline.doCancellableSleep(TimeSpan.FromSeconds(5));
            }

            // Wait until the host is up enough that we can ping it...
            waitForPingability(true, connectDeadline);

            // Now wait for it to be up enough that we can psexec to it.
            doWithRetryOnSomeExceptions(() =>
            {
                _executor.testConnectivity();
                return 0;
            }, connectDeadline);
        }

        public override void powerOff(cancellableDateTime deadline)
        {
            refCount<hypervisor_iLo_HTTP> ilo;
            lock (_ilos)
            {
                ilo = _ilos[_spec.iLoHostname];
            }

            lock (ilo)
            {
                if (ilo.tgt.getPowerStatus() == false)
                    return;

                while (true)
                {
                    ilo.tgt.powerOff();

                    if (getPowerStatus() == false)
                        break;

                    deadline.doCancellableSleep(TimeSpan.FromSeconds(5), "Failed to turn off machine via iLo");
                }
            }
        }

        public override void WaitForStatus(bool isPowerOn, cancellableDateTime deadline)
        {
            if (isPowerOn)
            {
                doWithRetryOnSomeExceptions(() =>
                {
                    _executor.testConnectivity();
                    return 0;
                }, deadline);
            }
            else
            {
                while (true)
                {
                    if (getPowerStatus() == false)
                        break;

                    deadline.doCancellableSleep(TimeSpan.FromSeconds(5), "Failed to turn off machine via iLo");
                }
            }
        }

        public override bool getPowerStatus()
        {
            refCount<hypervisor_iLo_HTTP> ilo;
            lock (_ilos)
            {
                ilo = _ilos[_spec.iLoHostname];
            }

            lock (ilo)
            {
                return ilo.tgt.getPowerStatus();
            }
        }

        public override string getFileFromGuest(string srcpath, cancellableDateTime deadline = null)
        {
            if (_executor == null)
                throw new NotSupportedException();

            return doWithRetryOnSomeExceptions(() => { return _executor.tryGetFileFromGuestWithRes(srcpath); }, deadline);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null, cancellableDateTime deadline = null)
        {
            if (_executor == null)
                throw new NotSupportedException();
            executionResult toRet = _executor.startExecutable(toExecute, args, workingdir, deadline);
            //Debug.WriteLine("Command '{0}' with args '{1}' returned {2} stdout '{3}' stderr '{4}'", toExecute, args, toRet.resultCode, toRet.stdout, toRet.stderr);

            return toRet;
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            if (_executor == null)
                throw new NotSupportedException();
            return _executor.startExecutableAsync(toExecute, args, workingDir);
        }

        public override IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null)
        {
            return _executor.startExecutableAsyncWithRetry(toExecute, args, workingDir);
        }

        public override IAsyncExecutionResult startExecutableAsyncInteractively(string cmdExe, string args, string workingDir = null)
        {
            IAsyncExecutionResult toRet = null;
            while (toRet == null)
                toRet =_executor.startExecutableAsyncInteractively(cmdExe, args, workingDir);
            return toRet;
        }

        public override void mkdir(string newDir, cancellableDateTime deadline = null)
        {
            if (_executor == null)
                throw new NotSupportedException();
            _executor.mkdir(newDir, deadline);
        }

        public override hypSpec_iLo getConnectionSpec()
        {
            return _spec;
        }

        public override void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline = null)
        {
            if (_executor == null)
                throw new NotSupportedException();
            _executor.copyToGuest(dstpath, srcpath, deadline);
        }

        public void deleteFile(string toDelete, cancellableDateTime deadline = null)
        {
            if (_executor == null)
                throw new NotSupportedException();

            _executor.deleteFile(toDelete, deadline);
        }

        public override string ToString()
        {
            return string.Format("{0}:{1}", _spec.kernelDebugIPOrHostname, _spec.kernelDebugSerialPort ?? _spec.kernelDebugPort.ToString());
        }

        protected override void Dispose(bool disposing)
        {
            _executor.Dispose();

            // FIXME: oh no is it permissible to lock in the GC thread or can we deadlock?
            refCount<hypervisor_iLo_HTTP> ilo;
            lock (_ilos)
            {
                ilo = _ilos[_spec.iLoHostname];
            }

            lock (ilo)
            {
                ilo.tgt.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}