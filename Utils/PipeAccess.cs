using System;
using System.IO.Pipes;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace HRpc.Utils
{
    /// <summary>
    /// Creates pipe streams, optionally restricted to the current user. On net8.0+ this is
    /// <see cref="PipeOptions"/>.CurrentUserOnly. net48 has no such flag, so the server gets an
    /// ACL that only grants the current user and the client checks the pipe's owner, which is
    /// what CurrentUserOnly does on Windows in modern .NET.
    /// </summary>
    internal static class PipeAccess
    {
        public static NamedPipeServerStream CreateServer(string pipeName, bool currentUserOnly)
        {
#if NETFRAMEWORK
            if (currentUserOnly)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                security.SetOwner(identity.User);
                return new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    security);
            }

            var options = PipeOptions.Asynchronous;
#else
            var options = PipeOptions.Asynchronous | (currentUserOnly ? PipeOptions.CurrentUserOnly : PipeOptions.None);
#endif
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                options);
        }

        public static NamedPipeClientStream CreateClient(string pipeName, bool currentUserOnly)
        {
#if NETFRAMEWORK
            var options = PipeOptions.Asynchronous;
#else
            var options = PipeOptions.Asynchronous | (currentUserOnly ? PipeOptions.CurrentUserOnly : PipeOptions.None);
#endif
            return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, options);
        }

        /// <summary>
        /// net48 only: throws <see cref="UnauthorizedAccessException"/> unless the connected pipe
        /// is owned by the current user. A no-op on net8.0+, where the connect already checked.
        /// </summary>
        public static void VerifyServerOwner(NamedPipeClientStream client, bool currentUserOnly)
        {
#if NETFRAMEWORK
            if (!currentUserOnly)
            {
                return;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var owner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner == null || !owner.Equals(identity.User))
            {
                throw new UnauthorizedAccessException("The pipe server is not owned by the current user.");
            }
#endif
        }
    }
}
