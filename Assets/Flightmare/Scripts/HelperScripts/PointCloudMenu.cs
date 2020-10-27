using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;

namespace RPGFlightmare
{
  public class PointCloudMenu : MonoBehaviour
  {
    public GameObject CameraController;
    SavePointCloud save_pointcloud;

    // Start is called before the first frame update
    void Start()
    {
      save_pointcloud = CameraController.GetComponent<SavePointCloud>();
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void UpdateSlider()
    {
      Slider slider = GetComponent<Slider>();
      InputField input = GetComponentInChildren<InputField>();
      slider.value = float.Parse(input.text);
    }

    public void UpdateInputField()
    {
      Slider slider = GetComponent<Slider>();
      InputField input = GetComponentInChildren<InputField>();
      input.text = slider.value.ToString();
    }

    public void UpdateRangeX()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.range.x = slider.value;
    }

    public void UpdateRangeY()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.range.y = slider.value;
    }
    public void UpdateRangeZ()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.range.z = slider.value;
    }
    public void UpdateOriginX()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.origin.x = slider.value;
    }

    public void UpdateOriginY()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.origin.y = slider.value;
    }
    public void UpdateOriginZ()
    {
      Slider slider = GetComponent<Slider>();
      save_pointcloud.origin.z = slider.value;
    }

    public void UpdateResolution()
    {
      InputField input = GetComponent<InputField>();
      save_pointcloud.resolution = float.Parse(input.text);
    }
    public void UpdateFileName()
    {
      InputField input = GetComponent<InputField>();


      if (string.IsNullOrEmpty(input.text))
      {
        save_pointcloud.fileName = "default";
      }
      else
      {
        save_pointcloud.fileName = input.text;
      }
    }

    public void UpdatePath()
    {
      InputField input = GetComponent<InputField>();


      if (string.IsNullOrEmpty(input.text))
      {
        save_pointcloud.path = "pointcloud_data/";
      }
      else
      {
        if (input.text.EndsWith("/"))
        {
          save_pointcloud.path = input.text;
        }
        else
        {
          save_pointcloud.path = input.text + "/";
        }

      }
    }

  }
}//rpg_flightmare
