using System;
using System.Diagnostics;
using System.IO;
using Renci.SshNet;
using VMware.Vim;

namespace hypervisors
{
    public abstract class hypervisor : IDisposable
    {
        public abstract void restoreSnapshotByName();
        public abstract void connect();
        public abstract void powerOn();
        public abstract void powerOff();
        public abstract void copyToGuest(string srcpath, string dstpath);
        public abstract string getFileFromGuest(string srcpath);
        public abstract executionResult startExecutable(string toExecute, string args, string workingdir = null);
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