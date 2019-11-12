using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using UnityEngine;
using System.Reflection;

namespace MinNetforUnity
{
    using eventSet = Tuple<Socket, CallBack>;
    public delegate void CallBack(Exception e);

    //[AttributeUsage(AttributeTargets.Method)]
    //public class MinNetRPCAttribute : Attribute
    //{
    //    public void test()
    //    {
    //
    //    }
    //}

    public enum MinNetRpcTarget { All = -1000, Others, AllViaServer, Server };

    public class MonoBehaviourMinNetCallBack : MonoBehaviour
    {
        public virtual void UserEnterRoom(int roomNumber, string roomName)
        {

        }

        public virtual void UserLeaveRoom()
        {

        }
    };

    public class RPCstorage
    {
        public RPCstorage(string methodName, MinNetRpcTarget target, object[] parameters)
        {
            this.methodName = methodName;
            this.target = target;
            this.parameters = parameters;
        }

        public string methodName;
        public MinNetRpcTarget target;
        public object[] parameters;
    }


    public class MonoBehaviourMinNet : MonoBehaviour
    {
        [HideInInspector]
        public int objectId = -1;
        [HideInInspector]
        public string prefabName = "";
        [HideInInspector]
        public Queue<RPCstorage> sendRPCq = new Queue<RPCstorage>();
        [HideInInspector]
        public bool isMine = false;

