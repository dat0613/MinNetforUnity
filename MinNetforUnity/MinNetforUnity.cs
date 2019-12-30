using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using UnityEngine;
using System.Reflection;
using System.Collections;
using UnityEngine.SceneManagement;
using System.Threading;


namespace MinNetforUnity
{
    using eventSet = Tuple<Socket, CallBack>;
    using UDPCallBackObject = Tuple<MinNetPacket, EndPoint>;

    public delegate void CallBack(Exception e);

    public enum MinNetRpcTarget { All = -1000, Others, AllViaServer, Server, P2Pgroup };// RPC 대상에 대한 옵션

    public class MonoBehaviourMinNetCallBack : MonoBehaviour
    {
        public virtual void UserEnterRoom(int roomNumber, string roomName)
        {
            
        }

        public virtual void UserLeaveRoom()
        {

        }

        public virtual void UserEnterRoomFail(int roomNumber, string reason)
        {

        }

        public virtual void GetUserValue(string key, string value)
        {

        }
    };

    public class RPCstorage// 아직 서버로부터 id를 발급받지 않은 오브젝트가 RPC할때 잠시 버퍼에 저장해둔 후 id를 발급받은 후 서버에게 보냄
    {
        public RPCstorage(string componentName,string methodName, MinNetRpcTarget target, object[] parameters)
        {
            this.componentName = componentName;
            this.methodName = methodName;
            this.target = target;
            this.parameters = parameters;
        }

        public string componentName;
        public string methodName;
        public MinNetRpcTarget target;
        public object[] parameters;
    }

    public class MinNetView : MonoBehaviour
    {
        [HideInInspector]
        public bool isMine = false;

        [HideInInspector]
        public int objectId = -1;

        [HideInInspector]
        public string prefabName = "";

        [HideInInspector]
        public Queue<RPCstorage> sendRPCq = new Queue<RPCstorage>();

        private Dictionary<string, MonoBehaviourMinNet> minnetComponentMap = new Dictionary<string, MonoBehaviourMinNet>();
        public List<MonoBehaviourMinNet> minnetComponents;

        public void SetIsMine(bool isMine)
        {
            this.isMine = isMine;
            foreach (var component in minnetComponents)
            {
                component.isMine = isMine;
            }
        }

        void Awake()
        {
            foreach(var component in minnetComponents)
            {
                component.minnetView = this;
                minnetComponentMap.Add(component.GetComponentName(), component);
            }
        }

        public void SetID(int objectId)
        {
            this.objectId = objectId;

            foreach (var component in minnetComponents)
            {
                component.objectId = objectId;
            }
        }

        public void OnSetID(int objectId)
        {
            foreach (var component in minnetComponents)
            {
                component.OnSetID(objectId);
            }
        }

        public void OtherUserEnterRoom()
        {
            foreach (var component in minnetComponents)
            {
                component.OtherUserEnterRoom();
            }
        }

        public void OtherUserLeaveRoom()
        {
            foreach (var component in minnetComponents)
            {
                component.OtherUserLeaveRoom();
            }
        }

        public void RPC(string componentName, string methodName, MinNetRpcTarget target, bool isTcp, params object[] parameters)
        {
            if (!isMine)
            {
                Debug.LogError("isMine이 false인 객체의 RPC는 허용되지 않습니다.");
                return;
            }
            MinNetUser.SendRPC(objectId, componentName, methodName, target, isTcp, parameters);
        }
    }

    public class MinNetP2PGroup
    {

    }

    public class MinNetPeer
    {
        public MinNetPeer(string ipAddress, int port, int ID)
        {
            id = ID;
            endPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
            holePunchTestTimer = new Timer(new TimerCallback(HolePunchTestCallBack));
        }

        public MinNetPeer(IPEndPoint ipEndPoint, int ID)
        {
            id = ID;
            endPoint = ipEndPoint;
            holePunchTestTimer = new Timer(new TimerCallback(HolePunchTestCallBack));
        }

        public void HolePunchTestStart()
        {
            if (holePunchTestCount >= maxHolePunchTestCount)// 테스트 패킷이 일정 횟수 이상 답이 없으면 해당 피어로 부터의 홀펀칭이 실패한 것임
            {
                Debug.Log(id + " 피어와 p2p 통신 실패");
                return;
            }

            holePunchTestCount++;
            holePunchTestTimer.Change(3000, System.Threading.Timeout.Infinite);

            Debug.Log(id + " 피어 에게 p2p 테스트 패킷 전송 #" + holePunchTestCount);

            MinNetUser.SendIsCanP2P(this);
        }

        private void HolePunchTestCallBack(System.Object state)
        {
            if (!isCanP2P)
                HolePunchTestStart();
        }

        private static Timer holePunchTestTimer = null;

        private int holePunchTestCount = 0;// p2p 응답 확인 패킷을 보낸 횟수
        private int maxHolePunchTestCount = 3;// p2p 응답 확인 패킷을 보낼 최대 횟수

        public IPEndPoint endPoint;

        private int id = -1;
        public int ID
        {
            get
            {
                return id;
            }
        }

        private bool isCanP2P = false;// 이 값이 true면 바로 해당 피어 에게 패킷을 보냄, false면 릴레이 서버를 통해 패킷을 보냄
        public bool IsCanP2P
        {
            get
            {
                return isCanP2P;
            }

            set
            {
                isCanP2P = value;
            }
        }
    }


    public class MonoBehaviourMinNet : MonoBehaviour
    {
        [HideInInspector]
        public bool isMine = false;
        [HideInInspector]
        public int objectId = -1;
        [HideInInspector]
        public MinNetView minnetView = null;
        [HideInInspector]
        public string componentName = "";

        public string GetComponentName()
        {
            return GetType().ToString();
        }

        public void RPC(string methodName, MinNetRpcTarget target, params object[] parameters)
        {
            if (target == MinNetRpcTarget.P2Pgroup)
            {
                Debug.LogError("target이 P2Pgroup인 RPC는 RPCudp함수 에서만 가능합니다.");
                return;
            }

            if (componentName == "")
                componentName = GetComponentName();

            if (minnetView == null)
            {
                Debug.LogError("MonoBehaviourMinNet을 사용하려면 MinNetView 컴포넌트가 있어야 합니다.");
                return;
            }

            if (minnetView.objectId < 0)
            {// 아직 서버로 부터 id를 발급 받지 못한 오브젝트
                minnetView.sendRPCq.Enqueue(new RPCstorage(componentName, methodName, target, parameters));
                return;
            }

            if (target == MinNetRpcTarget.All)
            {
                GetType().GetMethod(methodName).Invoke(this, parameters);
            }

            minnetView.RPC(componentName, methodName, target, true, parameters);
        }

