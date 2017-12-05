using System;
using System.Collections.Concurrent;
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
        public abstract void waitUntilISCSIConfigFlushed(bool force = false);

        public abstract List<snapshot> getSnapshots();
        public abstract void deleteSnapshot(snapshot toDelete);
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

        public abstract List<user> getUsers();
        public abstract user updateUser(user userToChange);
        public abstract iscsiPortal createPortal(string portalIPs);
        public abstract targetGroup createTargetGroup(iscsiPortal associatedPortal, iscsiTarget tgt);
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

    public class FreeNASWithCaching :  NASAccess
    {
        private readonly ConcurrentDictionary<int, iscsiTargetToExtentMapping> TTEExtents = new ConcurrentDictionary<int, iscsiTargetToExtentMapping>();
        private readonly ConcurrentDictionary<int, iscsiExtent> extents = new ConcurrentDictionary<int, iscsiExtent>();
        private readonly ConcurrentDictionary<int, iscsiTarget> targets = new ConcurrentDictionary<int, iscsiTarget>();
        private readonly ConcurrentDictionary<int, volume> volumes = new ConcurrentDictionary<int, volume>();
        private readonly ConcurrentDictionary<string, snapshot> snapshots = new ConcurrentDictionary<string, snapshot>();
        private readonly ConcurrentDictionary<string, targetGroup> targetGroups = new ConcurrentDictionary<string, targetGroup>();
        private readonly ConcurrentDictionary<string, iscsiPortal> portals = new ConcurrentDictionary<string, iscsiPortal>();

        private readonly FreeNAS uncachedNAS;

        private int waitingISCSIOperations = 0;
        private readonly ConcurrentDictionary<int, threadDirtInfo> dirtyISCSIThreads = new ConcurrentDictionary<int, threadDirtInfo>();

        public FreeNASWithCaching(string serverIP, string username, string password)
        {
            uncachedNAS = new FreeNAS(serverIP, username, password);
            init();
        }

        public int flushCount = 0;

        private void init()
        {
            uncachedNAS.getTargetToExtents().ForEach(x => TTEExtents.TryAdd(x.id, x));
            uncachedNAS.getExtents().ForEach(x => extents.TryAdd(x.id, x));
            uncachedNAS.getISCSITargets().ForEach(x => targets.TryAdd(x.id, x));
            uncachedNAS.getVolumes().ForEach(x => volumes.TryAdd(x.id, x));
            uncachedNAS.getSnapshots().ForEach(x => snapshots.TryAdd(x.id, x));
            uncachedNAS.getTargetGroups().ForEach(x => targetGroups.TryAdd(x.id, x));
            uncachedNAS.getPortals().ForEach(x => portals.TryAdd(x.id, x));

            // If there are no portals, then we make a default one
            if (portals.Count == 0)
            {
                iscsiPortal newPortal = uncachedNAS.createPortal("0.0.0.0:3260");
                portals.TryAdd(newPortal.id, newPortal);
            }
            // Same for target groups
            if (targetGroups.Count == 0)
            {
//                targetGroup newTgtGrp = uncachedNAS.createTargetGroup(portals.Values.First());
//                targetGroups.TryAdd(newTgtGrp.id, newTgtGrp);
            }

            waitUntilISCSIConfigFlushed(true);
        }
        
        public override iscsiTargetToExtentMapping addISCSITargetToExtent(int iscsiTarget, iscsiExtent newExtent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }

            iscsiTargetToExtentMapping newMapping = uncachedNAS.addISCSITargetToExtent(iscsiTarget, newExtent);
            TTEExtents.TryAdd(newMapping.id, newMapping);

            lock (this)
            {
                waitingISCSIOperations--;
            }

            markThisThreadISCSIDirty();

            return newMapping;
        }

        private static int iscsiOperationCounter = 1;
        private static readonly Object iscsiOperationCounterLock = new Object();
        private void markThisThreadISCSIDirty()
        {
            if (!dirtyISCSIThreads.ContainsKey(Thread.CurrentThread.ManagedThreadId))
                dirtyISCSIThreads.TryAdd(Thread.CurrentThread.ManagedThreadId, new threadDirtInfo());

            lock (iscsiOperationCounterLock)
            {
                dirtyISCSIThreads[Thread.CurrentThread.ManagedThreadId].lastUnclean = iscsiOperationCounter;
                iscsiOperationCounter++;
            }
        }

        public override void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }
            uncachedNAS.deleteISCSITargetToExtent(tgtToExtent);
            iscsiTargetToExtentMapping foo;
            TTEExtents.TryRemove(tgtToExtent.id, out foo);
            lock (this)
            {
                waitingISCSIOperations--;
            }
            markThisThreadISCSIDirty();
        }

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            return TTEExtents.Values.ToList();
        }

        public override iscsiExtent addISCSIExtent(iscsiExtent extent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }
            iscsiExtent newVal = uncachedNAS.addISCSIExtent(extent);
            extents.TryAdd(newVal.id, newVal);
            lock (this)
            {
                waitingISCSIOperations--;
            }
            markThisThreadISCSIDirty();
            return newVal;
        }

        public override void deleteISCSIExtent(iscsiExtent extent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }
            uncachedNAS.deleteISCSIExtent(extent);
            iscsiExtent foo;
            extents.TryRemove(extent.id, out foo);
            var toDel = TTEExtents.Where(x => x.Value.iscsi_extent == extent.id);
            foreach (var tte in toDel)
            {
                iscsiTargetToExtentMapping removedTTE;
                TTEExtents.TryRemove(tte.Key, out removedTTE);
            }
            lock (this)
            {
                waitingISCSIOperations--;
            }
            markThisThreadISCSIDirty();
        }

        public override List<iscsiExtent> getExtents()
        {
            return extents.Values.ToList();
        }

        public override void rollbackSnapshot(snapshot shotToRestore)
        {
            uncachedNAS.rollbackSnapshot(shotToRestore);
        }

        public override void waitUntilISCSIConfigFlushed(bool force = false)
        {
            // Its okay to return before other threads have completely flushed their config - the only one we're really interested
            // in is the config for the calling thread.
            if (force == false)
            {
                if (!dirtyISCSIThreads.ContainsKey(Thread.CurrentThread.ManagedThreadId) ||
                     dirtyISCSIThreads[Thread.CurrentThread.ManagedThreadId].hasPending == false)
                {
                    Console.WriteLine("No flush needed for this thread");
                    return;
                }
            }

            ConcurrentDictionary<int, threadDirtInfo> dirtyThreadsAtStart = new ConcurrentDictionary<int, threadDirtInfo>();

            // coalesce flushing by waiting until there are zero pending for all threads.
            while (true)
            {
                if (waitingISCSIOperations == 0)
                {
                    lock (this)
                    {
                        if (waitingISCSIOperations == 0)
                        {
                            foreach (KeyValuePair<int, threadDirtInfo> kvp in dirtyISCSIThreads)
                                dirtyThreadsAtStart.TryAdd(kvp.Key, kvp.Value);
                            uncachedNAS.waitUntilISCSIConfigFlushed(force);
                            flushCount++;
                            break;
                        }
                    }
                }

                //Console.WriteLine("Waiting for " + waitingISCSIOperations + " to finish before we flush");
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }

            // We flushed up to this index of iscsi operation (or more, maybe).
            foreach (KeyValuePair<int, threadDirtInfo> kvp in dirtyISCSIThreads)
            {
                if (dirtyThreadsAtStart.ContainsKey(kvp.Key))
                    kvp.Value.lastFlushed = dirtyThreadsAtStart[kvp.Key].lastUnclean;
            }
        }

        public override List<snapshot> getSnapshots()
        {
            return snapshots.Values.ToList();
        }

        public override void deleteSnapshot(snapshot toDelete)
        {
            uncachedNAS.deleteSnapshot(toDelete);
            snapshot foo;
            snapshots.TryRemove(toDelete.id, out foo);
        }

        public override snapshot createSnapshot(string dataset, string snapshotName)
        {
            snapshot newVal = uncachedNAS.createSnapshot(dataset, snapshotName);
            snapshots.TryAdd(newVal.id, newVal);
            return newVal;
        }

        public override void cloneSnapshot(snapshot toClone, string fullCloneName)
        {
            uncachedNAS.cloneSnapshot(toClone, fullCloneName);
        }

        public override List<iscsiTarget> getISCSITargets()
        {
            return targets.Values.ToList();
        }

        public override iscsiTarget addISCSITarget(iscsiTarget toAdd)
        {
            iscsiTarget newVal = uncachedNAS.addISCSITarget(toAdd);
            targets.TryAdd(newVal.id, newVal);
            markThisThreadISCSIDirty();
            return newVal;
        }

        public override void deleteISCSITarget(iscsiTarget tgt)
        {
            uncachedNAS.deleteISCSITarget(tgt);
            iscsiTarget foo;
            targets.TryRemove(tgt.id, out foo);
            var toDel = TTEExtents.Where(x => x.Value.iscsi_target == tgt.id);
            foreach (var tte in toDel)
            {
                iscsiTargetToExtentMapping removedTTE;
                TTEExtents.TryRemove(tte.Key, out removedTTE);
            }
            markThisThreadISCSIDirty();
        }

        public override List<volume> getVolumes()
        {
            return volumes.Values.ToList();
        }

        public override volume findVolumeByName(List<volume> volumesToSearch, string cloneName)
        {
            return uncachedNAS.findVolumeByName(volumesToSearch, cloneName);
        }

        public override volume findVolumeByMountpoint(List<volume> vols, string mountpoint)
        {
            return uncachedNAS.findVolumeByMountpoint(vols, mountpoint);
        }

        public override volume findParentVolume(List<volume> vols, volume volToFind)
        {
            return uncachedNAS.findParentVolume(vols, volToFind);
        }

        public override void deleteZVol(volume vol)
        {
            waitUntilISCSIConfigFlushed();
            uncachedNAS.deleteZVol(vol);
        }

        public override List<targetGroup> getTargetGroups()
        {
            return targetGroups.Values.ToList();
        }

        public override targetGroup addTargetGroup(targetGroup tgtGrp, iscsiTarget newTarget)
        {
            targetGroup newVal = uncachedNAS.addTargetGroup(tgtGrp, newTarget);
            targetGroups.TryAdd(newVal.id, newVal);
            markThisThreadISCSIDirty();
            return newVal;
        }

        public override List<iscsiPortal> getPortals()
        {
            return portals.Values.ToList();
        }

        public override List<user> getUsers()
        {
            return uncachedNAS.getUsers();
        }

        public override user updateUser(user userToChange)
        {
            return uncachedNAS.updateUser(userToChange);
        }

        public override iscsiPortal createPortal(string portalIPs)
        {
            return uncachedNAS.createPortal(portalIPs);
        }

        public override targetGroup createTargetGroup(iscsiPortal associatedPortal, iscsiTarget tgt)
        {
            var newTG = uncachedNAS.createTargetGroup(associatedPortal, tgt);
            targetGroups.TryAdd(newTG.id, newTG);
            return newTG;
        }
    }

    public class threadDirtInfo
    {
        public int lastUnclean;
        public int lastFlushed;

        public bool hasPending
        {
            get { return lastUnclean != lastFlushed; }
        }
    }

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
        private readonly Object nonAPIReqLock = new Object();

        /// <summary>
        /// This lock is used to serialise requests to the FreeNAS api. It is used because FreeNAS really doesn't deal well with
        /// lots of simultanenous requests, so we end up retrying with a delay, which is much less effecient than waiting on a 
        /// lock.
        /// </summary>
        private readonly Object ReqLock = new Object();

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
                    deadline.throwIfTimedOutOrCancelled();

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
                try
                {
                    return _doReq(url, method, expectedCode, payload);
                }
                catch (nasAccessException)
                {
                    if (!deadline.stillOK)
                        throw;

                    deadline.doCancellableSleep(TimeSpan.FromSeconds(10));
                }
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
                    if (e.Response == null)
                        throw new nasAccessException(e.Message);

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

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            return doReqForJSON<List<iscsiTargetToExtentMapping>>("http://" + _serverIp + "/api/v1.0/services/iscsi/targettoextent/?limit=99999", "get", HttpStatusCode.OK);
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

        public override iscsiTargetToExtentMapping addISCSITargetToExtent(int targetID, iscsiExtent extent)
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

    public class user
    {
        [JsonProperty("id")]
        public string id { get; set; }

        [JsonProperty("bsdusr_sshpubkey")]
        public string bsdusr_sshpubkey { get; set; }

        [JsonProperty("bsdusr_uid")]
        public int bsdusr_uid { get; set; }
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