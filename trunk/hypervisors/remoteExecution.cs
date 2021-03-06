using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public abstract class remoteExecution : IDisposable
    {
        public abstract void mkdir(string newDir, cancellableDateTime deadline = null);
        public abstract void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline = null);
        public abstract string tryGetFileFromGuest(string srcpath, out Exception errorOrNull);
        public abstract IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null);
        public abstract IAsyncExecutionResult startExecutableAsyncInteractively(string cmdExe, string args, string workingDir);
        public abstract void testConnectivity();
        public abstract void deleteFile(string toDelete, cancellableDateTime deadline);
        public abstract void Dispose();

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

        public virtual executionResult startExecutable(string toExecute, string args, string workingDir = null, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(3));

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
                    deadline.doCancellableSleep(TimeSpan.FromSeconds(3));
                }

                while (true)
                {
                    deadline.doCancellableSleep(TimeSpan.FromSeconds(3));

                    executionResult res = resultInProgress.getResultIfComplete();

                    if (res != null)
                        return res;
                }
            }
            finally
            {
                if (resultInProgress != null)
                    resultInProgress.Dispose();
            }
        }

        public void withRetryUntilSuccess(Action action, cancellableDateTime deadline)
        {
            withRetryUntilSuccess<int>(() =>
            {
                action.Invoke();
                return 0;
            }, deadline);
        }

        public T withRetryUntilSuccess<T>(Func<SMBExecutor.triedNetworkCallRes<T>> action, cancellableDateTime deadline)
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
                        !(res.error is IOException) &&
                        !(res.error is VimException))
                    {
                        throw res.error;
                    }
                    if (!deadline.stillOK)
                        throw res.error;
                }

                deadline.doCancellableSleep(TimeSpan.FromSeconds(3));
            }
        }

        public T withRetryUntilSuccess<T>(Func<T> action, cancellableDateTime deadline)
        {
            while (true)
            {
                try
                {
                    return action();
                }
                catch (Win32Exception e)
                {
                    if (e.NativeErrorCode == 86)
                        // Invalid password
                        throw;
                }
                catch (TimeoutException)
                {
                }
                catch (IOException)
                {
                }
                catch (VimException)
                {
                }

                deadline.doCancellableSleep(TimeSpan.FromSeconds(3));

                if (!deadline.stillOK)
                    throw new TimeoutException();
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
            using (temporaryFile payloadBatch = new temporaryFile(".bat"))
            using (temporaryFile launcherTemp = new temporaryFile(".bat"))
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
                launcherTemp.WriteAllText(launchFileContents);
                // And the payload batch.
                payloadBatch.WriteAllText(string.Format("@{0} {1}", toExecute, args));
                // Then, copy them to the guest.
                withRetryUntilSuccess(() => copyToGuest(launcherRemotePath, launcherTemp.filename), new cancellableDateTime(TimeSpan.FromMinutes(10)));
                withRetryUntilSuccess(() => copyToGuest(payloadRemotePath, payloadBatch.filename), new cancellableDateTime(TimeSpan.FromMinutes(10)));

                // Now return info about what files we're going to use, so the caller can.. use them.
                return new execFileSet(stdOutFilename, stdErrFilename, returnCodeFilename, launcherRemotePath);
            }
        }
    }
}