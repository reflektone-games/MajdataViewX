#nullable enable

using Cysharp.Threading.Tasks;
using System;
using MajProtocol.Generated;
using MajProtocol.Standard;
using MajProtocol.Standard.Packets;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;
using static MajdataViewX.Base.MajCtx;

namespace MajdataViewX
{
    public class WsServer : MonoBehaviour
    {
        // 这里是游戏及游戏外部的初始化
        private void Start()
        {
            QualitySettings.vSyncCount = 1;
            MajWebSocketServer.Start();
        }

        private void OnDestroy() => MajWebSocketServer.Close();
    }

    public class MajWebSocketServer : WebSocketBehavior, IMajProtocolServer
    {
        private const int Port = 8083;

        private static WebSocketServer?         _instance;
        private static WebSocketSessionManager? _majdataSessions;

        public static bool Active => _instance?.IsListening ?? false;

        public static event OptionsRequest.Callback? OnOptions;
        public static event StateRequest.Callback?   OnState;

        public static void Start()
        {
            _instance?.Stop();
            var socket = new WebSocketServer($"ws://0.0.0.0:{Port}");
            socket.AddWebSocketService<MajWebSocketServer>("/majdata");
            socket.Start();
            _instance        = socket;
            _majdataSessions = _instance.WebSocketServices["/majdata"].Sessions;
        }

        public static void Broadcast(byte[] bytes) => _majdataSessions?.Broadcast(bytes);

        protected override void OnMessage(MessageEventArgs e)
        {
            var span = e.RawData.AsSpan();
            ((IMajProtocolServer)this).Handle(span);
        }

        public static void Close()
        {
            _instance?.Stop();
        }

        public void OnHandshakeRequestReceived(HandshakeRequest req)
        {
            if (req.protocolVersion == Properties.ProtocolVersion)
            {
                Send(new HandshakeResponse { accepted = true });
                return;
            }

            Send(new HandshakeResponse { accepted = false });
            Close();
        }

        public void OnFilesRequestReceived(FilesRequest req)
        {
            _playManager.LoadAsync(req.trackPath, req.jacketPath, req.moviePath)
                        .ContinueWith(() => { Send(new FilesResponse { accepted = true }); })
                        .Forget();
        }

        public void OnSimaiRequestReceived(SimaiRequest req)
        {
            _playManager.ParseAsync(req.content, 0, req.offset, string.Empty, string.Empty, string.Empty, string.Empty, 0)
                        .ContinueWith(() => Send(new SimaiResponse { accepted = true }))
                        .Forget();
        }

        public void OnOptionsRequestReceived(OptionsRequest req)
        {
            OnOptions?.Invoke(req);
        }

        public void OnPauseRequestReceived(PauseRequest req)
        {
            _playManager.PauseAsync()
                        .ContinueWith(() => Send(new PauseResponse { accepted = true }))
                        .Forget();
        }

        public void OnPlayRequestReceived(PlayRequest req)
        {
            _playManager.PlayAsync(req.position, req.showIntroAnimation, req.saveRecordingPath)
                        .ContinueWith(() => Send(new PlayResponse { accepted = true }))
                        .Forget();
        }

        public void OnStateRequestReceived(StateRequest req)
        {
            OnState?.Invoke(req);
        }

        public void OnUnloadRequestReceived(UnloadRequest req)
        {
            _playManager.UnloadAsync()
                        .ContinueWith(() => Send(new UnloadResponse { accepted = true }))
                        .Forget();
        }
    }

    /// <summary>
    /// Setting 通道的私有 payload：MajProtocol 没规定 SettingRequest 形状，按决策走 JObject 自由形式。
    /// 客户端约定 <c>requestData</c> 必须包含 <c>viewSetting</c> + <c>volumeSetting</c> 两个属性；
    /// 缺失字段视为默认值。
    /// </summary>
    internal sealed class SettingsPayload
    {
        public MajdataViewX.Types.MajSetting.MajViewSetting?   ViewSetting   { get; set; }
        public MajdataViewX.Types.MajSetting.MajVolumeSetting? VolumeSetting { get; set; }
    }
}
