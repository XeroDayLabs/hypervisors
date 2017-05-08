using System;

namespace hypervisors
{
    public class refCount<T> : IDisposable where T: IDisposable
    {
        private int refCnt = 1;
        private T _tgt;

        public T tgt
        {
            get
            {
                if (refCnt == 0)
                    throw new ObjectDisposedException("target");

                return _tgt;
            }
        }

        public refCount(T toCount)
        {
            _tgt = toCount;
        }

        public void addRef()
        {
            refCnt++;
        }

        public void Dispose()
        {
            lock (this)
            {
                if (refCnt == 0)
                    throw new ObjectDisposedException("target");

                if (refCnt == 1)
                    _tgt.Dispose();
                
                refCnt--;
            }
        }
    }
}