using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public class hypervisor_vmware : hypervisorWithSpec<hypSpec_vmware>
    {
        private readonly hypSpec_vmware _spec;

        // Maybe this will make all my weird vmware problems go away :^)
        private static Object VMWareLock = new Object();

        private VimClientImpl VClient;
        private VirtualMachine _underlyingVM;

        /// <summary>
        /// This can be used to select if executions will be performed via VMWare tools, or via psexec. Make sure that you take
        /// the neccessary steps for configuring SMB on the client machine, if you're going to use it.
        /// </summary>
        private clientExecutionMethod _executionMethod;
        /// 
        private IRemoteExecution executor;

        public hypervisor_vmware(hypSpec_vmware spec, clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
        {
            _spec = spec;

            lock (VMWareLock)
            {
                VClient = new VimClientImpl();
                VClient.Connect("https://" + _spec.kernelVMServer + "/sdk");
                VClient.Login(_spec.kernelVMServerUsername, _spec.kernelVMServerPassword);

                List<EntityViewBase> vmlist = VClient.FindEntityViews(typeof (VirtualMachine), null, null, null);
                _underlyingVM = (VirtualMachine) vmlist.SingleOrDefault(x => ((VirtualMachine) x).Config.Name.ToLower() == _spec.kernelVMName.ToLower());
                if (_underlyingVM == null)
                    throw new Exception("Can't find VM named '" + _spec.kernelVMName + "'");
            }

            _executionMethod = newExecMethod;
            switch (newExecMethod)
            {
                case clientExecutionMethod.vmwaretools:
                    executor = new vmwareRemoteExecutor(spec, VClient, _underlyingVM);
                    break;
                case clientExecutionMethod.smb:
                    executor = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.kernelVMUsername, spec.kernelVMPassword);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("newExecMethod", newExecMethod, null);
            }
        }

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            lock (VMWareLock)
            {
                _underlyingVM.UpdateViewData();

                // Find its named snapshot
                VirtualMachineSnapshotTree snapshot = findRecusively(_underlyingVM.Snapshot.RootSnapshotList, snapshotNameOrID);

                // and revert it.
                VirtualMachineSnapshot shot = new VirtualMachineSnapshot(VClient, snapshot.Snapshot);
                shot.RevertToSnapshot(_underlyingVM.MoRef, false);
            }
        }

        private VirtualMachineSnapshotTree findRecusively(VirtualMachineSnapshotTree[] parent, string snapshotNameOrID)
        {
            foreach (VirtualMachineSnapshotTree tree in parent)
            {
                if (tree.Name == snapshotNameOrID || tree.Id.ToString() == snapshotNameOrID)
                    return tree;

                return findRecusively(tree.ChildSnapshotList, snapshotNameOrID);
            }
            return null;
        }

        private string doWithRetryOnSomeExceptions(Func<string> thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
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
                catch (VimException e)
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

        public override void connect()
        {
        }

        public override void powerOn()
        {
            lock (VMWareLock)
            {
                // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
                // particularly under load, hence the retries.
                doWithRetryOnSomeExceptions(() =>
                {
                    lock (VMWareLock)
                    {
                        _underlyingVM.UpdateViewData();
                        Debug.WriteLine(_underlyingVM.Runtime.PowerState);
                        if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOn)
                            return null;
                        _underlyingVM.PowerOnVM(_underlyingVM.Runtime.Host);
                    }
                    return null;
                }, TimeSpan.FromSeconds(3), 100);
            }

            // Wait for it to be ready
            if (_executionMethod == clientExecutionMethod.vmwaretools)
            {
                while (true)
                {
                    lock (VMWareLock)
                    {
                        _underlyingVM.UpdateViewData();
                        if (_underlyingVM.Guest.ToolsRunningStatus == VirtualMachineToolsRunningStatus.guestToolsRunning.ToString())
                            break;
                    }

                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }

            // No, really, wait for it to be ready
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
                }
                return "";
            }, TimeSpan.FromSeconds(5), 100);
        }

        public override void powerOff()
        {
            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            _underlyingVM.UpdateViewData();
            Debug.WriteLine("poweroff: old state " + _underlyingVM.Runtime.PowerState);
            while (_underlyingVM.Runtime.PowerState != VirtualMachinePowerState.poweredOff)
            {
                doWithRetryOnSomeExceptions(() =>
                {
                    lock (VMWareLock)
                    {
                        _underlyingVM.UpdateViewData();
                        Debug.WriteLine("poweroff: old state " + _underlyingVM.Runtime.PowerState);
                        if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOff)
                            return null;
                        _underlyingVM.PowerOffVM();
                    }
                    return null;
                }, TimeSpan.FromSeconds(1), 10);
            }
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _copyToGuest(srcpath, dstpath);
                }
                return null;
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        private void _copyToGuest(string srcpath, string dstpath)
        {
            executor.copyToGuest(srcpath, dstpath);
        }

        public override string getFileFromGuest(string srcpath)
        {
            return doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    return executor.getFileFromGuest(srcpath);
                }
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null)
        {
            return executor.startExecutable(toExecute, args, workingdir);
        }

        public override void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string returnCodeFilename = null)
        {
            executor.startExecutableAsync(toExecute, args, workingDir, stdoutfilename, stderrfilename, returnCodeFilename);
        }
        
        public override void mkdir(string newDir)
        {
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    executor.mkdir(newDir);
                }
                return null;
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        public override hypSpec_vmware getConnectionSpec()
        {
            return _spec;
        }

        public override string ToString()
        {
            return string.Format("{0}:{1}", _spec.kernelDebugIPOrHostname, _spec.kernelDebugPort);
        }
    }

    public enum clientExecutionMethod
    {
        vmwaretools,
        smb
    }
}