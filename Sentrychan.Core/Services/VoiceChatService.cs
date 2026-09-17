using LiteNetLib;
using LiteNetLib.Utils;
using NAudio.Wave;
using Concentus.Enums;
using Concentus.Structs;
using Sentrychan.Core.Interfaces;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Sentrychan.Core.Services;

public class VoiceChatService : IVoiceChatService, INetEventListener
{
    private NetManager? _netManager;
    private NetPacketProcessor _packetProcessor = new();
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private OpusEncoder? _encoder;
    private OpusDecoder? _decoder;
    private CancellationTokenSource? _pollCts;

    public bool IsCapturing { get; private set; }

    public VoiceChatService()
    {
    }

    private void EnsureAudioInitialized()
    {
        if (_encoder != null) return;
        try
        {
#pragma warning disable CS0618
            _encoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _decoder = new OpusDecoder(48000, 1);
#pragma warning restore CS0618
            
            _waveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1));
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveProvider);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Voice audio init failed: {ex.Message}");
        }
    }

    public void StartServer(int port)
    {
        // Feature disabled due to hardware compatibility issues
    }

    public void StartClient(string host, int port)
    {
        // Feature disabled due to hardware compatibility issues
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _netManager?.PollEvents();
                    await Task.Delay(15, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Voice poll error: {ex.Message}");
            }
        }, token);
    }

    public void StartCapture()
    {
        // Feature disabled due to hardware compatibility issues
    }

    public void StopCapture()
    {
        // Feature disabled due to hardware compatibility issues
    }

    private void OnVoiceDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_netManager == null || _encoder == null) return;

        try 
        {
            short[] pcm = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
            
            byte[] encoded = new byte[1275];
#pragma warning disable CS0618
            int len = _encoder.Encode(pcm, 0, 960, encoded, 0, encoded.Length);
#pragma warning restore CS0618
            
            byte[] final = new byte[len];
            Array.Copy(encoded, final, len);
            
            _netManager.SendToAll(NetDataWriter.FromBytes(final, 0, len), DeliveryMethod.Unreliable);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Voice encode error: {ex.Message}");
        }
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        StopCapture();
        _netManager?.Stop();
        _waveOut?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _waveOut?.Dispose();
    }

    // INetEventListener Implementation
    public void OnPeerConnected(NetPeer peer) { }
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (_decoder == null || _waveProvider == null) return;
        try
        {
            byte[] encoded = reader.GetRemainingBytes();
            short[] decoded = new short[960];
#pragma warning disable CS0618
            int len = _decoder.Decode(encoded, 0, encoded.Length, decoded, 0, 960, false);
#pragma warning restore CS0618
            byte[] pcm = new byte[len * 2];
            Buffer.BlockCopy(decoded, 0, pcm, 0, pcm.Length);
            _waveProvider.AddSamples(pcm, 0, pcm.Length);
            if (_waveOut?.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                _waveOut?.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Voice receive error: {ex.Message}");
        }
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndOfPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.AcceptIfKey("VoiceChat");
}
