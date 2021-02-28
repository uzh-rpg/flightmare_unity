// To allow for OBJ file import, you must download and include the TriLib Library
// into this project folder. Once the TriLib library has been included, you can enable
// OBJ importing by commenting out the following line.
#define TRILIB_DOES_NOT_EXIST

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization.Formatters.Binary;
// Array ops
using System.Linq;

// ZMQ/LCM
using NetMQ;

// Include JSON
using Newtonsoft.Json;

// Include message types
using MessageSpec;
using Unity.Collections;
using Unity.Jobs;
using System.IO;
using UnityEditor;

// Include postprocessing
// Dynamic scene management
using UnityEngine.SceneManagement;

namespace RPGFlightmare
{

  public class CameraController : MonoBehaviour
  {
    // ==============================================================================
    // Default Parameters 
    // ==============================================================================
    [HideInInspector]
    public const int pose_client_default_port = 10253;
    [HideInInspector]
    public const int video_client_default_port = 10254;
    [HideInInspector]
    public const string client_ip_default = "127.0.0.1";
    [HideInInspector]
    public const string client_ip_pref_key = "client_ip";
    [HideInInspector]
    public const int connection_timeout_seconds_default = 5;
    [HideInInspector]
    public string rpg_dsim_version = "";

    // ==============================================================================
    // Public Parameters
    // ==============================================================================

    // Inspector default parameters
    public string client_ip = client_ip_default;
    public int pose_client_port = pose_client_default_port;
    public int video_client_port = video_client_default_port;
    public const int connection_timeout_seconds = connection_timeout_seconds_default;

    public GameObject ecamera;
    public GameObject HD_camera;

    public GameObject quad_template;  // Main vehicle template
    public GameObject gate_template;  // Main vehicle template
    public GameObject splash_screen;
    public InputField input_ip;
    // default scenes and assets
    private string topLevelSceneName = "Top_Level_Scene";

    // NETWORK
    private NetMQ.Sockets.SubscriberSocket pull_socket;
    private NetMQ.Sockets.PublisherSocket push_socket;
    private bool socket_initialized = false;
    // setting message is also a kind of sub message,
    // but we only subscribe it for initialization.
    private SettingsMessage_t settings; // subscribed for initialization
    private SubMessage_t sub_message;  // subscribed for update
    private PubMessage_t pub_message; // publish messages, e.g., images, collision, etc.
                                      // Internal state & storage variables
    public Int64 current_time = 1;
    public Int64 time_last_frame = 1;
    public Int64 framerate = 30000;
    private bool timerStarted = false;
    private Int64 last_deltatime;
    private Int64 deltatime;
    private TimeStepMessage_t time_message;
    private UnityState_t internal_state;
    private Texture2D rendered_frame;
    private object socket_lock;
    // Unity Image Synthesis
    // post processing including object/category segmentation, 
    // optical flow, depth image. 
    private RPGImageSynthesis img_post_processing;
    private sceneSchedule scene_schedule;
    private Vector3 thirdPV_cam_offset;
    private int activate_vehicle_cam = 0;
    bool ready_to_render = false;
    bool not_started = true;
    private int count;
    private bool storing_frames = false;

    /* =====================
    * UNITY PLAYER EVENT HOOKS 
    * =====================
    */
    // Function called when Unity Player is loaded.
    // Only execute once.
    public void Start()
    {
      // Make sure that this gameobject survives across scene reloads
      DontDestroyOnLoad(this.gameObject);
      // Get application version
      rpg_dsim_version = Application.version;
      // Fixes for Unity/NetMQ conflict stupidity.
      AsyncIO.ForceDotNet.Force();
      socket_lock = new object();
      // Instantiate sockets
      InstantiateSockets();
      // Check if previously saved ip exists
      client_ip = PlayerPrefs.GetString(client_ip_pref_key, client_ip_default);
      //
      if (!Application.isEditor)
      {
        // Check if FlightGoggles should change its connection ports (for parallel operation)
        // Check if the program should use CLI arguments for IP.
        pose_client_port = Int32.Parse(GetArg("-input-port", "10253"));
        video_client_port = Int32.Parse(GetArg("-output-port", "10254"));
        // Check if the program should use CLI arguments for IP.
        string client_ip_from_cli = GetArg("-client-ip", "");
        if (client_ip_from_cli.Length > 0)
        {
          ConnectToClient(client_ip_from_cli);
        }
        else
        {
          ConnectToClient(client_ip);
        }
        Screen.fullScreen = false;
      }
      else
      {
        // Try to connect to the default ip
        ConnectToClient(client_ip);
      }

      // Initialize Internal State
      internal_state = new UnityState_t();
      // Do not try to do any processing this frame so that we can render our splash screen.
      internal_state.screenSkipFrames = 1;
      img_post_processing = GetComponent<RPGImageSynthesis>();

      scene_schedule = GetComponent<sceneSchedule>();
      
      ready_to_render = true;
      InitTime();
    }

