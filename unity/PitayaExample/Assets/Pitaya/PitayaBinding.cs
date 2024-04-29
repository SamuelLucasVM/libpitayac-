using System;
using System.IO;
using System.Text;
using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using Pitaya;
using Pitaya.SimpleJson;
using Pitaya.NativeImpl;
using Protos;

//typedef void (*request_callback)(pc_client_t* client, unsigned int cbid, const char* resp);
using NativeRequestCallback = System.Action<System.IntPtr, uint, System.IntPtr>;
// typedef void (*request_error_callback)(pc_client_t* client, unsigned int cbid, pc_error_t* error);
using NativeErrorCallback = System.Action<System.IntPtr, uint, System.IntPtr>;

using NativeNotifyCallback = System.Action<System.IntPtr, System.IntPtr>;


internal delegate void NativeEventCallback(IntPtr client, int ev, IntPtr exData, IntPtr arg1Ptr, IntPtr arg2Ptr);

internal delegate void NativePushCallback(IntPtr client, IntPtr route, IntPtr payloadBuffer);

internal delegate void NativeAssertCallback(IntPtr e, IntPtr file, int line);

public delegate void NativeLogFunction(PitayaLogLevel level, string msg);

#pragma warning disable 649
internal struct PitayaBindingError
{
    public int Code;
    public PitayaBuffer Buffer;
    public int Uvcode;
}

internal struct PitayaBuffer
{
    public IntPtr Data;
    public long Len;
}
#pragma warning restore 649

namespace Pitaya
{
    public interface IPitayaListener
    {
        void OnRequestResponse(uint rid, byte[] body);
        void OnRequestError(uint rid, PitayaError error);
        void OnNetworkEvent(PitayaNetWorkState state, NetworkError error);
        void OnUserDefinedPush(string route, byte[] serializedBody);
    }

    public interface IPitayaBinding
    {
        IPitayaQueueDispatcher QueueDispatcher { get; set; }
        PcClient CreateClient(bool enableTls, bool enablePolling, bool enableReconnect, int connTimeout, IPitayaListener listener);
        void Connect(PcClient client, string host, int port, string handshakeOpts);
        void Disconnect(PcClient client);
        void SetCertificateName(string name);
        void SetCertificatePath(string path);
        void Request(PcClient client, string route, byte[] msg, uint reqtId, int timeout);
        void Notify(PcClient client, string route, byte[] msg, int timeout);
        int Quality(PcClient client);
        PitayaClientState State(PcClient client);
        void Dispose(PcClient client);
        ProtobufSerializer.SerializationFormat ClientSerializer(PcClient client);
        void AddPinnedPublicKeyFromCertificateString(string caString);
        void AddPinnedPublicKeyFromCertificateFile(string name);
        void SkipKeyPinCheck(bool shouldSkip);
        void ClearPinnedPublicKeys();
    }

    public class PitayaBinding : IPitayaBinding
    {
        public IPitayaQueueDispatcher QueueDispatcher
        {
            get => StaticPitayaBinding.QueueDispatcher;
            set => StaticPitayaBinding.QueueDispatcher = value;
        }

        public PcClient CreateClient(bool enableTls, bool enablePolling, bool enableReconnect, int connTimeout, IPitayaListener listener) { return StaticPitayaBinding.CreateClient(enableTls, enablePolling, enableReconnect, connTimeout, listener); }
        public void SetLogLevel(PitayaLogLevel logLevel) { StaticPitayaBinding.SetLogLevel(logLevel); }
        public void Connect(PcClient client, string host, int port, string handshakeOpts) { StaticPitayaBinding.Connect(client, host, port, handshakeOpts); }
        public void Disconnect(PcClient client) { StaticPitayaBinding.Disconnect(client); }
        public void SetCertificateName(string name) { StaticPitayaBinding.SetCertificateName(name); }
        public void SetCertificatePath(string path) { StaticPitayaBinding.SetCertificatePath(path); }
        public void Request(PcClient client, string route, byte[] msg, uint reqtId, int timeout) { StaticPitayaBinding.Request(client, route, msg, reqtId, timeout); }
        public void Notify(PcClient client, string route, byte[] msg, int timeout) { StaticPitayaBinding.Notify(client, route, msg, timeout); }
        public int Quality(PcClient client) { return StaticPitayaBinding.Quality(client); }
        public PitayaClientState State(PcClient client) { return StaticPitayaBinding.State(client); }
        public void Dispose(PcClient client) { StaticPitayaBinding.Dispose(client); }
        public ProtobufSerializer.SerializationFormat ClientSerializer(PcClient client) { return StaticPitayaBinding.ClientSerializer(client); }
        public void AddPinnedPublicKeyFromCertificateString(string caString) { StaticPitayaBinding.AddPinnedPublicKeyFromCertificateString(caString); }
        public void AddPinnedPublicKeyFromCertificateFile(string name) { StaticPitayaBinding.AddPinnedPublicKeyFromCertificateFile(name); }
        public void SkipKeyPinCheck(bool shouldSkip) { StaticPitayaBinding.SkipKeyPinCheck(shouldSkip); }
        public void ClearPinnedPublicKeys() { StaticPitayaBinding.ClearPinnedPublicKeys(); }
    }

