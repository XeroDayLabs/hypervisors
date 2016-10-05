using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Net.NetworkInformation;
using System.Threading;
using Newtonsoft.Json;

namespace hypervisors
{
    /// <summary>
    /// This class provides methods for manipulating a cluster node containing an HP iLO card, booted via PXE and iSCSI.
    /// The iLo is used to provide power control, and the iSCSI root is manuipulated via the FreeNAS API.
    /// </summary>
    [Serializable]
    public class hypervisor_iLo_appdomainPayload : MarshalByRefObject 
    {
        private readonly hypSpec_iLo _spec;
        private string pathToHPModules = @"C:\Program Files\Hewlett-Packard\PowerShell\Modules";

        /// <summary>
        /// Only use one global powershell object. If we don't, we get cross-threading issues (!?). Lock it before you use it.
        /// </summary>
        private static readonly PowerShell _psContext = PowerShell.Create();

        private readonly hypervisor_null nullHyp;
        
        public hypervisor_iLo_appdomainPayload(hypSpec_iLo spec)
        {
            _spec = spec;

            if (spec.hostUsername != null && spec.hostPassword != null)
                nullHyp = new hypervisor_null(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
            else
                nullHyp = null;
        }

        private Collection<PSObject> doPowerShell(PowerShell psContext, string cmd)
        {
            // Debug.WriteLine(hostIP + " command " + cmd);

            psContext.Commands.Clear();
            psContext.AddScript(cmd);
            Collection<PSObject> toRet = psContext.Invoke();
            if (psContext.HadErrors || psContext.Streams.Error.Count > 0)
                throw new powerShellException(psContext);
            
            return toRet;
        }

        public void restoreSnapshotByName(string snapshotNameOrID)
        {
            try
            {
                _restoreSnapshotByName(snapshotNameOrID);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                throw;
            }
        }

        public void _restoreSnapshotByName(string snapshotNameOrID)
        {
            string fullName = _spec.extentPrefix + snapshotNameOrID;

            FreeNAS nas = new FreeNAS(_spec.iscsiserverIP, _spec.iscsiServerUsername, _spec.iscsiServerPassword);
            // Here we power the server down, tell the iSCSI server to use the right image, and power it back up again.
            powerOff();
            
            // Find the device snapshot, so we can get information about it needed to get the ISCSI volume
            List<snapshot> snapshots;
            snapshots = nas.getSnapshots();
            snapshot shotToRestore = snapshots.SingleOrDefault(x => x.name.Equals(fullName, StringComparison.CurrentCultureIgnoreCase) || x.id == fullName);
            if (shotToRestore == null)
                throw  new Exception("Cannot find snapshot " + fullName);

            // Now find the extent. We'll need to delete it before we can rollback the snapshot.
            List<iscsiExtent> extents = nas.getExtents();
            iscsiExtent extent = extents.SingleOrDefault(x => x.iscsi_target_extent_name.Equals(_spec.extentPrefix, StringComparison.CurrentCultureIgnoreCase));
            if (extent == null)
                throw new Exception("Cannot find extent " + _spec.extentPrefix);

            // Find the 'target to extent' mapping, since this will need to be depeted before we can delete the extent.
            List<iscsiTargetToExtentMapping> tgtToExtents = nas.getTargetToExtents();
            iscsiTargetToExtentMapping tgtToExtent = tgtToExtents.SingleOrDefault(x => x.iscsi_extent == extent.id);
            if (tgtToExtent == null)
                throw new Exception("Cannot find target-to-extent mapping with ID " + extent.id);

            // Now we can get started. We must remove the 'target to extent' mapping, then the target. Then we can safely roll back
            // the ZFS snapshot, and then re-add the target and mapping.
            // TODO: can we just tell freeNAS to delete this stuff instead?
            nas.deleteISCSITargetToExtent(tgtToExtent);
            nas.deleteISCSIExtent(extent);

            // Roll back the snapshot. Use a retry, since FreeNAS is complaining the dataset is in use occasionally.
            int retries = 100;
            while (true)
            {
                try
                {
                    nas.rollbackSnapshot(shotToRestore);
                    break;
                }
                catch (Exception)
                {
                    if (retries-- == 0)
                        throw;
                    Thread.Sleep(TimeSpan.FromSeconds(6));  // 6 sec * 100 retries = ten minutes
                }
            }

            // Finally, re-add the extent and target-to-extent mapping.
            iscsiExtent newExtent = nas.addISCSIExtent(extent);
            nas.addISCSITargetToExtent(tgtToExtent.iscsi_target, newExtent);

            powerOn();
        }

        public void connect()
        {
            lock (_psContext)
            {
                // We must set the execution policy so that we are permitted to run the third-party module.
                doPowerShell(_psContext, "Set-ExecutionPolicy Unrestricted -Scope CurrentUser");

                // Add dummy IO source to fix those "a command that prompts the user failed because the host .. does not support 
                // user interaction"
                doPowerShell(_psContext, "function write-host($out) { Write-Output $out }");

                // Now add the HP module path to the powershell path
                string cmd = string.Format(@"$Env:PSModulePath=""$Env:PSModulePath;{0}""", pathToHPModules);
                doPowerShell(_psContext, cmd);
                // And import the HP modules.
                doPowerShell(_psContext, "Import-Module HPiLoCmdLets");

                // Check we can connect and login to the iLo.
                cmd = String.Format("Get-HPiLOFirmwareInfo -Server \"{0}\" -Username \"{1}\" -Password \"{2}\"", 
                    _spec.iLoHostname, _spec.iLoUsername, _spec.iLoPassword);
                Collection<PSObject> output = doPowerShell(_psContext, cmd);
                PSPropertyInfo prop = output[0].Properties["STATUS_TYPE"];
                if ((string) prop.Value == "ERROR")
                    throw  new Exception("iLo error on login to " + _spec.iLoHostname + " : " + (string) output[0].Properties["STATUS_MESSAGE"].Value); 
            }
        }

        public void powerOff()
        {
            string cmd = String.Format("Set-HPiLOVirtualPowerButton -PressType \"HOLD\" -Server \"{0}\" -Username \"{1}\" -Password \"{2}\" ", _spec.iLoHostname, _spec.iLoUsername, _spec.iLoPassword);

            while (true)
            {
                try
                {
                    lock (_psContext)
                    {
                        doPowerShell(_psContext, cmd);
                    }

                    WaitForStatus(false, TimeSpan.FromSeconds(10));
                    break;
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Timeout powering box " + _spec.iLoHostname + " off, retrying");
                    //statusLogging.addLog("Timeout powering box " + _spec.iLoHostname + " off");
                }
                catch(Exception e)
                {
                    Console.WriteLine("Powering box " + _spec.iLoHostname + " off caused '" + e.Message + "', retrying");
                }
            }
        }

        private bool getPowerStatus()
        {
            while (true)
            {

                string powerStatusCmd = String.Format("Get-HPiLOHostPower -Server \"{0}\" -Username \"{1}\" -Password \"{2}\"", _spec.iLoHostname, _spec.iLoUsername, _spec.iLoPassword);
                Collection<PSObject> output;
                lock (_psContext)
                {
                    output = doPowerShell(_psContext, powerStatusCmd);
                }
                // I seriously don't know why, but sometimes the command just returns no data
                if (output.Count == 0)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(500));
                    continue;
                }

                PSPropertyInfo prop = output[0].Properties["STATUS_TYPE"];
                if (prop == null)
                {
                    // Probable timeout.
                    Thread.Sleep(TimeSpan.FromMilliseconds(500));
                    continue;
                }
                if ((string) prop.Value == "ERROR")
                {
                    if (((string) output[0].Properties["STATUS_MESSAGE"].Value).Contains("STATUS_MESSAGE=User login name was not found"))
                    {
                        // Eh, this seems to just happen sometimes?!
                        Thread.Sleep(TimeSpan.FromMilliseconds(500));
                        continue;
                    }

                    string msg = "iLo " + _spec.iLoHostname + ": " + output[0].Properties["STATUS_MESSAGE"].Value;
                    //statusLogging.addLog(msg);
                    throw new Exception(msg);
                }

                if ((string)output[0].Properties["HOST_POWER"].Value == "OFF")
                    return false;
                if ((string)output[0].Properties["HOST_POWER"].Value == "ON")
                    return true;
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
            Debug.Print("Waiting for box " + _spec.iLoHostname + " to " + (waitForState ? "come up" : "go down") );
            while (true)
            {
                if (DateTime.Now > deadline)
                    throw new TimeoutException();

                if (waitForState)
                {
                    try
                    {
                        using (Ping pinger = new Ping())
                        {
                            PingReply resp = pinger.Send(_spec.kernelDebugIPOrHostname, 500);

                            if (resp.Status == IPStatus.Success)
                            {
                                // FIXME: proper detection pls!
                                Debug.Print(".. Box " + _spec.iLoHostname + " pingable, giving it a few more seconds..");
                                Thread.Sleep(10 * 1000);
                                break;
                            }
                        }
                    }
                    catch (System.Net.NetworkInformation.PingException)
                    {
                        // The 'ping' will throw a System.Exception under certain conditions.
                        // Just swallow it.
                    }
                }
                else
                {
                    if (getPowerStatus() == false)
                        break;
                }

                Thread.Sleep(500);
            }

            Debug.Print(".. wait complete for box " + _spec.iLoHostname);
        }