    // Co-routine in Unity, executed every frame. 
    public IEnumerator WaitForRender()
    {
      // Wait until end of frame to transmit images
      while (true)
      {
        yield return new WaitForEndOfFrame();
        // Check if this frame should be rendered.
        if (internal_state.readyToRender && sub_message != null)
        {
          // Read the frame from the GPU backbuffer and send it via ZMQ.
          sendFrameOnWire();
        }
        yield return null;

      }
    }

    // Function called when Unity player is killed.
    private void OnApplicationQuit()
    {
      // Init simple splash screen
      splash_screen.GetComponentInChildren<Text>(true).text = "Welcome to RPG Flightmare!";
      // Close ZMQ sockets
      pull_socket.Close();
      push_socket.Close();
      Debug.Log("Terminated ZMQ sockets.");
      NetMQConfig.Cleanup();
    }

    void InstantiateSockets()
    {
      // Configure sockets
      Debug.Log("Configuring sockets.");
      pull_socket = new NetMQ.Sockets.SubscriberSocket();
      pull_socket.Options.ReceiveHighWatermark = 6;
      // Setup subscriptions.
      pull_socket.Subscribe("Pose");
      pull_socket.Subscribe("PointCloud");
      push_socket = new NetMQ.Sockets.PublisherSocket();
      push_socket.Options.Linger = TimeSpan.Zero; // Do not keep unsent messages on hangup.
      push_socket.Options.SendHighWatermark = 6; // Do not queue many images.
    }

    public void ConnectToClient(string inputIPString)
    {
      // pose_client_port=10270;
      // video_client_port=10271;
      Debug.Log("Trying to connect to: " + inputIPString);
      string pose_host_address = "tcp://" + inputIPString + ":" + pose_client_port.ToString();
      string video_host_address = "tcp://" + inputIPString + ":" + video_client_port.ToString();
      // Close ZMQ sockets
      pull_socket.Close();
      push_socket.Close();
      Debug.Log("Terminated ZMQ sockets.");
      NetMQConfig.Cleanup();

      // Reinstantiate sockets
      InstantiateSockets();

      // Try to connect sockets
      try
      {
        Debug.Log(pose_host_address);
        pull_socket.Connect(pose_host_address);
        push_socket.Connect(video_host_address);
        Debug.Log("Sockets bound.");
        // Save ip address for use on next boot.
        PlayerPrefs.SetString(client_ip_pref_key, inputIPString);
        PlayerPrefs.Save();
      }
      catch (Exception)
      {
        Debug.LogError("Input address from textbox is invalid. Note that hostnames are not supported!");
        throw;
      }

    }

    /* 
    * Update is called once per frame
    * Take the most recent ZMQ message and use it to position the cameras.
    * If there has not been a recent message, the renderer should probably pause rendering until a new request is received. 
    */
    void Update()
    {
      // Debug.LogError("Update: " + Time.deltaTime);
      if (pull_socket.HasIn || socket_initialized)
      {
        // if (splash_screen.activeSelf) splash_screen.SetActive(false);
        if (splash_screen != null && splash_screen.activeSelf)
          Destroy(splash_screen.gameObject);
        // Receive most recent message
        var msg = new NetMQMessage();
        var new_msg = new NetMQMessage();

        // Wait for a message from the client.
        bool received_new_packet = pull_socket.TryReceiveMultipartMessage(new TimeSpan(0, 0, connection_timeout_seconds), ref new_msg);

        if (!received_new_packet && socket_initialized)
        {
          // Close ZMQ sockets
          pull_socket.Close();
          push_socket.Close();
          // Debug.Log("Terminated ZMQ sockets.");
          NetMQConfig.Cleanup();
          Thread.Sleep(100); // [ms]
                             // Restart FlightGoggles and wait for a new connection.
          SceneManager.LoadScene(topLevelSceneName);
          // Initialize Internal State
          internal_state = new UnityState_t();
          // Kill this gameobject/controller script.
          Destroy(this.gameObject);
          // Don't bother with the rest of the script.
          return;
        }

        // Check if this is the latest message
        while (pull_socket.TryReceiveMultipartMessage(ref new_msg)) ;

        if ("Pose" == new_msg[0].ConvertToString())
        {
          // Check that we got the whole message
          if (new_msg.FrameCount >= msg.FrameCount) { msg = new_msg; }
          if (msg.FrameCount != 2) { return; }
          if (msg[1].MessageSize < 10) { return; }

          if (!internal_state.readyToRender)
          {
            settings = JsonConvert.DeserializeObject<SettingsMessage_t>(msg[1].ConvertToString());
            settings.InitParamsters();
            // Make sure that all objects are initialized properly
            initializeObjects(); // readyToRender set True if all objects are initialized.
            if (internal_state.readyToRender)
            {
              sendReady();
            }
            return; // no need to worry about the rest if not ready. 
          }
          else
          {
            pub_message = new PubMessage_t(settings);
            time_message = new TimeStepMessage_t();
            // after initialization, we only receive sub_message message of the vehicle. 
            sub_message = JsonConvert.DeserializeObject<SubMessage_t>(msg[1].ConvertToString());
            // Ensure that dynamic object settings such as depth-scaling and color are set correctly.
            updateDynamicObjectSettings();
            // Update position of game objects.
            updateObjectPositions();
            // Do collision detection
            updateVehicleCollisions();
            // Compute sensor data
            updateLidarData();
            //
            socket_initialized = true;
          }
        }
        else if ("PointCloud" == new_msg[0].ConvertToString())
        {
          PointCloudMessage_t pointcloud_msg = JsonConvert.DeserializeObject<PointCloudMessage_t>(new_msg[1].ConvertToString());
          SavePointCloud save_pointcloud = GetComponent<SavePointCloud>();

          // settings point cloud
          save_pointcloud.origin = ListToVector3(pointcloud_msg.origin);
          save_pointcloud.range = ListToVector3(pointcloud_msg.range);
          save_pointcloud.resolution = pointcloud_msg.resolution;
          save_pointcloud.path = pointcloud_msg.path;
          save_pointcloud.fileName = pointcloud_msg.file_name;

          PointCloudTask(save_pointcloud);
        }
      }
      if (ready_to_render && not_started)
      {
        not_started = false;
        StartCoroutine(WaitForRender());
      }
    }

