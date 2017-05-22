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
using Org.Mentalis.Network;

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

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            freeNASSnapshot.restoreSnapshotByNam(this, _spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);
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

        public override void powerOn()
        {
            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(6);
            powerOn(deadline);
        }
 
        public void powerOn(DateTime connectDeadline)
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

                if (DateTime.Now > connectDeadline)
                    throw new TimeoutException();

                Thread.Sleep(TimeSpan.FromSeconds(2));
            }

            // Wait until the host is up enough that we can ping it...
            TimeSpan remaining = connectDeadline - DateTime.Now;
            if (remaining < TimeSpan.FromSeconds(0))
                throw new TimeoutException();
            WaitForStatus(true, remaining );

            // Now wait for it to be up enough that we can psexec to it.
            doWithRetryOnSomeExceptions(() =>
            {
                _executor.testConnectivity();
                return 0;
            });
        }

        public static void doWithRetryOnSomeExceptions(Action thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
        {
            doWithRetryOnSomeExceptions<int>(
                new Func<int>(() => { 
                    thingtoDo(); 
                    return 0;
                }),
                retry, maxRetries);
        }

        public static T doWithRetryOnSomeExceptions<T>(Func<T> thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
        {
            int retries = maxRetries;
            if (retry == default(TimeSpan))
                retry = TimeSpan.Zero;

            while (true)
            {
                try
                {
                    return thingtoDo.Invoke();
                }
                catch (Win32Exception)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                catch (System.Net.WebException)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                catch (psExecException)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                catch (hypervisorExecutionException)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                catch (hypervisorExecutionException_retryable)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }

                Thread.Sleep(retry);
            }
        }

        public override void powerOff()
        {
            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(3);
            powerOff(deadline);
        }

        public void powerOff(DateTime deadline)
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

                    Thread.Sleep(TimeSpan.FromSeconds(3));

                    if (deadline < DateTime.Now)
                        throw new TimeoutException("Failed to turn off machine via iLo");
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

        public override string getFileFromGuest(string srcpath)
        {
            if (_executor == null)
                throw new NotSupportedException();
            return _executor.getFileFromGuest(srcpath);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null)
        {
            if (_executor == null)
                throw new NotSupportedException();
            executionResult toRet = _executor.startExecutable(toExecute, args, workingdir);
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

        public override void mkdir(string newDir)
        {
            if (_executor == null)
                throw new NotSupportedException();
            _executor.mkdir(newDir);
        }

        public override hypSpec_iLo getConnectionSpec()
        {
            return _spec;
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {
            if (_executor == null)
                throw new NotSupportedException();
            _executor.copyToGuest(srcpath, dstpath);
        }

        public override string ToString()
        {
            return string.Format("{0}:{1}", _spec.kernelDebugIPOrHostname, _spec.kernelDebugPort);
        }

        public void checkSnapshotSanity()
        {
            FreeNAS nas = new FreeNAS(_spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);
            freeNASSnapshot.getSnapshotObjectsFromNAS(nas, _spec.snapshotName);
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