        public void RPCudp(string methodName, MinNetRpcTarget target, params object[] parameters)
        {
            if (componentName == "")
                componentName = GetComponentName();

            if (minnetView == null)
            {
                Debug.LogError("MonoBehaviourMinNet을 사용하려면 MinNetView 컴포넌트가 있어야 합니다.");
                return;
            }

            if (minnetView.objectId < 0)
            {// 아직 서버로 부터 id를 발급 받지 못한 오브젝트
                minnetView.sendRPCq.Enqueue(new RPCstorage(componentName, methodName, target, parameters));
                return;
            }

            if (target == MinNetRpcTarget.All)
            {
                GetType().GetMethod(methodName).Invoke(this, parameters);
            }

            minnetView.RPC(componentName, methodName, target, false, parameters);
        }

        public void RPC(string methodName, MinNetRpcTarget target)
        {
            RPC(methodName, target, null);
        }

        public virtual void OtherUserEnterRoom()
        {

        }

        public virtual void OtherUserLeaveRoom()
        {

        }

        public virtual void OnSetID(int objectID)
        {

        }
    }

    public class MinNetPacketHandler : MonoBehaviour
    {
        Queue<MinNetPacket> packetQ = new Queue<MinNetPacket>();
        void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private MonoBehaviourMinNetCallBack[] GetCallBackComponents()
        {
            return GameObject.FindObjectsOfType<MonoBehaviourMinNetCallBack>();
        }

        private void UserEnterRoom(MinNetPacket packet)
        {
            var components = GetCallBackComponents();

            int roomNumber = packet.pop_int();
            string roomName = packet.pop_string();

            foreach (var component in components)
            {
                component.UserEnterRoom(roomNumber, roomName);
            }
        }

        private void UserLeaveRoom()
        {
            MinNetUser.ClearNetworkObjectDictionary();

            var components = GetCallBackComponents();
            foreach (var component in components)
            {
                component.UserLeaveRoom();
            }
        }

        private void UserEnterRoomFail(MinNetPacket packet)
        {
            var components = GetCallBackComponents();

            var roomNumber = packet.pop_int();
            var reason = packet.pop_string();

            foreach (var component in components)
            {
                component.UserEnterRoomFail(roomNumber, reason);
            }
        }

        private void GetUserValue(MinNetPacket packet)
        {
            var components = GetCallBackComponents();

            var key = packet.pop_string();
            var value = packet.pop_string();

            foreach (var component in components)
            {
                component.GetUserValue(key, value);
            }
        }

        private static void ObjectInstantiate(MinNetPacket packet)
        {
            MinNetUser.ObjectInstantiate
            (
                packet.pop_string(),
                packet.pop_vector3(),
                packet.pop_vector3(),
                packet.pop_int()
            );
        }

        private void ChangeScene(MinNetPacket packet)
        {
            string sceneName = packet.pop_string();

            if (String.IsNullOrEmpty(sceneName))
                return;

            // 새로운 씬을 로딩할 동안에는 잠시동안 패킷 핸들러를 멈추어야 함

            if (MinNetUser.loadSceneDelegate != null)
            {// 델리게이트가 있으면 비동기로 처리함
                StartCoroutine(MinNetUser.loadSceneDelegate(sceneName));
            }
            else
            {// 없으면 그냥 처리함
                SceneManager.LoadScene(sceneName);
                MinNetUser.LoadingComplete();
            }
        }

        private void PacketHandler(MinNetPacket packet)
        {
            switch ((Defines.MinNetPacketType)packet.packet_type)
            {
                case Defines.MinNetPacketType.PING:
                    MinNetUser.AnswerPing();
                    break;

                case Defines.MinNetPacketType.USER_ENTER_ROOM:
                    UserEnterRoom(packet);
                    break;

                case Defines.MinNetPacketType.USER_LEAVE_ROOM:
                    UserLeaveRoom();
                    break;

                case Defines.MinNetPacketType.OTHER_USER_ENTER_ROOM:
                    MinNetUser.OtherUserEnterRoom();
                    break;

                case Defines.MinNetPacketType.OTHER_USER_LEAVE_ROOM:
                    MinNetUser.OtherUserLeaveRoom();
                    break;

                case Defines.MinNetPacketType.OBJECT_INSTANTIATE:
                    ObjectInstantiate(packet);
                    break;

                case Defines.MinNetPacketType.OBJECT_DESTROY:
                    MinNetUser.ObjectDestroy(packet);
                    break;

                case Defines.MinNetPacketType.RPC:
                    MinNetUser.ObjectRPC(packet);
                    break;

                case Defines.MinNetPacketType.ID_CAST:
                    MinNetUser.IdCast(packet);
                    break;

                case Defines.MinNetPacketType.CHANGE_SCENE:
                    ChangeScene(packet);
                    break;

                case Defines.MinNetPacketType.USER_ENTER_ROOM_FAIL:
                    UserEnterRoomFail(packet);
                    break;

                case Defines.MinNetPacketType.GET_USER_VALUE:
                    GetUserValue(packet);
                    break;
            }

            MinNetUser.PushPacket(packet);
        }

        void Update()
        {
            lock (MinNetUser.packetQ)
            {
                while (MinNetUser.packetQ.Count > 0)
                {
                    packetQ.Enqueue(MinNetUser.packetQ.Dequeue());// 오랫동안 lock 되는걸 방지하기 위해 패킷들을 미리 가저옴
                }
            }

            while(packetQ.Count > 0)
            {
                PacketHandler(packetQ.Dequeue());
            }
        }
    }

    public class Defines
    {
        public static readonly short HEADERSIZE = 2 + 4;// short로 몸체의 크기를 나타내고, int로 주고받을 패킷 타입 열거형을 나타냄
        public static readonly short PACKETSIZE = 1024;

        public enum MinNetPacketType
        {
            OTHER_USER_ENTER_ROOM = -8200,
            OTHER_USER_LEAVE_ROOM,
            USER_ENTER_ROOM,
            USER_LEAVE_ROOM,
            OBJECT_INSTANTIATE,
            OBJECT_DESTROY,
            PING,
            PONG,
            PING_CAST,
            RPC,
            USER_ID_CAST,
            ID_CAST,
            CREATE_ROOM,
            CHANGE_SCENE,
            USER_ENTER_ROOM_FAIL,
            CHANGE_SCENE_COMPLETE,
            SET_USER_VALUE,
            GET_USER_VALUE,
            OTHER_JOIN_P2P_GROUP,
            OTHER_LEAVE_P2P_GROUP,
            JOIN_P2P_GROUP,
            LEAVE_P2P_GROUP,
            P2P_MEMBER_CAST,
            SEND_UDP_FIRST,
            SEND_UDP_FIRST_ACK,
            READY_TO_ENTER,
            IS_CAN_P2P,
            IS_CAN_P2P_ACK,
        };
    }

    public static class MinNetUser : object
    {
        public delegate IEnumerator LoadSceneDelegate(string sceneName);
        private static Queue<MinNetView> waitIdObject = new Queue<MinNetView>();// 서버로 부터 id부여를 기다리는 객체들이 임시적으로 있을 곳
        private static Dictionary<int, MinNetView> networkObjectDictionary = new Dictionary<int, MinNetView>();// 서버와 동기화 되는 객체들을 모아두는 곳
        private static Dictionary<string, GameObject> networkObjectCache = new Dictionary<string, GameObject>();// 각종 객체들의 캐시
        private static Timer sendUDPtimer = new Timer(new TimerCallback(sendUDPtimerCallBack));