    public static class StaticPitayaBinding
    {
        private static readonly NativeNotifyCallback NativeNotifyCallback;
        private static readonly RequestCallback NativeRequestCallback;
        private static readonly PcEventCallbackDelegate NativeEventCallback;
        private static readonly PcPushHandlerCallbackDelegate NativePushCallback;
        private static readonly RequestErrorCallback NativeErrorCallback;

        private static readonly Dictionary<PcClient, WeakReference> Listeners = new Dictionary<PcClient, WeakReference>();
        private static readonly Dictionary<PcClient, int> EventHandlersIds = new Dictionary<PcClient, int>();
        private static PitayaLogLevel _currentLogLevel = PitayaLogLevel.Disable;
        private static bool IsNativeLibInitialized;
        
        public static IPitayaQueueDispatcher QueueDispatcher { get; set; } = new NullPitayaQueueDispatcher();

        private static void DLog(object data)
        {
            if (_currentLogLevel != PitayaLogLevel.Disable)
            {
                Debug.Log(data);
            }
        }

        static StaticPitayaBinding()
        {
            NativeRequestCallback = OnRequest;
            NativeEventCallback = OnEvent;
            NativePushCallback = OnPush;
            NativeNotifyCallback = OnNotify;
            NativeErrorCallback = OnError;
            
            SetLogFunction(LogFunction);
#if UNITY_ANDROID
            InitializeNativeLib();
#endif
        }

        public static void SetLogFunction(NativeLogFunction fn)
        {
            int rc = NativeInitLogFunction(fn);
            if (rc != 0)
            {
                throw new Exception("Cannot initialize log function");
            }
        }

