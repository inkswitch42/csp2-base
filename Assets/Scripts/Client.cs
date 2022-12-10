using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class Client : MonoBehaviour
{
    private struct State
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    public float moveForce;
    public float jumpThreshold;
    public GameObject player;
    public Toggle errorCorrection;
    public Toggle correctionSmoothing;
    public Toggle redundantInput;

    private float currentTime;
    private int currentTick;
    private int latestTick;
    private const int BUFFER_SIZE = 1024;
    private Input[] inputBuffer; //predicted inputs
    private State[] stateBuffer; //predicted states
    private Vector3 positionError;
    private Quaternion rotationError;
    private NetworkDriver networkDriver;
    private NetworkConnection connection;
    private Scene scene;
    private PhysicsScene physicsScene;

    void Start()
    {
        currentTime = 0.0f;
        currentTick = 0;
        latestTick = 1;
        inputBuffer = new Input[BUFFER_SIZE];
        stateBuffer = new State[BUFFER_SIZE];
        positionError = Vector3.zero;
        rotationError = Quaternion.identity;
        connection = default(NetworkConnection);

        InitializeNetwork();
        InitializeScene();
    }

    public void OnErrorCorrectionToggle(bool value)
    {
        correctionSmoothing.interactable = value;
    }

    void Update()
    {
        ProcessNetworkEvents();
        AdvanceSimulation();
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
        NetworkEndPoint endpoint = NetworkEndPoint.Parse(Server.ADDRESS, Server.PORT);
        connection = networkDriver.Connect(endpoint);
    }

    private void InitializeScene()
    {
        scene = SceneManager.LoadScene("Background",
            new LoadSceneParameters()
            {
                loadSceneMode = LoadSceneMode.Additive,
                localPhysicsMode = LocalPhysicsMode.Physics3D
            });

        physicsScene = scene.GetPhysicsScene();

        SceneManager.MoveGameObjectToScene(player, scene);
    }

    private void ProcessNetworkEvents()
    {
        networkDriver.ScheduleUpdate().Complete();

        DataStreamReader reader;
        NetworkEvent.Type command;
        while ((command = connection.PopEvent(networkDriver, out reader)) != NetworkEvent.Type.Empty)
        {
            switch (command)
            {
                case NetworkEvent.Type.Connect:
                    Debug.Log("Connected to server");
                    break;
                case NetworkEvent.Type.Data:
                    Debug.Log("Received data");

                    StateMessage recievedState = StateMessage.Deserialize(ref reader);
                    ReconcileState(recievedState, Time.fixedDeltaTime);

                    //invoke ReconcileState
                    break;
                case NetworkEvent.Type.Disconnect:
                    Debug.Log("Disconnected froms server");
                    connection = default(NetworkConnection);
                    break;
            }
        }

        //simulate receiving a state message with a tick number from the server to rewind back to when/if we reconcile
        latestTick = Mathf.Clamp((currentTick - Random.Range(0, 15)), 0, currentTick);
    }

    private void AdvanceSimulation()
    {
        float deltaTime = Time.fixedDeltaTime;
        float time = this.currentTime;

        Rigidbody rigidbody = player.GetComponent<Rigidbody>();

        time += Time.deltaTime;
        while (time >= deltaTime)
        {
            time -= deltaTime;

            Input input = new()
            {
                up = UnityEngine.Input.GetKey(KeyCode.W),
                down = UnityEngine.Input.GetKey(KeyCode.S),
                right = UnityEngine.Input.GetKey(KeyCode.D),
                left = UnityEngine.Input.GetKey(KeyCode.A),
                jump = UnityEngine.Input.GetKey(KeyCode.Space)
            };

            int index = currentTick % BUFFER_SIZE;
            inputBuffer[index] = input;
            stateBuffer[index] = new()
            {
                position = rigidbody.position,
                rotation = rigidbody.rotation
            };

            ApplyForce(rigidbody, input);
            physicsScene.Simulate(deltaTime);

            SendInput();

            currentTick++;

        }

        this.currentTime = time;
    }

    private void ApplyForce(Rigidbody rigidbody, Input input)
    {
        Transform camera = Camera.main.transform;

        if (input.up)
        {
            rigidbody.AddForce(camera.forward * moveForce, ForceMode.Impulse);
        }

        if (input.down)
        {
            rigidbody.AddForce(-camera.forward * moveForce, ForceMode.Impulse);
        }

        if (input.right)
        {
            rigidbody.AddForce(camera.right * moveForce, ForceMode.Impulse);
        }

        if (input.left)
        {
            rigidbody.AddForce(-camera.right * moveForce, ForceMode.Impulse);
        }

        if (input.jump && rigidbody.transform.position.y <= jumpThreshold)
        {
            rigidbody.AddForce(camera.up * moveForce, ForceMode.Impulse);
        }
    }

    private void SendInput()
    {
        InputMessage message = new()
        {
            startTick = redundantInput.isOn ? latestTick : currentTick,
            inputs = new List<Input>()
        };

        //populate input(s)
        for (int index = message.startTick; index <= currentTick; index++)
        {
            message.inputs.Add(inputBuffer[index % BUFFER_SIZE]);
        }

        Debug.Log($"Client data: {message}");

        if (connection.GetState(networkDriver) == NetworkConnection.State.Connected)
        {
            networkDriver.BeginSend(connection, out DataStreamWriter writer);
            message.Serialize(ref writer);
            networkDriver.EndSend(writer);
        }
    }

    private void ReconcileState(StateMessage message, float deltaTime)
    {
        Rigidbody rigidbody = player.GetComponent<Rigidbody>();

        latestTick = message.tick;

        if (!errorCorrection.isOn)
        {
            return;
        }

        int index = message.tick % BUFFER_SIZE;
        State state = stateBuffer[index];
        Vector3 positionDelta = message.position - state.position;
        float rotationDelta = 1.0f - Quaternion.Dot(message.rotation, state.rotation);

        if (positionDelta.sqrMagnitude > 0.0000001f || rotationDelta > 0.00001f)
        {
            Debug.Log($"Correcting for error at tick {message.tick} "
                + $"(rewinding {(latestTick - message.tick)} ticks");

            Vector3 prevPosition = rigidbody.position + positionError;
            Quaternion prevRotation = rigidbody.rotation * rotationError;

            rigidbody.position = message.position;
            rigidbody.rotation = message.rotation;
            rigidbody.velocity = message.velocity;
            rigidbody.angularVelocity = message.angularVelocity;

            int rewindTick = message.tick;
            while (rewindTick < currentTick)
            {
                index = rewindTick % BUFFER_SIZE;
                stateBuffer[index] = new()
                {
                    position = rigidbody.position,
                    rotation = rigidbody.rotation
                };

                ApplyForce(rigidbody, inputBuffer[index]);
                physicsScene.Simulate(deltaTime);

                rewindTick++;
            }

            if ((prevPosition - rigidbody.position).sqrMagnitude >= 4.0f)
            {
                positionError = Vector3.zero;
                rotationError = Quaternion.identity;
            }
            else
            {
                positionError = prevPosition - rigidbody.position;
                rotationError =
                    Quaternion.Inverse(rigidbody.rotation) * prevRotation;
            }

        }

        if (correctionSmoothing.isOn)
        {
            positionError *= 0.9f;
            rotationError =
                Quaternion.Slerp(rotationError, Quaternion.identity, 0.1f);
        }
        else
        {
            positionError = Vector3.zero;
            rotationError = Quaternion.identity;
        }

        player.transform.position = rigidbody.position + positionError;
        player.transform.rotation = rigidbody.rotation * rotationError;
    }
}