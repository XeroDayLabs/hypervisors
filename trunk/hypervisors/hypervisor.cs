using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Services.Protocols;
using Renci.SshNet;
using Renci.SshNet.Common;
using VMware.Vim;

namespace hypervisors
{
    public abstract class hypervisor : IDisposable
    {
        public abstract void restoreSnapshot();
        public abstract void connect();
        public abstract void powerOn(cancellableDateTime deadline);
        public abstract void powerOff(cancellableDateTime deadline);
        public abstract void WaitForStatus(bool isPowerOn, cancellableDateTime deadline);
        public abstract void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline = null);
        public abstract string getFileFromGuest(string srcpath, cancellableDateTime deadline = null);
        public abstract executionResult startExecutable(string toExecute, string args, string workingdir = null, cancellableDateTime deadline = null);
        public abstract IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null);
        public abstract IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null);
        public abstract IAsyncExecutionResult startExecutableAsyncInteractively(string cmdExe, string args, string workingDir = null);
        public abstract void mkdir(string newDir, cancellableDateTime deadline = null);

        protected virtual void Dispose(bool disposing)
        {
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void powerOn()
        {
            powerOn(new cancellableDateTime());
        }

        public void powerOff()
        {
            powerOff(new cancellableDateTime());
        }

        public void copyDirToGuest(string src, string dest)
        {
            if (!File.Exists(dest))
                mkdir(dest);
            foreach (string srcName in Directory.GetFiles(src))
            {
                copyToGuest(dest + "\\", srcName);
            }
            foreach (string srcName in Directory.GetDirectories(src))
            {
                copyDirToGuest(Path.Combine(dest, Path.GetFileName(srcName)), srcName);
            }
        }

        public static T doWithRetryOnSomeExceptions<T>(Func<SMBExecutor.triedNetworkCallRes<T>> thingtoDo, 
            cancellableDateTime deadline = null, TimeSpan retryDelay = default(TimeSpan))
        {
            if (retryDelay == default(TimeSpan))
                retryDelay = TimeSpan.FromSeconds(1);
            if (deadline == null)
                deadline = new cancellableDateTime();

            while (true)
            {
                SMBExecutor.triedNetworkCallRes<T> res = thingtoDo.Invoke();
                if (!res.retryRequested)
                {
                    if (res.error == null)
                        return res.res;
            
                    if (res.error is VimException)
                    {
                        // This VimException is fatal
                        if (res.error.Message.Contains("are insufficient for the operation") ||
                            res.error.Message.Contains("Permission to perform this operation was denied."))
                            throw res.error;
                        // while others are not.
                        if (!deadline.stillOK)
                            throw res.error;
                    }
                    if (!(res.error is SocketException) &&
                        !(res.error is SoapException)   &&
                        !(res.error is TimeoutException)&& 
                        !(res.error is WebException)    &&
                        !(res.error is hypervisorExecutionException)   &&
                        !(res.error is hypervisorExecutionException_retryable)   &&
                        !(res.error is SshOperationTimeoutException)   &&
                        !(res.error is SshConnectionException)         &&
                        !(res.error is UnauthorizedAccessException) &&
                        !(res.error is COMException)                                )
                    {
                        // An exception of a type we don't anticipate, so throw
                        throw res.error;
                    }

                    // throw if the deadline has passed
                    if (!deadline.stillOK)
                        throw res.error;
                }

                // Otherwise, just retry.
                deadline.doCancellableSleep(retryDelay);
            }
        }

        public static T doWithRetryOnSomeExceptions<T>(Func<T> thingtoDo, cancellableDateTime deadline = null, TimeSpan retryDelay = default(TimeSpan))
        {
            if (retryDelay == default(TimeSpan))
                retryDelay = TimeSpan.FromSeconds(1);
            if (deadline == null)
                deadline = new cancellableDateTime();

            while (true)
            {
                try
                {
                    return thingtoDo.Invoke();
                }
                catch (VimException e)
                {
                    if (e.Message.Contains("are insufficient for the operation") ||
                        e.Message.Contains("Permission to perform this operation was denied."))
                        throw;
                    if (!deadline.stillOK)
                        throw;
                }
                catch (Exception e)
                {
                    // TODO: these should really be wrapped - ie, sshexceptions should only be caught by the sshexecutor, and rethrown as something sensible
                    if (e is SocketException ||
                        e is SoapException ||
                        e is TimeoutException ||
                        e is WebException ||
                        e is hypervisorExecutionException ||
                        e is hypervisorExecutionException_retryable ||
                        e is SshOperationTimeoutException ||
                        e is SshConnectionException ||
                        e is UnauthorizedAccessException || 
                        e is COMException)
                    {
                        if (!deadline.stillOK)
                        {
                            // Oh no, deadline has passed!
                            throw;
                        }
                        // An exception but we're still before the deadline, so wait and retry.
                        deadline.doCancellableSleep(retryDelay);
                    }
                    else
                    {
                        // An exception of a type we don't anticipate, rethrow
                        throw;
                    }
                }
            }
        }

        public static void doWithRetryOnSomeExceptions(System.Action thingtoDo,
            TimeSpan retry = default(TimeSpan) )
        {
            doWithRetryOnSomeExceptions(thingtoDo, new cancellableDateTime(), retry);
        }

        public static void doWithRetryOnSomeExceptions(System.Action thingtoDo, cancellableDateTime deadline, TimeSpan retry = default(TimeSpan) )
        {
            doWithRetryOnSomeExceptions(() =>
            {
                thingtoDo();
                return 0; // Return a dummy value
            }, deadline, retry);
        }

        public void copyToGuestFromBuffer(string dstpath, byte[] srcContents)
        {
            using (temporaryFile tmpFile = new temporaryFile())
            {
                using (FileStream tmpFileStream = new FileStream(tmpFile.filename, FileMode.OpenOrCreate))
                {
                    tmpFileStream.Write(srcContents, 0, srcContents.Length);
                    tmpFileStream.Seek(0, SeekOrigin.Begin);    // ?!
                }
                copyToGuest(dstpath, tmpFile.filename);
            }
        }

        public void copyToGuestFromBuffer(string dstPath, string srcContents)
        {
            copyToGuestFromBuffer(dstPath, Encoding.ASCII.GetBytes(srcContents));
        }

        public void patchFreeNASInstallation()
        {
            // This will install the XDL modifications to the FreeNAS web UI. 
            // It should only be done once, after installation of FreeNAS.

            // TODO: autodetect freenas version?
//            Byte[] patchFile = Properties.Resources.freenas_support_freenas11;
            Byte[] patchFile = Properties.Resources.freenas_support_freenas9;
            // Sometimes Git will change newlines, so be sure we give unix-style newlines when executing
            patchFile = patchFile.Where(x => x != '\r').ToArray();
            copyToGuestFromBuffer("/root/freenas-xdl.patch", patchFile);
            executionResult res = startExecutable("/bin/sh", "-c \"exec /usr/bin/patch --batch --quiet --directory=/usr/local/www < /root/freenas-xdl.patch\"");
            if (res.resultCode != 0)
                throw new executionFailedException(res);

            // Then restart nginx and django, which should both restart OK.
            executionResult nginxRestartRes = startExecutable("service", "nginx restart");
            if (nginxRestartRes.resultCode != 0)
                throw new executionFailedException(nginxRestartRes);
            executionResult djangoRestartRes = startExecutable("service", "django restart");
            if (djangoRestartRes.resultCode != 0)
                throw new executionFailedException(djangoRestartRes);
        }
    }

    public class executionFailedException : Exception
    {
        private string _msg;

        public executionFailedException(executionResult res)
        {
            _msg = String.Format(
                "Execution on remote systemn resulted in error: Return code was {0}, stdout was '{1}', and stderr was '{2}",
                res.resultCode, res.stdout, res.stderr);
        }

        public override string ToString()
        {
            return _msg;
        }
    }

    public class cancellableDateTime
    {
        private DateTime deadline;
        private cancellableDateTime childDeadline = null;
        private bool isCancelled;

        public cancellableDateTime()
        {
            deadline = DateTime.MaxValue;
            isCancelled = false;
        }

        public cancellableDateTime(TimeSpan newTimeout, cancellableDateTime newChildDeadline)
        {
            deadline = DateTime.Now + newTimeout;
            childDeadline = newChildDeadline;
            isCancelled = false;
        }

        public cancellableDateTime(TimeSpan newTimeout)
        {
            deadline = DateTime.Now + newTimeout;
            isCancelled = false;
        }

        public bool stillOK
        {
            get
            {
                if (isCancelled || DateTime.Now > deadline)
                    return false;
                if (childDeadline != null)
                    return childDeadline.stillOK;
                return true;
            }
        }

        public void throwIfTimedOutOrCancelled(string cancellationMessage = null)
        {
            if (!stillOK)
                throw new TimeoutException(cancellationMessage + " @ " + Environment.StackTrace);
            if (childDeadline != null)
                childDeadline.throwIfTimedOutOrCancelled(cancellationMessage);
        }

        public void markCancelled()
        {
            isCancelled = true;
            if (childDeadline != null)
                childDeadline.markCancelled();
        }

        public TimeSpan getRemainingTimespan()
        {
            return deadline - DateTime.Now;
        }

        public void doCancellableSleep(TimeSpan timeToSleep, string cancellationMessage = null)
        {
            DateTime wakeupTime = DateTime.Now + timeToSleep;
            while (true)
            {
                throwIfTimedOutOrCancelled(cancellationMessage);

                if (DateTime.Now > wakeupTime)
                    return;

                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    public interface IAsyncExecutionResult : IDisposable
    {
        executionResult getResultIfComplete();
    }

    public class asyncExecutionResultViaFile : IAsyncExecutionResult
    {
        private readonly string _stdOutFilename;
        private readonly string _stdErrFilename;
        private readonly string _returnCodeFilename;
        private readonly remoteExecution _host;

        public asyncExecutionResultViaFile(remoteExecution host, string stdOutFilename, string stdErrFilename, string returnCodeFilename)
        {
            _host = host;
            _stdOutFilename = stdOutFilename;
            _stdErrFilename = stdErrFilename;
            _returnCodeFilename = returnCodeFilename;
        }

        public asyncExecutionResultViaFile(remoteExecution host, execFileSet fileSet)
        {
            _host = host;
            _stdOutFilename = fileSet.stdOutFilename;
            _stdErrFilename = fileSet.stdErrFilename;
            _returnCodeFilename = fileSet.returnCodeFilename;
        }

        public executionResult getResultIfComplete()
        {
            // Read the return code last. We do this because there's no way in VMWare's guest tools specify file locking, so
            // we may see empty files before they have been written to.
            SMBExecutor.triedNetworkCallRes<string> returnCodeResultInfo = _host.tryGetFileFromGuestWithRes(_returnCodeFilename);
            SMBExecutor.triedNetworkCallRes<string> stdOutResultInfo = _host.tryGetFileFromGuestWithRes(_stdOutFilename);
            SMBExecutor.triedNetworkCallRes<string> stdErrResultInfo = _host.tryGetFileFromGuestWithRes(_stdErrFilename);

            if (returnCodeResultInfo.retryRequested || returnCodeResultInfo.error != null ||
                stdOutResultInfo.retryRequested || stdOutResultInfo.error != null ||
                stdErrResultInfo.retryRequested || stdErrResultInfo.error != null)
            {
                return null;
            }

            int retCode = Int32.Parse(returnCodeResultInfo.res);
            string stdOut = stdOutResultInfo.res;
            string stdErr = stdErrResultInfo.res;

            return new executionResult(stdOut, stdErr, retCode);
        }

        public void Dispose()
        {
        }
    }

    public class executionResult
    {
        public int resultCode;

        public string stdout;

        public string stderr;

        public executionResult(SshCommand src)
        {
            stdout = src.Result;
            stderr = src.Error;
            resultCode = src.ExitStatus;
        }

        public executionResult()
        {
        }

        public executionResult(Process src)
        {
            resultCode = src.ExitCode;
            stderr = src.StandardError.ReadToEnd();
            stdout = src.StandardOutput.ReadToEnd();
        }

        public executionResult(string newStdOut, string newStdErr, int newResultCode)
        {
            stdout = newStdOut;
            stderr = newStdErr;
            resultCode = newResultCode;
        }
    }
}