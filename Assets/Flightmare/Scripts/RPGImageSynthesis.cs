using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System.IO;
using System.Collections.Generic;

// From Unity ImageSynthesis
// @TODO:
// . support custom color wheels in optical flow via lookup textures
// . support custom depth encoding
// . support multiple overlay cameras
// . tests
// . better example scene(s)
// @KNOWN ISSUES
// . Motion Vectors can produce incorrect results in Unity 5.5.f3 when
//      1) during the first rendering frame
//      2) rendering several cameras with different aspect ratios - vectors do stretch to the sides of the screen

namespace RPGFlightmare
{
  // [RequireComponent (typeof(Camera))]
  public class RPGImageSynthesis : MonoBehaviour
  {
    public MaxFunction maximum;
    public int counter_ = 0;
    public Shader uberReplacementShader;
    public Shader opticalFlowShader;
    public Shader OFmagShader;

    public float opticalFlowSensitivity = 1.0f;
    // cached materials
    private Material opticalFlowMaterial;
    private Material OFmagMat;

    public RenderTexture rt;
    int target_display = 3;

    private int _lastFrameIdx;
    public float threshold;


    // pass configuration
    enum ReplacelementModes
    {
      ObjectId = 0,
      CatergoryId = 1,
      DepthCompressed = 2,
      DepthMultichannel = 3,
      Normals = 4,
      DepthRaw = 5
    };
    public Dictionary<string, bool> support_antialiasing = new Dictionary<string, bool>() { };
    public Dictionary<string, bool> needs_rescale = new Dictionary<string, bool>() { };

    public List<string> image_modes = new List<string>() { "depth", "object_segment", "optical_flow" };
    void Start()
    {
      // default fallbacks, if shaders are unspecified
      if (!uberReplacementShader)
        uberReplacementShader = Shader.Find("Hidden/UberReplacement");

      if (!OFmagShader)
        OFmagShader = Shader.Find("Hidden/OFmag");
      OFmagMat = new Material(OFmagShader);

      maximum = GetComponent<MaxFunction>();
    }