        public  void Dispose()
        {
            _psContext.Dispose();
            if (nullHyp != null)
                nullHyp.Dispose();
        }

        public  void powerOn()
        {
            Stopwatch watch = new Stopwatch();
            watch.Start();

            string cmd = String.Format("Set-HPiLOHostPower -Server \"{0}\" -Username \"{1}\" -Password \"{2}\" -HostPower \"On\" ", _spec.iLoHostname, _spec.iLoUsername, _spec.iLoPassword);
            while (true)
            {
                try
                {
                    lock (_psContext)
                    {
                        doPowerShell(_psContext, cmd);
                    }

                    // Firstly, ensure that the 'power on' command has succeded. This should happen pretty quickly, since it
                    // refers to the power state and not to the host OS.
                    DateTime deadline = DateTime.Now + TimeSpan.FromSeconds(5);
                    bool succeeded = false;
                    while (deadline > DateTime.Now)
                    {
                        if (getPowerStatus())
                        {
                            succeeded = true;
                            break;
                        }
                        Thread.Sleep(TimeSpan.FromMilliseconds(500));
                    }
                    if (!succeeded)
                    {
                        //statusLogging.addLog("iLo " + _spec.iLoHostname + " power-on failed to power on - retrying");
                        continue;
                    }

                    // Now wait until the host is up enough that we can ping it.
                    WaitForStatus(true, TimeSpan.FromMinutes(6));

                    break;
                }
                catch (TimeoutException)
                {
                    string msg = "Timeout powering box " + _spec.iLoHostname + " on, retrying";
                    //statusLogging.addLog(msg);
                    Console.WriteLine(msg);
                }
            }

            watch.Stop();
            Console.WriteLine("Box " + _spec.iLoHostname + " powered on in " + watch.Elapsed.ToString());
        }

