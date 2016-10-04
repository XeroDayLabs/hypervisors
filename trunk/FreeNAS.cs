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
    public class FreeNAS
    {
        private readonly string _serverIp;
        private readonly string _username;
        private readonly string _password;
        private readonly CookieContainer cookies = new CookieContainer();

        public FreeNAS(string serverIP, string username, string password)
        {
            _serverIp = serverIP;
            _username = username;
            _password = password;
        }
        
        private resp doReq(string url, string method, HttpStatusCode expectedCode, string payload = null)
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
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream respStream = resp.GetResponseStream())
                    {
                        using (StreamReader respStreamReader = new StreamReader(respStream))
                        {
                            string contentString = respStreamReader.ReadToEnd();

                            if (resp.StatusCode != expectedCode)
                                throw new Exception("FreeNAS API call failed, status " + resp.StatusCode + ", URL " + url + " HTTP response body " + contentString);

                            return new resp() {text = contentString };
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
                        throw new Exception("FreeNAS API call failed, status " + ((HttpWebResponse)e.Response).StatusCode + ", URL " + url + " HTTP response body " + contentString);
                    }
                }
            }
        }

        public List<iscsiTarget> getISCSITargets()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/target/", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiTarget>>(HTTPResponse);
        }

        public void deleteISCSITarget(iscsiTarget target)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/target/{1}", _serverIp, target.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/?limit=9999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiTargetToExtentMapping>>(HTTPResponse);
        }

        public List<iscsiExtent> getExtents()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/?limit=9999", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiExtent>>(HTTPResponse);
        }

        public void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/targettoextent/{1}", _serverIp, tgtToExtent.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);            
        }

        private void deleteZVol(volume parent, volume toDelete)
        {
            string url = String.Format("http://{0}/api/v1.0/storage/volume/{1}/zvols/{2}", _serverIp, parent.name, toDelete.name);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public void deleteISCSIExtent(iscsiExtent extent)
        {
            string url = String.Format("http://{0}/api/v1.0/services/iscsi/extent/{1}", _serverIp, extent.id);
            doReq(url, "DELETE", HttpStatusCode.NoContent);
        }

        public List<volume> getVolumes()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/volume", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<volume>>(HTTPResponse);
        }

        private void cloneSnapshot(snapshot snapshot, string path)
        {
            string url = String.Format("http://{0}/api/v1.0/storage/snapshot/{1}/clone/", _serverIp, snapshot.fullname);
            string payload = String.Format("{{\"name\": \"{0}\" }}", path);
            doReq(url, "POST", HttpStatusCode.Accepted, payload);
        }

        public volume findVolumeByMountpoint(List<volume> vols, string mountpoint)
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

        public volume findVolumeByName(List<volume> vols, string name)
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

        public List<snapshot> getSnapshots()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/storage/snapshot/?format=json", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<snapshot>>(HTTPResponse);
        }

        public void rollbackSnapshot(snapshot shotToRestore) //, volume parentVolume, volume clone)
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
            string resp = DoNonAPIReq("storage/snapshot/rollback/" + shotToRestore.fullname + "/", HttpStatusCode.OK, "");

            if (resp.Contains("\"error\": true") || !resp.Contains("Rollback successful."))
                throw new Exception("Rollback failed: " + resp);
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
                        throw new Exception("Statuss code was " + resp.StatusCode + " but expected " + expectedCode + " while requesting " + urlRel);

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

        public iscsiTarget addISCSITarget(iscsiTarget toAdd)
        {
            string payload = String.Format("{{\"iscsi_target_name\": \"{0}\", " +
                                           "\"iscsi_target_alias\": \"{1}\" " +
                                           "}}", toAdd.targetName, toAdd.targetAlias);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/target/", "POST", HttpStatusCode.Created, payload).text;
            iscsiTarget created = JsonConvert.DeserializeObject<iscsiTarget>(HTTPResponse);

            return created;
        }

        public iscsiTargetToExtentMapping addISCSITargetToExtent(int targetID, iscsiExtent extent)
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

        public targetGroup addTargetGroup(targetGroup toAdd, iscsiTarget target)
        {
            string payload = String.Format("{{\"iscsi_target\": \"{0}\", " +
                                           "\"iscsi_target_authgroup\": \"{1}\", " +
                                           "\"iscsi_target_authtype\": \"{2}\", " +
                                           "\"iscsi_target_portalgroup\": \"{3}\", " +
                                           "\"iscsi_target_initiatorgroup\": \"{4}\", " +
                                           "\"iscsi_target_initialdigest\": \"{5}\" " +
                                           "}}", target.id, 
                toAdd.iscsi_target_authgroup, toAdd.iscsi_target_authtype, toAdd.iscsi_target_portalgroup,
                toAdd.iscsi_target_initiatorgroup, toAdd.iscsi_target_initialdigest );
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/", "POST", HttpStatusCode.Created, payload).text;
            targetGroup created = JsonConvert.DeserializeObject<targetGroup>(HTTPResponse);

            return created;
        }

        public volume findParentVolume(List<volume> vols, volume volToFind)
        {
            volume toRet = vols.SingleOrDefault(x => x.children.Count(y => y.name == volToFind.name && x.volType == "dataset" ) > 0);
            if (toRet != null)
                return toRet;

            if (vols.All(x => x.children.Count == 0) || vols.Count == 0)
                return null;

            foreach (volume vol in vols)
                return findParentVolume(vol.children, volToFind);

            return null;
        }

        public iscsiExtent addISCSIExtent(iscsiExtent extent)
        {
            // Chop off leading '/dev/' from path
            string extentPath = extent.iscsi_target_extent_path;
            if (!extentPath.StartsWith("/dev/") )
                throw  new Exception();
            extentPath = extentPath.Substring(5);
            string payload = String.Format("{{" +
                                           "\"iscsi_target_extent_type\": \"{0}\", " +
                                           "\"iscsi_target_extent_name\": \"{1}\", " +
                                           "\"iscsi_target_extent_filesize\": \"{2}\", " +
                                           "\"iscsi_target_extent_disk\": \"{3}\" " +
                                           "}}", "Disk", extent.iscsi_target_extent_name,
                extent.iscsi_target_extent_filesize, extentPath);
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/extent/", "POST", HttpStatusCode.Created, payload).text;
            iscsiExtent created = JsonConvert.DeserializeObject<iscsiExtent>(HTTPResponse);

            return created;
            
        }

        public List<iscsiPortal> getPortals()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/portal/?format=json", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<iscsiPortal>>(HTTPResponse);
        }

        public List<targetGroup> getTargetGroups()
        {
            string HTTPResponse = doReq("http://" + _serverIp + "/api/v1.0/services/iscsi/targetgroup/?format=json", "get", HttpStatusCode.OK).text;
            return JsonConvert.DeserializeObject<List<targetGroup>>(HTTPResponse);
        }
    }
}