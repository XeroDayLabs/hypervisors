using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Lifetime;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace hypervisors
{
    public class ilo_resp_login
    {
        public string session_key;
        public string user_name;
        public string user_account;
    }

    public class ilo_resp_pwrState
    {
        public string hostpwr_state;
    }

    public class snapshotObjects
    {
        public iscsiTargetToExtentMapping tgtToExtent;
        public snapshot shotToRestore;
        public iscsiExtent extent;
    }

    public class hypervisor_iLo : hypervisorWithSpec<hypSpec_iLo>
    {
        private string _ip;
        private string _username;
        private string _password;

        private string baseURL;
        private hypSpec_iLo _spec;

        private string _sessionKey;
        private hypervisor_null _nullHyp;
        private CookieContainer _cookies = new CookieContainer();

        public hypervisor_iLo(hypSpec_iLo spec)
        {
            _spec = spec;
            _ip = spec.iLoHostname;
            _username = spec.iLoUsername;
            _password = spec.iLoPassword;

            baseURL = "https://" + _ip + "/json";

            _nullHyp = new hypervisor_null(spec.kernelDebugIPOrHostname, spec.hostUsername, spec.hostPassword);
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

            // Finally, re-add the extent and target-to-extent mapping.
            iscsiExtent newExtent = nas.addISCSIExtent(shotObjects.extent);
            nas.addISCSITargetToExtent(shotObjects.tgtToExtent.iscsi_target, newExtent);

            powerOn();
        }

        private snapshotObjects getSnapshotObjectsFromNAS(FreeNAS nas, string fullName)
        {
            var snapshots = nas.getSnapshots();
            snapshot shotToRestore = snapshots.SingleOrDefault(x => x.name.Equals(fullName, StringComparison.CurrentCultureIgnoreCase) || x.id == fullName);
            if (shotToRestore == null)
                throw new Exception("Cannot find snapshot " + fullName);

            // Now find the extent. We'll need to delete it before we can rollback the snapshot.
            List<iscsiExtent> extents = nas.getExtents();
            iscsiExtent extent = extents.SingleOrDefault(x => _spec.snapshotName.StartsWith(x.iscsi_target_extent_name, StringComparison.CurrentCultureIgnoreCase));
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

        private string doRequest(string pageName, string methodName, bool isPost = true)
        {
            string url = baseURL + "/" + pageName;
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.CookieContainer = _cookies;
            // Don't bother validating the SSL cert. :^)
            req.ServerCertificateValidationCallback += (a, b, c, d) => true;
            if (isPost)
            {
                req.Method = "POST";
                string payload = "{\"method\":\"" + methodName + "\",\"session_key\":\"" + _sessionKey + "\"}";
                Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
                req.ContentLength = dataBytes.Length;
                using (Stream stream = req.GetRequestStream())
                {
                    stream.Write(dataBytes, 0, dataBytes.Length);
                }
            }
            else
            {
                req.Method = "GET";
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode != HttpStatusCode.OK)
                                throw new Exception("iLo API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

                            return contentString;
                        }
                    }
                }
            }
            catch (WebException e)
            {
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();
                        
                        throw new Exception("iLo API call failed, status " + ((HttpWebResponse)e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
            catch (Exception)
            {
                throw new Exception("iLo API call failed, no response");
            }        
            
        }

        public override void connect()
        {
            string url = baseURL + "/login_session";
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.Method = "POST";
            req.CookieContainer = _cookies;
            string payload = "{\"method\":\"login\",\"user_login\":\"" + _username + "\",\"password\":\"" + _password +"\"}:";
            Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
            req.ContentLength = dataBytes.Length;
            // Don't bother validating the SSL cert. :^)
            req.ServerCertificateValidationCallback += (a,b,c,d) => true;
            using (Stream stream = req.GetRequestStream())
            {
                stream.Write(dataBytes, 0, dataBytes.Length);
            }

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode != HttpStatusCode.OK)
                                throw new Exception("iLo API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

                            ilo_resp_login result = JsonConvert.DeserializeObject<ilo_resp_login>(contentString);

                            _sessionKey = result.session_key;
                        }
                    }
                }
            }

            catch (WebException e)
            {
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();
                        throw new Exception("iLo API call failed, status " + ((HttpWebResponse)e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
            catch (Exception)
            {
                throw new Exception("iLo API call failed, no response");
            }        
        }

        public override void powerOn()
        {
            doRequest("host_power", "press_power_button");

            // Wait until the host is up enough that we can ping it...
            WaitForStatus(true, TimeSpan.FromMinutes(6));

            // Now wait for it to be up enough that we can psexec to it.
            doWithRetryOnSomeExceptions(() =>
            {
                startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
            });
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

                Thread.Sleep(retry);
            }
        }

        public override void powerOff()
        {
            doRequest("host_power", "hold_power_button");
        }

        private bool getPowerStatus()
        {
            while (true)
            {
                ilo_resp_pwrState pwrResp = JsonConvert.DeserializeObject<ilo_resp_pwrState>(doRequest("host_power", null, isPost: false));

                switch (pwrResp.hostpwr_state.ToUpper())
                {
                    case "ON":
                        return true;
                    case "OFF":
                        return false;
                    default:
                        throw new Exception("Unrecognised power state '" + pwrResp.hostpwr_state + "'");
                }
            }
        }

        public override void startExecutable(string toExecute, string args)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            _nullHyp.startExecutable(toExecute, args);
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

        public override void copyToGuest(string srcpath, string dstpath)
        {
            if (_nullHyp == null)
                throw new NotSupportedException();
            _nullHyp.copyToGuest(srcpath, dstpath);
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
    }
}