    // initialize time function for event camera
    void InitTime()
    {
      // initilaize time at one nanosecond for stability
      current_time = 1;
      time_last_frame = current_time;
      timerStarted = true;
    }
    // update time function 
    public void UpdateTimeFct(Int64 nexttimestep)
    {
      current_time = current_time + nexttimestep;
      last_deltatime = deltatime;
      deltatime = nexttimestep;
    }
    // update time function when also rgb camera rendered
    public void UpdateTimeFct(Int64 nexttimestep, bool rgb)
    {
      current_time = current_time + nexttimestep;
      time_last_frame = current_time;
      last_deltatime = deltatime;
      deltatime = nexttimestep;
    }
    /* ==================================
    * FlightGoggles High Level Functions 
    * ==================================
    */
    // Tries to initialize uninitialized objects multiple times until the object is initialized.
    // When everything is initialized, this function will NOP.
    void initializeObjects()
    {
      // Initialize Screen & keep track of frames to skip
      internal_state.screenSkipFrames = Math.Max(0, internal_state.screenSkipFrames - 1);

      // NOP if Unity wants us to skip this frame.
      if (internal_state.screenSkipFrames > 0)
      {
        return;
      }

      // Run initialization steps in order.
      switch (internal_state.initializationStep)
      {
        // Load scene if needed.
        case 0:
          Debug.Log("Destory Time Line");
          loadScene();
          internal_state.initializationStep++;
          // Takes one frame to take effect.
          internal_state.screenSkipFrames++;
          // Skip the rest of this frame
          break;

        // Initialize screen if scene is fully loaded and ready.
        case 1:
          Debug.Log("resize screen");
          resizeScreen();
          internal_state.initializationStep++;
          // Takes one frame to take effect.
          internal_state.screenSkipFrames++;
          // Skip the rest of this frame
          break;

        // Initialize gameobjects if screen is ready to render.
        case 2:
          Debug.Log("instantiate object");
          instantiateObjects();
          instantiateCameras();
          internal_state.initializationStep++;
          // Takes one frame to take effect.
          internal_state.screenSkipFrames++;
          // Skip the rest of this frame
          break;

        // Ensure cameras are rendering to correct portion of GPU backbuffer.
        // Note, this should always be run after initializing the cameras.
        case 3:
          Debug.Log("set camera view ports");
          setCameraViewports();
          // Go to next step.
          internal_state.initializationStep++;
          // Takes one frame to take effect.
          internal_state.screenSkipFrames++;
          // Skip the rest of this frame
          break;

        case 4:
          Debug.Log("set camera post process settings");
          setCameraPostProcessSettings();
          enableCollidersAndLandmarks();
          // Set initialization to -1 to indicate that we're done initializing.
          internal_state.initializationStep = -1;
          // Takes one frame to take effect.
          internal_state.screenSkipFrames++;
          // Skip the rest of this frame
          break;
          // If initializationStep does not match any of the ones above
          // then initialization is done and we need do nothing more.

      }
    }
    void loadScene()
    {
      scene_schedule.loadScene(settings.scene_id, false);
    }

    void setCameraPostProcessSettings()
    {
      foreach (Vehicle_t vehicle_i in settings.vehicles)
      {
        foreach (Camera_t camera in vehicle_i.cameras)
        {
          string camera_ID = camera.ID;
          // Get the camera object, create if not exist
          ObjectState_t internal_object_state = internal_state.getWrapperObject(camera_ID, HD_camera);
          GameObject obj = internal_object_state.gameObj;
        }
        foreach (EventCamera_t camera in vehicle_i.eventcameras)
        {
          string camera_ID = camera.ID;
          // Get the camera object, create if not exist
          ObjectState_t internal_object_state = internal_state.getWrapperObject(camera_ID, ecamera);
          GameObject obj = internal_object_state.gameObj;
        }
      }
    }

