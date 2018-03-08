using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace hypervisors
{
    public class FreeNASWithCaching : NASAccess
    {
        private readonly ConcurrentDictionary<string, iscsiTargetToExtentMapping> TTEExtents = new ConcurrentDictionary<string, iscsiTargetToExtentMapping>();
        private readonly ConcurrentDictionary<string, iscsiExtent> extents = new ConcurrentDictionary<string, iscsiExtent>();
        private readonly ConcurrentDictionary<string, iscsiTarget> targets = new ConcurrentDictionary<string, iscsiTarget>();
        private readonly ConcurrentDictionary<string, volume> volumes = new ConcurrentDictionary<string, volume>();
        private readonly ConcurrentDictionary<string, snapshot> snapshots = new ConcurrentDictionary<string, snapshot>();
        private readonly ConcurrentDictionary<string, targetGroup> targetGroups = new ConcurrentDictionary<string, targetGroup>();
        private readonly ConcurrentDictionary<string, iscsiPortal> portals = new ConcurrentDictionary<string, iscsiPortal>();

        // We don't want anything to use these collections while they are being updated, so we use a reader/writer lock to prevent
        // that while permitting many simultaneous readers.
        private ReaderWriterLockSlim TTEExtentsLock = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim extentsLock = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim targetsLock = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim snapshotsLock = new ReaderWriterLockSlim();

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
            invalidateTargetToExtents();
            invalidateExtents();
            invalidateSnapshots();
            invalidateTargets();
            uncachedNAS.getVolumes().ForEach(x => volumes.TryAdd(x.id, x));
            uncachedNAS.getTargetGroups().ForEach(x => targetGroups.TryAdd(x.id, x));
            uncachedNAS.getPortals().ForEach(x => portals.TryAdd(x.id, x));

            // If there are no portals, then we make a default one, since iscsi won't function without it
            if (portals.Count == 0)
            {
                iscsiPortal newPortal = uncachedNAS.createPortal("0.0.0.0:3260");
                portals.TryAdd(newPortal.id, newPortal);
            }

            waitUntilISCSIConfigFlushed(true);
        }

        public override iscsiTargetToExtentMapping addISCSITargetToExtent(string iscsiTargetID, iscsiExtent newExtent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }

            iscsiTargetToExtentMapping newMapping;
            try
            {
                newMapping = uncachedNAS.addISCSITargetToExtent(iscsiTargetID, newExtent);
                TTEExtents.TryAdd(newMapping.id, newMapping);
            }
            finally
            {
                lock (this)
                {
                    waitingISCSIOperations--;
                }
                markThisThreadISCSIDirty();
            }

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

            try
            {
                uncachedNAS.deleteISCSITargetToExtent(tgtToExtent);
                iscsiTargetToExtentMapping foo;
                TTEExtents.TryRemove(tgtToExtent.id, out foo);
            }
            finally
            {
                lock (this)
                {
                    waitingISCSIOperations--;
                }
                markThisThreadISCSIDirty();
            }
        }

        public override List<iscsiTargetToExtentMapping> getTargetToExtents()
        {
            return TTEExtents.Values.ToList();
        }

        public override void invalidateTargetToExtents()
        {
            invalidate(TTEExtentsLock, uncachedNAS.getTargetToExtents(), TTEExtents);
        }

        public override iscsiExtent addISCSIExtent(iscsiExtent extent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }

            iscsiExtent newVal;
            try
            {
                newVal = uncachedNAS.addISCSIExtent(extent);
                extents.TryAdd(newVal.id, newVal);
            }
            finally
            {
                lock (this)
                {
                    waitingISCSIOperations--;
                }
                markThisThreadISCSIDirty();
            }
            return newVal;
        }

        public override void deleteISCSIExtent(iscsiExtent extent)
        {
            lock (this)
            {
                waitingISCSIOperations++;
            }
            try
            {
                uncachedNAS.deleteISCSIExtent(extent);
                iscsiExtent foo;
                extents.TryRemove(extent.id, out foo);
                var toDel = TTEExtents.Where(x => x.Value.iscsi_extent == extent.id);
                foreach (var tte in toDel)
                {
                    iscsiTargetToExtentMapping removedTTE;
                    TTEExtents.TryRemove(tte.Key, out removedTTE);
                }
            }
            finally
            {
                lock (this)
                {
                    waitingISCSIOperations--;
                }
                markThisThreadISCSIDirty();
            }
        }

        public override List<iscsiExtent> getExtents()
        {
            return extents.Values.ToList();
        }

        public override void invalidateExtents()
        {
            invalidate(extentsLock, uncachedNAS.getExtents(), extents);
        }

        public override void rollbackSnapshot(snapshot shotToRestore)
        {
            snapshotsLock.EnterWriteLock();
            try
            {
                uncachedNAS.getSnapshots().Clear();
                uncachedNAS.getSnapshots().ForEach(x => snapshots.TryAdd(x.id, x));
            }
            catch (Exception)
            {
                snapshots.Clear();
                throw;
            }
            finally
            {
                snapshotsLock.ExitWriteLock();
            }
        }

        public override void invalidateSnapshots()
        {
            invalidate(extentsLock, uncachedNAS.getSnapshots(), snapshots);
        }

        public override void waitUntilISCSIConfigFlushed(bool force = false, TimeSpan timeout = default(TimeSpan))
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
                            uncachedNAS.waitUntilISCSIConfigFlushed(force, TimeSpan.FromMinutes(5 + (extents.Count * 0.01)));
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

        public override void invalidateTargets()
        {
            invalidate(targetsLock, uncachedNAS.getISCSITargets(), targets);
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

        public override iscsiPortal addPortal(iscsiPortal toAdd)
        {
            iscsiPortal newVal = uncachedNAS.addPortal(toAdd);
            portals.TryAdd(newVal.id, newVal);
            markThisThreadISCSIDirty();
            return newVal;
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

        private void invalidate<T>(ReaderWriterLockSlim lockToTake, List<T> collectionSource, ConcurrentDictionary<string, T> collectionToInvalidate)
            where T : thingWithID
        {
            lockToTake.EnterWriteLock();
            try
            {
                collectionToInvalidate.Clear();
                collectionSource.ForEach(x => collectionToInvalidate.TryAdd(x.id, x));
            }
            catch (Exception)
            {
                collectionToInvalidate.Clear();
                throw;
            }
            finally
            {
                lockToTake.ExitWriteLock();
            }
        }
    }

}