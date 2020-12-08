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
using UnityEngine.Rendering.PostProcessing;
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

    // public bool DEBUG = false;
    // public bool outputLandmarkLocations = false;
    public GameObject HD_camera;
    public GameObject quad_template;  // Main vehicle template
    public GameObject gate_template;  // Main vehicle template
    public GameObject splash_screen;
    public GameObject PointCloudSaved;
    public InputField input_ip;
    public bool enable_flying_cam = false;
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

    /* =====================
    * UNITY PLAYER EVENT HOOKS 
    * =====================
    */
    // Function called when Unity Player is loaded.
    // Only execute once.
    public void Start()
    {
      // Application.targetFrameRate = 9999;
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
        // Check if the program should use CLI arguments for IP.
        // obstacle_perturbation_file = GetArg("-obstacle-perturbation-file", "");
        // Disable fullscreen.
        Screen.fullScreen = false;
        Screen.SetResolution(1024, 768, false);
      }
      else
      {
        // Try to connect to the default ip
        ConnectToClient(client_ip);
      }

      // Init simple splash screen
      // Text text_obj = splash_screen.GetComponentInChildren<Text>(true);
      // input_ip.text = client_ip;
      // text_obj.text = "Welcome to RPG Flightmare!";
      // splash_screen.SetActive(true);

      // Initialize Internal State
      internal_state = new UnityState_t();
      // Do not try to do any processing this frame so that we can render our splash screen.
      internal_state.screenSkipFrames = 1;
      // cameraFilter.CameraFilterInit(HD_camera);
      img_post_processing = GetComponent<RPGImageSynthesis>();

      scene_schedule = GetComponent<sceneSchedule>();
      //
      StartCoroutine(WaitForRender());
    }

    // Co-routine in Unity, executed every frame. 
    public IEnumerator WaitForRender()
    {
      // Wait until end of frame to transmit images
      while (true)
      {
        // Wait until all rendering + UI is done.
        // Blocks until the frame is rendered.
        // Debug.Log("Wait for end of frame: " + Time.deltaTime);
        yield return new WaitForEndOfFrame();
        // Check if this frame should be rendered.
        if (internal_state.readyToRender && sub_message != null)
        {
          // Debug.Log("Ready to Render.");
          // Read the frame from the GPU backbuffer and send it via ZMQ.
          sendFrameOnWire();
        }
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
      // Debug.Log("Update: " + Time.deltaTime);
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
      else
      {
        // Throttle to 10hz when idle
        Thread.Sleep(1); // [ms]
      }
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
          // imgPostProcessingUpdate();
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
      // scene_schedule.destoryTimeLine();
      // if(settings.scene_id != scene_schedule.scenes.default_scene_id)
      // {
      scene_schedule.loadScene(settings.scene_id, false);
      // }
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
          // 
          var currentCam = obj.GetComponent<Camera>();
          currentCam.fieldOfView = camera.fov;
          currentCam.nearClipPlane = camera.nearClipPlane[0];
          currentCam.farClipPlane = camera.farClipPlane[0];
          // apply translation and rotation;
          var translation = ListToVector3(vehicle_i.position);
          var quaternion = ListToQuaternion(vehicle_i.rotation);
          var scale = new Vector3(1, 1, 1);

          Matrix4x4 T_WB = Matrix4x4.TRS(translation, quaternion, scale);
          var T_BC = ListToMatrix4x4(camera.T_BC);
          // translate camera from body frame to world frame
          var T_WC = T_WB * T_BC;
          Debug.Log(T_WC);
          // compute camera position and rotation with respect to world frame
          var position = new Vector3(T_WC[0, 3], T_WC[1, 3], T_WC[2, 3]);
          var rotation = T_WC.rotation;
          obj.transform.SetPositionAndRotation(position, rotation);
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
        if (Input.GetKeyDown(KeyCode.Space))
        {
          activate_vehicle_cam += 1;
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
            Debug.Log(vehicle_i.ID);
            Debug.Log(vehicle_i.rotation[0]);
            Debug.Log(vehicle_i.rotation[1]);
            Debug.Log(vehicle_i.rotation[2]);
            Debug.Log(vehicle_i.rotation[3]);
            Debug.Log(quaternion.eulerAngles);

            // Quaternion To Matrix conversion failed because input Quaternion(=quaternion) is invalid
            // create valid Quaternion from Euler angles
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
          // Debug.Log("xxxxxxxxx" + vehicle_i.ID);
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
          var tpv_cam = tpv_obj.GetComponent<Camera>();
          if ((activate_vehicle_cam == 0) || (settings.numCameras == 0))
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
            // Make sure camera renders to the correct portion of the screen.
            // currentCam.pixelRect = new Rect(settings.camWidth * camera.outputIndex, 0, 
            // settings.camWidth * (camera.outputIndex+1), settings.camHeight);
            currentCam.pixelRect = new Rect(0, 0,
                settings.camWidth, settings.camHeight);
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
                    img_post_processing.image_modes[layer_id], camera.fov, camera.nearClipPlane[layer_id + 1], camera.farClipPlane[layer_id + 1], currentCam);
                if (!internal_state.camera_filters.ContainsKey(filter_ID))
                {
                  internal_state.camera_filters[filter_ID] = cam_filter;
                }
              }
              layer_id += 1;
            }
          }
        }
      }
      {
        GameObject tpv_obj = internal_state.getGameobject(settings.mainVehicle.ID + "_ThirdPV", HD_camera);
        tpv_obj.GetComponent<Camera>().pixelRect = new Rect(0, 0,
                settings.screenWidth, settings.screenHeight);
        var tpv_cam = tpv_obj.GetComponent<Camera>();
        if ((activate_vehicle_cam == 0) || (settings.numCameras == 0))
        {
          tpv_cam.targetDisplay = 0;
        }
        else
        {
          tpv_cam.targetDisplay = 1;
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
          // string camera_ID = cam_config.ID;
          GameObject vehicle_obj = internal_state.getGameobject(vehicle_i.ID, quad_template);
          //
          GameObject obj = internal_state.getGameobject(cam_config.ID, HD_camera);
          var current_cam = obj.GetComponent<Camera>();
          var raw = readImageFromHiddenCamera(current_cam, cam_config);
          msg.Append(raw);

          int layer_id = 0;
          foreach (var layer_on in cam_config.enabledLayers)
          {
            if (layer_on)
            {
              string filter_ID = cam_config.ID + "_" + layer_id.ToString();
              var rawimage = img_post_processing.getRawImage(internal_state.camera_filters[filter_ID],
                  settings.camWidth, settings.camHeight, img_post_processing.image_modes[layer_id]);
              msg.Append(rawimage);
            }
            layer_id += 1;
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
      // rendered_frame.ReadPixels(new Rect(cam_config.outputIndex*(cam_config.width), 0, 
      // (cam_config.outputIndex+1)*cam_config.width,  cam_config.height), 0, 0);
      rendered_frame.ReadPixels(new Rect(0, 0,
          cam_config.width, cam_config.height), 0, 0);
      rendered_frame.Apply();
      byte[] raw = rendered_frame.GetRawTextureData();
      return raw;
    }
    byte[] readImageFromHiddenCamera(Camera subcam, Camera_t cam_config)
    {
      var templRT = RenderTexture.GetTemporary(cam_config.width, cam_config.height, 24);
      //
      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;
      //
      RenderTexture.active = templRT;
      subcam.targetTexture = templRT;
      //
      // subcam.pixelRect = new Rect(cam_config.width * cam_config.outputIndex, 0, 
      // cam_config.width * ( cam_config.outputIndex+1), cam_config.height);
      subcam.pixelRect = new Rect(0, 0,
          cam_config.width, cam_config.height);
      subcam.fieldOfView = cam_config.fov;

      subcam.Render();
      var image = new Texture2D(cam_config.width, cam_config.height, TextureFormat.RGB24, false, true);
      image.ReadPixels(new Rect(0, 0, cam_config.width, cam_config.height), 0, 0);
      image.Apply();
      // var bytes = tex.EncodeToPNG();
      byte[] raw = image.GetRawTextureData();
      //
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;
      //
      UnityEngine.Object.Destroy(image);
      RenderTexture.ReleaseTemporary(templRT);
      //
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

    public void startSim()
    {
      ConnectToClient(input_ip.text);
      // Init simple splash screen
      Text text_obj = splash_screen.GetComponentInChildren<Text>(true);
      text_obj.text = "Connected, waiting for ROS client...";

    }
    public void quiteSim() { Application.Quit(); }

    // Helper functions for point cloud
    public void ButtonPointCloud()
    {
      SavePointCloud save_pointcloud = GetComponent<SavePointCloud>();
      PointCloudTask(save_pointcloud);

    }
    async void PointCloudTask(SavePointCloud save_pointcloud)
    {
      await save_pointcloud.GeneratePointCloud();
      PointCloudSaved.SetActive(true);
    }

    // Helper functions to toggle flying camera variable

    public void enableFlyingCam()
    {
      enable_flying_cam = true;
      FlyingCamSettings();
    }

    public void disableFlyingCam()
    {
      enable_flying_cam = false;
      FlyingCamSettings();
    }

    void FlyingCamSettings()
    {
      GameObject flying_cam = GameObject.Find("HDCamera");
      if (flying_cam)
      {
        flying_cam.GetComponent<ExtendedFlycam>().enabled = enable_flying_cam;
        Animator anim = flying_cam.GetComponent<Animator>();
        if (anim) { anim.enabled = !enable_flying_cam; }
      }
    }

  }
}


// backup code. -----
//Modifier: Yunlong Song <song@ifi.uzh.ch>
//Date: May 2019

// void updateLandmarkVisibility()
// {
//     if (internal_state.readyToRender)
//     {

//         // Erase old set of landmarks
//         state.landmarksInView = new List<Landmark_t>();

//         // Get camera to cast from
//         ObjectState_t internal_object_state = internal_state.getWrapperObject(state.cameras[0].ID, camera_template);
//         Camera castingCamera = internal_object_state.gameObj.GetComponent<Camera>();
//         Vector3 cameraPosition = internal_object_state.gameObj.transform.position;

//         // Get camera collider
//         Collider cameraCollider = internal_object_state.gameObj.GetComponent<Collider>();

//         // Cull landmarks based on camera frustrum
//         // Gives lookup table of screen positions.
//         Dictionary<string, Vector3> visibleLandmarkScreenPositions = new Dictionary<string, Vector3>();

//         foreach (KeyValuePair<string, GameObject> entry in internal_state.landmarkObjects)
//         {
//             Vector3 screenPoint = castingCamera.WorldToViewportPoint(entry.Value.transform.position);
//             bool visible = screenPoint.z > 0 && screenPoint.x > 0 && screenPoint.x < 1 && screenPoint.y > 0 && screenPoint.y < 1;
//             if (visible)
//             {
//                 visibleLandmarkScreenPositions.Add(entry.Key, screenPoint);
//             }
//         }


//         int numLandmarksInView = visibleLandmarkScreenPositions.Count();

//         // Batch raytrace from landmarks to camera
//         var results = new NativeArray<RaycastHit>(numLandmarksInView, Allocator.TempJob);
//         var commands = new NativeArray<RaycastCommand>(numLandmarksInView, Allocator.TempJob);

//         int i = 0;
//         var visibleLandmarkScreenPosList = visibleLandmarkScreenPositions.ToArray();
//         foreach (var elm in visibleLandmarkScreenPosList)
//         {
//             var landmark = internal_state.landmarkObjects[elm.Key];
//             Vector3 origin = landmark.transform.position;
//             Vector3 direction = cameraPosition - origin;

//             commands[i] = new RaycastCommand(origin, direction, distance: direction.magnitude, maxHits: max_num_ray_collisions);
//             i++;
//         }

//         // Run the raytrace commands
//         JobHandle handle = RaycastCommand.ScheduleBatch(commands, results, 1, default(JobHandle));
//         // Wait for the batch processing job to complete.
//         // @TODO: Move to end of frame.
//         handle.Complete();

//         // Cull based on number of collisions
//         // Remove if it collided with something
//         for (int j = 0; j < numLandmarksInView; j++)
//         {
//             // Check collisions. NOTE: indexing is via N*max_hits with first null being end of hit list.
//             RaycastHit batchedHit = results[j];
//             if (batchedHit.collider == null)
//             {
//                 // No collisions here. Add it to the current state.
//                 var landmark = visibleLandmarkScreenPosList[j];
//                 Landmark_t landmarkScreenPosObject = new Landmark_t();

//                 landmarkScreenPosObject.ID = landmark.Key;
//                 landmarkScreenPosObject.position = Vector3ToList(landmark.Value);
//                 state.landmarksInView.Add(landmarkScreenPosObject);

//             } else if (batchedHit.collider == cameraCollider)
//             {
//                 // No collisions here. Add it to the current state.
//                 var landmark = visibleLandmarkScreenPosList[j];
//                 Landmark_t landmarkScreenPosObject = new Landmark_t();

//                 landmarkScreenPosObject.ID = landmark.Key;
//                 landmarkScreenPosObject.position = Vector3ToList(landmark.Value);
//                 state.landmarksInView.Add(landmarkScreenPosObject);
//             }

//         }

//         results.Dispose();
//         commands.Dispose();
//     }
// }

// void instantiateObjects()
// {
//     // Initialize additional objects
//     foreach (var obj_state in settings.objects){
//         // Get object
//         ObjectState_t internal_object_state = internal_state.getWrapperObject(obj_state.ID, object_template);
//         GameObject obj = internal_object_state.gameObj;
//         // @TODO Set object size
//         //obj.transform.localScale = ListToVector3(obj_state.size);
//     }

//     // Check if should load obstacle perturbation file.
//     if (obstacle_perturbation_file.Length > 0) {
//         using (var reader = new StreamReader(obstacle_perturbation_file)) {
//             while (reader.Peek() >= 0) {
//                 // Read line
//                 string str;
//                 str = reader.ReadLine();

//                 // Parse line
//                 string objectName = str.Split(':')[0];
//                 string translationString = str.Split(':')[1];
//                 float[] translationsFloat = Array.ConvertAll(translationString.Split(','), float.Parse);

//                 // Find object
//                 GameObject obj = GameObject.Find(objectName);
//                 if (obj != null)
//                 {
//                     //// Check if object is statically batched. (NOT AVAILABLE IN STANDALONE BUILD)
//                     //int flags = (int)GameObjectUtility.GetStaticEditorFlags(obj);
//                     //if ((flags & 4)!= 0)
//                     //{
//                     //    // Gameobject is not movable!!!
//                     //    Debug.LogError("WARNING: " + objectName + " is statically batched and not movable! Make sure the gameobject is only lightmap static.");
//                     //} else
//                     //{
//                         // Translate and rotate object
//                         obj.transform.Translate(-translationsFloat[1], 0, translationsFloat[0], Space.World);
//                         obj.transform.Rotate(0, translationsFloat[2], 0, Space.World);

//                     //}
//                 }
//             }
//         }
//     }
//     if (outputLandmarkLocations)
//     {
//         // Output current locations.
//         Dictionary<string, List<GameObject>> GateMarkers = new Dictionary<string, List<GameObject>>();

//         // Find all landmarks and print to file.
//         foreach (GameObject obj in GameObject.FindGameObjectsWithTag("IR_Markers"))
//         {
//             // Tag the landmarks
//             string gateName = obj.transform.parent.parent.name;
//             string landmarkID = obj.name;

//             // Check if gate already exists.
//             if (GateMarkers.ContainsKey(gateName))
//             {
//                 GateMarkers[gateName].Add(obj);
//             }
//             else
//             {
//                 List<GameObject> markerList = new List<GameObject>();
//                 markerList.Add(obj);
//                 GateMarkers.Add(gateName, markerList);
//             }
//         }

//         // Print results
//         //Write some text to the test.txt file
//         StreamWriter writer = new StreamWriter("markerLocations.yaml", false);
//         foreach (var pair in GateMarkers)
//         {
//             writer.WriteLine(pair.Key + ":");
//             writer.Write("  location: [");

//             int i = 0;
//             // Sort landmark IDs
//             foreach (GameObject marker in pair.Value.OrderBy(gobj=>gobj.name).ToList())
//             {

//                 // Convert vector from EUN to NWU.
//                 Vector3 NWU = new Vector3(marker.transform.position.z, -marker.transform.position.x, marker.transform.position.y);

//                 // Print out locations in yaml format.
//                 writer.Write("[" + NWU.x + ", " + NWU.y + ", " + NWU.z + "]");
//                 if (i < 3)
//                 {
//                     writer.Write(", ");
//                 }
//                 else
//                 {
//                     writer.WriteLine("]");
//                 }
//                 i++;
//             }
//         }
//         writer.Close();
//     }

// }