        private static string BuildNumber()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer:
                    return _PitayaGetCFBundleVersion();
                case RuntimePlatform.Android:
                    return _AndroidBuildNumber();
                default:
                    return "1";
            }
        }
        
        private static string _AndroidBuildNumber()
        {
            var contextCls = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            var context = contextCls.GetStatic<AndroidJavaObject>("currentActivity");
            var packageMngr = context.Call<AndroidJavaObject>("getPackageManager");
            var packageName = context.Call<string>("getPackageName");
            var packageInfo = packageMngr.Call<AndroidJavaObject>("getPackageInfo", packageName, 0);
            return (packageInfo.Get<int>("versionCode")).ToString();
        }

        private static string Platform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                    return "android";
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                    return "linux";
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                    return "windows";
                case RuntimePlatform.IPhonePlayer:
                    return "ios";
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    return "mac";
                default:
                    return Application.platform.ToString();
            }
        }
        
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnEditorInitialize()
        {
            if (Application.isEditor)
            {
#if !UNITY_ANDROID
            InitializeNativeLib();
#endif
                NativeLibUpdateClientInfo(Platform(), BuildNumber(), Application.version);
            }
        }

        private static void InitializeNativeLib()
        {
            StaticPitayaBindingCS.PcUnityLibInit((int)_currentLogLevel, null, null, OnAssert, Platform(), BuildNumber(), Application.version);
            IsNativeLibInitialized = true;
        }
        
        public static PcClient CreateClient(bool enableTls, bool enablePolling, bool enableReconnect, int connTimeout, IPitayaListener listener)
        {
            if (!IsNativeLibInitialized) InitializeNativeLib();
            
            var client = StaticPitayaBindingCS.PcUnityCreate(enableTls, enablePolling, enableReconnect, connTimeout);
            if (client == null)
            {
                throw new Exception("Fail to create a client");
            }

            var handlerId = StaticPitayaBindingCS.PcClientAddEvHandler(client, NativeEventCallback, IntPtr.Zero, null);
            StaticPitayaBindingCS.PcClientSetPushHandler(ref client, NativePushCallback);
            Listeners[client] = new WeakReference(listener);
            EventHandlersIds[client] = handlerId;

            return client;
        }

        public static void SetLogLevel(PitayaLogLevel logLevel)
        {
            NativeLibSetLogLevel((int)logLevel);
            _currentLogLevel = logLevel;
        }

        public static void Connect(PcClient client, string host, int port, string handshakeOpts)
        {
            // TODO - LoadTest

            // CheckClient(client);
            // var opts = string.IsNullOrEmpty(handshakeOpts) ? null : handshakeOpts;

            // switch (NativeConnect(client, host, port, opts))
            // {
            //     case PitayaConstants.PcRcOk:
            //         return;
            //     case PitayaConstants.PcRcInvalidJson:
            //         throw new Exception("Cannot connect: invalid handshake options json data");
            //     default:
            //         throw new Exception("Error when Connect was called");
            // }
        }

        public static void Disconnect(PcClient client)
        {
            // TODO - LoadTest

            // CheckClient(client);
            // NativeDisconnect(client);
        }

        public static void SetCertificateName(string name)
        {
            var certPath = FindCertPathFromName(name);
            NativeSetCertificatePath(certPath, null);
        }
        
        public static void SetCertificatePath(string path)
        {
            NativeSetCertificatePath(path, null);
        }

        public static void Request(PcClient client, string route, byte[] msg, uint reqtId, int timeout)
        {
            // TODO - LoadTest

            // var length = 0;
            // if (msg != null)
            //     length = msg.Length;

            // var rc = NativeBinaryRequest(client, route, msg, length, reqtId, timeout, NativeRequestCallback, NativeErrorCallback);

            // if (rc != PitayaConstants.PcRcOk)
            // {
            //     var rcStr = RcToStr(rc);
            //     DLog(string.Format("request - failed to perform request {0}", rcStr));

            //     WeakReference reference;
            //     if (!Listeners.TryGetValue(client, out reference) || !reference.IsAlive) return;
            //     var listener = reference.Target as IPitayaListener;
            //     if (listener != null) listener.OnRequestError(reqtId, new PitayaError(rcStr, "Failed to send request"));
            // }
        }

        public static void Notify(PcClient client, string route, byte[] msg, int timeout)
        {
            // TODO - LoadTest

            // var length = 0;
            // if (msg != null)
            //     length = msg.Length;

            // NativeBinaryNotify(client, route, msg, length, IntPtr.Zero, timeout, NativeNotifyCallback);
        }

        public static int Quality(PcClient client)
        {
            CheckClient(client);
            return StaticPitayaBindingCS.PcClientConnQuality(client);
        }

        public static PitayaClientState State(PcClient client)
        {
            CheckClient(client);
            return (PitayaClientState)StaticPitayaBindingCS.PcClientState(client);
        }

        public static void Dispose(PcClient client)
        {
            // TODO - LoadTest

            // NativeRemoveEventHandler(client, EventHandlersIds[client]);

            // Listeners.Remove(client);
            // EventHandlersIds.Remove(client);

            // NativeDestroy(client);
        }

        public static ProtobufSerializer.SerializationFormat ClientSerializer(PcClient client)
        {
            var serializer = StaticPitayaBindingCS.PcClientSerializer(client);
            // NativeFreeSerializer(nativeSerializer);
            return PitayaConstants.SerializerJson.Equals(serializer) ? ProtobufSerializer.SerializationFormat.Json : ProtobufSerializer.SerializationFormat.Protobuf;
        }

        public static void AddPinnedPublicKeyFromCertificateString(string caString)
        {
            var rc = NativeAddPinnedPublicKeyFromCertificateString(caString);

            if (rc != PitayaConstants.PcRcOk)
            {
                throw new Exception(string.Format("AddPineedPublicKeyFromCertificateString: {0}", RcToStr(rc)));
            }

            SkipKeyPinCheck(false);
        }

        public static void AddPinnedPublicKeyFromCertificateFile(string name)
        {
            var certPath = FindCertPathFromName(name);
            int rc = NativeAddPinnedPublicKeyFromCertificateFile(certPath);

            if (rc != PitayaConstants.PcRcOk)
            {
                throw new Exception(string.Format("AddPineedPublicKeyFromCertificateFile: {0}", RcToStr(rc)));
            }

            SkipKeyPinCheck(false);
        }

        public static void SkipKeyPinCheck(bool shouldSkip)
        {
            NativeSkipKeyPinCheck(shouldSkip);
        }

        public static void ClearPinnedPublicKeys()
        {
            NativeClearPinnedPublicKeys();
        }

        //--------------------HELPER METHODS----------------------------------//
        private static string EvToStr(int ev)
        {
            return Marshal.PtrToStringAnsi(NativeEvToStr(ev));
        }

        private static string RcToStr(int rc)
        {
            return Marshal.PtrToStringAnsi(NativeRcToStr(rc));
        }

        private static string FindCertPathFromName(string name)
        {
            string certPath = Path.Combine(Application.streamingAssetsPath, name);

            if (Application.platform == RuntimePlatform.Android)
            {
                var persistentPath = Path.Combine(Application.persistentDataPath, name);

                if (!PlayerPrefs.HasKey(persistentPath))
                {
                    var reader = new WWW(certPath);
                    while (!reader.isDone) { }
                    System.IO.File.WriteAllBytes(persistentPath, reader.bytes);
                    PlayerPrefs.SetInt(persistentPath, 1);
                }

                certPath = persistentPath;
            }

            return certPath;
        }

        private static PitayaError CreatePitayaError(PcError pcError, ProtobufSerializer.SerializationFormat format)
        {
            var rawData = new byte[pcError.Payload.Length];
            Array.Copy(pcError.Payload.Base, rawData, (int)pcError.Payload.Length);

            if (format == ProtobufSerializer.SerializationFormat.Protobuf)
            {
                Error error = new ProtobufSerializer(format).Decode<Error>(rawData);
                return new PitayaError(error.Code, error.Msg, error.Metadata);
            }

            var jsonStr = Encoding.UTF8.GetString(rawData);
            var json = SimpleJson.SimpleJson.DeserializeObject<Dictionary<string, object>>(jsonStr);

            var code = (string)json["code"];
            var msg = (string)json["msg"];

            Dictionary<string, string> metadata;
            if (json.ContainsKey("metadata"))
            {
                metadata = (Dictionary<string, string>)SimpleJson.SimpleJson.CurrentJsonSerializerStrategy.DeserializeObject(json["metadata"],
                    typeof(Dictionary<string, string>), new Dictionary<string, string>());
            }
            else
            {
                metadata = new Dictionary<string, string>();
            }

            return new PitayaError(code, msg, metadata);
        }

        //-------------------------PRIVATE METHODS------------------------------//
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        private static void CheckClient(PcClient client)
        {
            if (client == null)
                throw new NullReferenceException("invalid client");
        }

        //-----------------------NATIVE CALLBACKS-------------------------------//
        [MonoPInvokeCallback(typeof(NativeAssertCallback))]
        private static void OnAssert(IntPtr e, IntPtr file, int line)
        {
            var eName = Marshal.PtrToStringAnsi(e);
            var fileName = Marshal.PtrToStringAnsi(file);

            Debug.LogAssertion(string.Format("{0}:{1} Failed assertion {2}", fileName, line, eName));
        }

        [MonoPInvokeCallback(typeof(NativeErrorCallback))]
        private static void OnError(PcClient client, uint rid, PcError errBinding)
        {
            PitayaError error;

            if (errBinding.Code == PitayaConstants.PcRcServerError)
            {
                error = CreatePitayaError(errBinding, ClientSerializer(client));
            }
            else
            {
                var code = RcToStr(errBinding.Code);
                error = new PitayaError(code, "Internal Pitaya error");
            }

            QueueDispatcher.Dispatch(() =>
            {
                WeakReference reference;
                if (!Listeners.TryGetValue(client, out reference) || !reference.IsAlive) return;
                var listener = reference.Target as IPitayaListener;
                if (listener != null) listener.OnRequestError(rid, error);
            });
        }

        [MonoPInvokeCallback(typeof(NativeRequestCallback))]
        private static void OnRequest(PcClient client, uint rid, PcBuffer buffer)
        {
            var rawData = new byte[buffer.Length];
            Array.Copy(buffer.Base, rawData, (int)buffer.Length);

            QueueDispatcher.Dispatch(() =>
            {
                WeakReference reference;
                if (!Listeners.TryGetValue(client, out reference) || !reference.IsAlive) return;
                var listener = reference.Target as IPitayaListener;
                if (listener != null) listener.OnRequestResponse(rid, rawData);
            });
        }


        [MonoPInvokeCallback(typeof(NativeNotifyCallback))]
        private static void OnNotify(IntPtr req, IntPtr error)
        {
            var errBinding = (PitayaBindingError)Marshal.PtrToStructure(error, typeof(PitayaBindingError));
            DLog(string.Format("OnNotify | rc={0}", RcToStr(errBinding.Code)));
        }

        [MonoPInvokeCallback(typeof(PcPushHandlerCallbackDelegate))]
        private static void OnPush(PcClient client, string route, PcBuffer buffer)
        {
            var rawData = new byte[buffer.Length];
            Array.Copy(buffer.Base, rawData, (int)buffer.Length);

            WeakReference reference;

            if (!Listeners.TryGetValue(client, out reference) || !reference.IsAlive)
            {
                DLog(string.Format("OnEvent - no listener fond for client ev={0}", client));
                return;
            }

            var listener = reference.Target as IPitayaListener;
            QueueDispatcher.Dispatch(() =>
            {
                if (listener != null) listener.OnUserDefinedPush(route, rawData);
            });
        }

        [MonoPInvokeCallback(typeof(PcEventCallbackDelegate))]
        private static void OnEvent(PcClient client, int evType, IntPtr exData, string arg1, string arg2)
        {
            DLog(string.Format("OnEvent - pinvoke callback START | ev={0} client={1}", evType, client));
            if (arg1 != null)
            {
                DLog(string.Format("OnEvent - msg={0}", arg1));
            }

            WeakReference reference;

            if (!Listeners.TryGetValue(client, out reference) || !reference.IsAlive)
            {
                DLog(string.Format("OnEvent - no listener fond for client ev={0}", client));
                return;
            }

            var listener = reference.Target as IPitayaListener;

            QueueDispatcher.Dispatch(() =>
            {
                switch (evType)
                {
                    case PitayaConstants.PcEvConnected:
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Connected, null);
                        break;
                    case PitayaConstants.PcEvConnectError:
                    {
                        string error = arg1;
                        string description = arg2;
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.FailToConnect, new NetworkError(error, description));
                        break;
                    }
                    case PitayaConstants.PcEvConnectFailed:
                    {
                        string error = arg1;
                        string description = arg2;
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.FailToConnect, new NetworkError(error, description));
                        break;
                    }
                    case PitayaConstants.PcEvDisconnect:
                    {
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Disconnected, null);
                        break;
                    }
                    case PitayaConstants.PcEvKickedByServer:
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Kicked, null);
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Disconnected, null);
                        break;
                    case PitayaConstants.PcEvUnexpectedDisconnect:
                    {
                        string error = arg1;
                        string description = arg2;
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Disconnected, new NetworkError(error, description));
                        break;
                    }
                    case PitayaConstants.PcEvProtoError:
                    {
                        string error = arg1;
                        string description = arg2;
                        if (listener != null) listener.OnNetworkEvent(PitayaNetWorkState.Error, new NetworkError(error, description));
                        break;
                    }
                }
                DLog("OnEvent - main thread END");
            });
            DLog("OnEvent - pinvoke callback END");
        }

        [MonoPInvokeCallback(typeof(NativeLogFunction))]
        private static void LogFunction(PitayaLogLevel level, string msg)
        {
            switch (level)
            {
                case PitayaLogLevel.Debug:
                    Debug.Log("[DEBUG] " + msg);
                    break;
                case PitayaLogLevel.Info:
                    Debug.Log("[INFO] " + msg);
                    break;
                case PitayaLogLevel.Warn:
                    Debug.Log("[WARN] " + msg);
                    break;
                case PitayaLogLevel.Error:
                    Debug.Log("[ERROR] " + msg);
                    break;
                case PitayaLogLevel.Disable:
                    // Don't do anything
                    break;
            }
        }

