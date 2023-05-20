﻿using System.Net.Sockets;
using GameBase;
using GameProto;
using TEngine;
using CSPkg = GameProto.CSPkg;

namespace GameLogic
{
    public enum GameClientStatus
    {
        StatusInit, //初始化
        StatusReconnect, //重新连接
        StatusClose, //断开连接
        StatusLogin, //登录中
        StatusEnter, //AccountLogin成功，进入服务器了
    }

    public enum CsMsgResult
    {
        NoError = 0,
        NetworkError = 1,
        InternalError = 2,
        MsgTimeOut = 3,
        PingTimeOut = 4,
    }

    //定义消息回报的回调接口
    public delegate void CsMsgDelegate(CsMsgResult result, CSPkg msg);

    /// <summary>
    /// 统计网络协议的接口 
    /// </summary>
    public delegate void CsMsgStatDelegate(int cmdID, int pkgSize);

    public class GameClient : Singleton<GameClient>
    {
        public INetworkChannel Channel;

        private GameClientStatus m_status = GameClientStatus.StatusInit;

        private MsgDispatcher m_dispatcher;

        private ClientConnectWatcher m_connectWatcher;

        private float m_lastLogDisconnectErrTime = 0f;

        private int m_lastNetErrCode = 0;

        public int LastNetErrCode => m_lastNetErrCode;

        public GameClientStatus Status
        {
            get => m_status;
            set => m_status = value;
        }

        public bool IsEntered => m_status == GameClientStatus.StatusEnter;

        /// <summary>
        /// 连续心跳超时
        /// </summary>
        private int m_heatBeatTimeoutNum = 0;

        private int m_ping = -1;

        private float NowTime => GameTime.unscaledTime;

        private string _lastHost = null;
        private int _lastPort = 0;

        public GameClient()
        {
            m_connectWatcher = new ClientConnectWatcher(this);
            m_dispatcher = new MsgDispatcher();
            m_dispatcher.SetTimeout(5f);
            GameEvent.AddEventListener<INetworkChannel,object>(NetworkEvent.NetworkConnectedEvent,OnNetworkConnected);
            GameEvent.AddEventListener<INetworkChannel>(NetworkEvent.NetworkClosedEvent,OnNetworkClosed);
            GameEvent.AddEventListener<INetworkChannel,NetworkErrorCode,SocketError,string>(NetworkEvent.NetworkErrorEvent,OnNetworkError);
            GameEvent.AddEventListener<INetworkChannel,object>(NetworkEvent.NetworkCustomErrorEvent,OnNetworkCustomError);
            Channel = Network.Instance.CreateNetworkChannel("GameClient", ServiceType.Tcp, new NetworkChannelHelper());
        }
        
        private void OnNetworkConnected(INetworkChannel channel, object userdata)
        {
            bool isReconnect = (m_status == GameClientStatus.StatusReconnect);
            //准备登录
            Status = GameClientStatus.StatusLogin;
            // TODO Reconnected
        }

        private void OnNetworkClosed(INetworkChannel channel)
        {

        }

        private void OnNetworkError(INetworkChannel channel, NetworkErrorCode networkErrorCode, SocketError socketError, string errorMessage)
        {
            
        }

        private void OnNetworkCustomError(INetworkChannel channel, object userData)
        {

        }

        public void Connect(string host, int port, bool reconnect = false)
        {
            ResetParam();
            if (!reconnect)
            {
                SetWatchReconnect(false);
            }

            if (reconnect)
            {
                // GameEvent.Get<ICommUI>().ShowWaitUITip(WaitUISeq.LOGINWORLD_SEQID, G.R(TextDefine.ID_TIPS_RECONNECTING));
            }
            else
            {
                // GameEvent.Get<ICommUI>().ShowWaitUI(WaitUISeq.LOGINWORLD_SEQID);
            }

            _lastHost = host;
            _lastPort = port;

            Status = reconnect ? GameClientStatus.StatusReconnect : GameClientStatus.StatusInit;
            
            Channel.Connect(host, port);
        }

        public void Reconnect()
        {
            if (string.IsNullOrEmpty(_lastHost) || _lastPort <= 0)
            {
                // GameModule.UI.ShowTipMsg("Invalid reconnect param");
                return;
            }

            m_connectWatcher.OnReConnect();
            Connect(_lastHost, _lastPort, true);
        }


        public void Shutdown()
        {
            Channel.Close();
            m_status = GameClientStatus.StatusInit;
        }

        public bool SendCsMsg(CSPkg reqPkg)
        {
            if (!IsStatusCanSendMsg(reqPkg.Head.MsgId))
            {
                return false;
            }

            return DoSendData(reqPkg);
        }

        public bool IsStatusCanSendMsg(uint msgId)
        {
            bool canSend = false;
            if (m_status == GameClientStatus.StatusLogin)
            {
                canSend = (msgId == (uint)CSMsgID.CsCmdActLoginReq);
            }

            if (m_status == GameClientStatus.StatusEnter)
            {
                canSend = true;
            }

            if (!canSend)
            {
                float nowTime = NowTime;
                if (m_lastLogDisconnectErrTime + 5 < nowTime)
                {
                    Log.Error("GameClient not connected, send msg failed, msgId[{0}]", msgId);
                    m_lastLogDisconnectErrTime = nowTime;
                }

                //UISys.Mgr.ShowTipMsg(TextDefine.ID_ERR_NETWORKD_DISCONNECT);
            }

            return canSend;
        }

