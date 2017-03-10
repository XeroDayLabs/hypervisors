using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        private hypervisor_iLo_HTTP ilo;
        private SMBExecutor _nullHyp;

        private hypSpec_iLo _spec;

        public hypervisor_iLo(hypSpec_iLo spec)
        {
            _spec = spec;
            ilo = new hypervisor_iLo_HTTP(spec.iLoHostname, spec.iLoUsername, spec.iLoPassword);
            _nullHyp = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
        }

        public override void restoreSnapshotByName(string ignored)
        {
            string fullName = _spec.snapshotName;
            FreeNAS nas = new FreeNAS(_spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);

            snapshotObjects shotObjects = getSnapshotObjectsFromNAS(nas, fullName);

            // Here we power the server down, tell the iSCSI server to use the right image, and power it back up again.
            powerOff();

            // Find the device snapshot, so we can get information about it needed to get the ISCSI volume

            // Now we can get started. We must remove the 'target to extent' mapping, then the target. Then we can safely roll back
            // the ZFS snapshot, and then re-add the target and mapping.
            // TODO: can we just tell freeNAS to delete this stuff instead?
            nas.deleteISCSITargetToExtent(shotObjects.tgtToExtent);
            nas.deleteISCSIExtent(shotObjects.extent);

            // Roll back the snapshot. Use a retry, since FreeNAS is complaining the dataset is in use occasionally.
            int retries = 100;
            while (true)
            {
                try
                {
                    nas.rollbackSnapshot(shotObjects.shotToRestore);
                    break;
                }
                catch (Exception)
                {
                    if (retries-- == 0)
                        throw;
                    Thread.Sleep(TimeSpan.FromSeconds(6));  // 6 sec * 100 retries = ten minutes
                }
            }

            // Re-add the extent and target-to-extent mapping.
            iscsiExtent newExtent = nas.addISCSIExtent(shotObjects.extent);
            nas.addISCSITargetToExtent(shotObjects.tgtToExtent.iscsi_target, newExtent);

//            powerOn();
        }

        private snapshotObjects getSnapshotObjectsFromNAS(FreeNAS nas, string fullName)
        {
            var snapshots = nas.getSnapshots();
            snapshot shotToRestore = snapshots.SingleOrDefault(x => x.name.Equals(fullName, StringComparison.CurrentCultureIgnoreCase) || x.id == fullName);
            if (shotToRestore == null)
                throw new Exception("Cannot find snapshot " + fullName);

            // Now find the extent. We'll need to delete it before we can rollback the snapshot.
            List<iscsiExtent> extents = nas.getExtents();
            iscsiExtent extent = extents.SingleOrDefault(x => _spec.snapshotName.Equals(x.iscsi_target_extent_name, StringComparison.CurrentCultureIgnoreCase));
            if (extent == null)
                throw new Exception("Cannot find extent " + _spec.snapshotName);

            // Find the 'target to extent' mapping, since this will need to be depeted before we can delete the extent.
            List<iscsiTargetToExtentMapping> tgtToExtents = nas.getTargetToExtents();
            iscsiTargetToExtentMapping tgtToExtent = tgtToExtents.SingleOrDefault(x => x.iscsi_extent == extent.id);
            if (tgtToExtent == null)
                throw new Exception("Cannot find target-to-extent mapping with ID " + extent.id + " for snapshot " + shotToRestore.name);

            return new snapshotObjects()
            {
                extent = extent,
                shotToRestore = shotToRestore,
                tgtToExtent = tgtToExtent
            };
        }


        public override void connect()
        {
            ilo.connect();
        }

        public override void powerOn()
        {
            ilo.powerOn();

            // Wait until the host is up enough that we can ping it...
            WaitForStatus(true, TimeSpan.FromMinutes(6));

            if (_nullHyp != null)
            {
                // Now wait for it to be up enough that we can psexec to it.
                doWithRetryOnSomeExceptions(() =>
                {
                    _nullHyp.startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
                });
            }
        }

        public void WaitForStatus(bool waitForState, TimeSpan timeout = default(TimeSpan))
        {
            DateTime deadline;
            if (timeout == default(TimeSpan))
                deadline = DateTime.MaxValue;
            else
                deadline = DateTime.Now + timeout;

            // Wait for the box to go down/come up.
            Debug.Print("Waiting for box " + _spec.iLoHostname + " to " + (waitForState ? "come up" : "go down"));
            while (true)
            {
                if (DateTime.Now > deadline)
                    throw new TimeoutException();

                if (waitForState)
                {
                    Icmp pinger = new Org.Mentalis.Network.Icmp(IPAddress.Parse(_spec.kernelDebugIPOrHostname));
                    TimeSpan res = pinger.Ping(TimeSpan.FromMilliseconds(500));
                    if (res != TimeSpan.MaxValue)
                    {
                        Debug.Print(".. Box " + _spec.iLoHostname + " pingable, giving it a few more seconds..");
                        Thread.Sleep(10*1000);
                        break;
                    }
                }
                else
                {
                    if (getPowerStatus() == false)
                        break;
                }

                Thread.Sleep(5000);
            }

            Debug.Print(".. wait complete for box " + _spec.iLoHostname);
        }

        private static void doWithRetryOnSomeExceptions(Action thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
        {
            int retries = maxRetries;
            if (retry == default(TimeSpan))
                retry = TimeSpan.Zero;

            while (true)
            {
                try
                {
                    thingtoDo.Invoke();
                    break;
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

                Thread.Sleep(retry);
            }
        }

        public override void powerOff()
        {
            DateTime deadline = DateTime.Now + TimeSpan.FromSeconds(20);

            ilo.powerOff();
            while (ilo.getPowerStatus())
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
                if (deadline < DateTime.Now)
                    throw new Exception("Failed to turn off machine via iLo");
            }
        }


        private bool getPowerStatus()
        {
            return ilo.getPowerStatus();
        }

        public override string getFileFromGuest(string srcpath)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            return _nullHyp.getFileFromGuest(srcpath);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            return _nullHyp.startExecutable(toExecute, args, workingdir);
        }

        public override void startExecutableAsync(string toExecute, string args, string workingdir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            _nullHyp.startExecutableAsync(toExecute, args, workingdir, stdoutfilename, stderrfilename, retCodeFilename);
        }

        public override void mkdir(string newDir)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            _nullHyp.mkdir(newDir);
        }

        public override hypSpec_iLo getConnectionSpec()
        {
            return _spec;
        }

        public override void copyToGuest(string srcpath, string dstpath, bool ignoreExisting = false)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            _nullHyp.copyToGuest(srcpath, dstpath, ignoreExisting);
        }

        public override string ToString()
        {
            return _spec.iLoHostname;
        }

        public void checkSnapshotSanity()
        {
            FreeNAS nas = new FreeNAS(_spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);
            getSnapshotObjectsFromNAS(nas, _spec.snapshotName);
        }

        protected override void _Dispose()
        {
            ilo.logout();

            base._Dispose();
        }
    }
}