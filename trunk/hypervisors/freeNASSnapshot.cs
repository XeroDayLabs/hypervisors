using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace hypervisors
{
    public static class freeNASSnapshot
    {
        public static void restoreSnapshot<T>(hypervisorWithSpec<T> hyp, NASParams nasParams, cancellableDateTime deadline)
        {
            restoreSnapshot(hyp, nasParams.IP, nasParams.username, nasParams.password, deadline);
        }

        public static void restoreSnapshot<T>(hypervisorWithSpec<T> hyp, string freeNASIP, string freeNASUsername, string freeNASPassword, cancellableDateTime deadline)
        {
            hypSpec_withWindbgKernel _spec = hyp.getBaseConnectionSpec();

            NASAccess nas = new FreeNAS(freeNASIP, freeNASUsername, freeNASPassword);

            // Find the device snapshot, so we can get information about it needed to get the ISCSI volume
            snapshotObjects shotObjects = getSnapshotObjectsFromNAS(nas, _spec.snapshotFullName);

            // Here we power the server down, tell the iSCSI server to use the right image, and power it back up again.
            hyp.powerOff();

            // Now we can get started. We must remove the 'target to extent' mapping, then the extent. Then we can safely roll back
            // the ZFS snapshot, and then re-add the extent and mapping. We use a finally block so that it is less likely we will
            // leave the NAS object in an inconsistent state.
            // Note that removing the target will also remove any 'target to extent' mapping, so we don't need to do that explicitly.
            // FreeNAS will take care of it for us.
            nas.deleteISCSIExtent(shotObjects.extent);
            nas.waitUntilISCSIConfigFlushed();
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
                        deadline.doCancellableSleep(TimeSpan.FromSeconds(6)); // 6 sec * 100 retries = ten minutes
                    }
                }
            }
            finally
            {
                // Re-add the extent and target-to-extent mapping. Use the same target ID as the old target-to-extent used, and
                // the new extent's ID.
                iscsiExtent newExtent = nas.addISCSIExtent(shotObjects.extent);
                nas.addISCSITargetToExtent(shotObjects.tgtToExtent.iscsi_target, newExtent);
                nas.waitUntilISCSIConfigFlushed();
            }
        }

        private static snapshotObjects getSnapshotObjectsFromNAS(NASAccess nas, string snapshotFullName)
        {
            snapshot shotToRestore = nas.getSnapshots().SingleOrDefault(
                x => x.fullname.ToLower().Contains("/" + snapshotFullName.ToLower() + "@") || x.id == snapshotFullName);
            if (shotToRestore == null)
                throw new Exception("Cannot find snapshot " + snapshotFullName);

            // Now find the extent. We'll need to delete it before we can rollback the snapshot.
            iscsiExtent extent = nas.getExtents().SingleOrDefault(x => snapshotFullName.Equals(x.iscsi_target_extent_name, StringComparison.CurrentCultureIgnoreCase));
            if (extent == null)
                throw new Exception("Cannot find extent " + snapshotFullName);

            // Find the 'target to extent' mapping, since this will need to be deleted before we can delete the extent.
            iscsiTargetToExtentMapping tgtToExtent = nas.getTargetToExtents().SingleOrDefault(x => x.iscsi_extent == extent.id);
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