    void instantiateCameras()
    {
      // Disable cameras in scene
      foreach (Camera c in FindObjectsOfType<Camera>())
      {
        Debug.Log("Disable extra camera!");
        c.enabled = false;
      }
      foreach (var vehicle_i in settings.vehicles)
      {
        foreach (Camera_t camera in vehicle_i.cameras)
        {
          Debug.Log(camera.ID);
          // Get camera object
          GameObject obj = internal_state.getGameobject(camera.ID, HD_camera);
          var currentCam = obj.GetComponent<Camera>();
          currentCam.fieldOfView = camera.fov;
          // apply translation and rotation;
          var translation = ListToVector3(vehicle_i.position);
          var quaternion = ListToQuaternion(vehicle_i.rotation);
          var scale = new Vector3(1, 1, 1);

          Matrix4x4 T_WB = Matrix4x4.TRS(translation, quaternion, scale);
          var T_BC = ListToMatrix4x4(camera.T_BC);
          // translate camera from body frame to world frame
          var T_WC = T_WB * T_BC;
          // compute camera position and rotation with respect to world frame
          var position = new Vector3(T_WC[0, 3], T_WC[1, 3], T_WC[2, 3]);
          var rotation = T_WC.rotation;
          obj.transform.SetPositionAndRotation(position, rotation);
        }
        foreach (EventCamera_t camera in vehicle_i.eventcameras)
        {
          // Get camera object
          GameObject obj_ = internal_state.getGameobject(camera.ID, ecamera);
          var currentCam = obj_.GetComponent<Camera>();
          currentCam.fieldOfView = camera.fov;
          // apply translation and rotation;
          var translation = ListToVector3(vehicle_i.position);
          var quaternion = ListToQuaternion(vehicle_i.rotation);
          var scale = new Vector3(1, 1, 1);

          Matrix4x4 T_WB = Matrix4x4.TRS(translation, quaternion, scale);
          var T_BC = ListToMatrix4x4(camera.T_BC);
          // translate camera from body frame to world frame
          var T_WC = T_WB * T_BC;
          // compute camera position and rotaeventcamerastion with respect to world frame
          var position = new Vector3(T_WC[0, 3], T_WC[1, 3], T_WC[2, 3]);
          var rotation = T_WC.rotation;
          obj_.transform.SetPositionAndRotation(position, rotation);
        }
      }
      {
        // instantiate third person view camera
        GameObject tpv_obj = internal_state.getGameobject(settings.mainVehicle.ID + "_ThirdPV", HD_camera);
        var thirdPV_cam = tpv_obj.GetComponent<Camera>();
        // hard coded parameters for third person camera view
        thirdPV_cam.fieldOfView = 90.0f;
        thirdPV_cam_offset = new Vector3(0.0f, 2.0f, -4.0f);
        GameObject main_vehicle = internal_state.getGameobject(settings.mainVehicle.ID, quad_template);
        thirdPV_cam.transform.position = main_vehicle.transform.position + thirdPV_cam_offset;
        thirdPV_cam.transform.eulerAngles = new Vector3(20, 0, 0);
      }
    }

