using System;
using System.Threading.Tasks;

namespace Sentrychan.Core.Interfaces;

public interface IVoiceChatService : IDisposable
{
    bool IsCapturing { get; }
    void StartCapture();
    void StopCapture();
    void StartClient(string host, int port);
    void StartServer(int port);
    void Stop();
}
