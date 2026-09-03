using System;

namespace LiveDrive.Helpers
{
    public sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public DelegateProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