    // Update object and camera positions based on the positions sent by ZMQ.
    void updateObjectPositions()
    {
      if (internal_state.readyToRender)
      {
        // always activate vehcile cam  
        {
          activate_vehicle_cam = 1;
          if (activate_vehicle_cam > settings.numVehicles * settings.numCameras)
          {
            activate_vehicle_cam = 0;
          }
        }
        // Update camera positions
        int vehicle_count = 0;
        foreach (Vehicle_t vehicle_i in sub_message.vehicles)
        {
          foreach (Camera_t camera in vehicle_i.cameras)
          {
            vehicle_count += 1;
            // string camera_ID = vehicle_i.ID + "_" + camera.ID;
            // Get camera object
            GameObject obj = internal_state.getGameobject(camera.ID, HD_camera);
            // 
            var currentCam = obj.GetComponent<Camera>();
            currentCam.fieldOfView = camera.fov;
            // apply translation and rotation;
            var translation = ListToVector3(vehicle_i.position);
            var quaternion = ListToQuaternion(vehicle_i.rotation);
            // Debug.Log(vehicle_i.ID);
            // Debug.Log(vehicle_i.rotation[0]);
            // Debug.Log(vehicle_i.rotation[1]);
            // Debug.Log(vehicle_i.rotation[2]);
            // Debug.Log(vehicle_i.rotation[3]);
            // Debug.Log(quaternion.eulerAngles);
            Quaternion unity_quat = Quaternion.Euler(quaternion.eulerAngles);
            var scale = new Vector3(1, 1, 1);
            Matrix4x4 T_WB = Matrix4x4.TRS(translation, unity_quat, scale);
            var T_BC = ListToMatrix4x4(camera.T_BC);
            // translate camera from body frame to world frame
            var T_WC = T_WB * T_BC;
            //
            var position = new Vector3(T_WC[0, 3], T_WC[1, 3], T_WC[2, 3]);
            //
            var rotation = T_WC.rotation;
            obj.transform.SetPositionAndRotation(position, rotation);
            //
            if (vehicle_count == activate_vehicle_cam)
            {
              currentCam.targetDisplay = 0;

            }
            else
            {
              currentCam.targetDisplay = 1;
            }
          }
          vehicle_count = 0;
          // Update camera positions and set parameters
          foreach (EventCamera_t camera in vehicle_i.eventcameras)
          {
            vehicle_count += 1;
            // Get camera object
            GameObject obj = internal_state.getGameobject(camera.ID, ecamera);
            // 
            var currentCam = obj.GetComponent<Camera>();
            currentCam.fieldOfView = camera.fov;
            eventsCompute eventcreation = obj.GetComponent<eventsCompute>();
            eventcreation.pos_threshold = camera.Cp;
            eventcreation.neg_threshold = camera.Cm;
            eventcreation.sigma_cp = camera.sigma_Cp;
            eventcreation.sigma_cm = camera.sigma_Cm;
            // conversion to microseconds
            UInt64 refractory_per = (camera.refractory_period_ns / 1000);
            eventcreation.refractory_period = (int)(camera.refractory_period_ns);
            eventcreation.log_eps = camera.log_eps;
            eventcreation.SetTime(current_time, deltatime);

            // apply translation and rotation;
            var translation = ListToVector3(vehicle_i.position);
            var quaternion = ListToQuaternion(vehicle_i.rotation);
            // Debug.Log(vehicle_i.ID);
            // Debug.Log(vehicle_i.rotation[0]);
            // Debug.Log(vehicle_i.rotation[1]);
            // Debug.Log(vehicle_i.rotation[2]);
            // Debug.Log(vehicle_i.rotation[3]);
            // Debug.Log(quaternion.eulerAngles);
            Quaternion unity_quat = Quaternion.Euler(quaternion.eulerAngles);
            var scale = new Vector3(1, 1, 1);
            Matrix4x4 T_WB = Matrix4x4.TRS(translation, unity_quat, scale);
            var T_BC = ListToMatrix4x4(camera.T_BC);
            // translate camera from body frame to world frame
            var T_WC = T_WB * T_BC;
            //
            var position = new Vector3(T_WC[0, 3], T_WC[1, 3], T_WC[2, 3]);
            //
            var rotation = T_WC.rotation;
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
          }
          // Apply translation, rotation, and scaling to vehicle
          GameObject vehicle_obj = internal_state.getGameobject(vehicle_i.ID, quad_template);
          vehicle_obj.transform.SetPositionAndRotation(ListToVector3(vehicle_i.position), ListToQuaternion(vehicle_i.rotation));
          vehicle_obj.transform.localScale = ListToVector3(vehicle_i.size);
        }

        foreach (Object_t obj_state in sub_message.objects)
        {
          // Apply translation, rotation, and scaling
          // GameObject prefab = Resources.Load(obj_state.prefabID) as GameObject;
          GameObject other_obj = internal_state.getGameobject(obj_state.ID, gate_template);
          other_obj.transform.SetPositionAndRotation(ListToVector3(obj_state.position), ListToQuaternion(obj_state.rotation));
          other_obj.transform.localScale = ListToVector3(obj_state.size);
        }
        {
          // third person view camera
          GameObject tpv_obj = internal_state.getGameobject(settings.mainVehicle.ID + "_ThirdPV", HD_camera);
          GameObject main_vehicle = internal_state.getGameobject(settings.mainVehicle.ID, quad_template);
          Vector3 newPos = main_vehicle.transform.position + thirdPV_cam_offset;
          tpv_obj.transform.position = Vector3.Slerp(tpv_obj.transform.position, newPos, 0.5f);
          if ((activate_vehicle_cam == 0) || (settings.numCameras == 0 && settings.numEventCameras == 0))
          {
            tpv_cam.targetDisplay = 0;

          }
          else
          {
            tpv_cam.targetDisplay = 1;
          }
        }

      }
    }

    void updateDynamicObjectSettings()
    {
      // Update object settings.
      if (internal_state.readyToRender)
      {
        // Update depth cameras with dynamic depth scale.
        var depthCameras = sub_message.mainVehicle.cameras.Where(obj => obj.isDepth);
        foreach (var objState in depthCameras)
        {
          // Get object
          GameObject obj = internal_state.getGameobject(objState.ID, HD_camera);
        }
      }
    }

    void updateVehicleCollisions()
    {
      if (internal_state.readyToRender)
      {
        int vehicle_count = 0;
        foreach (var vehicl_i in settings.vehicles)
        {
          vehicle_count += 1;
          GameObject vehicle_obj = internal_state.getGameobject(vehicl_i.ID, quad_template);
          // Check if object has collided (requires that collider hook has been setup on object previously.)
          pub_message.pub_vehicles[vehicle_count - 1].collision = vehicle_obj.GetComponent<collisionHandler>().hasCollided;
        }
      }
    }