        private static Dictionary<string, Type> componentCache = new Dictionary<string, Type>();// 리플렉션사용의 최소화를 위해 한번 찾아낸 컴포넌트는 미리 저장해둠
        private static Dictionary<Type, Dictionary<string, MethodBase>> methodCache = new Dictionary<Type, Dictionary<string, MethodBase>>();// 한번 찾은 함수도 미리 저장해 둠, 첫 키값은 컴포넌트의 이름, 다음 키값은 함수의 이름
        private static Dictionary<MethodBase, Tuple<ParameterInfo[], object[]>> parameterCache = new Dictionary<MethodBase, Tuple<ParameterInfo[], object[]>>();// 함수의 파라미터 타입과 파라미터를 넣을때 사용할 배열을 미리 저장해 두어 new를 최소화 함

        private static List<MinNetPeer> p2pMemberList = new List<MinNetPeer>();
        private static Dictionary<int, MinNetPeer> p2pMemberIDMap = new Dictionary<int, MinNetPeer>();
        private static Dictionary<IPEndPoint, MinNetPeer> p2pMemberEndPointMap = new Dictionary<IPEndPoint, MinNetPeer>();

        private static IPEndPoint remoteEndpoint = null;

        public static string RemoteIP
        {
            get
            {
                return remoteEndpoint.Address.ToString();
            }
        }

        public static int RemotePort
        {
            get
            {
                return remoteEndpoint.Port;
            }
        }

        private static ushort udpPort = 0;
        public static ushort UDPport
        {
            get
            {
                return udpPort;
            }
        }
        private static ushort tcpPort = 0;

        public static ushort TCPport
        {
            get
            {
                return tcpPort;
            }
        }

        private static bool serverKnowThisIP = false;

        public static bool ServerKnowThisIP
        {
            get
            {
                return serverKnowThisIP;
            }
        }

        private static int userID = -1;

        public static int UserID
        {
            get
            {
                return userID;
            }
        }

        public static Queue<MinNetPacket> packetQ = new Queue<MinNetPacket>();
        private static Queue<MinNetPacket> packetPool = new Queue<MinNetPacket>();
        private static Queue<UDPCallBackObject> callbackObjectPool = new Queue<UDPCallBackObject>();

        private static EndPoint tcpServerEndPoint = null;
        private static EndPoint udpServerEndpoint = null;

        private static Socket tcpSocket = null;
        private static Socket udpSocket = null;

        private static int ping = 20;
        private static int serverTime = 0;// 서버가 시작된 후로 부터 흐른 시간 ms단위
        private static DateTime lastSyncTime = DateTime.Now;

        public static LoadSceneDelegate loadSceneDelegate = null;

        private static void AddP2PMember(int id, string remoteIP, int remotePort)
        {
            MinNetPeer peer = null;

            if (!p2pMemberIDMap.TryGetValue(id, out peer))
            {// 해당 id와 일치하는 값이 없다면
                peer = new MinNetPeer(remoteIP, remotePort, id);

                p2pMemberIDMap.Add(id, peer);
                p2pMemberEndPointMap.Add(peer.endPoint, peer);
                p2pMemberList.Add(peer);

                peer.HolePunchTestStart();
            }
        }

        private static void DelP2PMember(int id)
        {
            MinNetPeer peer = null;

            if (p2pMemberIDMap.TryGetValue(id, out peer))
            {
                p2pMemberIDMap.Remove(id);
                p2pMemberEndPointMap.Remove(peer.endPoint);
                p2pMemberList.Remove(peer);
            }
        }

        private static void DelP2PMember(IPEndPoint ipEndpoint)
        {
            MinNetPeer peer = null;

            if (p2pMemberEndPointMap.TryGetValue(ipEndpoint, out peer))
            {
                p2pMemberIDMap.Remove(peer.ID);
                p2pMemberEndPointMap.Remove(ipEndpoint);
                p2pMemberList.Remove(peer);
            }
        }

        private static MinNetPeer GetP2PMember(int id)
        {
            MinNetPeer peer = null;

            p2pMemberIDMap.TryGetValue(id, out peer);

            return peer;
        }

        private static MinNetPeer GetP2PMember(IPEndPoint ipEndPoint)
        {
            MinNetPeer peer = null;

            p2pMemberEndPointMap.TryGetValue(ipEndPoint, out peer);

            return peer;
        }

        private static void ClearP2PMember()
        {
            p2pMemberIDMap.Clear();
            p2pMemberEndPointMap.Clear();
            p2pMemberList.Clear();
        }

        public static void SendIsCanP2P(MinNetPeer peer)
        {
            var packet = MinNetUser.PopPacket();

            packet.create_packet((int)Defines.MinNetPacketType.IS_CAN_P2P);
            packet.push(MinNetUser.UserID);
            packet.push(MinNetUser.RemoteIP);
            packet.push(MinNetUser.RemotePort);
            packet.create_header();

            SendUdp(packet, peer.endPoint);
        }

        public static void SendIsCanP2PACK(MinNetPeer peer)
        {
            var packet = MinNetUser.PopPacket();

            packet.create_packet((int)Defines.MinNetPacketType.IS_CAN_P2P_ACK);
            packet.push(MinNetUser.UserID);
            packet.push(MinNetUser.RemoteIP);
            packet.push(MinNetUser.RemotePort);
            packet.create_header();

            SendUdp(packet, peer.endPoint);
        }

        private static void sendUDPtimerCallBack(System.Object state)
        {
            if(!serverKnowThisIP)
            {
                SendUdpFirst();
            }
            else
            {
            }
        }

        public static void ClearNetworkObjectDictionary()
        {
            networkObjectDictionary.Clear();
        }

        public static void LoadingComplete()
        {
            MinNetPacket packet = packetPool.Dequeue();
            packet.create_packet((int)Defines.MinNetPacketType.CHANGE_SCENE_COMPLETE);
            packet.create_header();
            Send(packet);
        }

        public static void SetUserValue(string key, int value)// 서버에게 클라이언트의 임시 값 들을 보낼때
        {
            SetUserValue(key, value.ToString());
        }

        public static void SetUserValue(string key, float value)
        {
            SetUserValue(key, value.ToString());
        }

        public static void SetUserValue(string key, bool value)
        {
            string str = "";
            if (value)
                str = "true";
            else
                str = "false";

            SetUserValue(key, str);
        }

        public static void SetUserValue(string key, string value)
        {
            if(string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("빈 key값은 혀용 되지 않습니다.");
                return;
            }

            var packet = PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.SET_USER_VALUE);

            packet.push(key);
            packet.push(value);

            packet.create_header();

            Send(packet);
        }

