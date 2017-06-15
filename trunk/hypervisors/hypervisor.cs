using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        public abstract void powerOn(DateTime deadline = default(DateTime));
        public abstract void powerOff(DateTime deadline = default(DateTime));
        public abstract void copyToGuest(string dstpath, string srcpath);
        public abstract string getFileFromGuest(string srcpath, TimeSpan timeout = default(TimeSpan));
        public abstract executionResult startExecutable(string toExecute, string args, string workingdir = null, DateTime deadline = default(DateTime));
        public abstract IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null);
        public abstract IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null);
        public abstract void mkdir(string newDir);

        protected virtual void Dispose(bool disposing)
        {
            
        }


        public void Dispose()
        {
            Dispose(true);
        }

        public void copyDirToGuest(string src, string dest)
        {
            if (!File.Exists(dest))
                mkdir(dest);
            foreach (string srcName in Directory.GetFiles(src))
            {
                copyToGuest(srcName, dest + "\\");
            }
            foreach (string srcName in Directory.GetDirectories(src))
            {
                copyDirToGuest(srcName, Path.Combine(dest, Path.GetFileName(srcName)));
            }
        }


        public static T doWithRetryOnSomeExceptions<T>(Func<T> thingtoDo, TimeSpan retryDelay = default(TimeSpan), TimeSpan timeout = default(TimeSpan))
        {
            if (retryDelay == default(TimeSpan))
                retryDelay = TimeSpan.FromSeconds(1);
            DateTime deadline;
            if (timeout == default(TimeSpan))
            {
                deadline = DateTime.MaxValue;
            }
            else
            {
                deadline = DateTime.Now + timeout;
            }

            while (true)
            {
                try
                {
                    return thingtoDo.Invoke();
                }
                catch (VimException e)
                {
                    if (e.Message.Contains("are insufficient for the operation"))
                        throw;
                    if (DateTime.Now > deadline)
                        throw;
                }
                catch (Exception e)
                {
                    // TODO: these should really be wrapped - ie, sshexceptions should only be caught by the sshexecutor, and rethrown as something sensible
                    if (e is SocketException ||
                        e is SshException ||
                        e is SoapException ||
                        e is TimeoutException ||
                        e is WebException ||
                        e is hypervisorExecutionException ||
                        e is hypervisorExecutionException_retryable)
                    {
                        if (DateTime.Now > deadline)
                        {
                            // Oh no, deadline has passed!
                            throw;
                        }
                        // An exception but we're still before the deadline, so wait and retry.
                        Thread.Sleep(retryDelay);
                    }
                    else
                    {
                        // An exception of a type we don't anticipate, rethrow
                        throw;
                    }
                }
            }
        }


        public static void doWithRetryOnSomeExceptions(System.Action thingtoDo, TimeSpan retry = default(TimeSpan), TimeSpan timeout = default(TimeSpan))
        {
            doWithRetryOnSomeExceptions((() => {
                    thingtoDo();
                    return 0;   // Return a dummy value
                }),
                retry, timeout);
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
            try
            {
                string stdOut = _host.withRetryUntilSuccess(() => _host.getFileFromGuest(_stdOutFilename));
                string stdErr = _host.withRetryUntilSuccess(() => _host.getFileFromGuest(_stdErrFilename));
                string retCodeStr = _host.withRetryUntilSuccess(() => _host.getFileFromGuest(_returnCodeFilename));

                return new executionResult(stdOut, stdErr, Int32.Parse(retCodeStr));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (VimException)
            {
                return null;
            }
            catch (hypervisorExecutionException)
            {
                return null;
            }
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