    // Update Lidar data
    void updateLidarData()
    {
      if (internal_state.readyToRender)
      {
        // Update camera positions
        int vehicle_count = 0;
        RaycastHit lidarHit;
        foreach (Vehicle_t vehicle_i in sub_message.vehicles)
        {
          vehicle_count += 1;
          if (vehicle_i.lidars.Count <= 0)
          {
            continue;
          }

          var lidar = vehicle_i.lidars[0];
          // apply translation and rotation;
          var translation = ListToVector3(vehicle_i.position);
          var quaternion = ListToQuaternion(vehicle_i.rotation);
          Matrix4x4 T_WB = Matrix4x4.TRS(translation, quaternion, new Vector3(1, 1, 1));
          var T_BS = ListToMatrix4x4(lidar.T_BS);
          // translate lidar from body frame to world frame
          var T_WS = T_WB * T_BS;
          //
          var lidar_position = new Vector3(T_WS[0, 3], T_WS[1, 3], T_WS[2, 3]);
          var lidar_rotation = T_WS.rotation;
          float angle_resolution = (lidar.num_beams == 1) ?
              (lidar.end_angle - lidar.start_angle) / lidar.num_beams :
              (lidar.end_angle - lidar.start_angle) / (lidar.num_beams - 1);
          for (int beam_i = 0; beam_i < lidar.num_beams; beam_i++)
          {

            float angle_i = lidar.start_angle + angle_resolution * beam_i;
            // Find direction and origin of raytrace for LIDAR. 
            var raycastDirection = lidar_rotation * new Vector3((float)Math.Cos(angle_i), 0, (float)Math.Sin(angle_i));
            // Run the raytrace
            bool hasHit = Physics.Raycast(lidar_position, raycastDirection, out lidarHit, lidar.max_distance);
            // Get distance. Return max+1 if out of range
            float raycastDistance = hasHit ? lidarHit.distance : lidar.max_distance + 1;
            // Save the result of the raycast.
            // vehicle_collisions.Add(raycastDistance); // don't use add.. 
            pub_message.pub_vehicles[vehicle_count - 1].lidar_ranges.Add(raycastDistance);
            Debug.DrawLine(lidar_position, lidar_position + raycastDirection * raycastDistance, Color.red);
          }
        }
      }
    }

    /* =============================================
    * FlightGoggles Initialization Functions 
    * =============================================
    */
    void resizeScreen()
    {
      // Set the max framerate to something very high
      Application.targetFrameRate = 100000000;
      Screen.SetResolution(settings.screenWidth, settings.screenHeight, false);
      // Set render texture to the correct size
      rendered_frame = new Texture2D(settings.camWidth, settings.camHeight, TextureFormat.RGB24, false, true);
    }

    void enableCollidersAndLandmarks()
    {
      // Enable object colliders in scene
      foreach (Collider c in FindObjectsOfType<Collider>())
      {
        c.enabled = true;
      }
      // Disable unneeded vehicle colliders
      var nonRaycastingVehicles = settings.vehicles.Where(obj => !obj.hasCollisionCheck);
      foreach (Vehicle_t vehicle in nonRaycastingVehicles){
          ObjectState_t internal_object_state = internal_state.getWrapperObject(vehicle.ID, quad_template);
          // Get vehicle collider
          Collider vehicleCollider = internal_object_state.gameObj.GetComponent<Collider>();
          vehicleCollider.enabled = false;
      }
      //UNload unused assets
      Resources.UnloadUnusedAssets();
    }

    void instantiateObjects()
    {
      // Initialize additional objects
      foreach (var obj_state in settings.objects)
      {
        // GameObject prefab = Resources.Load(obj_state.prefabID) as GameObject;
        Debug.Log("obj_state id : " + obj_state.ID);
        GameObject obj = internal_state.getGameobject(obj_state.ID, gate_template);
        obj.transform.localScale = ListToVector3(obj_state.size);
        // obj.layer = 9;
      }
      foreach (var vehicle in settings.vehicles)
      {
        Debug.Log("vehicle id : " + vehicle.ID);
        GameObject obj = internal_state.getGameobject(vehicle.ID, quad_template);
        obj.transform.SetPositionAndRotation(ListToVector3(vehicle.position), ListToQuaternion(vehicle.rotation));
        obj.transform.localScale = ListToVector3(vehicle.size);
      }
    }
    void setCameraViewports()
    {
      int vehicle_count = 0;
      foreach (var vehicle_i in settings.vehicles)
      {
        foreach (Camera_t camera in vehicle_i.cameras)
        {
          vehicle_count += 1;
          {
            // Get object
            GameObject obj = internal_state.getGameobject(camera.ID, HD_camera);
            var currentCam = obj.GetComponent<Camera>();
            Debug.Log("settins width and height " + settings.camHeight + "/" + settings.camWidth);
            // Make sure camera renders to the correct portion of the screen.
            // currentCam.pixelRect = new Rect(settings.camWidth * camera.outputIndex, 0,
            //   settings.camWidth * (camera.outputIndex + 1), settings.camHeight);
            // currentCam.pixelRect = new Rect(0, 0,
            //     settings.camWidth, settings.camHeight);
            // enable Camera.
            if (vehicle_count == activate_vehicle_cam)
            {
              currentCam.targetDisplay = 0;

            }
            else
            {
              currentCam.targetDisplay = 1;
            }
            int layer_id = 0;
            foreach (var layer_on in camera.enabledLayers)
            {
              if (layer_on)
              {
                string filter_ID = camera.ID + "_" + layer_id.ToString();
                var cam_filter = img_post_processing.CreateHiddenCamera(filter_ID,
                    img_post_processing.image_modes[layer_id], camera.fov, currentCam);
                if (!internal_state.camera_filters.ContainsKey(filter_ID))
                {
                  internal_state.camera_filters[filter_ID] = cam_filter;
                }
              }
              layer_id += 1;
            }
          }
        }
        vehicle_count = 0;
        foreach (EventCamera_t camera in vehicle_i.eventcameras)
        {
          vehicle_count += 1;
          {
            // Get object
            GameObject obj = internal_state.getGameobject(camera.ID, ecamera);
            var currentCam = obj.GetComponent<Camera>();
            currentCam.enabled = true;
            
            // Make sure camera renders to the correct portion of the screen.
            // currentCam.pixelRect = new Rect(settings.camWidth * camera.outputIndex, 0, 
            // settings.camWidth * (camera.outputIndex+1), settings.camHeight);
            currentCam.pixelRect = new Rect(0, 0,
                settings.camWidth, settings.camHeight);
            // enable Camera.
            obj.SetActive(true);
            string _name = camera.ID + "_" + "log";
            var cam_filter_ = img_post_processing.CreateHiddenLogCamera(_name, camera.fov, currentCam);

            if (!internal_state.camera_filters.ContainsKey(_name))
            {
              internal_state.camera_filters[_name] = cam_filter_;
            }

            string name = camera.ID + "_" + "event";
            var event_cam = img_post_processing.CreateEventCamera(name, camera.fov, currentCam);

            // TODO: maybe add a new member function specifically for the optical flow
            string cam_name = camera.ID + "_" + "of";
            var cam_filter = img_post_processing.CreateHiddenOFCamera(cam_name,
              camera.fov, currentCam);

            if (!internal_state.camera_filters.ContainsKey(cam_name))
            {
              internal_state.camera_filters[cam_name] = cam_filter;
            }


          }
        }
      }
      img_post_processing.OnSceneChange();
    }