        public static void GetUserValue(string key)// 서버에 저정된 임시 값 들을 받을때
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("빈 key값은 혀용 되지 않습니다.");
                return;
            }

            var packet = PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.GET_USER_VALUE);

            packet.push(key);

            packet.create_header();

            Send(packet);
        }

        public static MinNetPacket PopPacket()// 오브젝트 풀 에서 패킷 하나를 빼옴
        {
            MinNetPacket retval = null;

            lock(packetPool)
            {
                if (packetPool.Count > 0)
                {
                    retval = packetPool.Dequeue();
                }
                else// 풀에 남은 패킷이 없다면 새로 하나 만듦
                {
                    retval = new MinNetPacket();
                }
            }

            return retval;
        }

        public static void PushPacket(MinNetPacket packet)// 다 쓴 패킷을 다시 풀에 넣음
        {
            packet.Clear();

            lock(packetPool)
            {
                packetPool.Enqueue(packet);
            }
        }

        //public static UDPCallBackObject PopCallbackObject()// 오브젝트 풀 에서 패킷 하나를 빼옴
        //{
        //    UDPCallBackObject retval = null;

        //    lock (packetPool)
        //    {
        //        if (packetPool.Count > 0)
        //        {
        //            retval = packetPool.Dequeue();
        //        }
        //        else// 풀에 남은 패킷이 없다면 새로 하나 만듦
        //        {
        //            retval = new MinNetPacket();
        //        }
        //    }

        //    return retval;
        //}

        //public static void PushPacket(UDPCallBackObject callBackObject)// 다 쓴 패킷을 다시 풀에 넣음
        //{
        //    callBackObject.

        //    packet.Clear();

        //    lock (packetPool)
        //    {
        //        packetPool.Enqueue(packet);
        //    }
        //}

        public static int Ping
        {
            get
            {
                return ping;
            }
            private set
            {
                ping = value;
            }
        }

        public static int ServerTime
        {
            get
            {
                return serverTime + (int)((DateTime.Now - lastSyncTime).Ticks * 0.0001f);
            }
            private set
            {
                serverTime = value;
                lastSyncTime = DateTime.Now;
            }
        }

        public static void OtherUserEnterRoom()// 다른 클라이언트가 룸에 들어왔을때 콜백
        {
            foreach (var obj in networkObjectDictionary)
            {
                obj.Value.OtherUserEnterRoom();
            }
        }

        public static void OtherUserLeaveRoom()// 다른 클라이언트가 룸에서 나갈때 콜백
        {
            foreach (var obj in networkObjectDictionary)
            {
                obj.Value.OtherUserLeaveRoom();
            }
        }

        public static void ObjectInstantiate(string prefabName, Vector3 position, Vector3 euler, int id)// 서버로 부터 객체 생성을 요청 받았을때
        {
            GameObject prefab = null;
            MinNetView obj = null;
            Quaternion qu = Quaternion.identity;

            qu.eulerAngles = euler;

            if (networkObjectCache.TryGetValue(prefabName, out prefab))// 프리팹 캐시에 프리팹이 이미 있으면 캐시에서 가저옴
            {
                obj = GameObject.Instantiate(prefab, position, qu).GetComponent<MinNetView>();
            }
            else// 캐시에 프리팹이 없으면 리소스에서 찾은 후 캐시에 넣음
            {
                prefab = Resources.Load(prefabName) as GameObject;

                if (prefab == null)
                {
                    Debug.LogError(prefabName + " 프리펩을 찾을 수 없습니다.");
                    return;
                }

                networkObjectCache.Add(prefabName, prefab);

                obj = GameObject.Instantiate(prefab, position, qu).GetComponent<MinNetView>();
            }

            if (obj == null)// MinNetView 컴포넌트를 갖지 않은 오브젝트는 네트워크 오브젝트로 사용할 수 없음
            {
                Debug.LogError(prefabName + " 객체는 MinNetView 컴포넌트를 가지고 있지 않습니다.");
                return;
            }

            obj.SetID(id);
            obj.prefabName = prefabName;

            MinNetView view = null;

            if(networkObjectDictionary.TryGetValue(id, out view))// 이미 네트워크에 존재하는 오브젝트를 추가로 요청 받음
            {// 이미 값이 있으면 동기화에 오류가 있는 것임
                Debug.LogError("객체 동기화 오류 발생");
            }
            else
            {
                networkObjectDictionary.Add(id, obj);
                obj.OnSetID(id);
            }
        }

        public static void ObjectDestroy(MinNetPacket packet)
        {
            ObjectDestroy(packet.pop_string(), packet.pop_int());
        }

        private static void ObjectDestroy(string name, int id)// 서버로 부터 객체 파괴를 요청 받았을 때
        {
            MinNetView obj = null;
            if (networkObjectDictionary.TryGetValue(id, out obj))// 해당 객체가 있는지 확인 후
            {
                if (string.Equals(obj.prefabName, name))
                {
                    networkObjectDictionary.Remove(id);
                    GameObject.Destroy(obj.gameObject);
                }
                else
                {
                    Debug.LogError("동기화 실패 감지");
                }
            }
            else
            {
                Debug.LogError("동기화 실패 감지");
            }
        }

        public static void ObjectRPC(MinNetPacket packet)
        {
            MinNetView obj = null;
            int id = packet.pop_int();
            if (networkObjectDictionary.TryGetValue(id, out obj))
            {
                ObjectRPC(obj, packet);
            }
            else
            {
                Debug.LogError("동기화 실패 감지");
            }
        }

        private static Type GetComponentType(string componentName)// 컴포넌트 이름으로 타입 객체를 받는 함수
        {
            Type type = null;
            if(componentCache.TryGetValue(componentName, out type))
            {// 이미 저장된 컴포넌트가 있음
            }
            else
            {// 아직 저장된 컴포넌트가 없음
                type = System.Reflection.Assembly.Load("Assembly-CSharp").GetType(componentName);// 타입 검색
                if (type == null)
                {// 검색 실패시 새로운 방법으로 검색
                    type = Type.GetType(componentName);
                    if (type == null)// 이것 까지 실패하면 답이 없음
                    {
                        Debug.Log("RPC를 사용할 컴포넌트를 찾을 수 없습니다.");
                    }
                }
                componentCache.Add(componentName, type);// 한번 찾지 못한 컴포넌트 타입은 앞으로도 못찾기 때문에 null로 저장해둠
            }
            return type;
        }

        private static MethodBase GetMethod(Type componentType, string methodName)// 타입 객체와 함수의 이름으로 함수 객체를 받는 함수
        {
            var methodMap = GetMethodMap(componentType);
            MethodBase methodBase = null;
            if (methodMap.TryGetValue(methodName, out methodBase))
            {// 해당 함수가 캐시에 있음
            }
            else
            {// 없음
                methodBase = componentType.GetMethod(methodName);
                if(methodBase == null)
                {// 해당 이름을 가진 함수를 찾지 못함
                    Debug.Log("RPC를 사용할 함수를 찾을 수 없습니다.");
                }

                methodMap.Add(methodName, methodBase);// 새롭게 찾은 함수를 넣음
            }

            return methodBase;
        }

        private static Dictionary<string, MethodBase> GetMethodMap(Type componentType)// 함수 객체가 저장된 맵을 받는 함수
        {
            Dictionary<string, MethodBase> methodMap = null;
            if (methodCache.TryGetValue(componentType, out methodMap))
            {// 해당 컴포넌트에 대한 함수 캐시가 있음
            }
            else
            {// 없으면 새롭게 만들어 주고 캐시에 넣음
                methodMap = new Dictionary<string, MethodBase>();
                methodCache.Add(componentType, methodMap);
            }

            return methodMap;
        }

        private static void CallMethod(MinNetView obj, Type componentType, MethodBase methodBase, MinNetPacket packet)
        {
            Tuple<ParameterInfo[], object[]> tuple = null;
            ParameterInfo[] parameterInfos = null;
            object[] parameters = null;

            if (parameterCache.TryGetValue(methodBase, out tuple))
            {// 해당 함수의 파라미터 정보에 대한 캐시가 있음
                parameterInfos = tuple.Item1;
                parameters = tuple.Item2;
            }
            else
            {// 없으면 정보를 알아내고 캐시에 넣음
                parameterInfos = methodBase.GetParameters();
                parameters = new object[parameterInfos.Length];// parameters는 배열을 미리 할당해 두기 위함임. 여기 들어간 값을 저장할 필요는 없음

                tuple = new Tuple<ParameterInfo[], object[]>(parameterInfos, parameters);

                parameterCache.Add(methodBase, tuple);
            }

            for(int i = 0; i < parameterInfos.Length; i++)
            {
                parameters[i] = packet.pop(parameterInfos[i].ParameterType);
            }

            methodBase.Invoke(obj.gameObject.GetComponent(componentType), parameters);
        }

        private static void ObjectRPC(MinNetView obj, MinNetPacket packet)
        {
            if (obj == null)
                return;

            string componentName = packet.pop_string();
            string methodName = packet.pop_string();

            int target = packet.pop_int();

            var componentType = GetComponentType(componentName);
            if (componentType == null)
                return;

            var methodBase = GetMethod(componentType, methodName); 
            if (methodBase == null)
                return;

            CallMethod(obj, componentType, methodBase, packet);
        }

        public static void EnterRoom(string roomName)
        {
            var packet = MinNetUser.PopPacket();

            packet.create_packet((int)Defines.MinNetPacketType.USER_ENTER_ROOM);
            packet.push(-2);
            packet.push(roomName);
            packet.create_header();

            Send(packet);
        }

        public static void EnterRoom(int roomNumber)
        {
            var packet = MinNetUser.PopPacket();

            packet.create_packet((int)Defines.MinNetPacketType.USER_ENTER_ROOM);
            packet.push(roomNumber);
            packet.push("");
            packet.create_header();

            Send(packet);
        }

        public static void CreateRoom(string roomName)
        {
            CreateRoom(roomName, null);
        }

        public static void CreateRoom(string roomName, params object[] parameters)
        {
            var packet = MinNetUser.PopPacket();

            packet.create_packet((int)Defines.MinNetPacketType.CREATE_ROOM);
            packet.push(roomName);

            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    packet.push(parameters.GetValue(i));
                }
            }

            packet.create_header();
            Send(packet);
        }

        public static void SendRPC(int id, string componentName, string methodName, MinNetRpcTarget target, bool isTcp, params object[] parameters)
        {
            MinNetPacket packet = MinNetUser.PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.RPC);
            packet.push(id);
            packet.push(componentName);
            packet.push(methodName);
            packet.push((int)target);

            if(parameters != null)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    packet.push(parameters.GetValue(i));
                }
            }

            packet.create_header();

            if (isTcp)
                Send(packet);
            else
            {
                if (target == MinNetRpcTarget.P2Pgroup)
                {
                    foreach (var peer in p2pMemberList)
                    {
                        if (!peer.IsCanP2P)
                            continue;

                        MinNetPacket p2pPacket = MinNetUser.PopPacket();

                        p2pPacket.create_packet((int)Defines.MinNetPacketType.RPC);
                        packet.buffer.CopyTo(p2pPacket.buffer, 0);
                        p2pPacket.position = packet.position;

                        p2pPacket.create_header();

                        SendUdp(p2pPacket, peer.endPoint);
                    }

                    PushPacket(packet);
                }
                else
                    SendUdp(packet, udpServerEndpoint);
            }
        }

        public static void AnswerPing()
        {
            MinNetPacket pong = MinNetUser.PopPacket();
            pong.create_packet((int)Defines.MinNetPacketType.PONG);
            pong.create_header();
            Send(pong);
        }

        public static void IdCast(MinNetPacket packet)
        {
            string prefabName = packet.pop_string();
            int id = packet.pop_int();

            if(waitIdObject.Count > 0)
            {
                var obj = waitIdObject.Dequeue();

                if(string.Equals(prefabName, obj.prefabName))
                {
                    obj.SetID(id);
                    networkObjectDictionary.Add(id, obj);
                    obj.OnSetID(id);

                    while(obj.sendRPCq.Count > 0)
                    {
                        RPCstorage storage = obj.sendRPCq.Dequeue();
                        obj.RPC(storage.componentName, storage.methodName, storage.target, true, storage.parameters);
                    }
                }
                else
                {
                    Debug.LogError("ID 발급 동기화 패킷 조작 감지");
                }
            }
            else
            {
                Debug.LogError("ID 발급 동기화 실패 감지");
            }
        }

        private static void SendInstantiate(string prefabName, Vector3 position, Vector3 euler, bool autoDelete)
        {
            MinNetPacket packet = MinNetUser.PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.OBJECT_INSTANTIATE);
            packet.push(prefabName);
            packet.push(position);
            packet.push(euler);
            packet.push(autoDelete);
            packet.create_header();

            Send(packet);
        }

        public static UnityEngine.Object Instantiate(UnityEngine.Object original, Vector3 position, Quaternion rotation, bool autoDelete = true)
        {
            UnityEngine.Object obj = GameObject.Instantiate(original, position, rotation);

            MinNetView minnetView = ((GameObject)obj).GetComponent<MinNetView>();


            if(minnetView == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MinNetView 컴포넌트를 가지고 있지 않습니다");
                return null;
            }

            minnetView.SetIsMine(true);
            minnetView.prefabName = original.name;

            waitIdObject.Enqueue(minnetView);
            SendInstantiate(original.name, position, rotation.eulerAngles, autoDelete);

            return obj;
        }

        public static GameObject Instantiate(GameObject original)
        {
            return Instantiate(original, Vector3.zero, Quaternion.identity);
        }

        public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation, bool autoDelete = true)
        {
            if (original == null)
            {
                Debug.LogError("null 오브젝트는 Instantiate할 수 없습니다.");
                return null;
            }

            GameObject obj = GameObject.Instantiate(original, position, rotation);
            MinNetView minnetView = obj.GetComponent<MinNetView>();


            if (minnetView == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MinNetView 컴포넌트를 가지고 있지 않습니다");
                return obj;
            }
            minnetView.SetIsMine(true);
            minnetView.prefabName = original.name;
            waitIdObject.Enqueue(minnetView);
            MinNetUser.SendInstantiate(original.name, position, rotation.eulerAngles, autoDelete);

            return obj;
        }

        public static T Instantiate<T>(T original) where T : UnityEngine.Object
        {
            return Instantiate(original, Vector3.zero, Quaternion.identity);
        }

        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation, bool autoDelete = true) where T : UnityEngine.Object
        {
            if(original == null)
            {
                Debug.LogError("null 오브젝트는 Instantiate할 수 없습니다.");
                return null;
            }

            T obj = GameObject.Instantiate(original, position, rotation);
            Component gameObject = obj as Component;
            MinNetView minnetView = gameObject.GetComponent<MinNetView>();


            if (minnetView == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MinNetView 컴포넌트를 가지고 있지 않습니다");
                return obj;
            }
            minnetView.SetIsMine(true);
            minnetView.prefabName = original.name;
            waitIdObject.Enqueue(minnetView);
            MinNetUser.SendInstantiate(original.name, position, rotation.eulerAngles, autoDelete);

            return obj;
        }

        public static void Destroy(UnityEngine.Object obj)
        {
            MinNetView minnetobj = ((GameObject)obj).GetComponent<MinNetView>();

            if(minnetobj == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MinNetView 컴포넌트를 가지고 있지 않습니다.");
                GameObject.Destroy(obj);
                return;
            }

            MinNetPacket packet = MinNetUser.PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.OBJECT_DESTROY);
            packet.push(minnetobj.prefabName);
            packet.push(minnetobj.objectId);
            packet.create_header();

            Send(packet);
        }

        private static void SetRemoteAddress(MinNetPacket packet)
        {
            var ip = packet.pop_string();
            var port = packet.pop_int();

            if (remoteEndpoint == null)
            {
                remoteEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
            }
            else
            {
                remoteEndpoint.Address = IPAddress.Parse(ip);
                remoteEndpoint.Port = port;
            }
        }

        private static void SendReadyToEnter()
        {
            MinNetPacket packet = MinNetUser.PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.READY_TO_ENTER);
            packet.create_header();

            Send(packet);
        }

        private static void SetUserID(MinNetPacket packet)
        {
            userID = packet.pop_int();
        }

        private static void SendUdpFirst()
        {
            var packet = PopPacket();
            packet.create_packet((int)Defines.MinNetPacketType.SEND_UDP_FIRST);
            packet.push(userID);
            packet.create_header();
            SendUdp(packet, udpServerEndpoint);

            sendUDPtimer.Change(1000, System.Threading.Timeout.Infinite);
        }

        private static void OnUdpFirstACK(MinNetPacket packet)
        {
            serverKnowThisIP = true;
            var remoteIP = packet.pop_string();
            var remotePort = packet.pop_int();

            if(remoteEndpoint == null)
            {
                remoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteIP), remotePort);
            }
            else
            {
                remoteEndpoint.Address = IPAddress.Parse(remoteIP);
                remoteEndpoint.Port = remotePort;
            }
        }

        private static void OnIsCanP2P(MinNetPacket packet)
        {
            var peerID = packet.pop_int();
            var peerIP = packet.pop_string();
            var peerPort = packet.pop_int();

            var peer = GetP2PMember(peerID);

            if (peer == null)
                return;

            if (!(peer.endPoint.Address.ToString().Equals(peerIP) && peer.endPoint.Port.Equals(peerPort)))
                return;

            SendIsCanP2PACK(peer);
        }

        private static void OnIsCanP2PACK(MinNetPacket packet)
        {
            var peerID = packet.pop_int();
            var peerIP = packet.pop_string();
            var peerPort = packet.pop_int();

            var peer = GetP2PMember(peerID);

            if (peer == null)
                return;

            if (!(peer.endPoint.Address.ToString().Equals(peerIP) && peer.endPoint.Port.Equals(peerPort)))
                return;

            peer.IsCanP2P = true;

            Debug.Log(peer.ID + " 피어와 p2p 통신 성공");
        }

        private static void PacketHandler(MinNetPacket packet)
        {
            Defines.MinNetPacketType packetType = (Defines.MinNetPacketType)packet.packet_type;
            switch ((Defines.MinNetPacketType)packet.packet_type)
            {
                case Defines.MinNetPacketType.PING_CAST:
                    Ping = packet.pop_int();
                    ServerTime = packet.pop_int() - (int)(Ping * 0.5f);
                    MinNetUser.PushPacket(packet);
                    break;

                case Defines.MinNetPacketType.USER_ID_CAST:
                    SetUserID(packet);
                    MinNetUser.PushPacket(packet);
                    SendUdpFirst();
                    break;

                case Defines.MinNetPacketType.SEND_UDP_FIRST_ACK:
                    OnUdpFirstACK(packet);
                    MinNetUser.PushPacket(packet);
                    SendReadyToEnter(); 
                    break;

                case Defines.MinNetPacketType.IS_CAN_P2P:
                    OnIsCanP2P(packet);// 피어로부터 p2p 통신이 가능한지 물어보는 패킷이 도착함
                    MinNetUser.PushPacket(packet);
                    break;

                case Defines.MinNetPacketType.IS_CAN_P2P_ACK:
                    OnIsCanP2PACK(packet);// 피어로부터 p2p 통신이 가능하다는 패킷이 도착함
                    MinNetUser.PushPacket(packet);
                    break;

                case Defines.MinNetPacketType.OTHER_JOIN_P2P_GROUP:// 누군가가 p2p 그룹에 들어옴
                case Defines.MinNetPacketType.P2P_MEMBER_CAST:// 기존 p2p 그룹에 있는 정보를 받음
                    {
                        var peerID = packet.pop_int();
                        var peerIP = packet.pop_string();
                        var peerPort = packet.pop_int();
                        AddP2PMember(peerID, peerIP, peerPort);
                        MinNetUser.PushPacket(packet);
                        break;
                    }

                case Defines.MinNetPacketType.OTHER_LEAVE_P2P_GROUP:// 누군가가 p2p 그룹에서 나감
                    {
                        var peerID = packet.pop_int();
                        DelP2PMember(peerID);
                        MinNetUser.PushPacket(packet);
                        break;
                    }

                case Defines.MinNetPacketType.JOIN_P2P_GROUP:// 내가 p2p 그룹에 들어감
                    
                    break;

                case Defines.MinNetPacketType.LEAVE_P2P_GROUP:// 내가 p2p 그룹을 나감
                    ClearP2PMember();
                    MinNetUser.PushPacket(packet);
                    break;

                default:
                    lock(packetQ)
                    {
                        packetQ.Enqueue(packet);
                    }
                    break;
            }
        }

        public static void ConnectToServer(string ip, ushort tcpPort, ushort udpPort, CallBack callback = null)
        {
            try
            {
                var handler = GameObject.Find("MinNetHandler");

                if (handler == null)
                {
                    handler = new GameObject("MinNetHandler");
                    handler.AddComponent<MinNetPacketHandler>();
                }

                tcpServerEndPoint = tcpServerEndPoint ?? new IPEndPoint(IPAddress.Parse(ip), tcpPort);
                udpServerEndpoint = udpServerEndpoint ?? new IPEndPoint(IPAddress.Parse(ip), udpPort);

                serverKnowThisIP = false;

                tcpSocket = tcpSocket ?? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                udpSocket = udpSocket ?? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                eventSet es = new eventSet(tcpSocket, callback);

                tcpSocket.BeginConnect(tcpServerEndPoint, new AsyncCallback(ConnectCallBack), es);

            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
                callback?.Invoke(e);
            }
        }

        public static void DisconnectToServer(CallBack callback = null)
        {
            try
            {
                eventSet es = new eventSet(tcpSocket, callback);

                tcpSocket.BeginDisconnect(false, CloseCallBack, es);
            }
            catch (Exception e)
            {
                callback?.Invoke(e);
                Debug.Log(e.ToString());
            }
        }

        private static void StartRecvHead()// 패킷의 헤더를 비동기로 받아오기 시작합니다.
        {
            try
            {
                MinNetPacket packet = MinNetUser.PopPacket();// 패킷을 생성

                tcpSocket.BeginReceive
                (
                    packet.buffer,// 패킷의 데이터가 들어갈 버퍼
                    0,// 처음부터 데이터를 담음
                    Defines.HEADERSIZE,// 미리 지정해 둔 패킷의 헤더크기 만큼 받음
                    SocketFlags.None,
                    new AsyncCallback(RecvCallBack),// 비동기로 받아오기 시작 
                    packet
                );
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
                DisconnectToServer();
            }
        }

        private static void StartRecvUdp()
        {
            try
            {
                MinNetPacket packet = MinNetUser.PopPacket();
                EndPoint endPoint = new IPEndPoint(IPAddress.Any, udpPort);
                var callbackObject = new UDPCallBackObject(packet, endPoint);

                udpSocket.BeginReceiveFrom
                (
                    packet.buffer,
                    0,
                    Defines.PACKETSIZE,
                    SocketFlags.None,
                    ref endPoint,
                    new AsyncCallback(RecvUdpCallBack),
                    callbackObject
                );
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
                DisconnectToServer();
            }
        }

        private static void StartRecvBody(MinNetPacket packet, int body_size)// 패킷의 몸체를 비동기로 받아오기 시작합니다.
        {
            try
            { 
                tcpSocket.BeginReceive
                (
                    packet.buffer,
                    Defines.HEADERSIZE,// 헤더를 받은 후 몸체를 받아오기 때문에 헤더 데이터 이후에 몸체의 데이터가 들어옴
                    body_size,// 헤더에서 분석한 몸체의 크기만큼 받음
                    SocketFlags.None,
                    new AsyncCallback(RecvCallBack),
                    packet
                );
            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
                DisconnectToServer();
            }
        }

        private static void RecvCallBack(IAsyncResult ar)
        {
            try
            {
                MinNetPacket packet = (MinNetPacket)ar.AsyncState;

                int byteRead = tcpSocket.EndReceive(ar);

                if (byteRead > 0)
                {
                    if (packet.position < Defines.HEADERSIZE)// 아직 헤더만 받아오고 몸체는 받지 못했음
                    {
                        short body_size = packet.pop_short();// 받아온 헤더에서 몸체의 크기와 패킷 타입 분석
                        int packet_type = packet.pop_int();

                        packet.packet_type = packet_type;

                        if (body_size > 0)
                            StartRecvBody(packet, body_size);// 분석한 데이터를 사용하여 몸체의 데이터도 받음
                        else
                        {
                            PacketHandler(packet);
                            StartRecvHead();// body가 없는 패킷은 이대로 완성 임
                        }
                    }
                    else// 몸체까지 전부 받았음
                    {
                        PacketHandler(packet);
                        StartRecvHead();// 하나의 패킷을 전부 받았으므로 다음 패킷을 받기 시작함
                    }
                }
                else
                {
                    DisconnectToServer();
                }
            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
                DisconnectToServer();
            }
        }

        private static void RecvUdpCallBack(IAsyncResult ar)
        {
            UDPCallBackObject callbackObject = null;
            MinNetPacket packet = null;
            EndPoint endPoint = null;
            try
            {
                callbackObject = (UDPCallBackObject)ar.AsyncState;
                packet = callbackObject.Item1;
                endPoint = callbackObject.Item2;

                int byteRead = udpSocket.EndReceiveFrom(ar, ref endPoint);

                

                if (byteRead > 0)
                {
                    packet.position = 0;
                    var body_size = packet.pop_short();
                    var packet_type = packet.pop_int();

                    if (body_size > Defines.PACKETSIZE - Defines.HEADERSIZE || body_size < 0)
                    {
                        PushPacket(packet);
                    }
                    else
                    {
                        packet.packet_type = packet_type;
                        PacketHandler(packet);
                        StartRecvUdp();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
                if (endPoint == udpServerEndpoint)
                {
                    DisconnectToServer();
                }
            }
        }

        private static void Send(MinNetPacket packet)
        {
            try
            {
                tcpSocket.BeginSend
                (
                    packet.buffer,
                    0,
                    packet.position,
                    SocketFlags.None,
                    new AsyncCallback(SendCallBack),
                    packet
                );
            }
            catch (Exception e)
            {
                Debug.Log(e);
                DisconnectToServer();
            }
        }

        private static void SendCallBack(IAsyncResult ar)
        {
            try
            {
                tcpSocket.EndSend(ar);
                var packet = (MinNetPacket)ar.AsyncState;
                MinNetUser.PushPacket(packet);
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
                DisconnectToServer();
            }
        }

        private static void SendUdp(MinNetPacket packet, EndPoint endPoint)
        {
            var ipEndPoint = (IPEndPoint)endPoint;
            Debug.Log(ipEndPoint.Address.ToString() + " : " + ipEndPoint.Port.ToString());

            var callbackObject = new UDPCallBackObject(packet, endPoint);
            try
            {
                udpSocket.BeginSendTo
                (
                    packet.buffer,
                    0,
                    packet.position,
                    SocketFlags.None,
                    endPoint,
                    new AsyncCallback(SendUdpCallBack),
                    callbackObject
                );
            }
            catch (Exception e)
            {
                Debug.Log(e);
                DisconnectToServer();
            }
        }

        private static void SendUdpCallBack(IAsyncResult ar)
        {
            UDPCallBackObject callbackObject = null;
            MinNetPacket packet = null;
            EndPoint endPoint = null;

            try
            {
                callbackObject = (UDPCallBackObject)ar.AsyncState;
                packet = callbackObject.Item1;
                endPoint = callbackObject.Item2;

                udpSocket.EndSendTo(ar);
                MinNetUser.PushPacket(packet);
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
                if(endPoint == udpServerEndpoint)
                {
                    DisconnectToServer();
                }
            }
        }

        private static void ConnectCallBack(IAsyncResult ar)
        {
            eventSet es = (eventSet)ar.AsyncState;

            try
            {
                es.Item1.EndConnect(ar);
                Debug.LogFormat("Socket connected to {0}", es.Item1.RemoteEndPoint.ToString());
                es.Item2?.Invoke(null);

                StartRecvHead();
                StartRecvUdp();
            }
            catch (Exception e)
            {
                es.Item2?.Invoke(e);
                Debug.Log(e.ToString());
            }
        }

        private static void CloseCallBack(IAsyncResult ar)
        {
            eventSet es = (eventSet)ar.AsyncState;
            try
            {
                tcpSocket.EndDisconnect(ar);
                es.Item2?.Invoke(null);
                Debug.Log("연결 끊음");
            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
            }
        }
    }

    public class MinNetPacket : object
    {
        public byte[] buffer;                           //패킷의 전체 몸체
        public int position = 0;                            //패킷의 전체 몸체에서 제일 끝 바이트를 가리키기 위한 변수
        public int packet_type = 0;                         //패킷의 헤더를 제일 마지막에 추가시키기 때문에 패킷 타입을 저장해둠

        //패킷의 기본적인 골격을 만드는 함수들.

        public void Clear()
        {
            packet_type = position = 0;
            System.Array.Clear(buffer, 0, buffer.Length);
        }

        public MinNetPacket()
        {
            this.buffer = new byte[Defines.PACKETSIZE];               //패킷의 최대 크기 = 1024 byte
            position = 0;
        }

        public void create_packet(int packet_type)       //타입은 게임에서 추가시키고 dll수정은 최소화 시키기 위하여 int형으로 타입을 받아옴.
        {
            position = Defines.HEADERSIZE;              //패킷의 헤더는 제일 마지막에 추가시키기 때문에 헤더의 크기많큼 띄운체로 몸체를 채움

            this.packet_type = packet_type;
        }

        public void create_header()
        {
            Int16 body_size = (Int16)(this.position - Defines.HEADERSIZE);
            int header_position = 0;

            byte[] header_size = BitConverter.GetBytes(body_size);                  //헤더에 몸체의 크기 넣기
            header_size.CopyTo(this.buffer, header_position);
            header_position += header_size.Length;

            byte[] header_type = BitConverter.GetBytes(this.packet_type);            //헤더에 패킷의 타입 넣기
            header_type.CopyTo(this.buffer, header_position);
        }


        //데이터를 삽입하는 함수들

        public void push(int data)                      //패킷에 int형 데이터 삽입
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void push(short data)                      //패킷에 short형 데이터 삽입
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void push(float data)                    //패킷에 float형 데이터 삽입
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += temp_buffer.Length;
        }

        public void push(string data)                   //패킷에 string형 데이터 삽입
        {
            int len = Encoding.UTF8.GetByteCount(data);
            push(len);
            byte[] temp_buffer = Encoding.UTF8.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += len;
        }

        public void push(bool data)                     //패킷에 bool형 데이터 삽입
        {
            byte[] temp_buffer = BitConverter.GetBytes(data);
            temp_buffer.CopyTo(this.buffer, this.position);
            this.position += sizeof(bool);
        }

        public void push(Vector2 data)                  //패킷에 Vector2형 데이터 삽입
        {
            push(data.x);
            push(data.y);
        }

        public void push(Vector3 data)                  //패킷에 Vector3형 데이터 삽입
        {
            push(data.x);
            push(data.y);
            push(data.z);
        }

        //데이터를 빼내는 함수들

        public int pop_int()
        {
            int data = BitConverter.ToInt32(this.buffer, this.position);
            this.position += sizeof(int);
            return data;
        }

        public float pop_float()
        {
            float data = BitConverter.ToSingle(this.buffer, this.position);
            this.position += sizeof(float);
            return data;
        }

        public short pop_short()
        {
            short data = BitConverter.ToInt16(this.buffer, this.position);
            this.position += sizeof(short);
            return data;
        }

        public string pop_string()
        {
            int len = pop_int();

            string str = Encoding.UTF8.GetString(buffer, position, len);
            position += len;
            return str;
        }

        public bool pop_bool()
        {
            bool data = BitConverter.ToBoolean(this.buffer, this.position);
            this.position += sizeof(bool);
            return data;
        }

        public Vector2 pop_vector2()
        {
            Vector2 data;

            data.x = pop_float();
            data.y = pop_float();

            return data;
        }

        public Vector3 pop_vector3()
        {
            Vector3 data;

            data.x = pop_float();       //pop함수는 앞에서 부터 차레대로 데이터를 빼오기 때문에 x,y,z의 순서만 맞추어 주면 됨.
            data.y = pop_float();
            data.z = pop_float();
            return data;
        }

        public T pop<T>()
        {
            var Ttype = typeof(T);

            if (typeof(int) == Ttype)
            {
                return (T)Convert.ChangeType(pop_int(), typeof(T));
            }
            if (typeof(short) == Ttype)
            {
                return (T)Convert.ChangeType(pop_short(), typeof(T));
            }
            if (typeof(float) == Ttype)
            {
                return (T)Convert.ChangeType(pop_float(), typeof(T));
            }
            if (typeof(bool) == Ttype)
            {
                return (T)Convert.ChangeType(pop_bool(), typeof(T));
            }
            if (typeof(string) == Ttype)
            {
                return (T)Convert.ChangeType(pop_string(), typeof(T));
            }
            if (typeof(Vector2) == Ttype)
            {
                return (T)Convert.ChangeType(pop_vector2(), typeof(T));
            }
            if (typeof(Vector3) == Ttype)
            {
                return (T)Convert.ChangeType(pop_vector3(), typeof(T));
            }
            return default(T);
        }

        public object pop(Type type)
        {
            if (typeof(int) == type)
            {
                return pop_int();
            }
            if (typeof(short) == type)
            {
                return pop_short();
            }
            if (typeof(float) == type)
            {
                return pop_float();
            }
            if (typeof(bool) == type)
            {
                return pop_bool();
            }
            if (typeof(string) == type)
            {
                return pop_string();
            }
            if (typeof(Vector2) == type)
            {
                return pop_vector2();
            }
            if (typeof(Vector3) == type)
            {
                return pop_vector3();
            }
            return null;
        }

        public void push(object obj)
        {
            Type type = obj.GetType();

            if (typeof(int) == type)
            {
                push(Convert.ToInt32(obj));
            }
            if (typeof(short) == type)
            {
                push(Convert.ToInt16(obj));
            }
            if (typeof(float) == type)
            {
                push(Convert.ToSingle(obj));
            }
            if (typeof(bool) == type)
            {
                push(Convert.ToBoolean(obj));
            }
            if (typeof(string) == type)
            {
                push(Convert.ToString(obj));
            }
            if (typeof(Vector2) == type)
            {
                push((Vector2)obj);
            }
            if (typeof(Vector3) == type)
            {
                push((Vector3)obj);
            }
        }
    }
}