using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Pitaya.NativeImpl
{
    public static class StaticPitayaBindingCS 
    {
        public static PcTransportPlugin[] PcTransportPluginRepo = new PcTransportPlugin[PitayaNativeConstants.PC_TRANSPORT_PLUGIN_SLOT_COUNT];
        static int PcDefaultLogLevel = 0;
        static int PcInitiateded = 0;
        public static CustomAssertDelegate Assert = null;
        public static CustomLogDelegate PcLibLog;
        public static string PcLibPlatformStr = null;
        public static string PcLibClientBuildNumberStr = null;
        public static string PcLibClientVersionStr = null;

        // public static int TrUvTlsSetCaFile(string caFile, string caPath)
        // {
        //     if (instance.ctx) {
        //         int ret = SSL_CTX_load_verify_locations(instance.ctx, ca_file, ca_path);
        //         if (!ret) {
        //             this.PcLibLog(PC_LOG_WARN, "tr_uv_tls_set_ca_file - load verify locations error, cafile: "+ caFile + ", capath: " + caPath);
        //             return PC_RC_ERROR;
        //         }
        //         return PC_RC_OK;
        //     } else {
        //         return PC_RC_ERROR;
        //     }
        // }

        public static void PcClientSetPushHandler(ref PcClient client, PcPushHandlerCallback cb)
        {
            client.PushHandler = cb;
        }

        public static int PcClientAddEvHandler(PcClient client, PcEventCallbackDelegate cb, IntPtr exData, DestructorDelegate destructor)
        {
            PcEventHandler handler = new PcEventHandler();
            int handlerId = 0;

            if (client == null || cb== null) {
                PcLibLog(PitayaNativeConstants.PC_LOG_ERROR, "pc_client_add_ev_handler - invalid args");
                return PitayaNativeConstants.PC_EV_INVALID_HANDLER_ID;
            }

            handler.Queue = new Queue<dynamic>();
            handler.ExData = exData;
            handler.Callback = cb;
            handler.HandlerId = handlerId++;
            handler.Destructor = destructor;

            // pc_mutex_lock(client->handler_mutex);

            // QUEUE_INSERT_TAIL(&client->ev_handlers, &handler->queue);
            client.EventHandlers.Enqueue(handler);
            PcLibLog(PitayaNativeConstants.PC_LOG_INFO, "pc_client_add_ev_handler -" +
                    " add event handler, handler id: " + handler.HandlerId);

            // pc_mutex_unlock(&client->handler_mutex);

            return handler.HandlerId;
        }

        public static void PcUnityLibInit(int logLevel, string caFile, string caPath, CustomAssertDelegate customAssert, string platform, string buildNumber, string version){
            if (customAssert != null) {
                Assert = customAssert;
            }

            PcLibClientInfo clientInfo = new PcLibClientInfo{
                Platform = platform,
                BuildNumber = buildNumber,
                Version = version
            };

            PcDefaultLogLevel = logLevel;

            PcLibInit(CustomLogger.DefaultLog, clientInfo);

            #if PC_NO_UV_TLS_TRANS
                if (caFile || caPath) {
                    // TrUvTlsSetCaFile(caFile, caPath);
                }
            #endif
        }

        public static string PcLibStrdup(string str) {
            if (str == null)
                return null;

            int len = str.Length;
            char[] buf = new char[len + 1];
            str.CopyTo(0, buf, 0, len);
            buf[len] = '\0';

            return new string(buf);
        }

        public static int PcTransportPluginRegister(PcTransportPlugin plugin) {
            int transName;
            if (plugin == null || plugin.TransportName >= PitayaNativeConstants.PC_TRANSPORT_PLUGIN_SLOT_COUNT
                || plugin.TransportName < 0 || plugin.TransportCreate == null || plugin.TransportRelease == null)
                return PitayaNativeConstants.PC_RC_INVALID_ARG;

            transName = plugin.TransportName;
            if (PcTransportPluginRepo[transName] != null)
                PcTransportPluginDeregister(transName);

            PcTransportPluginRepo[transName] = plugin;

            if (plugin.OnRegister != null)
                plugin.OnRegister(plugin);

            return PitayaNativeConstants.PC_RC_OK;
        }

        public static int PcTransportPluginDeregister(int transName)
        {
            PcTransportPlugin tp;
            if (transName >= PitayaNativeConstants.PC_TRANSPORT_PLUGIN_SLOT_COUNT || transName < 0)
                return PitayaNativeConstants.PC_RC_INVALID_ARG;

            tp = PcTransportPluginRepo[transName];

            if (tp != null && tp.OnDeregister != null)
                tp.OnDeregister(tp);

            PcTransportPluginRepo[transName] = null;

            return PitayaNativeConstants.PC_RC_OK;
        }

        public static void PcLibInit(CustomLogDelegate pcLog, PcLibClientInfo clientInfo) {
            if(PcInitiateded == 1) {
                return; // init function already called
            }
            PcInitiateded = 1;

            // pc_mutex_init(&pc__pinned_keys_mutex);
            // pc_lib_clear_pinned_public_keys();

            PcTransportPlugin tp;

            PcLibLog = pcLog != null ? pcLog : CustomLogger.DefaultLog;

            PcLibPlatformStr = clientInfo.Platform != null 
                ? PcLibStrdup(clientInfo.Platform) 
                : PcLibStrdup("desktop");
            PcLibClientBuildNumberStr = clientInfo.BuildNumber != null 
                ? PcLibStrdup(clientInfo.BuildNumber)
                : PcLibStrdup("1");
            PcLibClientVersionStr = clientInfo.Version != null 
                ? PcLibStrdup(clientInfo.Version)
                : PcLibStrdup("0.1");

        // #if PC_NO_DUMMY_TRANS
            tp = StaticDummyTransport.PcTrDummyTransPlugin();
            PcTransportPluginRegister(tp);
            PcLibLog(PitayaNativeConstants.PC_LOG_INFO, "pc_lib_init - register dummy plugin");
        // #endif

        // #if PC_NO_UV_TCP_TRANS
        //     tp = pc_tr_uv_tcp_trans_plugin();
        //     pc_transport_plugin_register(tp);
        //     PcLibLog(PC_LOG_INFO, "pc_lib_init - register tcp plugin");
        // #if PC_NO_UV_TLS_TRANS
        //     tp = pc_tr_uv_tls_trans_plugin();
        //     pc_transport_plugin_register(tp);
        //     PcLibLog(PC_LOG_INFO, "pc_lib_init - register tls plugin");
        // #endif
        //     srand((unsigned int)time(0));
        // #endif
        }

        public static IntPtr PcUnityCreate(bool enableTls, bool enablePoll, bool enableReconnect, int connTimeout) {
            // Assert(connTimeout >= 0);

            PcClientInitResult res = new PcClientInitResult{ReturnCode = 0};
            PcClientConfig config = new PcClientConfig
            {
                ConnTimeout = 30,
                EnableReconn = true,
                ReconnMaxRetry = PitayaNativeConstants.PC_ALWAYS_RETRY,
                ReconnDelay = 2,
                ReconnDelayMax = 30,
                ReconnExpBackoff = 1,
                EnablePolling = false,
                TransportName = PitayaNativeConstants.PC_TR_NAME_UV_TCP,
                DisableCompression = 0,
            };

            if (enableTls) {
                config.TransportName = PitayaNativeConstants.PC_TR_NAME_UV_TLS;
            };

            config.EnablePolling = enablePoll;
            config.EnableReconn = enableReconnect;
            config.ConnTimeout = connTimeout;

            res = PcClientInit(IntPtr.Zero, config);
            if (res.ReturnCode == PitayaNativeConstants.PC_RC_OK) {
                GCHandle handle = GCHandle.Alloc(res.Client);
                IntPtr response = (IntPtr) handle;
                handle.Free();
                return response;
            }

            return IntPtr.Zero;
        } 

        private static PcTransportPlugin PcGetTransportPlugin(int transName)
        {
            if (transName >= PitayaNativeConstants.PC_TRANSPORT_PLUGIN_SLOT_COUNT || transName < 0)
                return null;

            return PcTransportPluginRepo[transName];
        }

        private static PcClientInitResult PcClientInit(IntPtr exData, PcClientConfig config)
        {
            PcClientInitResult res = new PcClientInitResult{ReturnCode = 0};

            res.ReturnCode = PitayaNativeConstants.PC_RC_ERROR;
            res.Client = new PcClient();

            if (config == null) {
                res.Client.Config = new PcClientConfig
                {
                ConnTimeout = 30,
                EnableReconn = true,
                ReconnMaxRetry = PitayaNativeConstants.PC_ALWAYS_RETRY,
                ReconnDelay = 2,
                ReconnDelayMax = 30,
                ReconnExpBackoff = 1,
                EnablePolling = false,
                TransportName = PitayaNativeConstants.PC_TR_NAME_UV_TCP,
                DisableCompression = 0,
                };
            } else {
                res.Client.Config = config;
            }

            // PcTransportPlugin tp = PcGetTransportPlugin(res.Client.Config.TransportName);
            PcTransportPlugin tp = PcGetTransportPlugin(7);
            if (tp == null) {
                PcLibLog(PitayaNativeConstants.PC_LOG_ERROR, "pc_client_init - no registered transport plugin found, transport plugin: " + config.TransportName);
                // IntPtr clientPtr = GetObjectPointer(res.Client);
                // PcLogger.PcLibFree(clientPtr);
                res.Client = null;
                res.ReturnCode = PitayaNativeConstants.PC_RC_NO_TRANS;
                return res;
            }

            // Assert(tp.TransportCreate != null);
            // Assert(tp.TransportRelease != null);

            PcTransport trans = tp.TransportCreate(tp);
            if (trans == null) {
                PcLibLog(PitayaNativeConstants.PC_LOG_ERROR, "pc_client_init - create transport error");
                // IntPtr clientPtr = GetObjectPointer(res.Client);
                // PcLogger.PcLibFree(clientPtr);
                res.Client = null;
                res.ReturnCode = PitayaNativeConstants.PC_RC_ERROR;
                return res;
            }

            res.Client.Transport = trans;

            // Assert(res.Client.Transport.Init != null);

            if (res.Client.Transport.Init(res.Client.Transport, res.Client) == null) {
                PcLibLog(PitayaNativeConstants.PC_LOG_ERROR, "pc_client_init - init transport error");
                tp.TransportRelease(tp, trans);
                // pc_lib_free(res.Client);
                res.Client = null;
                res.ReturnCode = PitayaNativeConstants.PC_RC_ERROR;
                return res;
            }

            res.Client.StateMutex = new PcMutex();

            res.Client.ExtraData = exData;

            res.Client.HandlerMutex = new PcMutex();
            res.Client.EventHandlers = new Queue<PcEventHandler>();

            res.Client.RequestMutex = new PcMutex();
            res.Client.NotifyMutex = new PcMutex();

            res.Client.RequestQueue = new Queue<PcRequest>();
            res.Client.NotificationQueue = new Queue<PcNotify>();

            res.Client.SequenceNumber = 0;
            res.Client.RequestIdSequence = 1;

            // memset(&res.client->requests[0], 0, sizeof(pc_request_t) * PC_PRE_ALLOC_REQUEST_SLOT_COUNT);
            // memset(&res.client->notifies[0], 0, sizeof(pc_notify_t) * PC_PRE_ALLOC_NOTIFY_SLOT_COUNT);

            for (int i = 0; i < PitayaNativeConstants.PC_PRE_ALLOC_REQUEST_SLOT_COUNT; i++) {
                res.Client.Requests[i] = new PcRequest();
                res.Client.Requests[i].Base = new PcCommonRequest();

                res.Client.Requests[i].Base.Queue = new Queue<PcRequest>();
                res.Client.Requests[i].Base.Client = res.Client;
                res.Client.Requests[i].Base.Type = PitayaNativeConstants.PC_REQ_TYPE_REQUEST | PitayaNativeConstants.PC_PRE_ALLOC_ST_IDLE | PitayaNativeConstants.PC_PRE_ALLOC;
            }

            for (int i = 0; i < PitayaNativeConstants.PC_PRE_ALLOC_NOTIFY_SLOT_COUNT; i++) {
                res.Client.Notifications[i] = new PcNotify();
                res.Client.Notifications[i].Base = new PcCommonRequest();

                res.Client.Notifications[i].Base.Queue = new Queue<PcRequest>();
                res.Client.Notifications[i].Base.Client = res.Client;
                res.Client.Notifications[i].Base.Type = PitayaNativeConstants.PC_REQ_TYPE_NOTIFY | PitayaNativeConstants.PC_PRE_ALLOC_ST_IDLE | PitayaNativeConstants.PC_PRE_ALLOC;
            }

            res.Client.EventMutex = new PcMutex();
            if (res.Client.Config.EnablePolling) {
                res.Client.PendingEventQueue = new Queue<dynamic>();

                // memset(&res.client->pending_events[0], 0, sizeof(pc_event_t) * PC_PRE_ALLOC_EVENT_SLOT_COUNT);

                for (int i = 0; i < PitayaNativeConstants.PC_PRE_ALLOC_EVENT_SLOT_COUNT; i++) {
                    PcEvent pendingEvent = new PcEvent();
                    res.Client.PendingEvents[i].Queue = new Queue<dynamic>();
                    res.Client.PendingEvents[i].Type = PitayaNativeConstants.PC_PRE_ALLOC_ST_IDLE | PitayaNativeConstants.PC_PRE_ALLOC;
                }
            }

            res.Client.IsInPoll = false;
            res.Client.State = PitayaNativeConstants.PC_ST_INITED;
            res.ReturnCode = PitayaNativeConstants.PC_RC_OK;

            // pc_lib_log(PitayaNativeConstants.PC_LOG_DEBUG, "pc_client_init - init ok");
            return res;
        }

    }
}