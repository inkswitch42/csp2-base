using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections;

public class Server : MonoBehaviour
{
    public const string ADDRESS = "127.0.0.1";
    public const ushort PORT = 9000;

    public GameObject serverPlayer;

    private float deltaTime;
    private int currentTick;
    private NetworkDriver networkDriver;
    private NativeList<NetworkConnection> connections;
    private Rigidbody playerBody;
    private GameScene gameScene;

    void Awake()
    {
        deltaTime = Time.fixedDeltaTime;
        currentTick = 0;
        connections = new NativeList<NetworkConnection>(Allocator.Persistent);
        playerBody = serverPlayer.GetComponent<Rigidbody>();
        gameScene = GameScene.Create(serverPlayer);

        InitializeNetwork();
    }

    public void OnServerPlayerToggle(bool toggle)
    {
        Renderer renderer = serverPlayer.GetComponent<Renderer>();
        renderer.enabled = toggle;
    }

    void Update()
    {
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
    }

    private void AcceptConnection()
    {
        NetworkConnection connection;
        while ((connection = networkDriver.Accept()) !=
            default(NetworkConnection))
        {
            connections.Add(connection);
            Debug.Log($"Accepted connection {connection.InternalId}");
        }
    }

    private void ProcessNetworkEvents()
    {
        networkDriver.ScheduleUpdate().Complete();

        DataStreamReader reader;
        NetworkEvent.Type command;
        NetworkConnection connection;
        for (int i = 0; i < connections.Length; i++)
        {
            connection = connections[i];
            while ((command = networkDriver.PopEventForConnection(connection, out reader)) !=
                NetworkEvent.Type.Empty)
            {
                switch (command)
                {
                    case NetworkEvent.Type.Connect:
                        Debug.Log("Client connected to server");
                        break;
                    case NetworkEvent.Type.Data:
                        InputMessage message = InputMessage.Deserialize(ref reader);
                        Debug.Log($"inputMessage={message}");
                        Debug.Log("server tick: " + currentTick);
                        AdvanceSimulation(message, connection);
                        break;
                    case NetworkEvent.Type.Disconnect:
                        Debug.Log("Client disconnected from server");
                        connections[i] = default(NetworkConnection);
                        break;
                }
            }
        }
    }

    private void AdvanceSimulation(
        InputMessage message,
        NetworkConnection connection)
    {
        int startTick = message.startTick;
        int inputCount = message.inputs.Count;
        int maxTick = startTick + inputCount;
        if (maxTick >= currentTick)
        {
            int offsetTick =
                currentTick > startTick ?
                currentTick - startTick :
                0;

            DataStreamWriter writer;
            for (int i = offsetTick; i < message.inputs.Count; i++)
            {
                Debug.Log($"server processing inputs from client tick {startTick + i}");
                GamePlayer.ApplyForce(playerBody, message.inputs[i]);
                gameScene.Simulate(deltaTime);

                //currentTick++;

                StateMessage stateMessage = new()
                {
                    tick = currentTick,
                    position = playerBody.position,
                    rotation = playerBody.rotation,
                    velocity = playerBody.velocity,
                    angularVelocity = playerBody.angularVelocity
                };

                currentTick++;

                Debug.Log($"stateMessage={stateMessage}");
                networkDriver.BeginSend(connection, out writer);
                stateMessage.Serialize(ref writer);
                networkDriver.EndSend(writer);
            }
        }
    }
}
