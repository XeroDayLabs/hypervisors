using System;

namespace hypervisors
{
    /// <summary>
    /// This is a hypervisor with a connection spec.
    /// </summary>
    /// <typeparam name="specType"></typeparam>
    public abstract class hypervisorWithSpec<specType> : hypervisor
    {
        public abstract specType getConnectionSpec();

        public hypSpec_withWindbgKernel getBaseConnectionSpec()
        {
            return getConnectionSpec() as hypSpec_withWindbgKernel;
        }

        private Action<specType> disposalCallback = null;

        public void setDisposalCallback(Action<specType> newDisposalCallback)
        {
            disposalCallback = newDisposalCallback;
        }

        protected override void _Dispose()
        {
            if (disposalCallback != null)
                disposalCallback.Invoke(getConnectionSpec());

            base._Dispose();
        }
    }
}