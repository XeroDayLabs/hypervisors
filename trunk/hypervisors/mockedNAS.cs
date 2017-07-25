using System;
using System.Collections.Generic;
using System.Linq;

namespace hypervisors
{

    public class mockedNAS : NASAccess
    {
        public List<mockedCall> events = new List<mockedCall>();

        private List<iscsiPortal> portalList = new List<iscsiPortal>();
        private Dictionary<int, iscsiTargetToExtentMapping> tgtToExtents = new Dictionary<int, iscsiTargetToExtentMapping>();
        private Dictionary<int, iscsiExtent> extents = new Dictionary<int, iscsiExtent>();
        private Dictionary<int, iscsiTarget> targets = new Dictionary<int, iscsiTarget>();
        private Dictionary<string, targetGroup> targetGroups = new Dictionary<string, targetGroup>();
        private Dictionary<string, snapshot> snapshots = new Dictionary<string, snapshot>();
        private List<volume> volumes = new List<volume>();

        Random idGen = new Random('1');
        
        public override iscsiTargetToExtentMapping addISCSITargetToExtent(int iscsiTarget, iscsiExtent newExtent)
        {
            lock (events)
            {
                events.Add(new mockedCall("addISCSITargetToExtent", "iscsiTarget: '" + iscsiTarget + "' newExtent: " + newExtent));
            }

            lock (tgtToExtents)
            {
                int newID = idGen.Next();
                iscsiTargetToExtentMapping newMapping = new iscsiTargetToExtentMapping()
                {
                    id = newID, iscsi_target = iscsiTarget, iscsi_extent = newExtent.id, iscsi_lunid = "0"
                };
                tgtToExtents.Add(newID, newMapping);
                return tgtToExtents[newID];
            }
        }

        public override void deleteISCSITargetToExtent(iscsiTargetToExtentMapping tgtToExtent)
        {
            lock (events)
            {
                events.Add(new mockedCall("deleteISCSITargetToExtent", "tgtToExtent: '" + tgtToExtent));
            }

            lock (tgtToExtents)
            {
                tgtToExtents.Remove(tgtToExtent.id);
            }
        }

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            lock (events)
            {
                events.Add(new mockedCall("getTargetToExtents", null));
            }

            lock (tgtToExtents)
            {
                return tgtToExtents.Values.ToList();
            }
        }


        public override iscsiExtent addISCSIExtent(iscsiExtent extent)
        {
            lock (events)
            {
                events.Add(new mockedCall("addISCSIExtent", "extent: '" + extent));
            }

            lock (extents)
            {
                int newID = idGen.Next();
                extent.id = newID;
                extents.Add(newID, extent);
                return extents[newID];
            }
        }

        public override void deleteISCSIExtent(iscsiExtent extent)
        {
            lock (events)
            {
                events.Add(new mockedCall("deleteISCSIExtent", "extent: " + extent));
            }

            lock (extents)
            {
                extents.Remove(extent.id);
            }
        }

        public override List<iscsiExtent> getExtents()
        {
            lock (events)
            {
                events.Add(new mockedCall("getExtents", null));
            }

            lock (extents)
            {
                return extents.Values.ToList();
            }
        }

        public override void rollbackSnapshot(snapshot shotToRestore)
        {
            lock (events)
            {
                events.Add(new mockedCall("rollbackSnapshot", "shotToRestore: '" + shotToRestore));
            }
        }

        public override List<iscsiTarget> getISCSITargets()
        {
            lock (events)
            {
                events.Add(new mockedCall("getISCSITargets", null));
            }

            lock (targets)
            {
                return targets.Values.ToList();
            }
        }

        public override void deleteISCSITarget(iscsiTarget tgt)
        {
            lock (events)
            {
                events.Add(new mockedCall("deleteISCSITarget", "tgt: " + tgt));
            }

            lock (targets)
            {
                targets.Remove(tgt.id);
            }
        }

        public override List<snapshot> getSnapshots()
        {
            lock (events)
            {
                events.Add(new mockedCall("getSnapshots", null));
            }

            lock (snapshots)
            {
                return snapshots.Values.ToList();
            }
        }

        public override snapshot deleteSnapshot(snapshot toDelete)
        {
            lock (events)
            {
                events.Add(new mockedCall("deleteSnapshot", "toDelete: " + toDelete));
            }

            lock (snapshots)
            {
                snapshots.Remove(toDelete.id);
            }

            return toDelete;
        }

        public override snapshot createSnapshot(string dataset, string snapshotName)
        {
            lock (events)
            {
                events.Add(new mockedCall("createSnapshot", "dataset: '" + dataset + "' snapshotName: " + snapshotName));
            }

            lock (snapshots)
            {
                string newID = idGen.Next().ToString();
                snapshot newShot = new snapshot()
                {
                    fullname = "/dev/foo/bar/" + snapshotName,
                    id = newID,
                    name = snapshotName,
                    filesystem = dataset
                };
                snapshots.Add(newID, newShot);
                return snapshots[newID];
            }
        }