    void sendReady()
    {
      ReadyMessage_t metadata = new ReadyMessage_t(internal_state.readyToRender);
      var msg = new NetMQMessage();
      msg.Append(JsonConvert.SerializeObject(metadata));
      if (push_socket.HasOut)
      {
        push_socket.TrySendMultipartMessage(msg);
      }
    }

    // Reads a scene frame from the GPU backbuffer and sends it via ZMQ.
    void sendFrameOnWire()
    {
      // Get metadata
      pub_message.frame_id = sub_message.frame_id;
      // Create packet metadata
      var msg = new NetMQMessage();
      msg.Append(JsonConvert.SerializeObject(pub_message));

      int vehicle_count = 0;
      foreach (var vehicle_i in settings.vehicles)
      {
        foreach (var cam_config in vehicle_i.cameras)
        {
          vehicle_count += 1;
          // Length of RGB slice
          GameObject vehicle_obj = internal_state.getGameobject(vehicle_i.ID, quad_template);
          GameObject obj = internal_state.getGameobject(cam_config.ID, HD_camera);
          var current_cam = obj.GetComponent<Camera>();
          var raw = readImageFromHiddenCamera(current_cam, cam_config);
          msg.Append(raw);
          int layer_id = 0;
          {
            foreach (var layer_on in cam_config.enabledLayers)
            {
              if (layer_on)
              {
                string filter_ID = cam_config.ID + "_" + layer_id.ToString();
                {
                  var rawimage = img_post_processing.getRawImage(internal_state.camera_filters[filter_ID],
                      settings.camWidth, settings.camHeight, img_post_processing.image_modes[layer_id]);
                  msg.Append(rawimage);
                }

              }
              layer_id += 1;
            }
          }

        }
        foreach (var cam_config in vehicle_i.eventcameras)
        {
          vehicle_count += 1;
          GameObject vehicle_obj = internal_state.getGameobject(vehicle_i.ID, quad_template);
          //get camera object
          GameObject obj = internal_state.getGameobject(cam_config.ID, ecamera);

          var current_cam = obj.GetComponent<Camera>();
          // get eventcamera component
          var script = obj.GetComponent<eventsCompute>();
          // get rgb image of eventcamera
          var raw = readImageFromHiddenCamera(current_cam, cam_config);

          var cam_name = cam_config.ID + "_" + "of";
          // internal_state.camera_filters[cam_name] is the camera needed
          // compute next timestep
          Int64 delta_time = img_post_processing.getDeltaTime(internal_state.camera_filters[cam_name], settings.camWidth, settings.camHeight, deltatime);

          bool entering_timefct = false;
          // control whether the time step applies or whther a general image renderin is necessary
          if ((time_last_frame + framerate) <= (delta_time + current_time))
          {
            time_message.next_timestep = (time_last_frame + framerate - current_time);
            entering_timefct = true;
          }
          else time_message.next_timestep = delta_time;
          msg.Append(raw);

          var buff = script.getoutput();

          if (script.GetTime() != current_time)
          {
            Debug.LogError("time functions do not match");
          }

          var buff_ = new EventsMessage_t(buff);
          var bytes = JsonConvert.SerializeObject(buff_);
          msg.Append(bytes);
          // TODO: check which time step applies rgb or the other
          time_message.rgb_frame = storing_frames;
          time_message.current_time = current_time;

          msg.Append(JsonConvert.SerializeObject(time_message));
          // update time function based on 
          if (entering_timefct)
          {
            UpdateTimeFct(time_message.next_timestep, time_message.rgb_frame);
            storing_frames = true;
          }
          else
          {
            UpdateTimeFct(time_message.next_timestep);
            storing_frames = false;
          }
        }
      }
      if (push_socket.HasOut)
      {
        push_socket.SendMultipartMessage(msg);
      }
    }

