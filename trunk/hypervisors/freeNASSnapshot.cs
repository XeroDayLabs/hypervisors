using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace hypervisors
{
    public static class freeNASSnapshot
    {
        public static void restoreSnapshotByNam<T>(hypervisorWithSpec<T> hyp, string freeNASIP, string freeNASUsername, string freeNASPassword)
        {
            hypSpec_withWindbgKernel _spec = hyp.getBaseConnectionSpec();
            string fullName = _spec.snapshotName;

            FreeNAS nas = new FreeNAS(freeNASIP, freeNASUsername, freeNASPassword);

            // Find the device snapshot, so we can get information about it needed to get the ISCSI volume
            snapshotObjects shotObjects = getSnapshotObjectsFromNAS(nas, fullName);

            // Here we power the server down, tell the iSCSI server to use the right image, and power it back up again.
            hyp.powerOff();

            // Now we can get started. We must remove the 'target to extent' mapping, then the target. Then we can safely roll back
            // the ZFS snapshot, and then re-add the target and mapping. We use a finally block so that it is less likely we will
            // leave the NAS object in an inconsistent state.
            // TODO: can we just tell freeNAS to delete this stuff instead?
            nas.deleteISCSITargetToExtent(shotObjects.tgtToExtent);
            nas.deleteISCSIExtent(shotObjects.extent);
            try
            {
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
                        Thread.Sleep(TimeSpan.FromSeconds(6)); // 6 sec * 100 retries = ten minutes
                    }
                }
            }
            finally
            {
                // Re-add the extent and target-to-extent mapping.
                iscsiExtent newExtent = nas.addISCSIExtent(shotObjects.extent);
                nas.addISCSITargetToExtent(shotObjects.tgtToExtent.iscsi_target, newExtent);
            }
        }

        public static snapshotObjects getSnapshotObjectsFromNAS(FreeNAS nas, string fullName)
        {
            var snapshots = nas.getSnapshots();
            snapshot shotToRestore = snapshots.SingleOrDefault(x => x.name.Equals(fullName, StringComparison.CurrentCultureIgnoreCase) || x.id == fullName);
            if (shotToRestore == null)
                throw new Exception("Cannot find snapshot " + fullName);

            // Now find the extent. We'll need to delete it before we can rollback the snapshot.
            List<iscsiExtent> extents = nas.getExtents();
            iscsiExtent extent = extents.SingleOrDefault(x => fullName.Equals(x.iscsi_target_extent_name, StringComparison.CurrentCultureIgnoreCase));
            if (extent == null)
                throw new Exception("Cannot find extent " + fullName);

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
    }
}