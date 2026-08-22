using System;

namespace HRpc.Utils
{
    /// <summary>
    /// Invokes each subscriber on an event's invocation list individually, isolating one
    /// throwing subscriber from the rest. A plain <c>handler?.Invoke(...)</c> call aborts the
    /// remaining delegates in the list the moment one of them throws; walking
    /// <see cref="Delegate.GetInvocationList"/> and guarding each call individually means a bug
    /// in one consumer's handler can't suppress delivery to another consumer's handler on the
    /// same event.
    /// </summary>
    internal static class SafeInvoke
    {
        public static void EachHandler<TEventArgs>(
            EventHandler<TEventArgs>? handler,
            object sender,
            TEventArgs args,
            Action<Exception> onError)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler<TEventArgs>)subscriber).Invoke(sender, args);
                }
                catch (Exception ex)
                {
                    onError(ex);
                }
            }
        }
    }
}
