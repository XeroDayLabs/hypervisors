using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace hypervisors
{
    public class FreeNAS : NASAccess
    {
        private readonly string _serverIp;
        private readonly string _username;
        private readonly string _password;
        private readonly CookieContainer cookies = new CookieContainer();

        /// <summary>
        /// If we do multiple non-API requests in parallel, we will run afoul of FreeNAS's CSRF protection, so lock this and just 
        /// do one at a time. We could manage different sessions properly for some speedup here, but freenas has issues with lots
        /// of parallelism anyway..
        /// </summary>
        private static readonly Object nonAPIReqLock = new Object();

        /// <summary>
        /// This lock is used to serialise requests to the FreeNAS api. It is used because FreeNAS really doesn't deal well with
        /// lots of simultanenous requests, so we end up retrying with a delay, which is much less effecient than waiting on a 
        /// lock.
        /// </summary>
        private static readonly Object ReqLock = new Object();

        public FreeNAS(hypSpec_iLo hyp)
        {
            _serverIp = hyp.iscsiserverIP;
            _username = hyp.iscsiServerUsername;
            _password = hyp.iscsiServerPassword;
        }

        public FreeNAS(string serverIP, string username, string password)
        {
            _serverIp = serverIP;
            _username = username;
            _password = password;
        }

        public FreeNAS(NASParams parms)
            : this(parms.IP, parms.username, parms.password)
        {
            
        }

        private T doReqForJSON<T>(string url, string method, HttpStatusCode expectedCode, string payload = null, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(4));

            while (true)
            {
                resp r = doReq(url, method, expectedCode, payload, deadline);

                if (r.text.Contains("The request has timed out"))
                {
                    deadline.doCancellableSleep(TimeSpan.FromSeconds(3));
                }
                else
                {
                    return JsonConvert.DeserializeObject<T>(r.text);
                }
            }
        }

        private resp doReq(string url, string method, HttpStatusCode expectedCode, string payload = null, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(4));

            while (true)
            {
                resp toRet = _doReq(url, method, expectedCode, payload);
                if (toRet != null)
                    return toRet;

                if (!deadline.stillOK)
                    throw new nasAccessException();

                deadline.doCancellableSleep(TimeSpan.FromSeconds(10));
            }
        }

        private resp _doReq(string url, string method, HttpStatusCode expectedCode, string payload = null)
        {
            lock (ReqLock)
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                CredentialCache cred = new CredentialCache();
                cred.Add(new Uri(url), "Basic", new NetworkCredential(_username, _password));
                req.Credentials = cred;
                req.PreAuthenticate = true;

                if (payload != null)
                {
                    req.ContentType = "application/json";
                    Byte[] dataBytes = Encoding.ASCII.GetBytes(payload);
                    req.ContentLength = dataBytes.Length;
                    using (Stream stream = req.GetRequestStream())
                    {
                        stream.Write(dataBytes, 0, dataBytes.Length);
                    }
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

                                if (resp.StatusCode != expectedCode)
                                    throw nasAccessException.create(resp, url, contentString);

                                return new resp() {text = contentString};
                            }
                        }
                    }
                }
                catch (WebException e)
                {
                    HttpWebResponse resp = e.Response as HttpWebResponse;

                    if (resp == null)
                        throw new nasAccessException(e.Message);

                    using (Stream respStream = resp.GetResponseStream())
                    {
                        if (respStream == null)
                            throw new nasAccessException(e.Message);

                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();
                            throw nasAccessException.create(resp, url, contentString);
                        }
                    }
                }
            }
        }

        public override void invalidateExtents()
        {

        }

        public override List<iscsiTarget> getISCSITargets()
        {
            return doReqForJSON<List<iscsiTarget>>("http://" + _serverIp + "/api/v1.0/services/iscsi/target/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override void deleteISCSITarget(iscsiTarget target)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/target/{1}/", _serverIp, target.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override void waitUntilISCSIConfigFlushed(bool force = false)
        {
            string url = String.Format("http://{0}/api/v1.0/system/aliztest/", _serverIp);
            doReq(url, "GET", HttpStatusCode.OK);
        }

        public override void invalidateTargets()
        {

        }

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            return doReqForJSON<List<iscsiTargetToExtentMapping>>("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override void invalidateTargetToExtents()
        {
            
        }

        public override List<iscsiExtent> getExtents()
        {
            return doReqForJSON<List<iscsiExtent>>("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/targettoextent/{1}/", _serverIp, tgtToExtent.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override void deleteZVol(volume toDelete)
        {
            // Oh no, the freenas API keeps returning HTTP 404 when I try to delete a volume! :( We ignore it and use the web UI
            // instead. ;_;
            lock (nonAPIReqLock)
            {
                DoNonAPIReq("", HttpStatusCode.OK);
                string url = "account/login/";
                string payloadStr = string.Format("username={0}&password={1}", _username, _password);
                DoNonAPIReq(url, HttpStatusCode.OK, payloadStr);
                // Now we can do the request to delete the snapshot.
                string resp = DoNonAPIReq("storage/zvol/delete/" + toDelete.path + "/", HttpStatusCode.OK, "");
                if (resp.Contains("\"error\": true") || !resp.Contains("Volume successfully destroyed"))
                    throw new nasAccessException("Volume deletion failed: " + resp);
            }
        }

        public override void deleteISCSIExtent(iscsiExtent extent)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/extent/{1}/", _serverIp, extent.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override void invalidateSnapshots()
        {

        }

        public override List<volume> getVolumes()
        {
            return doReqForJSON<List<volume>>("http://" + _serverIp + "/api/v1.0/storage/volume/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override void cloneSnapshot(snapshot snapshot, string path)
        {
            string url = String.Format("http://{0}/api/v1.0/storage/snapshot/{1}/clone/", _serverIp, snapshot.fullname);
            string payload = String.Format("{{\"name\": \"{0}\" }}", path);
            doReq(url, "POST", HttpStatusCode.Accepted, payload);
            // This API function returns a HTTP status of Accepted (202), which should indicate that the operation has been started
            // and will continue async. However, after studying the FreeNAS code ("FreeNAS-9.10.1 (d989edd)"), there appears to be
            // a shell out to 'zfs clone', which is sync. AFAICT, the call can never return before the clone is complete.
        }

        public override volume findVolumeByMountpoint(List<volume> vols, string mountpoint)
        {
            if (vols == null)
                return null;

            volume toRet = vols.SingleOrDefault(x => x.mountpoint == mountpoint);
            if (toRet != null)
                return toRet;

            if (vols.All(x => x.children != null && x.children.Count == 0) || vols.Count == 0)
                return null;

            foreach (volume vol in vols)
            {
                volume maybeThis = findVolumeByMountpoint(vol.children, mountpoint);
                if (maybeThis != null)
                    return maybeThis;
            }
            return null;
        }

        public override volume findVolumeByName(List<volume> vols, string name)
        {
            if (vols == null)
                return null;

            volume toRet = vols.SingleOrDefault(x => x.name == name);
            if (toRet != null)
                return toRet;

            if (vols.All(x => x.children != null && x.children.Count == 0) || vols.Count == 0)
                return null;

            foreach (volume vol in vols)
            {
                volume maybeThis = findVolumeByName(vol.children, name);
                if (maybeThis != null)
                    return maybeThis;
            }

            return null;
        }

        public override List<snapshot> getSnapshots()
        {
            return doReqForJSON<List<snapshot>>("http://" + _serverIp + "/api/v1.0/storage/snapshot/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override void rollbackSnapshot(snapshot shotToRestore) //, volume parentVolume, volume clone)
        {
            lock (nonAPIReqLock)
            {
                // Oh no, FreeNAS doesn't export the 'rollback' command via the API! :( We need to log into the web UI and faff with 
                // that in order to rollback instead.
                //
                // First, do an initial GET to / so we can get a CSRF token and some cookies.
                DoNonAPIReq("", HttpStatusCode.OK);
                //doInitialReq();

                // Now we can perform the login.
                string url = "account/login/";
                string payloadStr = string.Format("username={0}&password={1}", _username, _password);
                DoNonAPIReq(url, HttpStatusCode.OK, payloadStr);

                // Now we can do the request to rollback the snapshot.
                string resp = DoNonAPIReq("storage/snapshot/rollback/" + shotToRestore.fullname + "/", HttpStatusCode.OK,
                    "");

                if (resp.Contains("\"error\": true") || !resp.Contains("Rollback successful."))
                    throw new nasAccessException("Rollback failed: " + resp);
            }
        }

        private string DoNonAPIReq(string urlRel, HttpStatusCode expectedCode, string postVars = null)
        {
            Uri url = new Uri(String.Format("http://{0}/{1}", _serverIp, urlRel), UriKind.Absolute);
            HttpWebRequest req = WebRequest.CreateHttp(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 6.3; WOW64; rv:42.0) Gecko/20100101 Firefox/42.0";
            req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
            req.CookieContainer = cookies;

            if (postVars != null)
            {
                req.Method = "POST";
                string csrfToken = cookies.GetCookies(url)["csrftoken"].Value;
                string payload = postVars + "&csrfmiddlewaretoken=" + csrfToken;
                Byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);
                req.ContentLength = payloadBytes.Length;
                req.ContentType = "application/x-www-form-urlencoded";
                req.Headers.Add("form_id", "form_str");
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(payloadBytes, 0, payloadBytes.Length);
                }
            }

            HttpWebResponse resp = null;
            try
            {
                using (resp = (HttpWebResponse) req.GetResponse())
                {
                    if (resp.StatusCode != expectedCode)
                        throw new nasAccessException("Statuss code was " + resp.StatusCode + " but expected " + expectedCode + " while requesting " + urlRel);

                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            return respStreamReader.ReadToEnd();
                        }
                    }
                }
            }
            catch (WebException e)
            {
                string respText;
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        respText = respStreamReader.ReadToEnd();
                    }
                }

                Debug.WriteLine(e.Message);
                Debug.WriteLine(respText);

                throw;
            }
        }

        public override iscsiPortal addPortal(iscsiPortal toAdd)
        {
            string payload = String.Format("{{\"iscsi_target_portal_ips\": \"{0}\" " +
                                           "}}", toAdd.iscsi_target_portal_ips);
            return doReqForJSON<iscsiPortal>("http://" + _serverIp + "/api/v1.0/services/iscsi/portal/", "POST", HttpStatusCode.Created, payload);
        }

        public override List<user> getUsers()
        {
            return doReqForJSON<List<user>>("http://" + _serverIp + "/api/v1.0/account/users/", "GET", HttpStatusCode.OK);
        }

        public override user updateUser(user userToChange)
        {
            string payload = String.Format("{{\"id\": \"{0}\", " +
                                           "\"bsdusr_sshpubkey\": \"{1}\" " +
                                           "}}", userToChange.id, userToChange.bsdusr_sshpubkey.Trim());
            return doReqForJSON<user>("http://" + _serverIp + "/api/v1.0/account/users/" + userToChange.id + "/", "PUT", HttpStatusCode.OK, payload);
        }

        public override iscsiTarget addISCSITarget(iscsiTarget toAdd)
        {
            string payload = String.Format("{{\"iscsi_target_name\": \"{0}\", " +
                                           "\"iscsi_target_alias\": \"{1}\" " +
                                           "}}", toAdd.targetName, toAdd.targetAlias);
            return doReqForJSON<iscsiTarget>("http://" + _serverIp + "/api/v1.0/services/iscsi/target/", "POST", HttpStatusCode.Created, payload);
        }

        public override iscsiTargetToExtentMapping addISCSITargetToExtent(string targetID, iscsiExtent extent)
        {
            string payload = String.Format("{{" +
                                           "\"iscsi_target\": \"{0}\", " +
                                           "\"iscsi_extent\": \"{1}\", " +
                                           "\"iscsi_lunid\": null " +
                                           "}}", targetID, extent.id);
            return doReqForJSON<iscsiTargetToExtentMapping>("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/", "POST", HttpStatusCode.Created, payload);
        }

        public override targetGroup addTargetGroup(targetGroup toAdd, iscsiTarget target)
        {
            string payload = String.Format("{{\"iscsi_target\": \"{0}\", " +
                                           "\"iscsi_target_authgroup\": \"{1}\", " +
                                           "\"iscsi_target_authtype\": \"{2}\", " +
                                           "\"iscsi_target_portalgroup\": \"{3}\", " +
                                           "\"iscsi_target_initiatorgroup\": \"{4}\", " +
                                           "\"iscsi_target_initialdigest\": \"{5}\" " +
                                           "}}", target.id,
                toAdd.iscsi_target_authgroup, toAdd.iscsi_target_authtype, toAdd.iscsi_target_portalgroup,
                toAdd.iscsi_target_initiatorgroup, toAdd.iscsi_target_initialdigest);
            return doReqForJSON<targetGroup>("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/", "POST", HttpStatusCode.Created, payload);
        }

        public override volume findParentVolume(List<volume> vols, volume volToFind)
        {
            volume toRet = vols.SingleOrDefault(x => x.children.Count(y => y.name == volToFind.name && x.volType == "dataset") > 0);
            if (toRet != null)
                return toRet;

            if (vols.All(x => x.children.Count == 0) || vols.Count == 0)
                return null;

            foreach (volume vol in vols)
                return findParentVolume(vol.children, volToFind);

            return null;
        }

        public override iscsiExtent addISCSIExtent(iscsiExtent extent)
        {
            if (String.IsNullOrEmpty(extent.iscsi_target_extent_disk))
                extent.iscsi_target_extent_disk = extent.iscsi_target_extent_path;

            // See freenas bug 11296: "type is Disk, but you need to use iscsi_target_extent_disk"
            // ... "start it with zvol instead of dev/zvol"
            if (extent.iscsi_target_extent_disk.StartsWith("/dev/zvol", StringComparison.CurrentCultureIgnoreCase))
                extent.iscsi_target_extent_disk = extent.iscsi_target_extent_disk.Substring(5);

            if (extent.iscsi_target_extent_type == "ZVOL")
                extent.iscsi_target_extent_type = "Disk";  // You're a Disk now, 'arry

            string diskNameOrFilePath;
            if (extent.iscsi_target_extent_type == "File")
                diskNameOrFilePath = String.Format("\"iscsi_target_extent_path\": \"{0}\" ", extent.iscsi_target_extent_path);
            else if (extent.iscsi_target_extent_type == "Disk")
                diskNameOrFilePath = String.Format("\"iscsi_target_extent_disk\": \"{0}\" ", extent.iscsi_target_extent_disk);
            else
                throw new ArgumentException("iscsi_target_extent_type");
            
            string payload = String.Format("{{" +
                                           "\"iscsi_target_extent_type\": \"{0}\", " +
                                           "\"iscsi_target_extent_name\": \"{1}\", " +
                                           "{2}" +
                                           "}}", 
                extent.iscsi_target_extent_type,
                extent.iscsi_target_extent_name, 
                diskNameOrFilePath);
            return doReqForJSON<iscsiExtent>("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/", "POST", HttpStatusCode.Created, payload);
        }

        public override List<iscsiPortal> getPortals()
        {
            return doReqForJSON<List<iscsiPortal>>("http://" + _serverIp + "/api/v1.0/services/iscsi/portal/?format=json", "get", HttpStatusCode.OK);
        }

        public override List<targetGroup> getTargetGroups()
        {
            return doReqForJSON<List<targetGroup>>("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/?limit=99999", "get", HttpStatusCode.OK);
        }

        public override snapshot createSnapshot(string dataset, string name)
        {
            string payload = String.Format("{{\"dataset\": \"{0}\", " +
                                           "\"name\": \"{1}\" " +
                                           "}}", dataset, name);

            return doReqForJSON<snapshot>("http://" + _serverIp + "/api/v1.0/storage/snapshot/", "post", HttpStatusCode.Created, payload);
        }

        public override iscsiPortal createPortal(string portalIPs)
        {
            string payload = String.Format("{{\"iscsi_target_portal_ips\": [ \"{0}\" ] " + "}}", portalIPs);

            return doReqForJSON<iscsiPortal>("http://" + _serverIp + "/api/v1.0/services/iscsi/portal/", "post", HttpStatusCode.Created, payload);
        }

        public override void deleteSnapshot(snapshot toDelete)
        {
            string name = Uri.EscapeDataString(toDelete.fullname);
            string url = "http://" + _serverIp + "/api/v1.0/storage/snapshot/" + name;
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override targetGroup createTargetGroup(iscsiPortal associatedPortal, iscsiTarget tgt)
        {
            string payload = String.Format("{{\"iscsi_target_portalgroup\": \"{0}\", " +
                                           "\"iscsi_target\": \"{1}\" " +
                                           "}}", associatedPortal.id, tgt.id);

            return doReqForJSON<targetGroup>("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/", "post", HttpStatusCode.Created, payload);
        }
    }
}