        public  void startExecutable(string toExecute, string args)
        {
            if (nullHyp == null)
                throw new NotSupportedException();
            nullHyp.startExecutable(toExecute, args);
        }

        public  void mkdir(string newDir)
        {
            if (nullHyp == null)
                throw new NotSupportedException();
            nullHyp.mkdir(newDir);
        }

        public hypSpec_iLo getConnectionSpec()
        {
            return _spec;
        }

        public  void copyToGuest(string srcpath, string dstpath)
        {
            if (nullHyp == null)
                throw new NotSupportedException();
            nullHyp.copyToGuest(srcpath, dstpath);
        }

        public override string ToString()
        {
            return _spec.iLoHostname;
        }
    }

    public class targetGroup
    {
        [JsonProperty("id")]
        public string id { get; set; }

        [JsonProperty("iscsi_target")]
        public int iscsi_target { get; set; }

        [JsonProperty("iscsi_target_authgroup")]
        public string iscsi_target_authgroup { get; set; }

        [JsonProperty("iscsi_target_authtype")]
        public string iscsi_target_authtype { get; set; }

        [JsonProperty("iscsi_target_initialdigest")]
        public string iscsi_target_initialdigest { get; set; }

        [JsonProperty("iscsi_target_initiatorgroup")]
        public string iscsi_target_initiatorgroup { get; set; }

        [JsonProperty("iscsi_target_portalgroup")]
        public string iscsi_target_portalgroup { get; set; }
    }

    public class iscsiPortal
    {
        [JsonProperty("id")]
        public string id { get; set; }

        [JsonProperty("iscsi_target_portal_comment")]
        public string iscsi_target_portal_comment { get; set; }

        [JsonProperty("iscsi_target_portal_discoveryauthgroup")]
        public string iscsi_target_portal_discoveryauthgroup { get; set; }

        [JsonProperty("iscsi_target_portal_discoveryauthmethod")]
        public string iscsi_target_portal_discoveryauthmethod { get; set; }

        [JsonProperty("iscsi_target_portal_tag")]
        public string iscsi_target_portal_tag { get; set; }

        [JsonProperty("iscsi_target_portal_ips")]
        public string[] iscsi_target_portal_ips { get; set; }
    }


    public class snapshot
    {
        [JsonProperty("filesystem")]
        public string filesystem { get; set; }

        [JsonProperty("fullname")]
        public string fullname { get; set; }

