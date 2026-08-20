using System;

namespace HRpc.Events
{
    public class ErrorEventArgs : EventArgs
{
    public string Message { get;  }
    public Exception? Ex { get;  }  

    public ErrorEventArgs(string message, Exception? ex = null)
    {
        Message = message;
        Ex = ex;
    }
}

}