    byte[] readImageFromScreen(Camera_t cam_config)
    {
      rendered_frame.ReadPixels(new Rect(0, 0, cam_config.width, cam_config.height), 0, 0);
      rendered_frame.Apply();
      byte[] raw = rendered_frame.GetRawTextureData();
      return raw;
    }
    byte[] readImageFromHiddenCamera(Camera subcam, Camera_t cam_config)
    {
      var templRT = RenderTexture.GetTemporary(cam_config.width, cam_config.height, 24);
      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;
      RenderTexture.active = templRT;
      subcam.targetTexture = templRT;
      subcam.pixelRect = new Rect(0, 0,
          cam_config.width, cam_config.height);
      subcam.fieldOfView = cam_config.fov;

      subcam.Render();
      var image = new Texture2D(cam_config.width, cam_config.height, TextureFormat.RGB24, false, true);
      image.ReadPixels(new Rect(0, 0, cam_config.width, cam_config.height), 0, 0);
      image.Apply();
      byte[] raw = image.GetRawTextureData();
      //
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;
      //
      RenderTexture.ReleaseTemporary(templRT);
      UnityEngine.Object.Destroy(image);
      //
      return raw;
    }
    byte[] readImageFromHiddenCamera(Camera subcam, EventCamera_t cam_config)
    {
      var templRT = RenderTexture.GetTemporary(cam_config.width, cam_config.height, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 8);
      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;
      RenderTexture.active = templRT;
      subcam.targetTexture = templRT;

      subcam.pixelRect = new Rect(0, 0,
          cam_config.width, cam_config.height);
      subcam.fieldOfView = cam_config.fov;
      
      subcam.Render();
      var image = new Texture2D(cam_config.width, cam_config.height, TextureFormat.RGB24, false, true);
      image.ReadPixels(new Rect(0, 0, cam_config.width, cam_config.height), 0, 0);
      image.Apply();
      byte[] raw = image.GetRawTextureData();
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;
      RenderTexture.ReleaseTemporary(templRT);
      UnityEngine.Object.Destroy(image);
      return raw;
    }

    /* ==================================
    * FlightGoggles Helper Functions 
    * ==================================
    */

    // Helper function for getting command line arguments
    private static string GetArg(string name, string default_return)
    {
      var args = System.Environment.GetCommandLineArgs();
      for (int i = 0; i < args.Length; i++)
      {
        if (args[i] == name && args.Length > i + 1)
        {
          return args[i + 1];
        }
      }
      return default_return;
    }

    // Helper functions for converting list -> vector
    public static Vector3 ListToVector3(IList<float> list) { return new Vector3(list[0], list[1], list[2]); }
    public static Quaternion ListToQuaternion(IList<float> list) { return new Quaternion(list[0], list[1], list[2], list[3]); }
    public static Matrix4x4 ListToMatrix4x4(IList<float> list)
    {
      Matrix4x4 rot_mat = Matrix4x4.zero;
      rot_mat[0, 0] = list[0]; rot_mat[0, 1] = list[1]; rot_mat[0, 2] = list[2]; rot_mat[0, 3] = list[3];
      rot_mat[1, 0] = list[4]; rot_mat[1, 1] = list[5]; rot_mat[1, 2] = list[6]; rot_mat[1, 3] = list[7];
      rot_mat[2, 0] = list[8]; rot_mat[2, 1] = list[9]; rot_mat[2, 2] = list[10]; rot_mat[2, 3] = list[11];
      rot_mat[3, 0] = list[12]; rot_mat[3, 1] = list[13]; rot_mat[3, 2] = list[14]; rot_mat[3, 3] = list[15];
      return rot_mat;
    }
    public static Color ListHSVToColor(IList<float> list) { return Color.HSVToRGB(list[0], list[1], list[2]); }

    // Helper functions for converting  vector -> list
    public static List<float> Vector3ToList(Vector3 vec) { return new List<float>(new float[] { vec[0], vec[1], vec[2] }); }
    public static List<float> Vector2ToList(Vector2 vec) { return new List<float>(new float[] { vec[0], vec[1] }); }

    public void startSim()
    {
      ConnectToClient(input_ip.text);
      // Init simple splash screen
      Text text_obj = splash_screen.GetComponentInChildren<Text>(true);
      text_obj.text = "Connected, waiting for ROS client...";

    }
    public void quiteSim() { Application.Quit(); }

    async void PointCloudTask(SavePointCloud save_pointcloud)
    {
      await save_pointcloud.GeneratePointCloud();
    }

  }
}
