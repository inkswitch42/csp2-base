using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections;

public class Server : MonoBehaviour
{
    public const string ADDRESS = "127.0.0.1";
    public const ushort PORT = 9000;

    private NetworkDriver networkDriver;
    private NativeList<NetworkConnection> connections;

    void Start()
    {
        InitializeNetwork();
    }

    void Update()
    {
        networkDriver.ScheduleUpdate().Complete();

        AcceptConnection();
        ProcessNetworkEvents();
    }

    void OnDestroy()
    {
        if (networkDriver.IsCreated)
        {
            networkDriver.Dispose();
            connections.Dispose();
        }
    }

    private void InitializeNetwork()
    {
        networkDriver = NetworkDriver.Create();
        NetworkEndPoint endpoint = NetworkEndPoint.AnyIpv4;
        endpoint.Port = PORT;
        if (networkDriver.Bind(endpoint) != 0)
        {
            Debug.Log($"Failed to bind to port {PORT}");
        }
        else
        {
            networkDriver.Listen();
            Debug.Log($"Listing on port {PORT}");
        }

        connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
    }

    private void AcceptConnection()
    {
        NetworkConnection connection;
        while ((connection = networkDriver.Accept()) != default(NetworkConnection))
        {
            connections.Add(connection);
            Debug.Log($"Accepted a connection {connection.InternalId}");
        }
    }

    private void ProcessNetworkEvents()
    {
        DataStreamReader reader;
        for (int index = 0; index < connections.Length; index++)
        {
            NetworkEvent.Type command;
            while ((command = networkDriver.PopEventForConnection(connections[index], out reader)) != NetworkEvent.Type.Empty)
            {
                switch (command)
                {
                    case NetworkEvent.Type.Connect:
                        Debug.Log("Client connected to server");
                        break;
                    case NetworkEvent.Type.Data:
                        InputMessage message = InputMessage.Deserialize(ref reader);
                        Debug.Log($"Server data: {message}");
                        break;
                    case NetworkEvent.Type.Disconnect:
                        Debug.Log("Client disconnected from server");
                        connections[index] = default(NetworkConnection);
                        break;
                }
            }
        }
    }
}