    static private void SetupCameraWithPostShader(Camera cam, Material material, DepthTextureMode depthTextureMode = DepthTextureMode.None)
    {
      var cb = new CommandBuffer();
      cb.Blit(null, BuiltinRenderTextureType.CurrentActive, material);
      cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
      cam.depthTextureMode = depthTextureMode;
    }
    static private void SetupCameraWithPreShader(Camera cam, Material material, DepthTextureMode depthTextureMode = DepthTextureMode.None)
    {
      var cb = new CommandBuffer();
      cb.Blit(null, BuiltinRenderTextureType.CurrentActive, material);
      cam.AddCommandBuffer(CameraEvent.AfterEverything, cb);
      cam.depthTextureMode = depthTextureMode;
    }
    static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode)
    {
      SetupCameraWithReplacementShader(cam, shader, mode, Color.black);
    }
    static private void SetupCameraWithReplacementShader(Camera cam, Shader shader, ReplacelementModes mode, Color clearColor)
    {
      var cb = new CommandBuffer();
      cb.SetGlobalFloat("_OutputMode", (int)mode); // @TODO: CommandBuffer is missing SetGlobalInt() method
      cam.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cb);
      cam.AddCommandBuffer(CameraEvent.BeforeFinalPass, cb);
      cam.SetReplacementShader(shader, "");
      cam.backgroundColor = clearColor;
      cam.clearFlags = CameraClearFlags.SolidColor;
    }

    public Camera CreateHiddenCamera(string cam_name, string image_mode, float camFOV, float nearClipPlane, float farClipPlane, Camera mainCam)
    {
      var go = new GameObject(cam_name, typeof(Camera));
      go.hideFlags = HideFlags.HideAndDontSave;
      go.transform.parent = mainCam.transform;
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);
      var newCamera = go.GetComponent<Camera>();
      newCamera.nearClipPlane = nearClipPlane;
      newCamera.farClipPlane = farClipPlane;

      //
      updateCameraFilter(newCamera, image_mode);

      //
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);

      mainCam.GetComponent<Camera>();


      var mainCamera = mainCam.GetComponent<Camera>();
      if (!(mainCamera == newCamera))
      {
        // cleanup capturing camera
        // newCamera.RemoveAllCommandBuffers();
        // copy all "main" camera parameters into capturing camera
        newCamera.CopyFrom(mainCamera);
        newCamera.targetDisplay = target_display++;
        Debug.Log(newCamera.name + " targetdisplay: " + newCamera.targetDisplay);

      }
      //
      updateCameraFilter(newCamera, image_mode);
      newCamera.fieldOfView = camFOV;
      
      updateCameraFilter(newCamera, image_mode);
      // newCamera.targetDisplay = 1;
      return newCamera;
    }

    public Camera CreateHiddenOFCamera(string cam_name, float camFOV, Camera mainCam)
    {
      var go = new GameObject(cam_name, typeof(Camera));
      go.hideFlags = HideFlags.HideAndDontSave;
      go.transform.parent = mainCam.transform;
      //
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);

      var newCamera = go.GetComponent<Camera>();

      var mainCamera = mainCam.GetComponent<Camera>();
      if (!(mainCamera == newCamera))
      {
        // cleanup capturing camera
        // newCamera.RemoveAllCommandBuffers();
        // copy all "main" camera parameters into capturing camera
        newCamera.CopyFrom(mainCamera);
        // set targetDisplay here since it gets overriden by CopyFrom()
        newCamera.targetDisplay = target_display++;
        Debug.Log(newCamera.name + " targetdisplay: " + newCamera.targetDisplay);

      }
      string image_mode = "optical_flow";
      addOFCamera(newCamera, image_mode);

      return newCamera;
    }


    public Camera CreateEventCamera(string cam_name, float camFOV, Camera mainCam)
    {
      var go = new GameObject(cam_name, typeof(Camera));
      go.hideFlags = HideFlags.HideAndDontSave;
      go.transform.parent = mainCam.transform;
      //
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);

      var newCamera = go.GetComponent<Camera>();

      var mainCamera = mainCam.GetComponent<Camera>();
      if (!(mainCamera == newCamera))
      {
        // newCamera.RemoveAllCommandBuffers();
        newCamera.CopyFrom(mainCamera);
      }
      return newCamera;
    }
    public Camera CreateHiddenLogCamera(string cam_name, float camFOV, Camera mainCam)
    {

      var go = new GameObject(cam_name, typeof(Camera));
      go.hideFlags = HideFlags.HideAndDontSave;
      go.transform.parent = mainCam.transform;
      //
      go.transform.localPosition = new Vector3(0, 0, 0);
      go.transform.localEulerAngles = new Vector3(0, 0, 0);

      mainCam.GetComponent<Camera>();

      var newCamera = go.GetComponent<Camera>();

      var mainCamera = mainCam.GetComponent<Camera>();
      if (!(mainCamera == newCamera))
      {
        // newCamera.RemoveAllCommandBuffers();
        newCamera.CopyFrom(mainCamera);
        newCamera.targetDisplay = target_display++;
        Debug.Log(newCamera.name + " targetdisplay: " + newCamera.targetDisplay);

      }
      return newCamera;
    }

    public void updateCameraFilter(Camera subcam, string image_mode)
    {
      // cache materials and setup material properties
      if (!opticalFlowMaterial || opticalFlowMaterial.shader != opticalFlowShader)
        opticalFlowMaterial = new Material(opticalFlowShader);
      opticalFlowMaterial.SetFloat("_Sensitivity", opticalFlowSensitivity);

      switch (image_mode)
      {
        // Setup Object Segmentation for the camera
        case "object_segment":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.ObjectId);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup Catergory Segmentation for the camera
        case "category_segment":
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.CatergoryId);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup compressed depth for the camera
        case "depth":
          //SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.DepthCompressed, Color.white);
          SetupCameraWithReplacementShader(subcam, uberReplacementShader, ReplacelementModes.DepthRaw, Color.white);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = true;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
        // Setup optical flow for the camera
        case "optical_flow": // TODO: color encoding does not work well.......
          SetupCameraWithPostShader(subcam, opticalFlowMaterial, DepthTextureMode.MotionVectors);
          if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
          if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;
          break;
      }
    }
    public void addOFCamera(Camera subcam, string image_mode)
    {
      // cache materials and setup material properties
      if (!OFmagMat || OFmagMat.shader != OFmagShader)
        OFmagMat = new Material(OFmagShader);

      SetupCameraWithPreShader(subcam, OFmagMat, DepthTextureMode.MotionVectors);
      if (!support_antialiasing.ContainsKey(image_mode)) support_antialiasing[image_mode] = false;
      if (!needs_rescale.ContainsKey(image_mode)) needs_rescale[image_mode] = false;

    }


    public void OnSceneChange()
    {
      var renderers = Object.FindObjectsOfType<Renderer>();
      var mpb = new MaterialPropertyBlock();
      foreach (var r in renderers)
      {
        var id = r.gameObject.GetInstanceID();
        var layer = r.gameObject.layer;
        var tag = r.gameObject.tag;

        mpb.SetColor("_ObjectColor", ColorEncoding.EncodeIDAsColor(id));
        mpb.SetColor("_CategoryColor", ColorEncoding.EncodeLayerAsColor(layer));
        r.SetPropertyBlock(mpb);
      }
    }

    public byte[] getRawImage(Camera subcam, int width, int height, string image_mode)
    {
      bool supportsAntialiasing = support_antialiasing[image_mode];
      bool needsRescale = needs_rescale[image_mode];
      var depth = 24;
      var format = image_mode=="depth" ? RenderTextureFormat.RFloat : RenderTextureFormat.Default;
      var readWrite = RenderTextureReadWrite.Default;
      var antiAliasing = (supportsAntialiasing) ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

      // render texture for current camera
      var finalRT =
          RenderTexture.GetTemporary(width, height, depth, format, readWrite, antiAliasing);

      var renderRT = (!needsRescale) ? finalRT :
          RenderTexture.GetTemporary(subcam.pixelWidth, subcam.pixelHeight, depth, format, readWrite, antiAliasing);
      var tex = new Texture2D(width, height, image_mode=="depth" ? TextureFormat.RFloat : TextureFormat.RGB24, false);

      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;

      // render to offscreen texture (readonly from CPU side)
      RenderTexture.active = renderRT;
      subcam.targetTexture = renderRT;

      subcam.Render();
      if (needsRescale)
      {
        // blit to rescale (see issue with Motion Vectors in @KNOWN ISSUES)
        RenderTexture.active = finalRT;
        Graphics.Blit(renderRT, finalRT);
        RenderTexture.ReleaseTemporary(renderRT);
      }
      // read offsreen texture contents into the CPU readable texture
      tex.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
      tex.Apply();

      byte[] raw = tex.GetRawTextureData();

      // restore state and cleanup
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;

      Object.Destroy(tex);
      RenderTexture.ReleaseTemporary(renderRT);
      RenderTexture.ReleaseTemporary(finalRT);
      return raw;
    }

    public System.Int64 getDeltaTime(Camera subcam, int width, int height, System.Int64 last_timestep)
    {
      bool supportsAntialiasing = true;
      var depth = 24;
      var format = RenderTextureFormat.Default;
      var readWrite = RenderTextureReadWrite.Default;
      var antiAliasing = (supportsAntialiasing) ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

      // render texture for current camera
      var renderRT = RenderTexture.GetTemporary(subcam.pixelWidth, subcam.pixelHeight, depth, format, readWrite, antiAliasing);
      var tex_ = new Texture2D(width, height, TextureFormat.RGB24, false);

      var prevActiveRT = RenderTexture.active;
      var prevCameraRT = subcam.targetTexture;

      // render to offscreen texture (readonly from CPU side)
      RenderTexture.active = renderRT;
      subcam.targetTexture = renderRT;

      subcam.Render();

      // read offsreen texture contents into the CPU readable texture
      tex_.ReadPixels(new Rect(0, 0, tex_.width, tex_.height), 0, 0);
      tex_.Apply();

      var max_of = maximum.calculateMax(renderRT);

      // restore state and cleanup
      subcam.targetTexture = prevCameraRT;
      RenderTexture.active = prevActiveRT;
      //minimal framerate is minimally set to:
      float min_delta_sec = 0.003f;
      // transform the int to float
      // Debug.Log("lasttimestep: " + last_timestep);
      float last_dt_sec = (float)(last_timestep / 1e6);
      // initialize the of delta 
      float of_dt_sec = min_delta_sec;
      // constantvalue whcih changes the framerate
      float multiplier = 0.4f;
      // calculate of_max framerate in secs
      if (max_of > 0 && last_dt_sec != 0) of_dt_sec = multiplier / max_of ;

      // DefaultExecutionOrder which to take
      double delta_sec = Mathf.Min(of_dt_sec, min_delta_sec);

      // RectTransform to microsecs
      System.Int64 delta_micro = System.Convert.ToInt64(1e6 * delta_sec);
      Object.Destroy(tex_);
      RenderTexture.ReleaseTemporary(renderRT);
      return delta_micro;
    }

  }
}