        public void RPC(string methodName, MinNetRpcTarget target, params object[] parameters)
        {
            if(objectId < 0)
            {// 아직 서버로 부터 id를 발급 받지 못한 오브젝트
                sendRPCq.Enqueue(new RPCstorage(methodName, target, parameters));
                return;
            }

            if (target == MinNetRpcTarget.All)
            {
                GetType().GetMethod(methodName).Invoke(this, parameters);
            }


            MinNetUser.SendRPC(objectId, GetType().ToString(), methodName, target, parameters);
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
        private void UserEnterRoom(MinNetPacket packet)
        {
            var callbacks = GameObject.FindObjectsOfType<MonoBehaviourMinNetCallBack>();
            foreach (var callback in callbacks)
            {
                callback.UserEnterRoom(packet.pop_int(), packet.pop_string());
            }
        }

        private void UserLeaveRoom()
        {
            var callbacks = GameObject.FindObjectsOfType<MonoBehaviourMinNetCallBack>();
            foreach (var callback in callbacks)
            {
                callback.UserLeaveRoom();
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

        private void PacketHandler(MinNetPacket packet)
        {
            switch ((Defines.MinNetPacketType)packet.packet_type)
            {
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

            }
        }

        void Update()
        {
            lock(MinNetUser.packetQ)
            {
                while(MinNetUser.packetQ.Count > 0)
                {
                    PacketHandler(MinNetUser.packetQ.Dequeue());
                }
            }
        }
    }



    public class Defines
    {
        public static readonly short HEADERSIZE = 2 + 4;// short로 몸체의 크기를 나타내고, int로 주고받을 패킷 타입 열거형을 나타냄
        public enum MinNetPacketType { OTHER_USER_ENTER_ROOM = -8200, OTHER_USER_LEAVE_ROOM, USER_ENTER_ROOM, USER_LEAVE_ROOM, OBJECT_INSTANTIATE, OBJECT_DESTROY, PING, PONG, PING_CAST, RPC, ID_CAST, CREATE_ROOM };
    }

    public static class MinNetUser : object
    {
        private static Queue<MonoBehaviourMinNet> waitIdObject = new Queue<MonoBehaviourMinNet>();// 서버로 부터 id부여를 기다리는 객체들이 임시적으로 있을 곳
        private static Dictionary<int, MonoBehaviourMinNet> networkObjectDictionary = new Dictionary<int, MonoBehaviourMinNet>();// 서버와 동기화 되는 객체들을 모아두는 곳
        private static Dictionary<string, GameObject> networkObjectCache = new Dictionary<string, GameObject>();// 각종 객체들의 캐시

        public static Queue<MinNetPacket> packetQ = new Queue<MinNetPacket>();

        private static Socket socket = null;
        private static int ping = 20;
        private static int serverTime = 0;// 서버가 시작된 후로 부터 흐른 시간 ms단위
        private static DateTime lastSyncTime = DateTime.Now;

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

        public static void OtherUserEnterRoom()
        {
            foreach (var obj in networkObjectDictionary)
            {
                obj.Value.OtherUserEnterRoom();
            }
        }

        public static void OtherUserLeaveRoom()
        {
            foreach (var obj in networkObjectDictionary)
            {
                obj.Value.OtherUserLeaveRoom();
            }
        }

        public static void ObjectInstantiate(string prefabName, Vector3 position, Vector3 euler, int id)
        {
            GameObject prefab = null;
            MonoBehaviourMinNet obj = null;
            Quaternion qu = new Quaternion();
            qu.eulerAngles = euler;

            if (networkObjectCache.TryGetValue(prefabName, out prefab))
            {
                obj = GameObject.Instantiate(prefab, position, qu).GetComponent<MonoBehaviourMinNet>();
            }
            else
            {
                prefab = Resources.Load(prefabName) as GameObject;

                if (prefab == null)
                {
                    Debug.LogError(prefabName + " 프리펩을 찾을 수 없습니다.");
                    return;
                }

                networkObjectCache.Add(prefabName, prefab);

                obj = GameObject.Instantiate(prefab, position, qu).GetComponent<MonoBehaviourMinNet>();
            }

            if (obj == null)
            {
                Debug.LogError(prefabName + " 객체는 MonoBehaviourMinNet 컴포넌트를 가지고 있지 않습니다.");
                return;
            }

            obj.objectId = id;
            obj.prefabName = prefabName;
            networkObjectDictionary.Add(id, obj);
        }

        public static void ObjectDestroy(MinNetPacket packet)
        {
            ObjectDestroy(packet.pop_string(), packet.pop_int());
        }

        private static void ObjectDestroy(string name, int id)
        {
            MonoBehaviourMinNet obj = null;
            if (networkObjectDictionary.TryGetValue(id, out obj))
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
            MonoBehaviourMinNet obj = null;
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

        private static void ObjectRPC(MonoBehaviourMinNet obj, MinNetPacket packet)
        {
            string componentName = packet.pop_string();
            string methodName = packet.pop_string();

            int target = packet.pop_int();

            Type componentType = System.Reflection.Assembly.Load("Assembly-CSharp").GetType(componentName);
            if (componentType == null)
            {
                componentType = Type.GetType(componentName);
                if(componentType == null)
                {
                    Debug.Log("RPC를 사용할 컴포넌트를 찾을 수 없습니다.");
                    return;
                }
            }
            MethodBase methodBase = componentType.GetMethod(methodName);
            if (methodBase == null)
            {
                Debug.Log("RPC를 사용할 함수를 찾을 수 없습니다.");
                return;
            }

            ParameterInfo[] infoarr = methodBase.GetParameters();
            object[] parameters = new object[infoarr.Length];
            for (int i = 0; i < infoarr.Length; i++)
            {
                parameters[i] = packet.pop(infoarr[i].ParameterType);
            }

            methodBase.Invoke(obj.gameObject.GetComponent(componentType), parameters);
        }

        public static void EnterRoom(string roomName)
        {
            var packet = new MinNetPacket();

            packet.create_packet((int)Defines.MinNetPacketType.USER_ENTER_ROOM);
            packet.push(-2);
            packet.push(roomName);
            packet.create_header();

            Send(packet);
        }

        public static void EnterRoom(int roomNumber)
        {
            var packet = new MinNetPacket();

            packet.create_packet((int)Defines.MinNetPacketType.USER_ENTER_ROOM);
            packet.push(roomNumber);
            packet.push("");
            packet.create_header();

            Send(packet);
        }

        public static void CreateRoom(string roomName)
        {
            var packet = new MinNetPacket();

            packet.create_packet((int)Defines.MinNetPacketType.CREATE_ROOM);
            packet.push(roomName);
            packet.create_header();

            Send(packet);
        }

        public static void SendRPC(int id, string componentName, string methodName, MinNetRpcTarget target, params object[] parameters)
        {
            MinNetPacket packet = new MinNetPacket();
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
            Send(packet);
        }

        private static void AnswerPing()
        {
            MinNetPacket pong = new MinNetPacket();
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
                    obj.objectId = id;
                    networkObjectDictionary.Add(id, obj);
                    obj.OnSetID(id);
                    while(obj.sendRPCq.Count > 0)
                    {
                        RPCstorage storage = obj.sendRPCq.Dequeue();
                        obj.RPC(storage.methodName, storage.target, storage.parameters);
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
            MinNetPacket packet = new MinNetPacket();
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

            MonoBehaviourMinNet minnetobj = ((GameObject)obj).GetComponent<MonoBehaviourMinNet>();


            if(minnetobj == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MonoBehaviourMinNet 컴포넌트를 가지고 있지 않습니다");
                return null;
            }

            minnetobj.isMine = true;
            minnetobj.prefabName = original.name;

            waitIdObject.Enqueue(minnetobj);
            SendInstantiate(original.name, position, rotation.eulerAngles, autoDelete);

            return obj;
        }

        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation, bool autoDelete = true) where T : UnityEngine.Object
        {
            T obj = GameObject.Instantiate(original, position, rotation);

            MonoBehaviourMinNet minnetobj = (obj as GameObject).GetComponent<MonoBehaviourMinNet>();


            if (minnetobj == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MonoBehaviourMinNet 컴포넌트를 가지고 있지 않습니다");
                return null;
            }

            minnetobj.isMine = true;
            minnetobj.prefabName = original.name;

            waitIdObject.Enqueue(minnetobj);
            MinNetUser.
            SendInstantiate(original.name, position, rotation.eulerAngles, autoDelete);

            return obj;
        }

        public static void Destroy(UnityEngine.Object obj)
        {
            MonoBehaviourMinNet minnetobj = ((GameObject)obj).GetComponent<MonoBehaviourMinNet>();

            if(minnetobj == null)
            {
                Debug.LogError(obj.name + " 오브젝트는 MonoBehaviourMinNet 컴포넌트를 가지고 있지 않습니다.");
                return;
            }

            MinNetPacket packet = new MinNetPacket();
            packet.create_packet((int)Defines.MinNetPacketType.OBJECT_DESTROY);
            packet.push(minnetobj.prefabName);
            packet.push(minnetobj.objectId);
            packet.create_header();

            Send(packet);
        }


        private static void PacketHandler(MinNetPacket packet)
        {
            switch ((Defines.MinNetPacketType)packet.packet_type)
            {
                case Defines.MinNetPacketType.PING:
                    AnswerPing();
                    break;

                case Defines.MinNetPacketType.PING_CAST:
                    Ping = packet.pop_int();
                    ServerTime = packet.pop_int() - (int)(Ping * 0.5f);
                    break;

                default:
                    lock(packetQ)
                    {
                        packetQ.Enqueue(packet);
                    }
                    break;
            }
        }

        public static void ConnectToServer(string ip, int port, CallBack callback = null)
        {
            try
            {
                var handler = new GameObject("handler");
                handler.AddComponent<MinNetPacketHandler>();

                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse(ip), port);

                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                eventSet es = new eventSet(socket, callback);

                socket.BeginConnect(remoteEP, new AsyncCallback(ConnectCallBack), es);
            }
            catch (Exception e)
            {
                callback?.Invoke(e);
                //Debug.Log(e.ToString());
            }
        }

        public static void DisconnectToServer(CallBack callback = null)
        {
            try
            {
                socket.BeginDisconnect(false, CloseCallBack, socket);
                callback?.Invoke(null);
            }
            catch (Exception e)
            {
                callback?.Invoke(e);
                //Debug.Log(e.ToString());
            }
        }

        private static void StartRecvHead()// 패킷의 헤더를 비동기로 받아오기 시작합니다.
        {
            try
            {
                MinNetPacket packet = new MinNetPacket();// 패킷을 생성

                socket.BeginReceive
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

        private static void StartRecvBody(MinNetPacket packet, int body_size)// 패킷의 몸체를 비동기로 받아오기 시작합니다.
        {
            try
            {
                socket.BeginReceive
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

                int byteRead = socket.EndReceive(ar);

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

        private static void Send(MinNetPacket packet)
        {
            try
            {
                socket.BeginSend
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
                socket.EndSend(ar);
            }
            catch (Exception e)
            {
                Debug.Log(e.Message);
                DisconnectToServer();
            }
        }


        private static void ConnectCallBack(IAsyncResult ar)
        {
            eventSet es = (eventSet)ar.AsyncState;
            try
            {
                es.Item1.EndConnect(ar);
                Debug.LogFormat("Socket connected to {0}", es.Item1.RemoteEndPoint.ToString());

                StartRecvHead();
            }
            catch (Exception e)
            {
                es?.Item2.Invoke(e);
                Debug.Log(e.ToString());
            }
        }

        private static void CloseCallBack(IAsyncResult ar)
        {
            try
            {
                socket.EndDisconnect(ar);

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
        public int position;                            //패킷의 전체 몸체에서 제일 끝 바이트를 가리키기 위한 변수
        public int packet_type;                         //패킷의 헤더를 제일 마지막에 추가시키기 때문에 패킷 타입을 저장해둠

        //패킷의 기본적인 골격을 만드는 함수들.

        public MinNetPacket()
        {
            this.buffer = new byte[1024];               //패킷의 최대 크기 = 1024 byte
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
