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
        private static Dictionary<string, refCount<hypervisor_iLo_HTTP>> _ilos = new Dictionary<string, refCount<hypervisor_iLo_HTTP>>();

        private remoteExecution _executor;

        private hypSpec_iLo _spec;

        public hypervisor_iLo(hypSpec_iLo spec, clientExecutionMethod newExecMethod = clientExecutionMethod.smb)
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
            if (newExecMethod == clientExecutionMethod.smb)
                _executor = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
            else if (newExecMethod == clientExecutionMethod.vmwaretools)
                throw new NotSupportedException();
            else if (newExecMethod == clientExecutionMethod.SSHToBASH)
                _executor = new SSHExecutor(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
        }

        public override void restoreSnapshot()
        {
            freeNASSnapshot.restoreSnapshot(this, _spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);
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

                connectDeadline.throwIfTimedOutOrCancelled();

                Thread.Sleep(TimeSpan.FromSeconds(5));
            }

            // Wait until the host is up enough that we can ping it...
            connectDeadline.throwIfTimedOutOrCancelled();
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

                    Thread.Sleep(TimeSpan.FromSeconds(5));

                    if (!deadline.stillOK)
                        throw new TimeoutException("Failed to turn off machine via iLo");
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
                    deadline.throwIfTimedOutOrCancelled();

                    Thread.Sleep(TimeSpan.FromSeconds(1));
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

        public override string getFileFromGuest(string srcpath, cancellableDateTime deadline)
        {
            if (_executor == null)
                throw new NotSupportedException();

            return doWithRetryOnSomeExceptions(() => { return _executor.tryGetFileFromGuestWithRes(srcpath); }, deadline
                );
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
            return string.Format("{0}:{1}", _spec.kernelDebugIPOrHostname, _spec.kernelDebugPort);
        }

        protected override void Dispose(bool disposing)
        {
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