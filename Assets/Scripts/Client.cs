using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Networking.Transport;

struct ClientState
{
    public Vector3 position;
    public Quaternion rotation;

    public override string ToString()
    {
        return (position + " " + rotation );
    }
}

public class Client : MonoBehaviour
{
    public GameObject clientPlayer;
    public Toggle errorCorrection;
    public Toggle correctionSmoothing;
    public Toggle redundantInput;

    private const int BUFFER_SIZE = 1024;

    private float deltaTime;
    private float currentTime;
    private int currentTick;
    private int latestTick;
    private UserInput[] inputBuffer; //predicted inputs
    private ClientState[] stateBuffer; //predicted states
    private Vector3 positionError;
    private Quaternion rotationError;
    private NetworkDriver networkDriver;
    private NetworkConnection connection;
    private Rigidbody playerBody;
    private GameScene gameScene;

    void Start()
    {
        //Time.timeScale = 0.2f;

        deltaTime = Time.fixedDeltaTime;
        currentTime = 0.0f;
        currentTick = 0;
        latestTick = 0;
        inputBuffer = new UserInput[BUFFER_SIZE];
        stateBuffer = new ClientState[BUFFER_SIZE];
        positionError = Vector3.zero;
        rotationError = Quaternion.identity;
        connection = default(NetworkConnection);
        playerBody = clientPlayer.GetComponent<Rigidbody>();
        gameScene = GameScene.Create(clientPlayer);

        InitializeNetwork();
    }

    public void OnErrorCorrectionToggle(bool toggle)
    {
        correctionSmoothing.interactable = toggle;
    }


    void Update()
    {
        AdvanceSimulation();
        ProcessNetworkEvents();
        Debug.Log("end client update");
    }

    void OnDestroy()
    {
        if (networkDriver.IsCreated)
        {
            networkDriver.Dispose();
            connection = default(NetworkConnection);
        }
    }

    private void InitializeNetwork()
    {
        networkDriver = NetworkDriver.Create();
        NetworkEndPoint endpoint =
            NetworkEndPoint.Parse(Server.ADDRESS, Server.PORT);
        connection = networkDriver.Connect(endpoint);
    }

    private void AdvanceSimulation()
    {
        currentTime += Time.deltaTime;
        while (currentTime >= deltaTime)
        {
            currentTime -= deltaTime;

            UserInput input = new()
            {
                up = Input.GetKey(KeyCode.W),
                down = Input.GetKey(KeyCode.S),
                right = Input.GetKey(KeyCode.D),
                left = Input.GetKey(KeyCode.A),
                jump = Input.GetKey(KeyCode.Space)
            };

            int index = currentTick % BUFFER_SIZE;
            inputBuffer[index] = input;
            stateBuffer[index] = new()
            {
                position = playerBody.position,
                rotation = playerBody.rotation
            };
            Debug.Log($"stored this step at {index} with values {stateBuffer[index]} current tick = {currentTick}");

            GamePlayer.ApplyForce(playerBody, input);
            gameScene.Simulate(deltaTime);
                        
            SendInput();

            currentTick++;
        }
    }

    private void ProcessNetworkEvents()
    {
        networkDriver.ScheduleUpdate().Complete();

        DataStreamReader reader;
        NetworkEvent.Type command;
        while ((command = connection.PopEvent(networkDriver, out reader)) !=
            NetworkEvent.Type.Empty)
        {
            switch(command)
            {
                case NetworkEvent.Type.Connect:
                    Debug.Log("Connected to server");
                    break;
                case NetworkEvent.Type.Data:
                    StateMessage message = StateMessage.Deserialize(ref reader);
                    Debug.Log($"stateMessage: {message}");
                    //TODO: reconcile client state
                    ReconcileState(message);
                    break;
                case NetworkEvent.Type.Disconnect:
                    Debug.Log("Disconnected froms server");
                    connection = default(NetworkConnection);
                    break;
            }
        }
    }

    private void SendInput()
    {
        if (connection.GetState(networkDriver) !=
            NetworkConnection.State.Connected)
        {
            return;
        }

        InputMessage message = new()
        {
            startTick = redundantInput.isOn ? latestTick + 1 : currentTick,
            inputs = new List<UserInput>()
        };
        
        for (int i = message.startTick; i <= currentTick; ++i)
        {
            message.inputs.Add(inputBuffer[i % BUFFER_SIZE]);
        }

        Debug.Log($"inputMessage={message}");
        DataStreamWriter writer;
        networkDriver.BeginSend(connection, out writer);
        message.Serialize(ref writer);
        networkDriver.EndSend(writer);
    }

    //TODO: fix reconcilation logic
    //To see the bug, start simulation and enable server player, error
    //correction, and redundant inputs. Notice, the player jitter.
    private void ReconcileState(StateMessage message)
    {
        latestTick = message.tick;

        if (errorCorrection.isOn)
        {
            int index = (message.tick % BUFFER_SIZE);
            Vector3 positionError =
                message.position - stateBuffer[index].position;
            Debug.LogError($"position error = {positionError}");
            float rotationError = 1.0f - Quaternion.Dot(
                message.rotation,
                stateBuffer[index].rotation);
            //Debug.Log("client state at tick " + message.tick + " " + stateBuffer[index]);

            if (positionError.sqrMagnitude > 0.0000001f ||
                rotationError > 0.00001f)
            {
                Debug.LogWarning($"stored state at {index} = {stateBuffer[index]} current tick = {currentTick}");
                Debug.LogWarning($"Correct error at tick {message.tick} " +
                    $"(rewinding {currentTick - message.tick} ticks) " + $"current tick = {currentTick} " + $"message tick = {message.tick}");
                
                Vector3 previousPosition =
                    playerBody.position + this.positionError;
                Quaternion previousRotation =
                    playerBody.rotation * this.rotationError;

                playerBody.position = message.position;
                playerBody.rotation = message.rotation;
                playerBody.velocity = message.velocity;
                playerBody.angularVelocity = message.angularVelocity;

                int rewindTick = message.tick;
                while (rewindTick < currentTick)
                {
                    index = rewindTick % BUFFER_SIZE;
                    
                    stateBuffer[index] = new()
                    {
                        position = playerBody.position,
                        rotation = playerBody.rotation
                    };
                    Debug.Log($"storing at {index} with value {stateBuffer[index]} during rewind");

                    GamePlayer.ApplyForce(playerBody, inputBuffer[index]);
                    gameScene.Simulate(deltaTime);

                    rewindTick++;
                }

                Vector3 positionDelta = (previousPosition - playerBody.position);//*0.5f;
                Debug.Log($"position delta = {positionDelta}");
                //Debug.Log(Vector3.Lerp(playerBody.position, previousPosition, 0.5f));
                if (positionDelta.sqrMagnitude >= 4.0f)
                {
                    this.positionError = Vector3.zero;
                    this.rotationError = Quaternion.identity;
                }
                else
                {
                    this.positionError = positionDelta;
                    this.rotationError =
                        Quaternion.Inverse(playerBody.rotation) *
                        previousRotation;
                }
            }
        }

        if (correctionSmoothing.isOn)
        {
            Debug.Log($"position error = {positionError} a");
            this.positionError *= 0.1f;
            this.rotationError = Quaternion.Slerp(
                this.rotationError,
                Quaternion.identity,
                0.9f);

            Debug.Log($"position error = {positionError} b");
        }
        else
        {
            this.positionError = Vector3.zero;
            this.rotationError = Quaternion.identity;
        }

        Debug.Log($"position error = {positionError} f");
        playerBody.position += this.positionError;
        playerBody.rotation *= this.rotationError;
    }
}