        public void addSnapshot(snapshot baseSnapshot)
        {
            lock (events)
            {
                events.Add(new mockedCall("addSnapshot", "baseSnapshot: '" + baseSnapshot + "'"));
            }

            lock (snapshots)
            {
                snapshots.Add(baseSnapshot.id, baseSnapshot);
            }
        }

        public override void cloneSnapshot(snapshot toClone, string fullCloneName)
        {
            lock (events)
            {
                events.Add(new mockedCall("cloneSnapshot", "toClone: '" + toClone + "' fullCloneName: " + fullCloneName));
            }
        }

        public override List<volume> getVolumes()
        {
            lock (events)
            {
                events.Add(new mockedCall("getVolumes", null));
            }

            lock (volumes)
            {
                return new List<volume>(volumes);
            }
        }

        public void addVolume(volume toAdd)
        {
            //events.Add(new mockedCall("addVolume", "toAdd: " + toAdd));

            lock (volumes)
            {
                volumes.Add(toAdd);
            }
        }

        public override volume findVolumeByName(List<volume> volsToSearch, string cloneName)
        {
            lock (events)
            {
                events.Add(new mockedCall("findVolumeByName", "volumes: '" + volsToSearch + "' cloneName: " + cloneName));
            }

            if (volsToSearch == null)
                return null;

            volume toRet = volsToSearch.SingleOrDefault(x => x.name == cloneName);
            if (toRet != null)
                return toRet;

            if (volsToSearch.All(x => x.children != null && x.children.Count == 0) || volsToSearch.Count == 0)
                return null;

            foreach (volume vol in volsToSearch)
            {
                volume maybeThis = findVolumeByName(vol.children, cloneName);
                if (maybeThis != null)
                    return maybeThis;
            }

            return null;
        }

        public override volume findVolumeByMountpoint(List<volume> volumesToSearch, string mountpoint)
        {
            lock (events)
            {
                events.Add(new mockedCall("findVolumeByMountpoint", "vols: '" + volumesToSearch + "' mountpoint: " + mountpoint));
            }

            if (volumesToSearch == null)
                return null;

            volume toRet = volumesToSearch.SingleOrDefault(x => x.mountpoint == mountpoint);
            if (toRet != null)
                return toRet;

            if (volumesToSearch.All(x => x.children != null && x.children.Count == 0) || volumesToSearch.Count == 0)
                return null;

            foreach (volume vol in volumesToSearch)
            {
                volume maybeThis = findVolumeByMountpoint(vol.children, mountpoint);
                if (maybeThis != null)
                    return maybeThis;
            }
            return null;
        }

        public override volume findParentVolume(List<volume> volumesToSearch, volume volToFind)
        {
            lock (events)
            {
                events.Add(new mockedCall("findParentVolume", "vols: '" + volumesToSearch + "' volToFind: " + volToFind));
            }

            return volumesToSearch.Single(x => x.children.Contains(volToFind));
        }

        public override void deleteZVol(volume vol)
        {
            lock (events)
            {
                events.Add(new mockedCall("deleteZVol", "vol: " + vol));
            }
        }

        public override List<targetGroup> getTargetGroups()
        {
            lock (events)
            {
                events.Add(new mockedCall("getTargetGroups", null));
            }

            lock (targetGroups)
            {
                return targetGroups.Values.ToList();
            }
        }

        public override iscsiTarget addISCSITarget(iscsiTarget toAdd)
        {
            lock (events)
            {
                events.Add(new mockedCall("addISCSITarget", "toAdd: " + toAdd));
            }

            lock (targets)
            {
                int newID = idGen.Next();
                toAdd.id = newID;
                targets.Add(newID, toAdd);
                return targets[newID];
            }
        }

        public override targetGroup addTargetGroup(targetGroup tgtGrp, iscsiTarget newTarget)
        {
            events.Add(new mockedCall("addTargetGroup", "tgtGrp: '" + tgtGrp + "' newTarget: " + newTarget));

            lock (targetGroups)
            {
                string newID = idGen.Next().ToString();
                tgtGrp.id = newID;
                targetGroups.Add(newID, tgtGrp);
                return targetGroups[newID];
            }
        }

        public override List<iscsiPortal> getPortals()
        {
            lock (events)
            {
                events.Add(new mockedCall("getPortals", null));
            }

            lock (targetGroups)
            {
                return new List<iscsiPortal>(portalList);
            }
        }
    }

    [Serializable]
    public class mockedCall
    {
        public DateTime timestamp;
        public string message;
        public string functionName;

        public mockedCall()
        {
            // For XML de/ser
        }

        public mockedCall(string newFunctionName)
        {
            timestamp = DateTime.Now;
            functionName = newFunctionName;
            message = null;
        }

        public mockedCall(string newFunctionName, string newMessage)
        {
            timestamp = DateTime.Now;
            functionName = newFunctionName;
            message = newMessage;
        }

        public override string ToString()
        {
            return string.Format("{0} : '{1}' args '{2}' ", timestamp.ToString("T"), functionName, message ?? "(none)");
        }
    }
}