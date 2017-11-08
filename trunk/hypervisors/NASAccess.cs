using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace hypervisors
{
    public class nasConflictException : nasAccessException
    {
        public nasConflictException()
            : base()
        {
        }

        public nasConflictException(string s)
            : base(s)
        {
        }
    }

    public abstract class NASAccess
    {
        public abstract iscsiTargetToExtentMapping addISCSITargetToExtent(int iscsiTarget, iscsiExtent newExtent);
        public abstract void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent);
        public abstract List<iscsiTargetToExtentMapping> getTargetToExtents();

        public abstract iscsiExtent addISCSIExtent(iscsiExtent extent);
        public abstract void deleteISCSIExtent(iscsiExtent extent);
        public abstract List<iscsiExtent> getExtents();

        public abstract void rollbackSnapshot(snapshot shotToRestore);

        public abstract List<iscsiTarget> getISCSITargets();
        public abstract void deleteISCSITarget(iscsiTarget tgt);

        public abstract List<snapshot> getSnapshots();
        public abstract snapshot deleteSnapshot(snapshot toDelete);
        public abstract snapshot createSnapshot(string dataset, string snapshotName);
        public abstract void cloneSnapshot(snapshot toClone, string fullCloneName);

        public abstract List<volume> getVolumes();
        public abstract volume findVolumeByName(List<volume> volumes, string cloneName);
        public abstract volume findVolumeByMountpoint(List<volume> vols, string mountpoint);
        public abstract volume findParentVolume(List<volume> vols, volume volToFind);

        public abstract void deleteZVol(volume vol);

        public abstract List<targetGroup> getTargetGroups();
        public abstract iscsiTarget addISCSITarget(iscsiTarget toAdd);
        public abstract targetGroup addTargetGroup(targetGroup tgtGrp, iscsiTarget newTarget);

        public abstract List<iscsiPortal> getPortals();
    }

    [DataContract]
    public class NASParams
    {
        [DataMember]
        public string IP;

        [DataMember]
        public string username;
        
        [DataMember]
        public string password;
    }

    public class FreeNAS : NASAccess
    {
        private readonly string _serverIp;
        private readonly string _username;
        private readonly string _password;
        private readonly CookieContainer cookies = new CookieContainer();

        /// <summary>
        /// If we do multiple non-API requests in parallel, we will run afoul of FreeNAS's CSRF protection, so lock this and just 
        /// do one at a time. We could manage different sessions properly for some speedup here.
        /// </summary>
        private readonly Object nonAPIReqLock = new Object();

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

        private resp doReq(string url, string method, HttpStatusCode expectedCode, string payload = null)
        {
            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(4);
            while (true)
            {
                try
                {
                    return _doReq(url, method, expectedCode, payload);
                }
                catch (nasAccessException)
                {
                    if (DateTime.Now > deadline)
                        throw;

                    Thread.Sleep(TimeSpan.FromSeconds(10));
                }
            }
        }

        private resp _doReq(string url, string method, HttpStatusCode expectedCode, string payload = null)
        {
            HttpWebRequest req = (HttpWebRequest) WebRequest.Create(url);
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
                using (HttpWebResponse resp = (HttpWebResponse) req.GetResponse())
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
                using (Stream respStream = e.Response.GetResponseStream())
                {
                    using (StreamReader respStreamReader = new StreamReader(respStream))
                    {
                        string contentString = respStreamReader.ReadToEnd();
                        throw nasAccessException.create(((HttpWebResponse) e.Response), url, contentString);
                    }
                }
            }
        }

        public override List<iscsiTarget> getISCSITargets()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/target/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiTarget>>(HTTPResponse);
        }

        public override void deleteISCSITarget(iscsiTarget target)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/target/{1}", _serverIp, target.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiTargetToExtentMapping>>(HTTPResponse);
        }

        public override List<iscsiExtent> getExtents()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiExtent>>(HTTPResponse);
        }

        public override void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/targettoextent/{1}", _serverIp, tgtToExtent.id);
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
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/extent/{1}", _serverIp, extent.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public override List<volume> getVolumes()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/volume/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<volume>>(HTTPResponse);
        }

        public override void cloneSnapshot(snapshot snapshot, string path)
        {
            string url = String.Format("http://{0}/api/v1.0/storage/snapshot/{1}/clone/", _serverIp, snapshot.fullname);
            string payload = String.Format("{{\"name\": \"{0}\" }}", path);
            doReq(url, "POST", HttpStatusCode.Accepted, payload);
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
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/snapshot/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<snapshot>>(HTTPResponse);
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

        public override iscsiTarget addISCSITarget(iscsiTarget toAdd)
        {
            string payload = String.Format("{{\"iscsi_target_name\": \"{0}\", " +
                                           "\"iscsi_target_alias\": \"{1}\" " +
                                           "}}", toAdd.targetName, toAdd.targetAlias);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/target/", "POST", HttpStatusCode.Created, payload).text;
            iscsiTarget created = JsonConvert.DeserializeObject<iscsiTarget>(HTTPResponse);

            return created;
        }

        public override iscsiTargetToExtentMapping addISCSITargetToExtent(int targetID, iscsiExtent extent)
        {
            string payload = String.Format("{{" +
                                           "\"iscsi_target\": \"{0}\", " +
                                           "\"iscsi_extent\": \"{1}\", " +
                                           "\"iscsi_lunid\": null " +
                                           "}}", targetID, extent.id);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/", "POST", HttpStatusCode.Created, payload).text;
            iscsiTargetToExtentMapping created = JsonConvert.DeserializeObject<iscsiTargetToExtentMapping>(HTTPResponse);

            return created;
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
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/", "POST", HttpStatusCode.Created, payload).text;
            targetGroup created = JsonConvert.DeserializeObject<targetGroup>(HTTPResponse);

            return created;
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
            string payload = String.Format("{{" +
                                           "\"iscsi_target_extent_type\": \"{0}\", " +
                                           "\"iscsi_target_extent_name\": \"{1}\", " +
                                           "\"iscsi_target_extent_disk\": \"{2}\" " +
                                           "}}", "Disk", extent.iscsi_target_extent_name, extent.iscsi_target_extent_disk);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/", "POST", HttpStatusCode.Created, payload).text;
            iscsiExtent created = JsonConvert.DeserializeObject<iscsiExtent>(HTTPResponse);

            return created;
        }

        public override List<iscsiPortal> getPortals()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/portal/?format=json", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiPortal>>(HTTPResponse);
        }

        public override List<targetGroup> getTargetGroups()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/?limit=99999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<targetGroup>>(HTTPResponse);
        }

        public override snapshot createSnapshot(string dataset, string name)
        {
            string payload = String.Format("{{\"dataset\": \"{0}\", " +
                                           "\"name\": \"{1}\" " +
                                           "}}", dataset, name);

            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/snapshot/", "post", HttpStatusCode.Created, payload).text;
            return JsonConvert.DeserializeObject<snapshot>(HTTPResponse);
        }

        public override snapshot deleteSnapshot(snapshot toDelete)
        {
            string name = toDelete.fullname;
            name = Uri.EscapeDataString(name);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/snapshot/" + name, "DELETE", HttpStatusCode.NoContent).text;
            return JsonConvert.DeserializeObject<snapshot>(HTTPResponse);
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

        [JsonProperty("iscsi_target_extent_disk")]
        public string iscsi_target_extent_disk { get; set; }

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