using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using hypervisors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace tests
{
    [TestClass]
    public class freeNASTests
    {
        private readonly string nashostname = Properties.Settings.Default.NASHostname;
        private readonly string nasusername = Properties.Settings.Default.NASUsername;
        private readonly string naspassword = Properties.Settings.Default.NASPassword;
        private readonly string nastempspace = "/mnt/test/";
//        private string nastempspace = "/mnt/SSDs/";

        [TestInitialize]
        public void init()
        {
            var s = Properties.Settings.Default;
            using (hypervisor_vmware hyp = new hypervisor_vmware(new hypSpec_vmware(
                s.NASVMName, s.NASVMServerHostname, s.NASVMServerUsername, s.NASVMServerPassword, 
                nasusername, naspassword,
                "clean", null, 0, null, nashostname), clientExecutionMethod.SSHToBASH))
            {
                // Restore the FreeNAS VM to a known-good state, power it on, and wait for it to boot.
                hyp.powerOff();
                hyp.restoreSnapshot();
                hyp.powerOn();

                // Wait until boot has finished (this isn't a great way of checking, but oh well)
                while (true)
                {
                    executionResult cronStatus = hyp.startExecutable("service", "cron status");
                    if (cronStatus.resultCode == 0)
                        break;
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }

                var aa = hyp.startExecutable("service", "collectd stop");
                var bb = hyp.startExecutable("service", "syslog-ng stop");

                hyp.patchFreeNASInstallation();
            }
        }

        private void clearAll()
        {
            // We assume no-one else is using this FreeNAS server right now. We delete everything on it. >:D
            // We do, however, avoid deleting anything beginning with 'blade', so its sort-of safe to run this on
            // the production FreeNAS install, if no-one else is using it, and the only iscsi info you want to keep
            // begins with this string.

            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            foreach (iscsiTarget tgt in foo.getISCSITargets().Where(x => !x.targetName.StartsWith("blade")))
                foo.deleteISCSITarget(tgt);

            foreach (iscsiExtent ext in foo.getExtents().Where(x => !x.iscsi_target_extent_name.StartsWith("blade")))
                foo.deleteISCSIExtent(ext);

            foreach (iscsiTargetToExtentMapping tte in foo.getTargetToExtents())
            {
                var tgt = foo.getISCSITargets().SingleOrDefault(x => x.id == tte.iscsi_target);
                var ext = foo.getExtents().SingleOrDefault(x => x.id == tte.iscsi_extent);
                if (tgt == null || ext == null)
                    foo.deleteISCSITargetToExtent(tte);
            }

            foo.waitUntilISCSIConfigFlushed();
        }

        [TestMethod]
        public void checkThatReloadsScaleOkay()
        {
            string testPrefix = Guid.NewGuid().ToString();

            Dictionary<int, TimeSpan> reloadTimesByFileCount = new Dictionary<int, TimeSpan>();

            for (int filecount = 1; filecount < 100; filecount += 10)
            {
                clearAll();
                TimeSpan reloadTime = canExportNewFilesQuickly(testPrefix, 10);
                reloadTimesByFileCount.Add(filecount, reloadTime);
            }

            foreach (KeyValuePair<int, TimeSpan> kvp in reloadTimesByFileCount)
                Debug.WriteLine("Adding " + kvp.Key + " files took " + kvp.Value);

            // They should not deviate from the average by more than 10%.
            double avg = reloadTimesByFileCount.Values.Average(x => x.TotalMilliseconds);
            foreach (TimeSpan val in reloadTimesByFileCount.Values)
                Assert.AreEqual(avg, val.TotalMilliseconds, avg / 10);
        }

        [TestMethod]
        public void canAddExtentsQuickly()
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();
            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);
            timer.Stop();
            Debug.WriteLine("Instantiation took " + timer.ElapsedMilliseconds + " ms");

            string testPrefix = Guid.NewGuid().ToString();

            // Make some test files to export
            int extentcount = 50;
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                for (int i = 0; i < extentcount; i++)
                {
                    exec.startExecutable("touch", nastempspace + "testfile_" + i);
                }
            }

            // Add some extents and watch the time taken
            timer.Restart();
            int flushesBefore = foo.flushCount;
            iscsiExtent[] extentsAdded = new iscsiExtent[extentcount];
            for (int i = 0; i < extentcount; i++)
            {
                extentsAdded[i] = foo.addISCSIExtent(new iscsiExtent()
                {
                    iscsi_target_extent_name = testPrefix + "_" + i,
                    iscsi_target_extent_type = "File",
                    iscsi_target_extent_path = nastempspace + "/testfile_" + i
                });
            }
            timer.Stop();
            int flushesAfter = foo.flushCount;
            Debug.WriteLine("Adding " + extentcount + " extents took " + timer.ElapsedMilliseconds + " ms and required " +
                            (flushesAfter - flushesBefore) + " flushes");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(10));

            // Each should be unique by these properties
            foreach (iscsiExtent ext in extentsAdded)
            {
                Assert.AreEqual(1, extentsAdded.Count(x => x.id == ext.id));
                Assert.AreEqual(1, extentsAdded.Count(x => x.iscsi_target_extent_path == ext.iscsi_target_extent_path));
                Assert.AreEqual(1, extentsAdded.Count(x => x.iscsi_target_extent_name == ext.iscsi_target_extent_name));
            }

            timer.Restart();
            foo.waitUntilISCSIConfigFlushed();
            timer.Stop();
            Debug.WriteLine("Reloading config took " + timer.ElapsedMilliseconds + " ms");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(7));
        }

        [TestMethod]
        public void canAddDiskBasedExtent()
        {
            clearAll();   
            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            string testPrefix = Guid.NewGuid().ToString();

            // Make a test datastore to export
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("zfs", "create -V 10MB test/" + testPrefix);
            }

            // Add some extents and watch the time taken
            foo.addISCSIExtent(new iscsiExtent()
            {
                iscsi_target_extent_name = testPrefix,
                iscsi_target_extent_type = "Disk",
                iscsi_target_extent_path = "/dev/zvol/test/" + testPrefix
            });

            foo.waitUntilISCSIConfigFlushed();
        }

        [TestMethod]
        public void canAddTargetsQuickly()
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();
            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);
            timer.Stop();
            Debug.WriteLine("Instantiation took " + timer.ElapsedMilliseconds + " ms");

            string testPrefix = Guid.NewGuid().ToString();

            int targetCount = 50;

            // Add some targets and watch the time taken
            timer.Restart();
            int flushesBefore = foo.flushCount;
            iscsiTarget[] targetsAdded = new iscsiTarget[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                targetsAdded[i] = foo.addISCSITarget(new iscsiTarget()
                {
                    targetName = testPrefix + "-" + i
                });
            }
            timer.Stop();
            int flushesAfter = foo.flushCount;
            Debug.WriteLine("Adding " + targetCount + " targets took " + timer.ElapsedMilliseconds + " ms and required " +
                            (flushesAfter - flushesBefore) + " flushes");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(7));

            // Each should be unique by these properties
            try
            {
                foreach (iscsiTarget tgt in targetsAdded)
                {
                    Assert.AreEqual(1, targetsAdded.Count(x => x.id == tgt.id));
                    Assert.AreEqual(1, targetsAdded.Count(x => x.targetName == tgt.targetName));
                }
            }
            catch (AssertFailedException)
            {
                foreach (var tgt in targetsAdded)
                {
                    Debug.WriteLine("Target:");
                    Debug.WriteLine(" id = " + tgt.id);
                    Debug.WriteLine(" targetName = " + tgt.targetName);
                    Debug.WriteLine(" targetAlias = " + tgt.targetAlias);
                }
                throw;
            }

            timer.Restart();
            foo.waitUntilISCSIConfigFlushed();
            timer.Stop();
            Debug.WriteLine("Reloading config took " + timer.ElapsedMilliseconds + " ms");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(7));
        }

        [TestMethod]
        public void canAddExportNewFilesQuickly()
        {
            clearAll();

            string testPrefix = Guid.NewGuid().ToString();
            int extentcount = 50;
            try
            {
                TimeSpan reloadTime = canExportNewFilesQuickly(testPrefix, extentcount);
                Debug.WriteLine("Reloading config took " + reloadTime + " ms");
                Assert.IsTrue(reloadTime < TimeSpan.FromSeconds(7));
            }
            finally
            {
                // TODO: clean up
            }
        }

        public TimeSpan canExportNewFilesQuickly(string testPrefix, int newExportsCount)
        {
            // FIXME: use testprefix for filenames

            Stopwatch timer = new Stopwatch();
            timer.Start();
            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);
            timer.Stop();
            Debug.WriteLine("Instantiation took " + timer.ElapsedMilliseconds + " ms");

            // Make some test files to export
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                for (int i = 0; i < newExportsCount; i++)
                    exec.startExecutable("touch", nastempspace + "/testfile_" + i);
            }
            // Add some targets, extents, target-to-extents, and watch the time taken.
            timer.Restart();
            int flushesBefore = foo.flushCount;
            for (int i = 0; i < newExportsCount; i++)
            {
                iscsiTarget tgt = foo.addISCSITarget(new iscsiTarget() { targetName = testPrefix + "-" + i });
                foo.createTargetGroup(foo.getPortals()[0], tgt);
                iscsiExtent ext = foo.addISCSIExtent(new iscsiExtent()
                {
                    iscsi_target_extent_name = testPrefix + "_" + i,
                    iscsi_target_extent_type = "File",
                    iscsi_target_extent_path = nastempspace + "/testfile_" + i
                });

                foo.addISCSITargetToExtent(tgt.id, ext);
            }
            timer.Stop();
            int flushesAfter = foo.flushCount;
            Debug.WriteLine("Adding " + newExportsCount + " target, extents, and target-to-extent records took " + timer.ElapsedMilliseconds + " ms and required " +
                            (flushesAfter - flushesBefore) + " flushes");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(15));

            timer.Restart();
            foo.waitUntilISCSIConfigFlushed();
            timer.Stop();

            // Check that ctld has exported the files as we asked
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                executionResult res = exec.startExecutable("ctladm", "portlist");
                Assert.AreEqual(0, res.resultCode);
                // Each of our testfiles should appear exactly once in this list.
                string[] lines = res.stdout.Split('\n');
                for (int i = 0; i < newExportsCount; i++)
                {
                    try
                    {
                        Assert.AreEqual(1, lines.Count(x => x.Contains(testPrefix + "-" + i + ",")));
                    }
                    catch (AssertFailedException)
                    {
                        Debug.Write(res.stdout);
                        Debug.Write(res.stderr);
                        throw;
                    }
                }
            }

            return timer.Elapsed;
        }

        [TestMethod]
        public void canExportAFile()
        {
            string testPrefix = Guid.NewGuid().ToString();

            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            // Make a test file to export
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("touch", nastempspace + "/testfile");
            }
            // Add some targets, extents, target-to-extents, and watch the time taken.
            iscsiTarget tgt = foo.addISCSITarget(new iscsiTarget() { targetName = testPrefix, targetAlias = testPrefix });
            foo.createTargetGroup(foo.getPortals()[0], tgt);
            iscsiExtent ext = foo.addISCSIExtent(new iscsiExtent()
            {
                iscsi_target_extent_name = testPrefix,
                iscsi_target_extent_type = "File",
                iscsi_target_extent_path = nastempspace + "/testfile"
            });

            foo.addISCSITargetToExtent(tgt.id, ext);
            foo.waitUntilISCSIConfigFlushed();

            // Check that ctld has exported the file as we asked
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                executionResult res = exec.startExecutable("ctladm", "portlist");
                Assert.AreEqual(0, res.resultCode);

                string[] lines = res.stdout.Split('\n');
                Assert.AreEqual(1, lines.Count(x => x.Contains(testPrefix + ",")));
            }
        }

        [TestMethod]
        public void canInvalidateThings()
        {
            // Here, we use a second FreeNAS instance to modify data on the server. We then invalidate the first, and ensure that
            // the differences are seen.
            string testPrefix = Guid.NewGuid().ToString();

            FreeNASWithCaching uut = new FreeNASWithCaching(nashostname, nasusername, naspassword);
            FreeNAS directNAS = new FreeNAS(nashostname, nasusername, naspassword);

            // Make a test file to export
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("touch", nastempspace + "/testfile");
            }
            // Add a targets, extents, and target-to-extents
            iscsiTarget tgt = directNAS.addISCSITarget(new iscsiTarget() { targetName = testPrefix, targetAlias = "idk lol" });
            directNAS.createTargetGroup(directNAS.getPortals()[0], tgt);
            iscsiExtent ext = directNAS.addISCSIExtent(new iscsiExtent()
            {
                iscsi_target_extent_name = testPrefix,
                iscsi_target_extent_type = "File",
                iscsi_target_extent_path = nastempspace + "/testfile"
            });

            directNAS.addISCSITargetToExtent(tgt.id, ext);
            directNAS.waitUntilISCSIConfigFlushed();

            // The uut should now be out-of-date
            Assert.AreNotEqual(uut.getExtents().Count, directNAS.getExtents().Count);
            Assert.AreNotEqual(uut.getISCSITargets().Count, directNAS.getISCSITargets().Count);
            Assert.AreNotEqual(uut.getTargetToExtents().Count, directNAS.getTargetToExtents().Count);

            // Invalidate the uut and check that it is then up-to-date.
            uut.invalidateExtents();
            Assert.AreEqual(uut.getExtents().Count, directNAS.getExtents().Count);
            uut.invalidateTargets();
            Assert.AreEqual(uut.getISCSITargets().Count, directNAS.getISCSITargets().Count);
            uut.invalidateTargetToExtents();
            Assert.AreEqual(uut.getTargetToExtents().Count, directNAS.getTargetToExtents().Count);
        }

        [TestMethod]
        public void canCloningSnapshots()
        {
            string testPrefix = Guid.NewGuid().ToString();

            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            // Make a test datastore to export
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("zfs", "create -V 10MB test/" + testPrefix);
            }

            snapshot snap = foo.createSnapshot("test/" + testPrefix, "snapshot-of-" + testPrefix);
            Assert.AreEqual(1, foo.getSnapshots().Count(x => x.id == snap.id));
            foo.cloneSnapshot(snap, "test/clone-of-" + testPrefix);
        }

        [TestMethod]
        public void willAllowManyExportsAtOnce()
        {
            clearAll();
            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            //
            // Here we export many files (targets/extents/ttes) from ctld. Eventually, the kernel will hit CTL_MAX_PORTS and things
            // will fail. We ensure that this number is high enough that we are unlikely to have any problems.
            //

            // Make some test files to export
            string testPrefix = Guid.NewGuid().ToString();
            int maxfiles = 10000;
            int maxFilesPossible = 0;
            List<string> uncheckedExports = new List<string>(10);
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("/bin/sh", string.Format("-c " +
                    "'for a in `seq 0 {0}`; do touch {1}/testfile_${{a}}; done'", maxfiles, nastempspace));

                // Add some targets, extents, target-to-extents, and see how many we add before they stop working.
                // This is likely to be 256 for a 'factory' image, or 2048 for an XDL-customised image. Note, though,
                // that if you're using a small root partition on the FreeNAS box (which you should be, imo, because
                // a large amount of space encourages issues like this one to go unnoticed until they subtly degrade
                // performance later - and wear out the media, if root is on sdcard, which it is on store.xd.lan),
                // you may run out of space if collectd is not stopped. You can confirm this by observing that
                // collectd's dir in /var/db/collectd/rrd is huge.

                int preExisting = foo.getTargetToExtents().Count;
                for (int i = preExisting; i < maxfiles; i++)
                {
                    iscsiTarget tgt = foo.addISCSITarget(new iscsiTarget() { targetName = testPrefix + "-" + i });
                    foo.createTargetGroup(foo.getPortals()[0], tgt);
                    iscsiExtent ext = foo.addISCSIExtent(new iscsiExtent()
                    {
                        iscsi_target_extent_name = testPrefix + "_" + i,
                        iscsi_target_extent_type = "File",
                        iscsi_target_extent_path = nastempspace + "/testfile_" + i
                    });

                    foo.addISCSITargetToExtent(tgt.id, ext);

                    uncheckedExports.Add(tgt.targetName);

                    if (i % 100 == 0)
                    {
                        // Every hundred, we reload and see if each has been exported correctly.
                        Stopwatch watch = new Stopwatch();
                        watch.Start();
                        bool limitReached = false;
                        try
                        {
                            foo.waitUntilISCSIConfigFlushed();
                        }
                        catch (nasServerFullException)
                        {
                            // ok cool, we've reached our limit.
                            limitReached = true;
                        }
                        //Thread.Sleep(1000 + (i * 10));
                        watch.Stop();
                        Debug.WriteLine("Flush after adding " + i + " exports: took " + watch.ElapsedMilliseconds + " ms");

                        executionResult res = exec.startExecutable("ctladm", "portlist -f iscsi");
                        Assert.AreEqual(0, res.resultCode);

                        // Each of our unchecked testfiles should appear exactly once in this list.
                        // If not, we know we've reached the limit of what the kernel can support.
                        string[] lines = res.stdout.Split('\n');
                        for (int uncheckedIndex = 0; uncheckedIndex < uncheckedExports.Count; uncheckedIndex++)
                        {
                            var exportToCheck = uncheckedExports[uncheckedIndex];

                            if (lines.Count(x => x.Contains(exportToCheck + ",")) != 1)
                            {
                                // Oh, we've failed to export something!
                                maxFilesPossible = i - uncheckedIndex;
                                maxFilesPossible -= 2; 
                                i = maxfiles;
                                break;
                            }
                        }
                        if (limitReached)
                        {
                            maxFilesPossible = i;
                            break;
                        }
                        uncheckedExports.Clear();
                    }
                }
            }
            // Our XDL build has CTL_MAX_PORTS set to 2048, so this should be very high.
            Assert.IsTrue(maxFilesPossible >= 2048, "maxFilesPossible is " + maxFilesPossible + " which is less than 2048");
        }

        [TestMethod]
        public void extentDeletionImpliesTTEDeletion()
        {
            string testPrefix = Guid.NewGuid().ToString();

            FreeNASWithCaching foo = new FreeNASWithCaching(nashostname, nasusername, naspassword);

            int origExtentCount = foo.getExtents().Count;
            int origTargetCount = foo.getISCSITargets().Count;
            int origTTECount = foo.getTargetToExtents().Count;

            // Make a test file to export
            string filePath = nastempspace + "/" + testPrefix;
            using (SSHExecutor exec = new SSHExecutor(nashostname, nasusername, naspassword))
            {
                exec.startExecutable("touch", filePath);
            }

            iscsiTarget tgt1 = foo.addISCSITarget(new iscsiTarget() { targetName = testPrefix });
            iscsiExtent ext1 = foo.addISCSIExtent(new iscsiExtent()
            {
                iscsi_target_extent_name = testPrefix,
                iscsi_target_extent_type = "File",
                iscsi_target_extent_path = filePath
            });

            iscsiTargetToExtentMapping tte1 = foo.addISCSITargetToExtent(tgt1.id, ext1);

            foo.waitUntilISCSIConfigFlushed();

            foo.deleteISCSIExtent(ext1);

            Assert.AreEqual(origTTECount, foo.getTargetToExtents().Count());
            Assert.AreEqual(origTargetCount + 1, foo.getISCSITargets().Count());
            Assert.AreEqual(origExtentCount, foo.getExtents().Count());
        }

    }
}