#if (UNITY_IPHONE || UNITY_XBOX360) && !UNITY_EDITOR
        private const string LibName = "__Internal";
#elif (UNITY_ANDROID) && !UNITY_EDITOR
        private const string LibName = "libpitaya-android";
#elif (UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX)
        private const string LibName = "libpitaya-mac";
#elif (UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN)
        private const string LibName = "pitaya-windows";
#else
        private const string LibName = "pitaya-linux";
#endif

        // ReSharper disable UnusedMember.Local
        [DllImport(LibName, EntryPoint = "tr_uv_tls_set_ca_file")]
        private static extern void NativeSetCertificatePath(string caFile, string caPath);

        [DllImport(LibName, EntryPoint = "pc_unity_lib_init")]
        private static extern void NativeLibInit(int logLevel, string caFile, string caPath, NativeAssertCallback assert, string platform, string buildNumber, string version);

        [DllImport(LibName, EntryPoint = "pc_unity_update_client_info")]
        private static extern void NativeLibUpdateClientInfo(string platform, string buildNumber, string version);

        [DllImport(LibName, EntryPoint = "pc_lib_set_default_log_level")]
        private static extern void NativeLibSetLogLevel(int logLevel);

        [DllImport(LibName, EntryPoint = "pc_client_ev_str")]
        private static extern IntPtr NativeEvToStr(int ev);
        [DllImport(LibName, EntryPoint = "pc_client_rc_str")]
        private static extern IntPtr NativeRcToStr(int rc);

        [DllImport(LibName, EntryPoint = "pc_unity_create")]
        private static extern IntPtr NativeCreate(bool enableTls, bool enablePoll, bool enableReconnect, int connTimeout);
        [DllImport(LibName, EntryPoint = "pc_unity_destroy")]
        private static extern int NativeDestroy(IntPtr client);

        [DllImport(LibName, EntryPoint = "pc_client_connect")]
        private static extern int NativeConnect(IntPtr client, string host, int port, string handshakeOpts);

        [DllImport(LibName, EntryPoint = "pc_client_disconnect")]
        private static extern int NativeDisconnect(IntPtr client);

        [DllImport(LibName, EntryPoint = "pc_unity_request")]
        private static extern int NativeRequest(IntPtr client, string route, string msg, uint cbUid, int timeout, NativeRequestCallback callback, NativeErrorCallback errorCallback);

        [DllImport(LibName, EntryPoint = "pc_unity_binary_request")]
        private static extern int NativeBinaryRequest(IntPtr client, string route, byte[] data, long len, uint cbUid, int timeout, NativeRequestCallback callback, NativeErrorCallback errorCallback);

        [DllImport(LibName, EntryPoint = "pc_string_notify_with_timeout")]
        private static extern int NativeNotify(IntPtr client, string route, string msg, IntPtr exData, int timeout, NativeNotifyCallback callback);
        [DllImport(LibName, EntryPoint = "pc_binary_notify_with_timeout")]
        private static extern int NativeBinaryNotify(IntPtr client, string route, byte[] data, long len, IntPtr exData, int timeout, NativeNotifyCallback callback);
        [DllImport(LibName, EntryPoint = "pc_client_poll")]
        private static extern int NativePoll(IntPtr client);

        [DllImport(LibName, EntryPoint = "pc_client_add_ev_handler")]
        private static extern int NativeAddEventHandler(IntPtr client, NativeEventCallback callback, IntPtr exData, IntPtr destructor);

        [DllImport(LibName, EntryPoint = "pc_client_set_push_handler")]
        private static extern int NativeAddPushHandler(IntPtr client, NativePushCallback callback);

        [DllImport(LibName, EntryPoint = "pc_client_rm_ev_handler")]
        private static extern int NativeRemoveEventHandler(IntPtr client, int handlerId);

        [DllImport(LibName, EntryPoint = "pc_client_conn_quality")]
        private static extern int NativeQuality(IntPtr client);
        [DllImport(LibName, EntryPoint = "pc_client_state")]
        private static extern int NativeState(IntPtr client);

        [DllImport(LibName, EntryPoint = "pc_client_serializer")]
        private static extern IntPtr NativeSerializer(IntPtr client);

        [DllImport(LibName, EntryPoint = "pc_client_free_serializer")]
        private static extern IntPtr NativeFreeSerializer(IntPtr serializer);

        // ReSharper restore UnusedMember.Local

        [DllImport(LibName, EntryPoint = "pc_lib_add_pinned_public_key_from_certificate_string")]
        private static extern int NativeAddPinnedPublicKeyFromCertificateString(string ca_string);

        [DllImport(LibName, EntryPoint = "pc_lib_add_pinned_public_key_from_certificate_file")]
        private static extern int NativeAddPinnedPublicKeyFromCertificateFile(string caPath);

        [DllImport(LibName, EntryPoint = "pc_lib_skip_key_pin_check")]
        private static extern void NativeSkipKeyPinCheck(bool shouldSkip);

        [DllImport(LibName, EntryPoint = "pc_lib_clear_pinned_public_keys")]
        private static extern void NativeClearPinnedPublicKeys();

        [DllImport(LibName, EntryPoint = "pc_unity_init_log_function")]
        private static extern int NativeInitLogFunction(NativeLogFunction fn);
#if UNITY_IPHONE && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern string _PitayaGetCFBundleVersion();
#else
        private static string _PitayaGetCFBundleVersion() { return "1"; }
#endif
    }
}