        [JsonProperty("id")]
        public string id { get; set; }

        [JsonProperty("mostrecent")]
        public string mostrecent { get; set; }

        [JsonProperty("name")]
        public string name { get; set; }

        [JsonProperty("parent_type")]
        public string parent_type { get; set; }

        [JsonProperty("refer")]
        public string refer { get; set; }

        [JsonProperty("replication")]
        public string replication { get; set; }

        [JsonProperty("used")]
        public string used { get; set; }
    }

    public class volume
    {
        [JsonProperty("id")]
        public int id { get; set; }

        [JsonProperty("avail")]
        public string avail { get; set; }

        [JsonProperty("compression")]
        public string compression { get; set; }

        [JsonProperty("compressratio")]
        public string compressratio { get; set; }

        [JsonProperty("is_decrypted")]
        public string is_decrypted { get; set; }

        [JsonProperty("is_upgraded")]
        public string is_upgraded { get; set; }

        [JsonProperty("mountpoint")]
        public string mountpoint { get; set; }

        [JsonProperty("name")]
        public string name { get; set; }

        [JsonProperty("path")]
        public string path { get; set; }

        [JsonProperty("readonly")]
        public string isreadonly { get; set; }

        [JsonProperty("used")]
        public string used { get; set; }

        [JsonProperty("used_pct")]
        public string used_pct { get; set; }

        [JsonProperty("vol_encrypt")]
        public string vol_encrypt { get; set; }

        [JsonProperty("vol_encryptkey")]
        public string vol_encryptkey { get; set; }

        [JsonProperty("vol_fstype")]
        public string vol_fstype { get; set; }

        [JsonProperty("vol_guid")]
        public string vol_guid { get; set; }

        [JsonProperty("vol_name")]
        public string vol_name { get; set; }

        [JsonProperty("status")]
        public string status { get; set; }

        [JsonProperty("type")]
        public string volType { get; set; }

        [JsonProperty("children")]
        public List<volume> children { get; set; }

    }

    public class iscsiExtent
    {
        [JsonProperty("id")]
        public int id { get; set; }

        [JsonProperty("iscsi_target_extent_avail_threshold")]
        public string iscsi_target_extent_avail_threshold { get; set; }

        [JsonProperty("iscsi_target_extent_blocksize")]
        public string iscsi_target_extent_blocksize { get; set; }

        [JsonProperty("iscsi_target_extent_comment")]
        public string iscsi_target_extent_comment { get; set; }

        [JsonProperty("iscsi_target_extent_filesize")]
        public string iscsi_target_extent_filesize { get; set; }

        [JsonProperty("iscsi_target_extent_insecure_tpc")]
        public string iscsi_target_extent_insecure_tpc { get; set; }

        [JsonProperty("iscsi_target_extent_legacy")]
        public string iscsi_target_extent_legacy { get; set; }

        [JsonProperty("iscsi_target_extent_naa")]
        public string iscsi_target_extent_naa { get; set; }

        [JsonProperty("iscsi_target_extent_name")]
        public string iscsi_target_extent_name { get; set; }

        [JsonProperty("iscsi_target_extent_path")]
        public string iscsi_target_extent_path { get; set; }

        [JsonProperty("iscsi_target_extent_pblocksize")]
        public string iscsi_target_extent_pblocksize { get; set; }

        [JsonProperty("iscsi_target_extent_ro")]
        public string iscsi_target_extent_ro { get; set; }

        [JsonProperty("iscsi_target_extent_rpm")]
        public string iscsi_target_extent_rpm { get; set; }

        [JsonProperty("iscsi_target_extent_serial")]
        public string iscsi_target_extent_serial { get; set; }

        [JsonProperty("iscsi_target_extent_type")]
        public string iscsi_target_extent_type { get; set; }

        [JsonProperty("iscsi_target_extent_xen")]
        public string iscsi_target_extent_xen { get; set; }
    }

    public class iscsiTargetToExtentMapping
    {
        [JsonProperty("iscsi_target")]
        public int iscsi_target { get; set; }

        [JsonProperty("iscsi_extent")]
        public int iscsi_extent { get; set; }

        [JsonProperty("iscsi_lunid")]
        public string iscsi_lunid { get; set; }

        [JsonProperty("id")]
        public int id { get; set; }
    }

    public class iscsiTarget
    {
        [JsonProperty("iscsi_target_name")]
        public string targetName { get; set; }

        [JsonProperty("iscsi_target_alias")]
        public string targetAlias { get; set; }

        [JsonProperty("id")]
        public int id { get; set; }
    }
}