using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace hypervisors
{
    public class nasConflictException : nasAccessException
    {
        public nasConflictException() : base() { }

        public nasConflictException(string s) : base(s) { }
    }

    public class nasNotFoundException : nasAccessException
    {
        public nasNotFoundException() : base() { }

        public nasNotFoundException(string s) : base(s) { }
    }

    public abstract class NASAccess
    {
        public abstract iscsiTargetToExtentMapping addISCSITargetToExtent(string iscsiTargetID, iscsiExtent newExtent);
        public abstract void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent);
        public abstract List<iscsiTargetToExtentMapping> getTargetToExtents();
        public abstract void invalidateTargetToExtents();

        public abstract iscsiExtent addISCSIExtent(iscsiExtent extent);
        public abstract void deleteISCSIExtent(iscsiExtent extent);
        public abstract List<iscsiExtent> getExtents();
        public abstract void invalidateExtents();

        public abstract List<iscsiTarget> getISCSITargets();
        public abstract void deleteISCSITarget(iscsiTarget tgt);
        public abstract void waitUntilISCSIConfigFlushed(bool force = false);
        public abstract void invalidateTargets();

        public abstract List<snapshot> getSnapshots();
        public abstract void deleteSnapshot(snapshot toDelete);
        public abstract snapshot createSnapshot(string dataset, string snapshotName);
        public abstract void cloneSnapshot(snapshot toClone, string fullCloneName);
        public abstract void rollbackSnapshot(snapshot shotToRestore);
        public abstract void invalidateSnapshots();

        public abstract List<volume> getVolumes();
        public abstract volume findVolumeByName(List<volume> volumes, string cloneName);
        public abstract volume findVolumeByMountpoint(List<volume> vols, string mountpoint);
        public abstract volume findParentVolume(List<volume> vols, volume volToFind);

        public abstract void deleteZVol(volume vol);

        public abstract List<targetGroup> getTargetGroups();
        public abstract iscsiTarget addISCSITarget(iscsiTarget toAdd);
        public abstract targetGroup addTargetGroup(targetGroup tgtGrp, iscsiTarget newTarget);

        public abstract List<iscsiPortal> getPortals();
        public abstract iscsiPortal addPortal(iscsiPortal toAdd);

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

    public static class FreeNasGroup 
    {
        private static readonly ConcurrentDictionary<string, FreeNASWithCaching> nasses = new ConcurrentDictionary<string, FreeNASWithCaching>();

        public static FreeNASWithCaching getOrMake(string host, string username, string password)
        {
            string IDString = String.Format("{0}-{1}-{2}", host, username, password);
            if (nasses.ContainsKey(IDString))
                return nasses[IDString];
            lock (nasses)
            {
                if (nasses.ContainsKey(IDString))
                    return nasses[IDString];
                nasses.TryAdd(IDString, new FreeNASWithCaching(host, username, password));
                return nasses[IDString];
            }
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

    public class thingWithID
    {
        [JsonProperty("id")]
        public string id { get; set; }        
    }

    public class user : thingWithID
    {
        [JsonProperty("bsdusr_sshpubkey")]
        public string bsdusr_sshpubkey { get; set; }

        [JsonProperty("bsdusr_uid")]
        public string bsdusr_uid { get; set; }
    }

    public class targetGroup : thingWithID
    {
        [JsonProperty("iscsi_target")]
        public string iscsi_target { get; set; }

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

    public class iscsiPortal : thingWithID
    {
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


    public class snapshot : thingWithID
    {
        [JsonProperty("filesystem")]
        public string filesystem { get; set; }

        [JsonProperty("fullname")]
        public string fullname { get; set; }

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

    public class volume : thingWithID
    {
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

    public class iscsiExtent : thingWithID
    {
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

    public class iscsiTargetToExtentMapping : thingWithID
    {
        [JsonProperty("iscsi_target")]
        public string iscsi_target { get; set; }

        [JsonProperty("iscsi_extent")]
        public string iscsi_extent { get; set; }

        [JsonProperty("iscsi_lunid")]
        public string iscsi_lunid { get; set; }
    }

    public class iscsiTarget : thingWithID
    {
        [JsonProperty("iscsi_target_name")]
        public string targetName { get; set; }

        [JsonProperty("iscsi_target_alias")]
        public string targetAlias { get; set; }
    }
}