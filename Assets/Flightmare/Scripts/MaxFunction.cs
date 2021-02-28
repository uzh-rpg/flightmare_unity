using UnityEngine;
using System.IO;

public class MaxFunction : MonoBehaviour
{
  ComputeShader shader;

  string shaderName = "maxShader";
  public float[] groupMaxData;

  public int groupMax;
  public float maxval;
  int counter_ = 0;

  private ComputeBuffer groupMaxBuffer;
  private ComputeBuffer maxlumbuffer;


  private int handleMaximumMain;

  void Start()
  {
    shader = (ComputeShader)Resources.Load(shaderName);

    if (null == shader)
    {
      Debug.LogError("Shader texture missing.");
      return;
    }
    handleMaximumMain = shader.FindKernel("MaximumMain");
  }

  void OnDestroy()
  {
    if (null != groupMaxBuffer)
    {
      groupMaxBuffer.Release();
    }
  }

  public float calculateMax(RenderTexture src)
  {
    if (null == shader)
    {
      Debug.LogError("Shader texture missing.");
    }
    Texture2D tex = new Texture2D(src.width, src.height, TextureFormat.RGB24, false);
    tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
    RenderTexture.active = null;

    if (handleMaximumMain < 0 || null == groupMaxBuffer || null == groupMaxData)
    {
      groupMaxBuffer = new ComputeBuffer((src.height ) / 32, sizeof(uint));
      groupMaxData = new float[((src.height ) / 32)];
      shader.SetBuffer(handleMaximumMain, "GroupMaxBuffer", groupMaxBuffer);

    }
    shader.SetTexture(handleMaximumMain, "InputTexture", src);
    shader.SetInt("InputTextureWidth", src.width);
    shader.Dispatch(handleMaximumMain, (src.height ) / 32, 1, 1);

    // get maxima of groups
    groupMaxBuffer.GetData(groupMaxData);

    // find maximum of all groups
    groupMax = 0;
    maxval = 0;
    for (int group = 1; group < (src.height ) / 32; group++)
    {
      if (groupMaxData[group] > groupMaxData[groupMax])
      {
        groupMax = group;
        maxval = groupMaxData[group];
      }
    }
    maxval = groupMaxData[groupMax];
    return maxval;
  }
}