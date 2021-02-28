using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MessageSpec;
using System;

// [ExecuteInEditMode]
public class eventsCompute : MonoBehaviour
{
  // this was assigned in the unity gui
  public RenderTexture rt;
  public RenderTexture tex_out;


  public ComputeShader shader;
  public ComputeShader pre_shader;
  public ComputeShader scan_shader;



  private int n_samples = 2;
  private int _lastFrameIdx;
  private int max_n_events = 2;
  public float pos_threshold;
  public float neg_threshold;
  public float sigma_cp;
  public float sigma_cm;
  public int refractory_period;
  public float log_eps;
  private RenderTexture tempSource = null;
  private RenderTexture tempDestination = null;
  private Int64 current_time;

  private Int64 deltatime;
  private int frame = 0;
  public int counts = 0;
  private bool is_first = true;
  private bool is_firstsix = true;

  private int to_be_canceled;

  private Event_t[] output;

  public bool SetTime(Int64 time, Int64 dt)
  {
    current_time = time;
    deltatime = dt;
    return true;
  }
  public Int64 GetTime()
  {
    return current_time;
  }

  void Start()
  {
    Debug.Log("number of events per pixel" + max_n_events);
  }

  public static List<float> Vector2ToList(Vector2 vec) { return new List<float>(new float[] { vec[0], vec[1] }); }


  // after a whole scene is rendered
  void OnRenderImage(RenderTexture source, RenderTexture destination)
  {
    int buff_size = source.height * source.width;

    if (null == tempSource || source.width != tempSource.width
       || source.height != tempSource.height)
    {
      if (null != tempSource)
      {
        tempSource.Release();
      }
      tempSource = new RenderTexture(source.width, source.height,
        source.depth);
      tempSource.Create();
    }

    // copy source pixels
    Graphics.Blit(source, tempSource);

    int potw = Mathf.NextPowerOfTwo(source.width);
    int poth = Mathf.NextPowerOfTwo(source.height);
    if (rt == null)
    {

      // RenderTexture creation without a depth buffer
      rt = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGBFloat);
      rt.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
      rt.volumeDepth = n_samples;
      rt.useMipMap = false;
      rt.autoGenerateMips = false;
      // rt.antiAliasing=8;
      rt.filterMode = FilterMode.Point;
      rt.enableRandomWrite = true;

      rt.Create();
    }


    if (tex_out == null)
    {
      tex_out = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGBFloat);
      tex_out.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
      tex_out.useMipMap = false;
      tex_out.autoGenerateMips = false;
      tex_out.filterMode = FilterMode.Point;
      tex_out.volumeDepth = n_samples;
      tex_out.enableRandomWrite = true;

