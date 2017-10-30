using System;
using System.IO;
using System.Threading;

namespace hypervisors
{
    public class temporaryFile : IDisposable
    {
        public string filename { get; private set;  }

        public temporaryFile()
        {
            filename = Path.GetTempFileName();
        }

        public temporaryFile(string fileExtension)
        {
            filename = Path.GetTempFileName() + ".bat";
        }

        public void Dispose()
        {
            // Delete with a retry, in case something (eg windows defender) has started using the file.
            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(3);

            while (true)
            {
                try
                {
                    File.Delete(filename);
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
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }            
        }

        public void WriteAllText(string contents)
        {
            File.WriteAllText(filename, contents);
        }
    }
}