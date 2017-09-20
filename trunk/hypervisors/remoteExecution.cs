using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public abstract class remoteExecution
    {
        public abstract void mkdir(string newDir);
        public abstract void copyToGuest(string dstpath, string srcpath);
        public abstract string tryGetFileFromGuest(string srcpath, out Exception errorOrNull);
        public abstract IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null);
        public abstract void testConnectivity();
        public abstract void deleteFile(string toDelete);

        public string getFileFromGuest(string srcpath)
        {
            Exception e;
            string toRet = tryGetFileFromGuest(srcpath, out e);
            if (e != null)
                throw e;
            return toRet;
        }

        public SMBExecutor.triedNetworkCallRes<string> tryGetFileFromGuestWithRes(string srcpath)
        {
            Exception e;
            string toRet = tryGetFileFromGuest(srcpath, out e);
            if (e != null)
                return new SMBExecutor.triedNetworkCallRes<string>() {error = e};

            return new SMBExecutor.triedNetworkCallRes<string>() { res = toRet };
        }

        public executionResult startExecutable(string toExecute, string args, string workingDir, DateTime deadline)
        {
            return startExecutable(toExecute, args, workingDir, deadline - DateTime.Now);
        }

        public virtual executionResult startExecutable(string toExecute, string args, string workingDir = null, TimeSpan timeout = default(TimeSpan))
        {
            DateTime deadline;
            if (timeout == default(TimeSpan))
                deadline = DateTime.Now + TimeSpan.FromMinutes(3);
            else
                deadline = DateTime.Now + timeout;

            IAsyncExecutionResult resultInProgress = null;
            try
            {
                while (resultInProgress == null)
                {
                    resultInProgress = startExecutableAsync(toExecute, args, workingDir);
//	FIXME!!!
//     This is causing an awful assembly load failure when it throws :/
//     Disabling the timeout for now because I am waaay behind schedule already
//                if (DateTime.Now > deadline)
//                    throw new hypervisorExecutionException();
                    Thread.Sleep(3);
                }

                while (true)
                {
                    executionResult res = resultInProgress.getResultIfComplete();
                    if (res != null)
                        return res;
                    Thread.Sleep(3);
                }
            }
            finally
            {
                if (resultInProgress != null)
                    resultInProgress.Dispose();
            }
        }

        public void withRetryUntilSuccess(Action action)
        {
            withRetryUntilSuccess<int>(() =>
            {
                action.Invoke();
                return 0;
            });
        }

        public T withRetryUntilSuccess<T>(Func<SMBExecutor.triedNetworkCallRes<T>> action)
        {
            while (true)
            {
                SMBExecutor.triedNetworkCallRes<T> res = action();
                if (!res.retryRequested)
                {
                    if (res.error == null)
                        return res.res;

                    if (!(res.error is Win32Exception) &&
                        !(res.error is TimeoutException) &&
                        !(res.error is VimException))
                    {
                        throw res.error;
                    }
                }

                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }

        public T withRetryUntilSuccess<T>(Func<T> action)
        {
            while (true)
            {
                try
                {
                    return action();
                }
                catch (Win32Exception)
                {
                }
                catch (TimeoutException)
                {
                }
                catch (VimException)
                {
                }
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }

        public IAsyncExecutionResult startExecutableAsyncWithRetry(string toExec, string args, string workingDir)
        {
            IAsyncExecutionResult asyncRes = null;
            while (asyncRes == null)
                asyncRes = startExecutableAsync(toExec, args, workingDir);
            return asyncRes;
        }

        protected execFileSet prepareForExecution(string toExecute, string args, string tempDir)
        {
            // We do this by creating two batch files on the target.
            // The first contains the command we're executing, and the second simply calls the first with redirection to the files
            // we want our output in. This simplifies escaping on the commandline via psexec.
            string payloadBatchfile = Path.GetTempFileName() + ".bat";
            string launcherTempFile = Path.GetTempFileName() + ".bat";
            try
            {
                string launcherRemotePath = tempDir + Guid.NewGuid() + "_launcher.bat";
                string payloadRemotePath = tempDir + Guid.NewGuid() + "_payload.bat";

                // Assemble the 'launcher' file, which calls the payload. We give std/out/err randomly-generated filenames here.
                string stdOutFilename = string.Format(tempDir + "\\hyp_stdout_" + Guid.NewGuid());
                string stdErrFilename = string.Format(tempDir + "\\hyp_stderr_" + Guid.NewGuid());
                string returnCodeFilename = string.Format(tempDir + "\\hyp_retcode_" + Guid.NewGuid());
                string launchFileContents = String.Format(
                    "@call \"{0}\" 1> {2} 2> {3} \r\n" +
                    "@echo %ERRORLEVEL% > {1}", payloadRemotePath, returnCodeFilename, stdOutFilename, stdErrFilename);
                File.WriteAllText(launcherTempFile, launchFileContents);
                // And the payload batch.
                File.WriteAllText(payloadBatchfile, string.Format("@{0} {1}", toExecute, args));
                // Then, copy them to the guest.
                withRetryUntilSuccess(() => copyToGuest(launcherRemotePath, launcherTempFile));
                withRetryUntilSuccess(() => copyToGuest(payloadRemotePath, payloadBatchfile));

                // Now return info about what files we're going to use, so the caller can.. use them.
                return new execFileSet(stdOutFilename, stdErrFilename, returnCodeFilename, launcherRemotePath);
            }
            finally
            {
                // and delete temp files.

                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(2);
                while (true)
                {
                    try
                    {
                        File.Delete(payloadBatchfile);
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        if (deadline < DateTime.Now)
                            throw;
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
                while (true)
                {
                    try
                    {
                        File.Delete(launcherTempFile);
                        break;
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        if (deadline < DateTime.Now)
                            throw;
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
            }
        }
    }
}