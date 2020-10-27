using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RPGFlightmare
{
  public class ScaleBoundingBox : MonoBehaviour
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
      this.transform.position = CoordinateChange(save_pointcloud.origin);
      this.transform.localScale = CoordinateChange(save_pointcloud.range);
    }

    Vector3 CoordinateChange(Vector3 vec)
    {
      return new Vector3(vec.x, vec.z, vec.y);
    }
  }
} // namespace RPGFlightmares