        public bool SendCsMsg(CSPkg reqPkg, uint resCmd, CsMsgDelegate resHandler = null, bool needShowWaitUI = true)
        {
            if (!IsStatusCanSendMsg(reqPkg.Head.MsgId))
            {
                return false;
            }

            var ret = DoSendData(reqPkg);
            if (!ret)
            {
                if (resHandler != null)
                {
                    resHandler(CsMsgResult.InternalError, null);
                }

                m_dispatcher.NotifyCmdError(resCmd, CsMsgResult.InternalError);
            }
            else
            {
                //注册消息
                if (resHandler != null)
                {
                    m_dispatcher.RegSeqHandle(reqPkg.Head.Echo, resCmd, resHandler);
                    if (reqPkg.Head.Echo > 0 && IsWaitingCmd(resCmd) && needShowWaitUI)
                    {
                        // TODO
                        // GameEvent.Get<ICommUI>().ShowWaitUI(reqPkg.Head.Echo);
                    }
                }
            }

            return ret;
        }

        private bool DoSendData(CSPkg reqPkg)
        {
            if (!IsIgnoreLog(reqPkg.Head.MsgId))
            {
                Log.Debug("[c-s] CmdId[{0}]\n{1}", reqPkg.Head.MsgId, reqPkg.Body.ToString());
            }
            var sendRet = Channel.Send(reqPkg);
            return sendRet;
            return true;
        }

        private bool IsIgnoreLog(uint msgId)
        {
            bool ignoreLog = false;
            /*switch (msgId)
            {
                case (uint)CSMsgID.CsCmdHeatbeatReq:
                case (uint)CSMsgID.CsCmdHeatbeatRes:
                    ignoreLog = true;
                    break;
            }*/

            return ignoreLog;
        }

        public static bool IsWaitingCmd(uint msgId)
        {
            //心跳包不需要读条等待
            if (msgId == (uint)CSMsgID.CsCmdHeatbeatRes)
            {
                return false;
            }

            return true;
        }

        private void ResetParam()
        {
            m_lastLogDisconnectErrTime = 0f;
            m_heatBeatTimeoutNum = 0;
            _lastHbTime = 0f;
            m_ping = -1;
            m_lastNetErrCode = 0;
        }

        public void OnUpdate()
        {
            m_dispatcher.Update();
            TickHeartBeat();
            CheckHeatBeatTimeout();
            m_connectWatcher.Update();
        }

        /// <summary>
        /// 设置加密密钥
        /// </summary>
        /// <param name="key"></param>
        public void SetEncryptKey(string key)
        {
        }

        /// <summary>
        /// 设置是否需要监控网络重连。
        /// 登录成功后，开启监控,可以自动重连或者提示玩家重连。
        /// </summary>
        /// <param name="needWatch"></param>
        public void SetWatchReconnect(bool needWatch)
        {
            m_connectWatcher.Enable = needWatch;
        }

        public bool IsNetworkOkAndLogin()
        {
            return m_status == GameClientStatus.StatusEnter;
        }

        #region 心跳处理

        /// <summary>
        /// 最近一次心跳的时间
        /// </summary>
        private float _lastHbTime = 0f;

        /// <summary>
        /// 心跳间隔
        /// </summary>
        private readonly float _heartBeatDurTime = 5;

        /// <summary>
        /// 心跳超时的最大次数
        /// </summary>
        private const int HeatBeatTimeoutMaxCount = 2;

        private bool CheckHeatBeatTimeout()
        {
            if (m_heatBeatTimeoutNum >= HeatBeatTimeoutMaxCount)
            {
                //断开连接
                Shutdown();

                //准备重连
                m_heatBeatTimeoutNum = 0;
                Status = GameClientStatus.StatusClose;
                Log.Error("heat beat detect timeout");
                return false;
            }

            return true;
        }

        void TickHeartBeat()
        {
            if (Status != GameClientStatus.StatusEnter)
            {
                return;
            }

            var nowTime = NowTime;
            if (_lastHbTime + _heartBeatDurTime < nowTime)
            {
                _lastHbTime = nowTime;

                CSPkg heatPkg = ProtobufUtility.BuildCsMsg((int)CSMsgID.CsCmdHeatbeatReq);
                heatPkg.Body.HeatBeatReq = new CSHeatBeatReq { HeatEchoTime = _lastHbTime };
                SendCsMsg(heatPkg, (int)CSMsgID.CsCmdHeatbeatRes, HandleHeatBeatRes);
            }
        }

        void HandleHeatBeatRes(CsMsgResult result, CSPkg msg)
        {
            if (result != CsMsgResult.NoError)
            {
                //如果是超时了，则标记最近收到包的次数
                if (result == CsMsgResult.MsgTimeOut)
                {
                    m_heatBeatTimeoutNum++;
                    Log.Warning("heat beat timeout: {0}", m_heatBeatTimeoutNum);
                }
            }
            else
            {
                var resBody = msg.Body.HeatBeatRes;
                float diffTime = NowTime - resBody.HeatEchoTime;
                m_ping = (int)(diffTime * 1000);
                m_heatBeatTimeoutNum = 0;
            }
        }

        #endregion

        #region Ping值

        /// <summary>
        /// ping值
        /// </summary>
        public int Ping
        {
            get
            {
                if (IsPingValid())
                {
                    return m_ping / 4;
                }
                else
                {
                    return 0;
                }
            }
        }

        public bool IsPingValid()
        {
            if (IsNetworkOkAndLogin())
            {
                return m_ping >= 0;
            }

            return false;
        }

        #endregion
    }
}