using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;


namespace RPGFlightmare
{

  public class SavePointCloud : MonoBehaviour
  {
    public Vector3 range = new Vector3(20.0f, 20.0f, 20.0f); //range of pointcloud around origin
    public Vector3 origin = Vector3.zero; //origin of the pointcloud
    public float resolution = 0.15f; //default
    public string path = "point_cloud_data/";
    public string fileName = "default"; //just name without format
    List<string> ignore = new List<string>(); // object that should not appear in pointcloud

    // Start is called before the first frame update
    void Start()
    {
      //Unwanted objects for pointcloud
      ignore.Add("HDCamera");
      ignore.Add("Drone_red");
      ignore.Add("Transparent_Cube");
    }

    // Update is called once per frame
    void Update()
    {

    }

    public async Task GeneratePointCloud()
    {
      const float epsilon = 0.00001f;
      // add epsilon so that the pointcloud certainly includes the boundries
      Vector3 range_eps = new Vector3(range.x + epsilon, range.y + epsilon,
                                          range.z + epsilon);

      // Rasterizing world and checking collisions...

      List<Vector3> occupied_points = new List<Vector3>();

      // iterate over z coordinate
      for (float z = resolution / 2.0f + origin.z -
                      range_eps.z / 2.0f;
          z < origin.z + range_eps.z / 2.0f;
          z += resolution)
      {
        Debug.Log("===================Generating Pointcloud====================");
        // iterate over x coordinate
        for (float x = resolution / 2.0f + origin.x -
                        range_eps.x / 2.0f;
            x < origin.x + range_eps.x / 2.0f;
            x += resolution)
        {
          // iterate over y coordinate
          for (float y = resolution / 2.0f + origin.y -
                          range_eps.y / 2.0f;
              y < origin.y + range_eps.y / 2.0f;
              y += resolution)
          {
            // check if current position is occupied
            // switch in order (x, y, z) to (x, z, y) so that
            // ground is normal to z
            Vector3 point = new Vector3(x, z, y);
            if (checkIfOccupied(point, resolution))
            {
              point = new Vector3(x, y, z);
              occupied_points.Add(point);
            }
          }
        }
        await Task.Yield();
      }
      Debug.Log(occupied_points.Count);
      SaveInFile(occupied_points);

    }

    bool checkIfOccupied(Vector3 point, float leaf_size)
    {

      var hitColliders = Physics.OverlapSphere(point, leaf_size);

      // check if collider hit an object
      if (hitColliders.Length > 0.0f)
      {
        // removes Camera from Point Cloud
        if (checkIfNotUnwantedOnly(hitColliders))
        {
          return true;
        }
      }

      return false;
    }

    bool checkIfNotUnwantedOnly(Collider[] hitColliders)
    {
      for (int i = 0; i < hitColliders.Length; i++)
      {

        if (!ignore.Contains(hitColliders[i].gameObject.name))
        {
          return true;
        }
      }
      return false;
    }

    // C# code snippet to write a Stanford Polygon binary file 
    // https://dominoc925.blogspot.com/2014/09/c-code-snippet-to-write-stanford.html
    void SaveInFile(List<Vector3> occupied_points)
    {

      Directory.CreateDirectory(path);
      string filePath = path + fileName + ".ply";

      BinaryWriter writer = new BinaryWriter(new FileStream(filePath, FileMode.Create), Encoding.ASCII);
      //Write the headers for vertices
      writer.Write(str2byteArray("ply\n"));
      writer.Write(str2byteArray("format binary_little_endian 1.0\n"));
      writer.Write(str2byteArray("element vertex " + occupied_points.Count + "\n"));
      writer.Write(str2byteArray("property float x\n"));
      writer.Write(str2byteArray("property float y\n"));
      writer.Write(str2byteArray("property float z\n"));
      writer.Write(str2byteArray("end_header\n"));

      //Write the vertices
      for (int i = 0; i < occupied_points.Count; i++)
      {
        writer.Write(float2byteArray(occupied_points.ElementAt(i).x));
        writer.Write(float2byteArray(occupied_points.ElementAt(i).y));
        writer.Write(float2byteArray(occupied_points.ElementAt(i).z));
      }
      writer.Close();
    }
    private byte[] float2byteArray(float value)
    {
      return BitConverter.GetBytes(value);
    }
    private byte[] str2byteArray(string theString)
    {
      return System.Text.Encoding.ASCII.GetBytes(theString);
    }

  }

} //RPG_Flightmare