      tex_out.Create();
    }

    if (null == tempDestination || source.width != tempDestination.width
         || source.height != tempDestination.height)
    {
      if (null != tempDestination)
      {
        tempDestination.Release();
      }
      tempDestination = new RenderTexture(source.width, source.height,
         source.depth);
      tempDestination.enableRandomWrite = true;
      tempDestination.Create();
    }
    // // // check amount of events
    uint[] amount_events = new uint[buff_size];

    //INITIALIZE the data HERE the struct array is initialized
    ComputeBuffer pre_buffer = new ComputeBuffer(amount_events.Length, 4);


    int pre_kernel = pre_shader.FindKernel("preScan");
    // load the texture and the buffer into the shader
    pre_shader.SetTexture(pre_kernel, "newImage", tempSource);
    if (frame % 2 == 0)
    {
      pre_shader.SetTexture(pre_kernel, "imageBuffer", rt);
    }
    else
    {
      pre_shader.SetTexture(pre_kernel, "imageBuffer", tex_out);
    }
    pre_shader.SetBuffer(pre_kernel, "dataBuffer", pre_buffer);


    // set time
    pre_shader.SetBool("is_first", is_first);
    pre_shader.SetFloat("Thresh", pos_threshold);
    pre_shader.SetInt("imageWidth", source.width);

    // RunShader the shader
    pre_shader.Dispatch(pre_kernel, (source.width / 8), (source.height / 8), 1);
    pre_buffer.GetData(amount_events);

    // Compute how many thread groups we will need.
    int threadsPerGroup = 8;
    int threadGroupCount = buff_size / threadsPerGroup;

    uint[] cumulative_amount_events = new uint[buff_size];

    ComputeBuffer scan_buffer = new ComputeBuffer(cumulative_amount_events.Length, 4);
    scan_buffer.SetData(amount_events);

    ComputeBuffer aux_buffer = new ComputeBuffer(cumulative_amount_events.Length, 4);

    ComputeBuffer res_buffer = new ComputeBuffer(cumulative_amount_events.Length, 4);

    int scanInBucketKernel = scan_shader.FindKernel("ScanInBucketExclusive");
    int _scanBucketResultKernel = scan_shader.FindKernel("ScanBucketResult");
    int _scanAddBucketResultKernel = scan_shader.FindKernel("ScanAddBucketResult");

    scan_shader.SetBuffer(scanInBucketKernel, "_Input", scan_buffer);
    scan_shader.SetBuffer(scanInBucketKernel, "_Result", res_buffer);
    scan_shader.Dispatch(scanInBucketKernel, threadGroupCount, 1, 1);

    // ScanBucketResult.
    scan_shader.SetBuffer(_scanBucketResultKernel, "_Input", res_buffer);
    scan_shader.SetBuffer(_scanBucketResultKernel, "_Result", aux_buffer);
    scan_shader.Dispatch(_scanBucketResultKernel, 1, 1, 1);

    // ScanAddBucketResult.
    scan_shader.SetBuffer(_scanAddBucketResultKernel, "_Input", aux_buffer);
    scan_shader.SetBuffer(_scanAddBucketResultKernel, "_Result", res_buffer);
    scan_shader.Dispatch(_scanAddBucketResultKernel, threadGroupCount, 1, 1);
    res_buffer.GetData(cumulative_amount_events);

    uint total_events = cumulative_amount_events[buff_size - 1] + 1;
    if (is_firstsix) total_events = System.Convert.ToUInt32(source.width) * System.Convert.ToUInt32(source.height);

    counts++;
    // // // create events
    output = new Event_t[total_events];


    //INITIALIZE the data HERE the struct array is initialized
    ComputeBuffer buffer = new ComputeBuffer(output.Length, 16);
    buffer.SetData(output);

    int kernel = shader.FindKernel("EventShader");
    // load the texture and the buffer into the shader
    shader.SetTexture(kernel, "newImage", tempSource);
    shader.SetTexture(kernel, "Destination", tempDestination);
    if (frame % 2 == 0)
    {
      shader.SetTexture(kernel, "imageBuffer", rt);
      shader.SetTexture(kernel, "imageCopy", tex_out);
    }
    else
    {
      shader.SetTexture(kernel, "imageBuffer", tex_out);
      shader.SetTexture(kernel, "imageCopy", rt);
    }
    shader.SetBuffer(kernel, "amountBuffer", pre_buffer);
    shader.SetBuffer(kernel, "positionBuffer", res_buffer);

    shader.SetBuffer(kernel, "dataBuffer", buffer);
    // setitime
    shader.SetInt("Time_", System.Convert.ToInt32(current_time));
    shader.SetInt("deltaTime", System.Convert.ToInt32(deltatime));
    shader.SetBool("is_first", is_firstsix);
    // shader.SetInt("amount_events", max_n_events);
    shader.SetFloat("negThresh", pos_threshold);
    shader.SetFloat("posThresh", neg_threshold);
    shader.SetInt("imageWidth", source.width);
    shader.SetInt("imageHeight", source.height);
    shader.SetInt("refractory_period", refractory_period);

    if (System.Convert.ToInt32(current_time) != current_time)
    {
      Debug.LogError("the time is wrongly transformed to shader");
    }

    // RunShader the shader
    shader.Dispatch(kernel, (source.width / 8), (source.height / 8), 1);
    // grabs the data from the gpu and dumps it into "output" array
    buffer.GetData(output);
    Graphics.Blit(tempDestination, destination);

    buffer.Release();
    pre_buffer.Release();
    scan_buffer.Release();
    aux_buffer.Release();
    res_buffer.Release();

    if (frame > 0) is_first = false;
    if (counts == 5) is_firstsix = false;
    frame++;
  }
  public Event_t[] getoutput()
  { return output; }

  void OnDestroy()
  {

  }
}